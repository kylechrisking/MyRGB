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
internal sealed class CorsairKeyboard : IRgbDevice, IAsyncDisposable
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
    private VolumeWheel? _wheel;

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

    public static async Task<CorsairKeyboard> OpenAsync(HidInterface iface, ILogger log, CancellationToken ct)
    {
        var stream = HidDevices.Open(iface.Path);
        var kb = new CorsairKeyboard(stream, SerialHint(iface.Path), log);
        try
        {
            var reply = await kb.CommandAsync(Bragi.OpenSession(SessionToken), Bragi.SessionTarget, Bragi.Session, ct);
            kb._session = Bragi.SessionId(reply);
            log.LogDebug("Corsair keyboard session {Session}", kb._session);

            await kb.CommandAsync(Bragi.Set(kb._session, Bragi.PropertyMode, Bragi.ModeSoftware), kb._session, Bragi.SetProperty, ct);
            await kb.CommandAsync(Bragi.Open(kb._session, Bragi.LightingHandle, Bragi.LightingResource), kb._session, Bragi.OpenHandle, ct);
            kb._lightingOpen = true;
            // Software mode silences the volume wheel; pass its turns and presses on to Windows.
            kb._wheel = VolumeWheel.TryOpen(log);
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
        if (Faulted || !_lightingOpen) return;
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
        _log.LogWarning(ex, "Lost the Corsair keyboard");
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
        if (_wheel is not null) await _wheel.DisposeAsync();
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
