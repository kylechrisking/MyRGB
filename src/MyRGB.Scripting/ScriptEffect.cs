using System.Diagnostics;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using MyRGB.Core;
using MyRGB.Core.Audio;
using MyRGB.Core.Effects;

namespace MyRGB.Scripting;

/// <summary>
/// An effect someone wrote in JavaScript. Each instance has its own script engine, so the live lights,
/// the thumbnails and the wallpaper never share one (an engine is not safe across threads).
/// </summary>
/// <remarks>
/// When the script breaks or runs too slowly it is stopped for good and the lights show a dim, steady
/// glow, so one bad effect can't freeze the app or flicker the desk. <see cref="Problem"/> says why.
/// </remarks>
public sealed class ScriptEffect : Effect
{
    /// <summary>The glow shown when a script has stopped, so it reads as "not working" rather than "off".</summary>
    public static readonly Rgb Stopped = new(40, 8, 8);

    /// <summary>A frame that takes longer than this stops the script at once.</summary>
    internal static readonly TimeSpan HardLimit = TimeSpan.FromMilliseconds(200);

    /// <summary>Frames slower than this count as strikes; <see cref="StrikesAllowed"/> in a row stops it.</summary>
    internal static readonly TimeSpan SoftLimit = TimeSpan.FromMilliseconds(40);

    internal const int StrikesAllowed = 30;

    /// <summary>
    /// Most a script may allocate in one frame. The examples use under 1 MB for 300 lights; the per-call
    /// limit in <see cref="ScriptSandbox"/> doesn't see a script that allocates a little in every call.
    /// </summary>
    internal static long FrameAllocationLimit = 48L * 1024 * 1024;

    /// <summary>
    /// If MyRGB's memory grows past this, running script effects stop. Retained memory can't be
    /// measured per script, and scripts are the only code in the app that strangers wrote, so a
    /// slow leak that slips under the per-frame limit is caught here. Settable for tests.
    /// </summary>
    internal static long HeapLimit = 1024L * 1024 * 1024;

    private long _nextHeapCheck;

    private readonly ScriptSource _source;
    private Engine? _engine;
    private JsValue _effect = JsValue.Undefined;
    private JsValue _frame = JsValue.Undefined, _light = JsValue.Undefined;
    private ObjectInstance? _settingsObject;
    private EffectSettings? _settingsShown;
    private JsValue[] _points = [];
    private LedPoint _firstPoint, _lastPoint;
    private MusicObject? _music;
    private int _strikes;
    private readonly JsValue[] _lightArgs = new JsValue[2];
    private readonly JsValue[] _frameArgs = new JsValue[3];

    internal ScriptEffect(ScriptSource source) => _source = source;

    /// <summary>Why the script stopped, in plain words, or null while it is running fine.</summary>
    public string? Problem { get; private set; }

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        if (Problem is not null) { output.Fill(Stopped); return; }

        var clock = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            var engine = _engine ??= Start();
            SyncSettings(engine);
            SyncPoints(engine, leds);
            _music?.Refresh();

            var time = JsNumber.Create(frame.Time);
            if (!_frame.IsUndefined())
            {
                _frameArgs[0] = time;
                _frameArgs[1] = JsNumber.Create(frame.DeltaTime);
                _frameArgs[2] = JsNumber.Create(leds.Length);
                engine.Call(_frame, _effect, _frameArgs);
            }

