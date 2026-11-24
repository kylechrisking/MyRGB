using Microsoft.Extensions.Logging;
using MyRGB.Core;

namespace MyRGB.App.Services;

/// <summary>
/// Keeps the monitors' brightness in step with the Monitors options: follow the lights, a level per
/// preset, a night level, and the slider. It only sends a level when the rules' answer changes, so a
/// change made with a monitor's own buttons stays until something here has a reason to change it.
/// </summary>
public sealed class MonitorSync : IMonitorBrightness, IDisposable
{
    private readonly AppHost _host;
    private readonly IMonitorControl _control;
    private readonly Func<DateTime> _now;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer? _timer;
    private int? _lastTarget, _want;
    private bool _busy, _read;

    public MonitorSync(AppHost host, IMonitorControl control, Func<DateTime>? now, ILogger log)
    {
        _host = host;
        _control = control;
        _now = now ?? (() => DateTime.Now);
        _log = log;
        Level = host.Settings.MonitorLevel ?? 100;
    }

    /// <summary>Level, monitors or status changed. Any thread.</summary>
    public event Action? Changed;

    public bool Installed => _control.Installed;
    public bool Available => Installed && _host.Settings.MonitorsOn;
    public int Level { get; private set; } = 100;
    public IReadOnlyList<string> Monitors { get; private set; } = [];

    /// <summary>The last problem talking to the monitors, or null.</summary>
    public string? Problem { get; private set; }

    public void Start()
    {
        _host.Lights.StateChanged += (_, _) => Update();
        _timer = new System.Threading.Timer(_ => Update(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        if (Available) _ = RefreshAsync();
    }

    /// <summary>Read the monitors again, e.g. right after the option was switched on.</summary>
    public async Task RefreshAsync()
    {
        if (!Installed) return;
        try
        {
            var list = await _control.ListAsync(_cts.Token);
            int level = list.Count > 0 ? (int)Math.Round(list.Average(m => MonitorLevels.Unmap(m.Brightness, RangeFor(m.Name)))) : Level;
            lock (_gate)
            {
                Monitors = list.Select(m => m.Name).ToList();
                if (!_read)
                {
                    // Start from where the monitors are, so turning this on changes nothing by itself.
                    _read = true;
                    Level = level;
                    _lastTarget ??= level;
                    _host.Settings.MonitorLevel ??= level;
                }
                Problem = list.Count == 0 ? "No monitor answered. Check that DDC/CI is on in each monitor's own menu." : null;
            }
            _log.LogInformation("Monitors: {Count} answering over DDC/CI, at {Level}%", list.Count, level);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            Problem = $"Couldn't reach the monitors: {ex.Message}";
            _log.LogWarning("Couldn't reach the monitors: {Reason}", ex.Message);
        }
        Changed?.Invoke();
        Update();
    }

    /// <summary>The on/off switch or a rule changed: tell the UI and apply it.</summary>
    public void SettingsChanged()
    {
        Changed?.Invoke();
        Update();
    }

    /// <summary>A monitor's own darkest..brightest, if one was set in Settings.</summary>
    public MonitorRange? RangeFor(string name) =>
        _host.Settings.MonitorRanges.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A monitor's range changed: send the current level again so the change shows at once.</summary>
    public void RangesChanged()
    {
        if (!Available || !_read) return;
        Send(Level);
    }

    /// <summary>The level the options call for right now.</summary>
    public int Target()
    {
        var s = _host.Settings;
        var rules = new MonitorRules(s.MonitorsFollowLights, s.MonitorsOffLevel, s.MonitorsNightOn, s.MonitorsNightLevel,
            s.MonitorPresetLevelsOn ? s.MonitorPresetLevels : null);
        bool night = s.MonitorsNightOn && LightsOffWatcher.NightFor(s).Contains(TimeOnly.FromDateTime(_now()));
        var state = _host.Lights.State;
        return MonitorLevels.Target(rules, s.MonitorLevel ?? Level, state.Enabled, night, state.ActivePresetId);
    }

    /// <summary>Send the options' level if it changed since last time. Cheap; called on every lights change and every few seconds.</summary>
    public void Update()
    {
        if (!Available || !_read) return;
        int target;
        lock (_gate)
        {
            try { target = Target(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Working out the monitor level failed");
                return;
            }
            if (target == _lastTarget) return;
            _lastTarget = target;
        }
        Send(target);
    }

    /// <summary>The slider: set the monitors now and remember it as the everyday level.</summary>
    public void SetLevel(int level)
    {
        level = MonitorLevels.Clamp(level);
        _host.Settings.MonitorLevel = level;
        _host.ScheduleSave();
        if (!Available) return;
        lock (_gate)
        {
            _read = true;
            _lastTarget = Target(); // hold this until the rules next say something different
        }
        Send(level);
    }

    private void Send(int level)
    {
        lock (_gate)
        {
            Level = level;
            _want = level;
            if (_busy) { Changed?.Invoke(); return; }
            _busy = true;
        }
        Changed?.Invoke();
        _ = Task.Run(PumpAsync);
    }

    /// <summary>One monitor command at a time; while one runs, only the newest level waits (a slider drag sends a few, not dozens).</summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            int level;
            lock (_gate)
            {
                if (_want is null || _cts.IsCancellationRequested) { _busy = false; return; }
                level = _want.Value;
                _want = null;
            }
            try
            {
                await _control.SetAllAsync(name => MonitorLevels.Map(level, RangeFor(name)), _cts.Token);
                if (Problem is not null) { Problem = null; Changed?.Invoke(); }
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                Problem = $"Couldn't set the monitors: {ex.Message}";
                _log.LogWarning("Couldn't set the monitors to {Level}%: {Reason}", level, ex.Message);
                Changed?.Invoke();
            }
            catch (Exception) { /* shutting down */ }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Cancel();
    }
}
