using Acornima.Ast;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using MyRGB.Core;

namespace MyRGB.Scripting;

/// <summary>
/// Where effect scripts run. A script gets plain JavaScript and the few helpers below, and nothing
/// else: no files, no network, no way to reach the app or Windows. Effects come from strangers, so this
/// is the line that keeps them to painting colors.
/// </summary>
internal static class ScriptSandbox
{
    /// <summary>Helpers every effect can use. Set on the global object so a script may shadow them.</summary>
    private const string PreludeSource = """
        "use strict";
        globalThis.clamp = function (v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; };
        globalThis.lerp = function (a, b, t) { return a + (b - a) * t; };
        globalThis.fract = function (v) { return v - Math.floor(v); };
        globalThis.smooth = function (edge0, edge1, v) {
            const t = clamp((v - edge0) / (edge1 - edge0), 0, 1);
            return t * t * (3 - 2 * t);
        };
        globalThis.rgb = function (r, g, b) {
            return (clamp(Math.round(r), 0, 255) << 16) | (clamp(Math.round(g), 0, 255) << 8) | clamp(Math.round(b), 0, 255);
        };
        globalThis.red = function (c) { return (c >> 16) & 255; };
        globalThis.green = function (c) { return (c >> 8) & 255; };
        globalThis.blue = function (c) { return c & 255; };
        globalThis.hsv = function (h, s, v) {
            if (s === undefined) s = 1;
            if (v === undefined) v = 1;
            h = ((h % 360) + 360) % 360; s = clamp(s, 0, 1); v = clamp(v, 0, 1);
            const c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c;
            let r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return rgb((r + m) * 255, (g + m) * 255, (b + m) * 255);
        };
        globalThis.mix = function (a, b, t) {
            t = clamp(t, 0, 1);
            return rgb(lerp(red(a), red(b), t), lerp(green(a), green(b), t), lerp(blue(a), blue(b), t));
        };
        globalThis.dim = function (c, amount) {
            amount = clamp(amount, 0, 1);
            return rgb(red(c) * amount, green(c) * amount, blue(c) * amount);
        };
        globalThis.black = 0;
        globalThis.white = 0xffffff;
        """;

    private static readonly Prepared<Script> Prelude = Engine.PrepareScript(PreludeSource, "helpers", strict: true);

    /// <summary>Longest a single call into a script may run before it is cut off.</summary>
    internal static readonly TimeSpan CallLimit = TimeSpan.FromMilliseconds(250);

    public static Engine CreateEngine() => new(o => o
        .Strict()
        .LimitMemory(64L * 1024 * 1024)
        .LimitRecursion(200)
        .TimeoutInterval(CallLimit)
        .DisableStringCompilation());

    /// <summary>
    /// Run a script's top level and return the object it handed to <c>effect(...)</c>.
    /// </summary>
    /// <exception cref="ScriptProblemException">It never called <c>effect(...)</c>, or called it twice.</exception>
    public static JsValue Run(Engine engine, Prepared<Script> script)
    {
        engine.Execute(Prelude);
        engine.SetValue("noise", new ClrFunction(engine, "noise", static (_, args) =>
            JsNumber.Create(Noise.Sample(Num(args, 0), Num(args, 1), Num(args, 2)))));

        JsValue found = JsValue.Undefined;
        int calls = 0;
        engine.SetValue("effect", new ClrFunction(engine, "effect", (_, args) =>
        {
            calls++;
            found = args.Length > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        }));

        engine.Execute(script);
        if (calls == 0) throw new ScriptProblemException("It never calls effect({ ... }).");
        if (calls > 1) throw new ScriptProblemException("It calls effect({ ... }) more than once. One file holds one effect.");
        if (found is not ObjectInstance) throw new ScriptProblemException("effect(...) needs an object, like effect({ name: \"My effect\", light(p, time) { ... } }).");
        return found;
    }

    /// <summary>A function the effect object defines, or undefined when it has none.</summary>
    public static JsValue Function(JsValue effect, string name)
    {
        var v = effect.AsObject().Get(name);
        if (v.IsUndefined() || v.IsNull()) return JsValue.Undefined;
        if (v is not Jint.Native.Function.Function) throw new ScriptProblemException($"\"{name}\" should be a function.");
        return v;
    }

    private static double Num(JsValue[] args, int i) => args.Length > i ? TypeConverter.ToNumber(args[i]) : 0;
}

/// <summary>Turns what a script returns into a color. Anything it can't read becomes black.</summary>
internal static class ScriptColor
{
    public static int Pack(Rgb c) => (c.R << 16) | (c.G << 8) | c.B;

    public static Rgb From(JsValue v)
    {
        if (v.IsNumber())
        {
            double d = v.AsNumber();
            if (!double.IsFinite(d)) return Rgb.Black;
            int n = (int)Math.Clamp(d, 0, 0xFFFFFF);
            return new Rgb((byte)(n >> 16), (byte)(n >> 8), (byte)n);
        }
        if (v.IsString()) return Rgb.TryParse(v.AsString(), out var c) ? c : Rgb.Black;
        if (v.IsArray())
        {
            var a = v.AsArray();
            return new Rgb(Channel(a.Get(0)), Channel(a.Get(1)), Channel(a.Get(2)));
        }
        return Rgb.Black;
    }

    private static byte Channel(JsValue v)
    {
        double d = v.IsNumber() ? v.AsNumber() : 0;
        return double.IsFinite(d) ? (byte)Math.Clamp(Math.Round(d), 0, 255) : (byte)0;
    }
}

/// <summary>Turns a script failure into one plain sentence a person can act on.</summary>
internal static class ScriptErrors
{
    public const string TooSlow = "It stopped because it was too slow and would have made the app lag.";

    public static string Describe(Exception e) => (e.InnerException is Acornima.ParseErrorException inner ? inner : e) switch
    {
        TooSlowException or TimeoutException => TooSlow,
        MemoryLimitExceededException => "It stopped because it used too much memory.",
        RecursionDepthOverflowException => "It stopped because a function kept calling itself.",
        ScriptProblemException p => p.Message,
        JavaScriptException js => $"It hit an error{Where(js.Location)}: {Short(js.Message)}",
        Acornima.ParseErrorException parse => $"It has a typing mistake on line {parse.LineNumber}: {Short(parse.Description)}",
        _ => $"It stopped: {Short(e.Message)}",
    };

    private static string Where(Acornima.SourceLocation at) =>
        at.Start.Line > 0 ? $" on line {at.Start.Line}" : "";

    /// <summary>One line, not too long, ending like a sentence so more text can follow it.</summary>
    private static string Short(string? s)
    {
        s = (s ?? "").ReplaceLineEndings(" ").Trim();
        if (s.Length > 160) s = s[..157] + "...";
        return s.Length == 0 || ".!?".Contains(s[^1]) ? s : s + ".";
    }
}