            _lightArgs[1] = time;
            for (int i = 0; i < leds.Length; i++)
            {
                _lightArgs[0] = _points[i];
                output[i] = ScriptColor.From(engine.Call(_light, _effect, _lightArgs));
                // Checked as it goes, so a slow script is caught inside the frame, not after it.
                if ((i & 31) == 31)
                {
                    if (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore > FrameAllocationLimit) throw new MemoryLimitExceededException("frame");
                    if (clock.Elapsed > HardLimit) throw new TooSlowException();
                }
            }
        }
        catch (Exception e)
        {
            Stop(ScriptErrors.Describe(e));
            output.Fill(Stopped);
            return;
        }

        if (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore > FrameAllocationLimit) { Stop(ScriptErrors.TooMuchMemory); return; }
        if (HeapTooBig()) { Stop(ScriptErrors.AppOutOfMemory); return; }

        if (clock.Elapsed > HardLimit) Stop(ScriptErrors.TooSlow);
        else if (clock.Elapsed > SoftLimit && ++_strikes >= StrikesAllowed) Stop(ScriptErrors.TooSlow);
        else if (clock.Elapsed <= SoftLimit) _strikes = 0;
    }

    /// <summary>Looks at the size of the app's memory after the last garbage collection, at most once a second.</summary>
    private bool HeapTooBig()
    {
        long now = Environment.TickCount64;
        if (now < _nextHeapCheck) return false;
        _nextHeapCheck = now + 1000;
        return GC.GetGCMemoryInfo().HeapSizeBytes > HeapLimit;
    }

    private void Stop(string why)
    {
        Problem = why;
        _engine = null; // let the script's memory go
        _points = [];
        _source.Report(why);
    }

    private Engine Start()
    {
        var engine = ScriptSandbox.CreateEngine();
        _effect = ScriptSandbox.Run(engine, _source.Prepared);
        _frame = ScriptSandbox.Function(_effect, "frame");
        _light = ScriptSandbox.Function(_effect, "light");
        if (_light.IsUndefined()) throw new ScriptProblemException("It has no light function.");
        if (_source.UsesMusic)
        {
            _music = new MusicObject(engine);
            engine.SetValue("music", _music.Object);
        }
        return engine;
    }

    /// <summary>Hand the script a fresh `settings` object whenever the user changes something.</summary>
    private void SyncSettings(Engine engine)
    {
        var s = Settings;
        if (ReferenceEquals(s, _settingsShown) && _settingsObject is not null) return;
        _settingsShown = s;
        var obj = new JsObject(engine);
        foreach (var spec in _source.Parameters)
        {
            JsValue value = spec switch
            {
                ColorParameter => JsNumber.Create(ScriptColor.Pack(s.GetColor(spec.Key))),
                NumberParameter => JsNumber.Create(s.GetNumber(spec.Key)),
                BoolParameter => s.GetBool(spec.Key) ? JsBoolean.True : JsBoolean.False,
                _ => new JsString(s.GetString(spec.Key)),
            };
            obj.Set(spec.Key, value);
        }
        _settingsObject = obj;
        engine.SetValue("settings", obj);
    }

    /// <summary>One small object per light, made once and reused every frame.</summary>
    private void SyncPoints(Engine engine, ReadOnlySpan<LedPoint> leds)
    {
        if (_points.Length == leds.Length && (leds.Length == 0 || (leds[0] == _firstPoint && leds[^1] == _lastPoint)))
            return;
        var points = new JsValue[leds.Length];
        for (int i = 0; i < leds.Length; i++)
        {
            var p = new JsObject(engine);
            p.Set("x", JsNumber.Create(Math.Round(leds[i].X, 5)));
            p.Set("y", JsNumber.Create(Math.Round(leds[i].Y, 5)));
            p.Set("localX", JsNumber.Create(Math.Round(leds[i].LocalX, 5)));
            p.Set("localY", JsNumber.Create(Math.Round(leds[i].LocalY, 5)));
            p.Set("index", JsNumber.Create(i));
            points[i] = p;
        }
        _points = points;
        if (leds.Length > 0) (_firstPoint, _lastPoint) = (leds[0], leds[^1]);
    }

    /// <summary>
    /// The `music` object a script reads. Only made for scripts that mention music, because reading it
    /// asks the app to listen to what the PC is playing.
    /// </summary>
    private sealed class MusicObject
    {
        private readonly Engine _engine;
        private readonly JsArray _bands;

        public MusicObject(Engine engine)
        {
            _engine = engine;
            Object = new JsObject(engine);
            _bands = new JsArray(engine, (uint)AudioFrame.BandCount);
            for (int i = 0; i < AudioFrame.BandCount; i++) _bands.Push(JsNumber.Create(0));
            Object.Set("bands", _bands);
        }

        public ObjectInstance Object { get; }

        public void Refresh()
        {
            var a = AudioFeed.Get();
            Object.Set("level", JsNumber.Create(Math.Round(a.Level, 4)));
            Object.Set("beats", JsNumber.Create(a.Beats));
            Object.Set("bass", JsNumber.Create(Math.Round(Average(a.Bands, 0, 4), 4)));
            Object.Set("mid", JsNumber.Create(Math.Round(Average(a.Bands, 4, 16), 4)));
            Object.Set("treble", JsNumber.Create(Math.Round(Average(a.Bands, 16, 32), 4)));
            for (int i = 0; i < AudioFrame.BandCount && i < a.Bands.Length; i++)
                _bands.Set((uint)i, JsNumber.Create(Math.Round(a.Bands[i], 4)));
        }

        private static double Average(float[] v, int from, int to)
        {
            double sum = 0;
            to = Math.Min(to, v.Length);
            for (int i = from; i < to; i++) sum += v[i];
            return to > from ? sum / (to - from) : 0;
        }
    }
}

internal sealed class TooSlowException : Exception;

internal sealed class ScriptProblemException(string message) : Exception(message);
