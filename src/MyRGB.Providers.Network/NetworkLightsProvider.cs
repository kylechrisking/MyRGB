using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Core.Devices;

namespace MyRGB.Providers.Network;

/// <summary>
/// Lights on the home network that the user added on the Devices page: WLED and Govee. With none added it does
/// nothing at all, not even a network scan.
/// </summary>
public sealed class NetworkLightsProvider : IDeviceProvider, IHardwareRescan
{
    public const string ProviderId = "network";

    /// <summary>How long a light that didn't answer is left alone before it's asked again.</summary>
    private static readonly TimeSpan RetryOffline = TimeSpan.FromMinutes(1);

    private readonly Func<IReadOnlyList<NetworkLight>> _lights;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IRgbDevice> _online = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _offlineSince = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _retry;
    private bool _forceCheck;

    /// <param name="lights">The lights the user added, read on every discovery.</param>
    public NetworkLightsProvider(Func<IReadOnlyList<NetworkLight>> lights, ILogger<NetworkLightsProvider>? log = null)
    {
        _lights = lights;
        _log = log ?? NullLogger<NetworkLightsProvider>.Instance;
    }

    public string Id => ProviderId;
    public string DisplayName => "Network lights";
    public int Priority => 10;
    public ProviderStatus Status { get; private set; } = ProviderStatus.Stopped;
    public string? StatusMessage { get; private set; }

    public event EventHandler? DevicesChanged;

    /// <summary>True when the light added at <paramref name="address"/> answered the last time it was asked.</summary>
    public bool IsOnline(string address)
    {
        lock (_online) return _online.ContainsKey(address.Trim());
    }

