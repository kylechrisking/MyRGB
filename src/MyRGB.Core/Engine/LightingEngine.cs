using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Core.Devices;
using MyRGB.Core.Effects;
using MyRGB.Core.Layout;
using MyRGB.Core.Presets;

namespace MyRGB.Core.Engine;

/// <summary>One rendered frame, for live previews. Colors[i] belongs to Devices[i].</summary>
public sealed record FrameSnapshot(IReadOnlyList<IRgbDevice> Devices, IReadOnlyList<Rgb[]> Colors);

/// <summary>
/// The render loop. Each frame it runs the active preset's effects over the canvas positions of
/// every LED, applies brightness and power, and pushes the result to each device.
/// </summary>
public sealed class LightingEngine : IDisposable
{
    /// <summary>Resend unchanged frames this often; some devices drop back to hardware mode if left idle.</summary>
    private const double KeepAliveSeconds = 1.0;
    private const double PreviewInterval = 1 / 30.0;

    private readonly DeviceManager _devices;
    private readonly EffectRegistry _effects;
    private readonly CanvasLayout _layout;
    private readonly ILogger _log;
    private readonly object _rebuildGate = new();

    private volatile RenderPlan _plan = RenderPlan.Empty;
    private volatile Preset? _preset;
    private volatile IReadOnlySet<string> _excluded = new HashSet<string>();
    private volatile bool _enabled = true;
    private volatile int _fps = 30;
    private double _brightness = 1;
    private Thread? _thread;
    private volatile bool _running;

    public LightingEngine(DeviceManager devices, EffectRegistry effects, CanvasLayout layout, ILogger<LightingEngine>? log = null)
    {
        _devices = devices;
        _effects = effects;
        _layout = layout;
        _log = log ?? NullLogger<LightingEngine>.Instance;
        _devices.DevicesChanged += (_, _) => Rebuild(keepEffects: true);
        _layout.Changed += (_, _) => Rebuild(keepEffects: true);
    }

    /// <summary>Raised about 30 times a second on the engine thread while anyone is listening.</summary>
    public event Action<FrameSnapshot>? FrameRendered;

    public Preset? ActivePreset => _preset;

    public int Fps
    {
        get => _fps;
        set => _fps = Math.Clamp(value, 5, 120);
    }

    /// <summary>Master brightness, 0..1.</summary>
    public double Brightness
    {
        get => Volatile.Read(ref _brightness);
        set => Volatile.Write(ref _brightness, Math.Clamp(value, 0, 1));
    }

    /// <summary>Master power. Off pushes black to every device but keeps the preset running.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void Activate(Preset preset)
    {
        _preset = preset;
        Rebuild(keepEffects: false);
    }

    /// <summary>Devices MyRGB leaves alone entirely (no frames sent), e.g. so vendor software can keep them.</summary>
    public IReadOnlySet<string> ExcludedDevices
    {
        get => _excluded;
        set
        {
            _excluded = new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
            Rebuild(keepEffects: true);
        }
    }

