using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyRGB.Core.Sensors;

/// <summary>
/// Reads every sensor once a second on its own thread, keeps a few minutes of history, and drives
/// the fans from their settings. Everything else reads the latest values from here.
/// </summary>
public sealed class SensorHub : IDisposable
{
    /// <summary>Five minutes at one reading a second.</summary>
    public const int HistoryLength = 300;

    private readonly ISensorSource _source;
    private readonly ILogger _log;
    private readonly FanController _fans = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, float[]> _history = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SensorReading> _latest = [];
    private Dictionary<string, double?> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, FanSetting> _settings = new Dictionary<string, FanSetting>();
    private readonly Dictionary<string, FanCalibrator> _tests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FanCalibrator> _ended = new(StringComparer.OrdinalIgnoreCase);
    private int _ticks;
    private Thread? _thread;
    private volatile bool _running;

    public SensorHub(ISensorSource source, ILogger? log = null)
    {
        _source = source;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Raised on the sensor thread after each reading.</summary>
    public event Action? Updated;

    public IReadOnlyList<SensorReading> Latest
    {
        get { lock (_gate) return _latest; }
    }

    public IReadOnlyList<IFanChannel> Fans => _source.Fans;

    /// <summary>What MyRGB is asking each fan for right now, by fan id.</summary>
    public IReadOnlyDictionary<string, double> FanTargets { get; private set; } = new Dictionary<string, double>();

    /// <summary>How many readings have been taken; history holds the last <see cref="HistoryLength"/>.</summary>
    public int Ticks => _ticks;

    public double? Get(string key)
    {
        lock (_gate) return _byKey.GetValueOrDefault(key);
    }

    /// <summary>The last readings of a sensor (by id or key), oldest first; NaN where there was no value.</summary>
    public float[] History(string idOrKey)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(idOrKey, out var ring)) return [];
            int n = Math.Min(_ticks, HistoryLength), start = _ticks % HistoryLength;
            var result = new float[n];
            for (int i = 0; i < n; i++) result[i] = ring[(start - n + i + HistoryLength * 2) % HistoryLength];
            return result;
        }
    }

    /// <summary>Raised on the sensor thread when a fan test ends, whether it finished or not.</summary>
    public event Action<FanCalibrator>? CalibrationEnded;

    /// <summary>
    /// Start testing a fan (see <see cref="FanCalibrator"/>). Returns why it can't, or null once it
    /// has started. The fan's own setting is set aside until the test ends.
    /// </summary>
    public string? StartCalibration(string fanId)
    {
        var fan = _source.Fans.FirstOrDefault(f => f.Id.Equals(fanId, StringComparison.OrdinalIgnoreCase));
        if (fan is null) return "That fan isn't there any more.";
        var test = FanCalibrator.TryStart(fan, out var whyNot);
        if (test is null) return whyNot;
        lock (_gate)
        {
            if (_tests.ContainsKey(fanId)) return "This fan is already being tested.";
            _tests[fanId] = test;
            _ended.Remove(fanId);
        }
        return null;
    }

    public void CancelCalibration(string fanId)
    {
        lock (_gate) if (_tests.TryGetValue(fanId, out var test)) test.Cancel();
    }

    /// <summary>The test running on a fan, or the last one that ended (check <see cref="FanCalibrator.Finished"/>).</summary>
    public FanCalibrator? Calibration(string fanId)
    {
        lock (_gate) return _tests.GetValueOrDefault(fanId) ?? _ended.GetValueOrDefault(fanId);
    }

    public void SetFanSettings(IEnumerable<FanSetting> settings) =>
        _settings = settings.ToDictionary(s => s.FanId, StringComparer.OrdinalIgnoreCase);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "MyRGB sensors" };
        _thread.Start();
    }

    /// <summary>Read once now, on the calling thread (tests and snapshots).</summary>
    public void Poll()
    {
        IReadOnlyList<SensorReading> readings;
        try { readings = _source.Read(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading sensors failed");
            return;
        }

        // One averaged temperature, so case fans can follow the whole system instead of one chip.
        var temps = readings.Where(r => r.Kind == SensorKind.Temperature && r.Value is not null)
            .Select(r => r.Value!.Value).ToList();
        if (temps.Count > 0)
            readings = [.. readings,
                new SensorReading("myrgb/avg/temp", SensorCategory.Other, "All sensors", "Average temperature",
                    SensorKind.Temperature, temps.Average(), SensorKeys.AvgTemp)];

        lock (_gate)
        {
            int slot = _ticks % HistoryLength;
            var byKey = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in readings)
            {
                Record(r.Id, slot, r.Value);
                if (r.Key is { } key)
                {
                    byKey[key] = r.Value;
                    Record(key, slot, r.Value);
                }
            }
            _latest = readings;
            _byKey = byKey;
            _ticks++;
        }

        StepCalibrations();

        try
        {
            _fans.Apply(_source.Fans, _settings, Get);
            FanTargets = _fans.Targets;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Driving fans failed"); }

        try { Updated?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "Sensor listener failed"); }
    }

    private void StepCalibrations()
    {
        List<FanCalibrator> tests;
        lock (_gate) tests = [.. _tests.Values];
        foreach (var test in tests)
        {
            _fans.Paused.Add(test.FanId);
            try { test.Step(Get, DateTime.UtcNow); }
            catch (Exception ex) { _log.LogWarning(ex, "Testing a fan failed"); test.Cancel(); }
            if (!test.Finished) continue;

            lock (_gate)
            {
                _tests.Remove(test.FanId);
                _ended[test.FanId] = test;
            }
            _fans.Paused.Remove(test.FanId);
            _fans.Forget(test.FanId);
            // Hand it back; if MyRGB drives it, Apply below sets it again straight away.
            var fan = _source.Fans.FirstOrDefault(f => f.Id.Equals(test.FanId, StringComparison.OrdinalIgnoreCase));
            if (fan is not null) try { fan.Reset(); } catch (Exception) { /* keeps its own control */ }
            try { CalibrationEnded?.Invoke(test); }
            catch (Exception ex) { _log.LogWarning(ex, "Fan test listener failed"); }
        }
    }

    private void Record(string id, int slot, double? value)
    {
        if (!_history.TryGetValue(id, out var ring))
        {
            _history[id] = ring = new float[HistoryLength];
            Array.Fill(ring, float.NaN);
        }
        ring[slot] = value is { } v ? (float)v : float.NaN;
    }

    private void Run()
    {
        while (_running)
        {
            var started = DateTime.UtcNow;
            Poll();
            var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(3));
        try
        {
            // A fan left mid-test could be sitting stopped: give it back to the hardware.
            List<string> testing;
            lock (_gate) testing = [.. _tests.Keys];
            foreach (var fan in _source.Fans.Where(f => testing.Contains(f.Id, StringComparer.OrdinalIgnoreCase))) fan.Reset();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Handing tested fans back failed"); }
        try { _fans.ResetAll(_source.Fans); } catch (Exception ex) { _log.LogWarning(ex, "Handing fans back failed"); }
        _source.Dispose();
    }
}
