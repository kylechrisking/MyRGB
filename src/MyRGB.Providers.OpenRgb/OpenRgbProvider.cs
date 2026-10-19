using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Core.Devices;

namespace MyRGB.Providers.OpenRgb;

public sealed class OpenRgbOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;

    /// <summary>OpenRGB.exe to launch (hidden, in server mode) when no server is listening. Null to never launch.</summary>
    public string? ExecutablePath { get; set; }

    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Scheduled task that starts OpenRGB with admin rights (see tools\setup-openrgb-admin.ps1). Used instead of
    /// <see cref="ExecutablePath"/> when it exists: SMBus devices like RGB RAM need OpenRGB elevated.
    /// </summary>
    public string? LaunchTask { get; set; } = @"\MyRGB\OpenRGB";
}

/// <summary>
/// Devices exposed by an OpenRGB SDK server. This is the broad-coverage provider: anything
/// OpenRGB supports (including SMBus RAM and GPUs) shows up here. Native providers, when added,
/// get a higher <see cref="Priority"/> and take over the same hardware.
/// </summary>
public sealed class OpenRgbProvider : IDeviceProvider
{
    public const string ProviderId = "openrgb";

    private readonly OpenRgbOptions _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenRgbClient? _client;
    private Process? _startedServer;
    private DateTime _lastTaskLaunch = DateTime.MinValue;
    private CancellationTokenSource? _reconnect;
    private CancellationTokenSource? _debounce;

    private readonly Func<IReadOnlyList<ZoneSize>> _zoneSizes;

    /// <param name="zoneSizes">The user's LED counts for resizable zones, read on every discovery.</param>
    public OpenRgbProvider(OpenRgbOptions options, ILogger<OpenRgbProvider>? log = null, Func<IReadOnlyList<ZoneSize>>? zoneSizes = null)
    {
        _options = options;
        _log = log ?? NullLogger<OpenRgbProvider>.Instance;
        _zoneSizes = zoneSizes ?? (() => []);
    }

    public string Id => ProviderId;
    public string DisplayName => "OpenRGB";
    public int Priority => 0;
    public ProviderStatus Status { get; private set; } = ProviderStatus.Stopped;
    public string? StatusMessage { get; private set; }

    public event EventHandler? DevicesChanged;