    /// <summary>
    /// Change settings on the running effect without restarting it, so animations (ripples, stars)
    /// keep their state while the user drags a slider. <paramref name="deviceId"/> null targets the
    /// preset's main effect; otherwise that device's override effect.
    /// </summary>
    public void UpdateLiveSettings(string? deviceId, EffectSettings settings)
    {
        var group = _plan.Groups.FirstOrDefault(g => g.Key == (deviceId ?? ""));
        if (group is null) return;
        var def = _effects.Find(group.EffectId);
        if (def is not null) group.Effect.Settings = def.Bind(settings);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        Rebuild(keepEffects: true);
        _thread = new Thread(Run) { IsBackground = true, Name = "MyRGB render", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Rebuild(bool keepEffects)
    {
        lock (_rebuildGate)
        {
            try
            {
                var excluded = _excluded;
                var devices = _devices.Devices.Where(d => !excluded.Contains(d.Id) && d.Leds.Count > 0).ToList();
                _plan = RenderPlan.Build(devices, _preset, _effects, _layout, keepEffects ? _plan : null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Building the render plan failed");
            }
        }
    }

    private void Run()
    {
        using var timer = HighResolutionTimer.Begin();
        var clock = Stopwatch.StartNew();
        double last = 0, nextPreview = 0;
        int failures = 0;

        while (_running)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - last, 0, 0.25);
            last = now;
            var plan = _plan;

            try
            {
                RenderFrame(plan, now, dt);
                failures = 0;
            }
            catch (Exception ex)
            {
                if (failures++ % 300 == 0) _log.LogError(ex, "Frame failed");
            }

            if (FrameRendered is { } preview && now >= nextPreview)
            {
                nextPreview = now + PreviewInterval;
                try { preview(new FrameSnapshot(plan.Devices, plan.Colors.Select(c => (Rgb[])c.Clone()).ToArray())); }
                catch (Exception ex) { _log.LogWarning(ex, "Preview listener failed"); }
            }

            double sleep = 1.0 / _fps - (clock.Elapsed.TotalSeconds - now);
            if (sleep > 0) Thread.Sleep(TimeSpan.FromSeconds(sleep));
        }
    }

    internal void RenderFrame(RenderPlan plan, double time, double dt) =>
        plan.Render(new FrameContext(time, dt), _enabled, Brightness, _log);

    /// <summary>
    /// Everything the loop needs for one preset on one set of devices, precomputed so a frame does
    /// no allocation. Rebuilt (and swapped atomically) when the preset, devices, or layout change.
    /// </summary>
    internal sealed class RenderPlan
    {
        public static readonly RenderPlan Empty = new([], [], [], [], []);

        private RenderPlan(IRgbDevice[] devices, Rgb[][] colors, bool[] off, DeviceOutput[] outputs, Group[] groups)
        {
            Devices = devices;
            Colors = colors;
            Off = off;
            Outputs = outputs;
            Groups = groups;
        }

        public IRgbDevice[] Devices { get; }
        public Rgb[][] Colors { get; }
        public bool[] Off { get; }
        public DeviceOutput[] Outputs { get; }
        public Group[] Groups { get; }

        public static RenderPlan Build(
            IReadOnlyList<IRgbDevice> devices, Preset? preset, EffectRegistry effects, CanvasLayout layout, RenderPlan? previous)
        {
            if (preset is null || devices.Count == 0) return Empty;

            var overrides = preset.Overrides.ToDictionary(o => o.DeviceId, StringComparer.OrdinalIgnoreCase);
            var colors = new Rgb[devices.Count][];
            var off = new bool[devices.Count];
            var outputs = new DeviceOutput[devices.Count];
            var members = new Dictionary<string, (string EffectId, Dictionary<string, string> Settings, List<(int Device, LedPoint[] Points)> Devices)>();

            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                colors[i] = new Rgb[d.Leds.Count];
                int prev = previous is null ? -1 : Array.IndexOf(previous.Devices, d);
                outputs[i] = prev >= 0 ? previous!.Outputs[prev] : new DeviceOutput(d.Leds.Count);

                string key = "";
                string effectId = preset.EffectId;
                var settings = preset.Settings;
                if (overrides.TryGetValue(d.Id, out var ov))
                {
                    if (ov.Off) { off[i] = true; continue; }
                    if (ov.EffectId is { } oid && effects.Find(oid) is not null)
                    {
                        key = d.Id;
                        effectId = oid;
                        settings = ov.Settings;
                    }
                }

                var p = layout.GetOrCreate(d);
                var points = d.Leds
                    .Select(l => new LedPoint((float)(p.X + l.X * p.Width), (float)(p.Y + l.Y * p.Height)))
                    .ToArray();

                if (!members.TryGetValue(key, out var m))
                    members[key] = m = (effectId, settings, []);
                m.Devices.Add((i, points));
            }

            var groups = new List<Group>();
            foreach (var (key, m) in members)
            {
                var def = effects.Find(m.EffectId);
                if (def is null) continue;
                var reuse = previous?.Groups.FirstOrDefault(g => g.Key == key && g.EffectId == m.EffectId);
                var effect = reuse?.Effect ?? def.Create(new EffectSettings(m.Settings));
                groups.Add(new Group(key, m.EffectId, effect, m.Devices));
            }

            return new RenderPlan([.. devices], colors, off, outputs, [.. groups]);
        }

        public void Render(in FrameContext frame, bool enabled, double brightness, ILogger log)
        {
            if (enabled)
            {
                foreach (var g in Groups)
                {
                    g.Effect.Render(frame, g.Points, g.Buffer);
                    for (int k = 0; k < g.Targets.Length; k++)
                        Colors[g.Targets[k].Device][g.Targets[k].Led] = g.Buffer[k];
                }
            }

            for (int i = 0; i < Devices.Length; i++)
            {
                var c = Colors[i];
                if (!enabled || Off[i] || brightness <= 0) Array.Fill(c, Rgb.Black);
                else if (brightness < 0.999)
                    for (int k = 0; k < c.Length; k++) c[k] = c[k].Scale(brightness);

                Outputs[i].Push(Devices[i], c, frame.Time, log);
            }
        }
    }

    internal sealed class Group
    {
        public Group(string key, string effectId, Effect effect, List<(int Device, LedPoint[] Points)> devices)
        {
            Key = key;
            EffectId = effectId;
            Effect = effect;
            Points = devices.SelectMany(d => d.Points).ToArray();
            Targets = devices.SelectMany(d => Enumerable.Range(0, d.Points.Length).Select(led => (d.Device, led))).ToArray();
            Buffer = new Rgb[Points.Length];
        }

        /// <summary>"" for the preset's main effect, or the device id of an override.</summary>
        public string Key { get; }
        public string EffectId { get; }
        public Effect Effect { get; }
        public LedPoint[] Points { get; }
        public (int Device, int Led)[] Targets { get; }
        public Rgb[] Buffer { get; }
    }

    /// <summary>Per-device send state: skip unchanged frames and never queue behind a slow device.</summary>
    internal sealed class DeviceOutput(int ledCount)
    {
        private readonly Rgb[] _sent = new Rgb[ledCount];
        private double _sentAt = double.NegativeInfinity;
        private Task? _inflight;
        private bool _loggedFailure;

        public void Push(IRgbDevice device, Rgb[] colors, double now, ILogger log)
        {
            if (_inflight is { IsCompleted: false }) return; // device is still busy: drop this frame
            if (_inflight is { IsFaulted: true } && !_loggedFailure)
            {
                _loggedFailure = true;
                log.LogWarning(_inflight.Exception, "Writing to {Device} failed", device.Name);
            }

            if (colors.AsSpan().SequenceEqual(_sent) && now - _sentAt < KeepAliveSeconds) return;
            colors.CopyTo(_sent, 0);
            _sentAt = now;

            try
            {
                var pending = device.ApplyAsync(_sent);
                _inflight = pending.IsCompletedSuccessfully ? null : pending.AsTask();
            }
            catch (Exception ex)
            {
                if (!_loggedFailure) log.LogWarning(ex, "Writing to {Device} failed", device.Name);
                _loggedFailure = true;
            }
        }
    }
}
