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

        try
        {
            _fans.Apply(_source.Fans, _settings, Get);
            FanTargets = _fans.Targets;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Driving fans failed"); }

        try { Updated?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "Sensor listener failed"); }
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
        try { _fans.ResetAll(_source.Fans); } catch (Exception ex) { _log.LogWarning(ex, "Handing fans back failed"); }
        _source.Dispose();
    }
}