    /// <summary>The light list changed in Settings: look again now.</summary>
    public void LightsChanged()
    {
        _forceCheck = true;
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RescanHardwareAsync(CancellationToken ct = default)
    {
        LightsChanged();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<IRgbDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var lights = _lights().Where(l => l.Address.Trim().Length > 0)
                .DistinctBy(l => l.Address.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
            var wanted = lights.Select(l => l.Address.Trim()).ToList();

            // Forget lights the user removed.
            lock (_online)
            {
                foreach (var gone in _online.Keys.Except(wanted, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    (_online[gone] as IDisposable)?.Dispose();
                    _online.Remove(gone);
                }
            }
            foreach (var gone in _offlineSince.Keys.Except(wanted, StringComparer.OrdinalIgnoreCase).ToList())
                _offlineSince.Remove(gone);

            if (wanted.Count == 0)
            {
                SetStatus(ProviderStatus.Stopped, null);
                return [];
            }

            // Ask the ones not yet online, unless one went unanswered a moment ago (the retry timer has those).
            bool force = _forceCheck;
            _forceCheck = false;
            var now = DateTime.UtcNow;
            List<NetworkLight> toCheck;
            lock (_online)
                toCheck = lights.Where(l => !_online.ContainsKey(l.Address.Trim())
                    && (force || !_offlineSince.TryGetValue(l.Address.Trim(), out var since) || now - since >= RetryOffline)).ToList();
            await Task.WhenAll(toCheck.Select(l => ConnectAsync(l, ct)));

            List<IRgbDevice> devices;
            lock (_online) devices = wanted.Where(_online.ContainsKey).Select(a => (IRgbDevice)_online[a]).ToList();
            int offline = wanted.Count - devices.Count;
            SetStatus(devices.Count > 0 || offline == 0 ? ProviderStatus.Connected : ProviderStatus.Error,
                (devices.Count == 1 ? "1 network light" : $"{devices.Count} network lights") + (offline == 0 ? "" : $", {offline} not answering"));
            if (offline > 0) ScheduleRetry();
            return devices;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConnectAsync(NetworkLight light, CancellationToken ct)
    {
        string address = light.Address.Trim();
        IRgbDevice? device = null;
        try
        {
            device = light.Kind switch
            {
                NetworkLightKind.Wled => await ConnectWledAsync(address, ct),
                NetworkLightKind.Govee => await ConnectGoveeAsync(address, light.Name, ct),
                _ => null,
            };
        }
        catch (SocketException ex)
        {
            _log.LogWarning("Couldn't reach {Kind} light at {Address}: {Reason}", light.Kind, address, ex.Message);
        }
        lock (_online)
        {
            if (device is null)
            {
                if (!_offlineSince.ContainsKey(address))
                    _log.LogInformation("{Kind} light at {Address} isn't answering", light.Kind, address);
                _offlineSince[address] = DateTime.UtcNow;
                return;
            }
            _offlineSince.Remove(address);
            _online[address] = device;
        }
    }

    private async Task<IRgbDevice?> ConnectWledAsync(string address, CancellationToken ct)
    {
        var info = await LightFinder.ReadWledInfoAsync(address, ct);
        var ip = info is null ? null : await ResolveAsync(address, ct);
        if (info is null || ip is null) return null;
        _log.LogInformation("WLED light {Name} at {Address}: {Leds} LEDs{Matrix}, WLED {Version}",
            info.Name, address, info.LedCount, info.MatrixWidth > 0 ? $" ({info.MatrixWidth}x{info.MatrixHeight})" : "", info.Version);
        return new WledDevice(ip, info);
    }

    private async Task<IRgbDevice?> ConnectGoveeAsync(string address, string? name, CancellationToken ct)
    {
        if (await ResolveAsync(address, ct) is not { AddressFamily: AddressFamily.InterNetwork } ip) return null;
        if (!await GoveeLan.AnswersAsync(ip, ct)) return null;
        // The scan answer carries the light's own id, so its place on the canvas survives a new IP address.
        var scan = await GoveeLan.ScanAsync(TimeSpan.FromSeconds(1.5), ct);
        var info = scan.FirstOrDefault(s => s.Address.Equals(ip));
        _log.LogInformation("Govee light {Model} at {Address}", info?.Model ?? "(model unknown)", address);
        return new GoveeDevice(ip, info?.DeviceId ?? address, info?.Model ?? "", name);
    }

    /// <summary>True if the light answers now; used while waiting for a missing one to come back.</summary>
    private static async Task<bool> AnswersAsync(NetworkLight light, CancellationToken ct)
    {
        try
        {
            return light.Kind switch
            {
                NetworkLightKind.Wled => await LightFinder.ReadWledInfoAsync(light.Address.Trim(), ct) is not null,
                NetworkLightKind.Govee => await ResolveAsync(light.Address.Trim(), ct) is { } ip && await GoveeLan.AnswersAsync(ip, ct),
                _ => false,
            };
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<IPAddress?> ResolveAsync(string address, CancellationToken ct)
    {
        if (IPAddress.TryParse(address, out var ip)) return ip;
        try
        {
            var all = await Dns.GetHostAddressesAsync(address, ct);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>While a light is missing, look again every minute, and only bother the rest of the app if it's back.</summary>
    private void ScheduleRetry()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _retry, cts)?.Cancel();
        _ = Task.Delay(RetryOffline, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try
            {
                List<NetworkLight> missing;
                await _gate.WaitAsync(cts.Token);
                try { missing = _lights().Where(l => _offlineSince.ContainsKey(l.Address.Trim())).ToList(); }
                finally { _gate.Release(); }
                if (missing.Count == 0) return;

                var answers = await Task.WhenAll(missing.Select(l => AnswersAsync(l, cts.Token)));
                if (answers.Any(a => a)) LightsChanged();
                else ScheduleRetry();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning(ex, "Looking for missing network lights failed"); }
        }, TaskScheduler.Default);
    }

    private void SetStatus(ProviderStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
    }

    public ValueTask DisposeAsync()
    {
        _retry?.Cancel();
        foreach (var d in _online.Values) (d as IDisposable)?.Dispose();
        _online.Clear();
        SetStatus(ProviderStatus.Stopped, null);
        return ValueTask.CompletedTask;
    }
}
