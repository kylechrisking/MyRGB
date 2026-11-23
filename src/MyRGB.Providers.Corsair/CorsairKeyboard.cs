using MyRGB.Hid;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MyRGB.Core;
using MyRGB.Core.Devices;

namespace MyRGB.Providers.Corsair;

/// <summary>
/// A Corsair keyboard driven directly over its vendor HID interface. Opening it starts a session and
/// switches to software mode; each <see cref="ApplyAsync"/> writes one full frame; disposing hands the
/// lighting back to the keyboard and closes the session, as iCUE does when it quits.
/// </summary>
internal sealed class CorsairKeyboard : IScreenDevice, IDeviceCommands, IAsyncDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Arbitrary client token for the session request ("MyRG").</summary>
    private const uint SessionToken = 0x4D795247;

    private readonly FileStream _stream;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Channel<byte[]> _replies = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _frame = new byte[Bragi.ReportLength];
    private readonly byte[] _rgb = new byte[VanguardPro96Layout.SlotCount * 3];
    private readonly int[] _slots;
    private readonly Task _reader;
    private byte _session;
    private bool _lightingOpen;
    private int _fileWrites;
    private KeyboardControls? _controls;

    private CorsairKeyboard(FileStream stream, string serialHint, ILogger log)
    {
        _stream = stream;
        _log = log;
        _slots = VanguardPro96Layout.Keys.Select(k => k.Slot).ToArray();
        Leds = VanguardPro96Layout.Leds();
        Zones = [new ZoneInfo("Keys", 0, Leds.Count)];
        Id = $"corsair:vanguard-pro-96:{serialHint}";
        _reader = Task.Run(ReadLoopAsync);
    }

    public string Id { get; }
    public string Name => "Corsair Vanguard Pro 96";
    public string Vendor => "Corsair";
    public DeviceType Type => DeviceType.Keyboard;
    public string ProviderId => CorsairProvider.ProviderId;
    public string? HardwareKey => "usb:1b1c:2b0e";
    public IReadOnlyList<LedInfo> Leds { get; }
    public IReadOnlyList<ZoneInfo> Zones { get; }

    /// <summary>True once I/O has failed (unplugged, or the device stopped answering).</summary>
    public bool Faulted { get; private set; }

    public event Action? Lost;

    public event Action<IRgbDevice, DeviceCommand>? Command;

    public static Task<CorsairKeyboard> OpenAsync(HidInterface iface, ILogger log, CancellationToken ct) =>
        OpenAsync(iface, log, Bragi.ModeSoftware, ct);

    /// <summary>Opens the keyboard in the given operating mode (normally <see cref="Bragi.ModeSoftware"/>).</summary>
    internal static async Task<CorsairKeyboard> OpenAsync(HidInterface iface, ILogger log, uint mode, CancellationToken ct)
    {
        var stream = HidDevices.Open(iface.Path);
        var kb = new CorsairKeyboard(stream, SerialHint(iface.Path), log);
        try
        {
            var reply = await kb.CommandAsync(Bragi.OpenSession(SessionToken), Bragi.SessionTarget, Bragi.Session, ct);
            kb._session = Bragi.SessionId(reply);
            log.LogDebug("Corsair keyboard session {Session}", kb._session);

            await kb.CommandAsync(Bragi.Set(kb._session, Bragi.PropertyMode, mode), kb._session, Bragi.SetProperty, ct);
            await kb.CommandAsync(Bragi.Open(kb._session, Bragi.LightingHandle, Bragi.LightingResource), kb._session, Bragi.OpenHandle, ct);
            kb._lightingOpen = true;
            // Software mode leaves the wheel and some Fn keys to us; pass them on to Windows or MyRGB.
            kb._controls = KeyboardControls.TryOpen(log, c => kb.Command?.Invoke(kb, c));
            return kb;
        }
        catch
        {
            await kb.DisposeAsync();
            throw;
        }
    }

    public async ValueTask ApplyAsync(ReadOnlyMemory<Rgb> colors, CancellationToken ct = default)
    {
        // Lighting frames and file writes are both command 0x06; skip frames while a file is being written
        // so a late frame reply can't be taken for a write reply.
        if (Faulted || !_lightingOpen || Volatile.Read(ref _fileWrites) > 0) return;
        var c = colors.Span;
        Array.Clear(_rgb);
        for (int i = 0; i < _slots.Length && i < c.Length; i++)
        {
            int o = _slots[i] * 3;
            _rgb[o] = c[i].R;
            _rgb[o + 1] = c[i].G;
            _rgb[o + 2] = c[i].B;
        }

        await _io.WaitAsync(ct);
        try
        {
            Bragi.WriteColors(_frame, _session, Bragi.LightingHandle, _rgb);
            await _stream.WriteAsync(_frame, ct); // the reply is drained by the read loop
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Fault(ex);
        }
        finally
        {
            _io.Release();
        }
    }

    // ---- The screen (docs/LCD-PROTOCOL.txt) ----

    private readonly SemaphoreSlim _screenGate = new(1, 1);

    public int ScreenWidth => ScreenFiles.Width;
    public int ScreenHeight => ScreenFiles.Height;

    /// <summary>
    /// Where the backup of the upload now on the keyboard lives (in <c>current\</c>). Set by the provider;
    /// null turns uploads off.
    /// </summary>
    internal string? ScreenDataDirectory { get; set; }

    private string? CurrentUploadDir => ScreenDataDirectory is { } d ? Path.Combine(d, "current") : null;

    public bool HasScreenImage => CurrentUploadDir is { } d && File.Exists(Path.Combine(d, ScreenUpload.ManifestName));

    public bool ScreenFirst { get; private set; }

    public async Task SetScreenImageAsync(ReadOnlyMemory<byte> rgb, CancellationToken ct = default)
    {
        var dir = CurrentUploadDir ?? throw new InvalidOperationException("Screen uploads are off: there is no folder for the backup.");
        await _screenGate.WaitAsync(ct);
        try
        {
            await InHostModeAsync(async () =>
            {
                // One MyRGB picture at a time: put the keyboard back as it was, then upload the new one.
                if (HasScreenImage) await UndoUploadAsync(dir, ct);
                try
                {
                    await ScreenUpload.UploadStaticImageAsync(this, rgb, dir, s => _log.LogDebug("Screen upload: {Step}", s), ct);
                    _log.LogInformation("Put a picture on the keyboard's screen");
                }
                catch (Exception ex) when (File.Exists(Path.Combine(dir, ScreenUpload.ManifestName)))
                {
                    // Something was written; take it back off rather than leave half an upload.
                    _log.LogWarning(ex, "Screen upload failed part way; undoing it");
                    try { await UndoUploadAsync(dir, CancellationToken.None); }
                    catch (Exception undo) { _log.LogError(undo, "Undoing the failed screen upload failed too; the backup stays in {Dir}", dir); }
                    throw;
                }
            }, ct);
        }
        finally
        {
            _screenGate.Release();
        }
    }

    public async Task ClearScreenImageAsync(CancellationToken ct = default)
    {
        if (CurrentUploadDir is not { } dir || !HasScreenImage) return;
        await _screenGate.WaitAsync(ct);
        try
        {
            await InHostModeAsync(() => UndoUploadAsync(dir, ct), ct);
            _log.LogInformation("Took MyRGB's picture off the keyboard's screen");
        }
        finally
        {
            _screenGate.Release();
        }
    }

    /// <summary>Restores what the upload in <paramref name="dir"/> changed, then removes its backup.</summary>
    private async Task UndoUploadAsync(string dir, CancellationToken ct)
    {
        await ScreenUpload.RollBackAsync(this, dir, s => _log.LogDebug("Screen rollback: {Step}", s), ct);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// The picture only shows while the keyboard runs itself, and in practice only once the host has let go
    /// of it completely: on 2026-11-15 mode 1 with MyRGB's session still open showed nothing, while every
    /// full release (MyRGB closed, the keyboard dropped) showed it. So On does what quitting does: mode 1,
    /// lighting handle closed, session closed. Off opens a new session and takes the lighting again.
    /// </summary>
    public async Task SetScreenFirstAsync(bool on, CancellationToken ct = default)
    {
        await _screenGate.WaitAsync(ct);
        try
        {
            if (on == ScreenFirst || Faulted) return;
            if (on)
            {
                ScreenFirst = true;
                try
                {
                    await ReleaseAsync(ct);
                }
                catch
                {
                    // Don't leave the keys frozen on the last frame: take the lighting back.
                    try { await TakeLightingAsync(CancellationToken.None); } catch (Exception ex) { _log.LogDebug(ex, "Taking the lighting back failed"); }
                    ScreenFirst = false;
                    throw;
                }
            }
            else
            {
                await TakeLightingAsync(ct);
                ScreenFirst = false;
            }
        }
        finally
        {
            _screenGate.Release();
        }
    }

    /// <summary>
    /// Web Hub switches to host mode for every file operation and back afterwards. While the keyboard is
    /// released, open a session in host mode for the work and release it again after.
    /// </summary>
    private async Task InHostModeAsync(Func<Task> work, CancellationToken ct)
    {
        if (!ScreenFirst)
        {
            await work();
            return;
        }
        await OpenSessionAsync(Bragi.ModeSoftware, ct);
        try { await work(); }
        finally
        {
            try { await ReleaseAsync(CancellationToken.None); }
            catch (Exception ex) { _log.LogDebug(ex, "Releasing the keyboard after a file operation failed"); }
        }
    }

    /// <summary>Host mode with the lighting handle open, opening a session first if there is none.</summary>
    private async Task TakeLightingAsync(CancellationToken ct)
    {
        if (_session == 0) await OpenSessionAsync(Bragi.ModeSoftware, ct);
        else await CommandAsync(Bragi.Set(_session, Bragi.PropertyMode, Bragi.ModeSoftware), _session, Bragi.SetProperty, ct);
        if (!_lightingOpen)
        {
            await CommandAsync(Bragi.Open(_session, Bragi.LightingHandle, Bragi.LightingResource), _session, Bragi.OpenHandle, ct);
            _lightingOpen = true;
        }
    }

    private async Task OpenSessionAsync(uint mode, CancellationToken ct)
    {
        var reply = await CommandAsync(Bragi.OpenSession(SessionToken), Bragi.SessionTarget, Bragi.Session, ct);
        _session = Bragi.SessionId(reply);
        _log.LogDebug("Corsair keyboard session {Session}", _session);
        await CommandAsync(Bragi.Set(_session, Bragi.PropertyMode, mode), _session, Bragi.SetProperty, ct);
    }

    /// <summary>What quitting does, minus closing the device: mode 1, lighting handle closed, session closed.</summary>
    private async Task ReleaseAsync(CancellationToken ct)
    {
        if (_session == 0) return;
        _lightingOpen = false; // frames stop here, before the handle goes
        await CommandAsync(Bragi.Set(_session, Bragi.PropertyMode, Bragi.ModeHardware), _session, Bragi.SetProperty, ct);
        await TryCommandAsync(Bragi.Close(_session, Bragi.LightingHandle), _session, Bragi.CloseHandle, ct);
        await TryCommandAsync(Bragi.CloseSession(_session), _session, Bragi.Session, ct);
        _session = 0;
    }

    /// <summary>A property's u32 value, or null if the keyboard rejects it.</summary>
    internal async Task<uint?> GetPropertyAsync(ushort property, CancellationToken ct = default)
    {
        var reply = await TryCommandAsync(Bragi.Get(_session, property), _session, Bragi.GetProperty, ct);
        return reply is null ? null : Bragi.PropertyValue(reply);
    }

    /// <summary>
    /// Reads one onboard file on <see cref="Bragi.FileBuffer"/>, or returns null if it doesn't exist.
    /// Read-only: it never creates or changes a file. The buffer is always unbound afterwards.
    /// </summary>
    internal async Task<byte[]?> ReadFileAsync(ushort fileId, CancellationToken ct = default)
    {
        const byte buf = Bragi.FileBuffer;
        try
        {
            if (!await OpenFileAsync(fileId, ct)) return null;

            int size = Bragi.DescribedSize(await CommandAsync(Bragi.Describe(_session, buf), _session, Bragi.DescribeBuffer, ct));
            if (size is < 0 or > 16 * 1024 * 1024) throw new IOException($"File {fileId} reports an implausible size ({size}).");
            var data = new byte[size];
            int got = 0;
            while (got < size)
            {
                var reply = await CommandAsync(Bragi.Read(_session, buf), _session, Bragi.ReadBuffer, ct);
                int chunk = Math.Min(size - got, reply.Length - Bragi.ReplyDataOffset);
                if (chunk <= 0) throw new IOException($"Empty read from file {fileId}.");
                reply.AsSpan(Bragi.ReplyDataOffset, chunk).CopyTo(data.AsSpan(got));
                got += chunk;
            }
            return data;
        }
        finally
        {
            await CloseFileAsync();
        }
    }

    /// <summary>True if the file exists. Opens it and closes it again; changes nothing.</summary>
    internal async Task<bool> FileExistsAsync(ushort fileId, CancellationToken ct = default)
    {
        try { return await OpenFileAsync(fileId, ct); }
        finally { await CloseFileAsync(); }
    }

    /// <summary>
    /// Replaces an onboard file's contents with <paramref name="data"/>, creating the file if it doesn't exist
    /// (Web Hub's <c>writeFile</c>). Lighting frames are skipped while it runs.
    /// </summary>
    internal async Task WriteFileAsync(ushort fileId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _fileWrites);
        try
        {
            if (!await OpenFileAsync(fileId, ct))
            {
                await CloseFileAsync();
                await CommandAsync(Bragi.Create(_session, fileId), _session, Bragi.CreateFile, ct);
                if (!await OpenFileAsync(fileId, ct)) throw new IOException($"Could not open file {fileId} after creating it.");
            }
            foreach (var report in Bragi.FileWrite(_session, Bragi.FileBuffer, data))
                await CommandAsync(report, _session, report[4], ct);
        }
        finally
        {
            await CloseFileAsync();
            Interlocked.Decrement(ref _fileWrites);
        }
    }

    /// <summary>Deletes an onboard file. False if the keyboard refused (for example, it didn't exist).</summary>
    internal async Task<bool> DeleteFileAsync(ushort fileId, CancellationToken ct = default) =>
        await TryCommandAsync(Bragi.Delete(_session, fileId), _session, Bragi.DeleteFile, ct) is not null;

    /// <summary>Opens a file on <see cref="Bragi.FileBuffer"/>. False if it doesn't exist.</summary>
    private async Task<bool> OpenFileAsync(ushort fileId, CancellationToken ct)
    {
        var open = Bragi.Open(_session, Bragi.FileBuffer, fileId);
        if (await TryCommandAsync(open, _session, Bragi.OpenHandle, ct) is not null) return true;
        // Web Hub retries once after unbinding, for a buffer left bound by an earlier failure.
        await CloseFileAsync();
        return await TryCommandAsync(open, _session, Bragi.OpenHandle, ct) is not null;
    }

    private Task<byte[]?> CloseFileAsync() =>
        TryCommandAsync(Bragi.Close(_session, Bragi.FileBuffer), _session, Bragi.CloseHandle, CancellationToken.None);

    private async Task<byte[]> CommandAsync(byte[] report, byte target, byte command, CancellationToken ct) =>
        await TryCommandAsync(report, target, command, ct)
        ?? throw new IOException($"Keyboard rejected command 0x{command:X2}.");

    /// <summary>Send a command and wait for its reply. Null if the device answered with an error status.</summary>
    private async Task<byte[]?> TryCommandAsync(byte[] report, byte target, byte command, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            while (_replies.Reader.TryRead(out _)) { } // drop replies to earlier frames
            await _stream.WriteAsync(report, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReplyTimeout);
            while (true)
            {
                var reply = await _replies.Reader.ReadAsync(timeout.Token);
                if (!Bragi.IsReplyTo(reply, target, command)) continue;
                byte status = Bragi.Status(reply);
                if (status == 0) return reply;
                _log.LogDebug("Command 0x{Command:X2} on target {Target} returned status 0x{Status:X2}", command, target, status);
                return null;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No reply to command 0x{command:X2}.");
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var buf = new byte[Bragi.ReportLength];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                _replies.Writer.TryWrite(buf[..n]);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!_cts.IsCancellationRequested) Fault(ex);
        }
    }

    private void Fault(Exception ex)
    {
        if (Faulted) return;
        Faulted = true;
        Disconnects.Warn(_log, ex, "Lost the Corsair keyboard");
        Lost?.Invoke();
    }

    /// <summary>The instance part of the HID path, stable for a given USB port.</summary>
    private static string SerialHint(string path)
    {
        var parts = path.Split('#');
        var hint = parts.Length > 2 ? parts[2] : path;
        return new string(hint.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_controls is not null) await _controls.DisposeAsync();
        if (!Faulted && _session != 0)
        {
            // Hand the lighting back to the keyboard's own effects, then free the session slot.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await TryCommandAsync(Bragi.Set(_session, Bragi.PropertyMode, Bragi.ModeHardware), _session, Bragi.SetProperty, cts.Token);
                if (_lightingOpen)
                    await TryCommandAsync(Bragi.Close(_session, Bragi.LightingHandle), _session, Bragi.CloseHandle, cts.Token);
                await TryCommandAsync(Bragi.CloseSession(_session), _session, Bragi.Session, cts.Token);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                _log.LogDebug(ex, "Releasing the keyboard failed");
            }
        }
        _cts.Cancel();
        await _stream.DisposeAsync();
        try { await _reader; } catch { /* closing */ }
        _cts.Dispose();
    }
}