    public async Task<IReadOnlyList<IRgbDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not { Connected: true })
            {
                if (_client is not null) await _client.DisposeAsync();
                _client = null;
                SetStatus(ProviderStatus.Connecting, "Connecting to OpenRGB");
                _client = await ConnectOrLaunchAsync(ct);
                _client.DeviceListUpdated += OnServerDeviceListUpdated;
                _client.Disconnected += OnDisconnected;
            }

            var client = _client;
            int count = await client.GetControllerCountAsync(ct);
            var devices = new List<IRgbDevice>(count);
            var sizes = _zoneSizes();
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    var data = await client.GetControllerAsync(i, ct);
                    if (await ApplyZoneSizesAsync(client, i, data, sizes, ct))
                        data = await client.GetControllerAsync(i, ct);
                    // Unsized fan channels report no LEDs; keep them so the user can size them.
                    if (data.LedNames.Count == 0 && !data.Zones.Any(z => z.Resizable)) continue;
                    await client.SetCustomModeAsync(i, ct);
                    devices.Add(new OpenRgbDevice(client, i, data));
                }
                catch (InvalidDataException ex)
                {
                    _log.LogWarning(ex, "Skipping OpenRGB controller {Index}", i);
                }
            }

            SetStatus(ProviderStatus.Connected, $"{devices.Count} devices (protocol v{client.ProtocolVersion})");
            return devices;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(ProviderStatus.Error, ex.Message);
            ScheduleReconnect();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resize zones whose saved size differs from what OpenRGB reports. True if anything changed.</summary>
    private async Task<bool> ApplyZoneSizesAsync(
        OpenRgbClient client, uint index, ControllerData data, IReadOnlyList<ZoneSize> sizes, CancellationToken ct)
    {
        if (sizes.Count == 0 || !data.Zones.Any(z => z.Resizable)) return false;
        var id = OpenRgbDevice.MakeId(data);
        bool changed = false;
        for (int zi = 0; zi < data.Zones.Count; zi++)
        {
            var z = data.Zones[zi];
            if (!z.Resizable) continue;
            if (sizes.FirstOrDefault(s => string.Equals(s.DeviceId, id, StringComparison.OrdinalIgnoreCase) && s.Zone == z.Name) is not { } saved)
                continue;
            int want = (int)Math.Clamp((uint)Math.Max(0, saved.LedCount), z.LedsMin, z.LedsMax);
            if (want == z.LedCount) continue;
            _log.LogInformation("Resizing {Device} zone {Zone}: {Old} -> {New} LEDs", data.Name, z.Name, z.LedCount, want);
            await client.ResizeZoneAsync(index, zi, want, ct);
            changed = true;
        }
        return changed;
    }

    private async Task<OpenRgbClient> ConnectOrLaunchAsync(CancellationToken ct)
    {
        try
        {
            return await OpenRgbClient.ConnectAsync(_options.Host, _options.Port, "MyRGB", ct);
        }
        catch (SocketException) when (CanLaunch())
        {
            LaunchServer();
        }

        // OpenRGB scans hardware before it opens the SDK port; that can take 10-20 s.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            await Task.Delay(1000, ct);
            try
            {
                return await OpenRgbClient.ConnectAsync(_options.Host, _options.Port, "MyRGB", ct);
            }
            catch (SocketException) when (DateTime.UtcNow < deadline) { }
        }
    }

    private bool CanLaunch() =>
        _options.AutoStart
        && _options.Host is "127.0.0.1" or "localhost"
        && _startedServer is not { HasExited: false }
        && DateTime.UtcNow - _lastTaskLaunch > TimeSpan.FromMinutes(1) // the task's process isn't ours to watch
        && (TaskExists() || _options.ExecutablePath is { } exe && File.Exists(exe));

    private bool TaskExists() => _options.LaunchTask is { } task && RunSchtasks("/query", "/tn", task) == 0;

    private static int RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return p.HasExited ? p.ExitCode : -1;
    }

    private void LaunchServer()
    {
        if (TaskExists())
        {
            _log.LogInformation("Starting OpenRGB server via scheduled task {Task}", _options.LaunchTask);
            SetStatus(ProviderStatus.Connecting, "Starting OpenRGB (scanning hardware)");
            _lastTaskLaunch = DateTime.UtcNow;
            if (RunSchtasks("/run", "/tn", _options.LaunchTask!) == 0) return;
            _log.LogWarning("Scheduled task {Task} failed to start; launching OpenRGB directly", _options.LaunchTask);
        }
        if (_options.ExecutablePath is not { } exe || !File.Exists(exe)) return;
        _log.LogInformation("Starting OpenRGB server: {Exe}", exe);
        SetStatus(ProviderStatus.Connecting, "Starting OpenRGB (scanning hardware)");
        _startedServer = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "--server", "--server-port", _options.Port.ToString() },
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private void OnServerDeviceListUpdated()
    {
        // OpenRGB sends a burst of these while rescanning; wait for it to settle.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Delay(750, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) DevicesChanged?.Invoke(this, EventArgs.Empty);
        }, TaskScheduler.Default);
    }

    private void OnDisconnected(Exception? error)
    {
        _log.LogWarning(error, "Lost connection to OpenRGB");
        SetStatus(ProviderStatus.Error, "Disconnected from OpenRGB");
        DevicesChanged?.Invoke(this, EventArgs.Empty); // drops the dead devices
    }

    private void ScheduleReconnect()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _reconnect, cts)?.Cancel();
        _ = Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) DevicesChanged?.Invoke(this, EventArgs.Empty);
        }, TaskScheduler.Default);
    }

    private void SetStatus(ProviderStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
    }

    public async ValueTask DisposeAsync()
    {
        _reconnect?.Cancel();
        _debounce?.Cancel();
        if (_client is not null) await _client.DisposeAsync();
        if (_startedServer is { HasExited: false } p)
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
        }
        SetStatus(ProviderStatus.Stopped, null);
    }
}
