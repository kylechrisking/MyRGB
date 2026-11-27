using System.Diagnostics;
using MyRGB.Core;
using MyRGB.Core.Effects;
using MyRGB.Scripting;

namespace MyRGB.Tests;

/// <summary>Effects people write themselves: they load, they paint, and they can't hurt the app.</summary>
public class ScriptEffectTests
{
    private static LedPoint[] Grid(int cols = 20, int rows = 6)
    {
        var points = new LedPoint[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                points[r * cols + c] = LedPoint.Whole((c + 0.5f) / cols, (r + 0.5f) / rows);
        return points;
    }

    private static EffectDefinition Load(string code, string file = "Test.js")
    {
        var r = ScriptEffectLoader.Load(file, code);
        Assert.True(r.Definition is not null, r.Problem);
        return r.Definition!;
    }

    private static string? Problem(string code) => ScriptEffectLoader.Load("Test.js", code).Problem;

    private static (ScriptEffect Effect, Rgb[] Colors) RenderFrames(EffectDefinition def, int frames, LedPoint[]? points = null)
    {
        points ??= Grid();
        var effect = (ScriptEffect)def.Create(new EffectSettings(null));
        var colors = new Rgb[points.Length];
        for (int f = 0; f < frames; f++) effect.Render(new FrameContext(f / 30.0, 1 / 30.0), points, colors);
        return (effect, colors);
    }

    [Fact]
    public void The_examples_load_and_light_something()
    {
        var examples = EffectFolder.Examples().ToList();
        Assert.True(examples.Count >= 3);
        foreach (var (file, code) in examples)
        {
            var def = Load(code, file);
            Assert.Equal(ScriptEffectLoader.Category, def.Category);
            var effect = (ScriptEffect)def.Create(new EffectSettings(null));
            var points = Grid();
            var colors = new Rgb[points.Length];
            long best = 0;
            for (double t = 0; t <= 4; t += 1 / 15.0)
            {
                effect.Render(new FrameContext(t, 1 / 15.0), points, colors);
                best = Math.Max(best, colors.Sum(c => (long)c.R + c.G + c.B));
            }
            Assert.Null(effect.Problem);
            Assert.True(best > 1000, $"{file}: brightest frame summed to {best}");
        }
    }

    [Fact]
    public void Reads_name_settings_and_colors()
    {
        var def = Load("""
            effect({
              name: "Two Tone", description: "Left and right.", author: "Sam",
              settings: {
                left:  { type: "color", label: "Left", default: "#ff0000" },
                right: { type: "color", default: [0, 0, 255] },
                cut:   { type: "number", label: "Where", min: 0, max: 1, default: 0.5, step: 0.1 },
                style: { type: "choice", options: ["A", "B"], default: "B" },
                on:    { type: "switch", default: true },
              },
              light(p) { return p.x < settings.cut ? settings.left : settings.right; },
            });
            """, "Two Tone.js");

        Assert.Equal("script:two-tone", def.Id);
        Assert.Equal("Two Tone", def.Name);
        Assert.Equal("Left and right. Made by Sam.", def.Description);
        Assert.Equal(["left", "right", "cut", "style", "on"], def.Parameters.Select(p => p.Key));
        Assert.Equal(new Rgb(0, 0, 255), ((ColorParameter)def.Parameters[1]).Default);
        Assert.Equal("right", def.Parameters[1].Label);
        Assert.Equal("B", ((ChoiceParameter)def.Parameters[3]).Default);
        Assert.True(((BoolParameter)def.Parameters[4]).Default);

        var (_, colors) = RenderFrames(def, 1, [LedPoint.Whole(0.1f, 0.5f), LedPoint.Whole(0.9f, 0.5f)]);
        Assert.Equal(new Rgb(255, 0, 0), colors[0]);
        Assert.Equal(new Rgb(0, 0, 255), colors[1]);
    }

    [Fact]
    public void Picks_up_new_settings_without_restarting()
    {
        var def = Load("""
            let frames = 0;
            effect({
              name: "Count", settings: { c: { type: "color", default: "#000000" } },
              frame() { frames++; },
              light() { return frames > 1 ? settings.c : 0x00ff00; },
            });
            """);
        var effect = def.Create(new EffectSettings(null));
        var points = Grid(2, 1);
        var colors = new Rgb[2];
        effect.Render(new FrameContext(0, 0), points, colors);
        Assert.Equal(new Rgb(0, 255, 0), colors[0]);
        effect.Settings = def.Bind(new EffectSettings(new Dictionary<string, string> { ["c"] = "#123456" }));
        effect.Render(new FrameContext(0.1, 0.1), points, colors);
        Assert.Equal(new Rgb(0x12, 0x34, 0x56), colors[1]);
    }

    [Theory]
    [InlineData("rgb(300, -4, 10.4)", 255, 0, 10)]
    [InlineData("hsv(120, 1, 1)", 0, 255, 0)]
    [InlineData("mix(0x000000, 0xffffff, 0.5)", 128, 128, 128)]
    [InlineData("dim(0xff8000, 0.5)", 128, 64, 0)]
    [InlineData("\"#0a0b0c\"", 10, 11, 12)]
    [InlineData("[1, 2, 3]", 1, 2, 3)]
    [InlineData("NaN", 0, 0, 0)]
    [InlineData("\"nonsense\"", 0, 0, 0)]
    [InlineData("undefined", 0, 0, 0)]
    public void Color_helpers_and_return_values(string expression, byte r, byte g, byte b)
    {
        var def = Load($"effect({{ name: \"C\", light() {{ return {expression}; }} }});");
        var (effect, colors) = RenderFrames(def, 1, [LedPoint.Whole(0.5f, 0.5f)]);
        Assert.Null(effect.Problem);
        Assert.Equal(new Rgb(r, g, b), colors[0]);
    }

    [Fact]
    public void Noise_is_smooth_and_stays_in_range()
    {
        var def = Load("effect({ name: \"N\", light(p, t) { const n = noise(p.x * 3, p.y * 3, t); return rgb(n * 255, 0, 0); } });");
        var (_, colors) = RenderFrames(def, 3, Grid(40, 1));
        Assert.True(colors.Select(c => c.R).Distinct().Count() > 5);
        for (int i = 1; i < colors.Length; i++) Assert.True(Math.Abs(colors[i].R - colors[i - 1].R) < 60);
    }

    [Theory]
    [InlineData("function light() { return 0; }", "never calls effect")]
    [InlineData("effect({ light() {} }); effect({ light() {} });", "more than once")]
    [InlineData("effect({ name: \"x\" });", "no light function")]
    [InlineData("effect({ light() { return 0; } });", "needs a name")]
    [InlineData("effect({ name: \"x\", light: 4 });", "should be a function")]
    [InlineData("effect({ name: \"x\", light() {}, settings: { s: { type: \"slider\" } } });", "needs a type")]
    [InlineData("effect({ name: \"x\", light() {}, settings: { s: { type: \"number\", min: 5, max: 1 } } });", "max bigger than its min")]
    [InlineData("effect({ name: \"x\", light() {}, settings: { \"two words\": { type: \"switch\" } } });", "short word")]
    [InlineData("effect({ name: \"x\",\n light() { return 0 }\n settings: {} });", "typing mistake on line 3")]
    [InlineData("throw new Error(\"boom\");", "boom")]
    public void Explains_what_is_wrong_with_a_broken_file(string code, string expected)
    {
        var problem = Problem(code);
        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_error_while_running_stops_the_effect_with_a_dim_glow()
    {
        var def = Load("effect({ name: \"x\", light(p, t) {\n  if (t > 0.05) missing.thing = 1;\n  return 0xffffff; } });");
        var (effect, colors) = RenderFrames(def, 5);
        Assert.Contains("line 2", effect.Problem);
        Assert.All(colors, c => Assert.Equal(ScriptEffect.Stopped, c));
    }

    [Fact]
    public void Endless_loops_are_cut_off_quickly()
    {
        var clock = Stopwatch.StartNew();
        Assert.Contains("too slow", Problem("while (true) {} effect({ name: \"x\", light() { return 0; } });"));

        var def = Load("effect({ name: \"x\", light() { for (;;) {} } });");
        var (effect, _) = RenderFrames(def, 3);
        Assert.Equal(ScriptErrors.TooSlow, effect.Problem);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_frame_made_slow_by_many_small_calls_is_cut_off_too()
    {
        // Each call is quick, but together they keep the lights waiting.
        var def = Load("effect({ name: \"x\", light() { let s = 0; for (let i = 0; i < 20000; i++) s += Math.sqrt(i); return s; } });");
        var clock = Stopwatch.StartNew();
        var (effect, _) = RenderFrames(def, 2, Grid(60, 20));
        Assert.Equal(ScriptErrors.TooSlow, effect.Problem);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
    }

    [Fact]
    public void The_time_limit_is_per_frame_not_for_the_whole_run()
    {
        // 15 frames of 20 ms each: well over the per-call limit added up, but every frame is fine.
        var def = Load("effect({ name: \"x\", frame() { const end = Date.now() + 20; while (Date.now() < end) {} }, light() { return 0xff0000; } });");
        var (effect, colors) = RenderFrames(def, 15, Grid(2, 1));
        Assert.Null(effect.Problem);
        Assert.Equal(new Rgb(255, 0, 0), colors[0]);
    }

    [Fact]
    public void Using_too_much_memory_in_one_call_stops_the_effect()
    {
        var def = Load("effect({ name: \"x\", light() { const a = []; for (let i = 0; i < 200; i++) a.push(new Array(500000).fill(i)); return 0; } });");
        var (effect, _) = RenderFrames(def, 1, Grid(1, 1));
        Assert.Equal(ScriptErrors.TooMuchMemory, effect.Problem);
    }

    [Fact]
    public void Huge_arrays_are_refused()
    {
        var (effect, _) = RenderFrames(Load("effect({ name: \"x\", light() { return new Array(50000000).length; } });"), 1, Grid(1, 1));
        Assert.NotNull(effect.Problem);
    }

    [Fact]
    public void Allocating_a_little_in_every_call_still_counts_for_the_frame()
    {
        // Each call stays far under the per-call limit; the frame as a whole doesn't.
        var def = Load("const keep = []; effect({ name: \"x\", light(p) { keep.push(\"ab\".repeat(500000) + p.index); return 0; } });");
        var (effect, _) = RenderFrames(def, 1, Grid(10, 10));
        Assert.Equal(ScriptErrors.TooMuchMemory, effect.Problem);
    }

    [Fact]
    public void A_slow_leak_is_caught_when_the_app_grows_too_big()
    {
        long saved = ScriptEffect.HeapLimit;
        try
        {
            GC.Collect();
            ScriptEffect.HeapLimit = GC.GetGCMemoryInfo().HeapSizeBytes + 64L * 1024 * 1024;
            // About 4 MB kept every frame: under the per-frame limit, but it never stops growing.
            var def = Load("const keep = []; effect({ name: \"x\", frame() { keep.push(new Array(500000).fill(1)); }, light() { return 0; } });");
            var effect = (ScriptEffect)def.Create(new EffectSettings(null));
            var points = Grid(1, 1);
            var colors = new Rgb[1];
            var clock = Stopwatch.StartNew();
            for (int f = 0; f < 400 && effect.Problem is null && clock.Elapsed < TimeSpan.FromSeconds(20); f++)
            {
                effect.Render(new FrameContext(f / 30.0, 1 / 30.0), points, colors);
                if (f % 10 == 0) GC.Collect();
                Thread.Sleep(10);
            }
            Assert.Equal(ScriptErrors.AppOutOfMemory, effect.Problem);
        }
        finally { ScriptEffect.HeapLimit = saved; }
    }

    [Fact]
    public void Scripts_cannot_reach_the_app_files_or_network()
    {
        var def = Load("""
            const reach = [typeof System, typeof importNamespace, typeof require, typeof fetch,
                           typeof XMLHttpRequest, typeof process, typeof setTimeout, typeof host];
            effect({ name: "x", light() { return reach.every(t => t === "undefined") ? 0x00ff00 : 0xff0000; } });
            """);
        var (effect, colors) = RenderFrames(def, 1, Grid(1, 1));
        Assert.Null(effect.Problem);
        Assert.Equal(new Rgb(0, 255, 0), colors[0]);

        Assert.NotNull(RenderFrames(Load("effect({ name: \"x\", light() { return eval(\"1\"); } });"), 1).Effect.Problem);
        Assert.NotNull(RenderFrames(Load("effect({ name: \"x\", light() { return new Function(\"return 1\")(); } });"), 1).Effect.Problem);
        Assert.NotNull(Problem("import fs from \"fs\"; effect({ name: \"x\", light() {} });"));
        Assert.NotNull(RenderFrames(Load("effect({ name: \"x\", light() { import(\"fs\"); return 0; } });"), 1).Effect.Problem);
    }

    [Fact]
    public void Only_scripts_that_mention_music_get_it()
    {
        var plain = Load("effect({ name: \"x\", light() { return globalThis[\"mu\" + \"sic\"] === undefined ? 0xffffff : 0; } });");
        Assert.Equal(Rgb.White, RenderFrames(plain, 1, Grid(1, 1)).Colors[0]);

        var music = Load("effect({ name: \"x\", light() { return music.bands.length === 32 && music.level >= 0 ? 0xffffff : 0; } });");
        Assert.Equal(Rgb.White, RenderFrames(music, 1, Grid(1, 1)).Colors[0]);
    }

    [Fact]
    public void Each_copy_runs_on_its_own()
    {
        var def = Load("let n = 0; effect({ name: \"x\", frame() { n++; }, light() { return n; } });");
        var a = def.Create(new EffectSettings(null));
        var b = def.Create(new EffectSettings(null));
        var points = Grid(1, 1);
        var ca = new Rgb[1];
        var cb = new Rgb[1];
        for (int i = 0; i < 5; i++) a.Render(new FrameContext(i, 1), points, ca);
        b.Render(new FrameContext(0, 1), points, cb);
        Assert.Equal(5, ca[0].B);
        Assert.Equal(1, cb[0].B);
    }

    [Fact]
    public void The_folder_adds_updates_and_removes_effects()
    {
        var dir = Directory.CreateTempSubdirectory("myrgb-effects-");
        try
        {
            var registry = EffectRegistry.CreateDefault();
            int builtIn = registry.All.Count;
            var folder = new EffectFolder(registry, dir.FullName);
            int changes = 0;
            registry.Changed += (_, _) => changes++;

            folder.Load();
            Assert.Equal(builtIn, registry.All.Count); // empty folder: nothing added

            File.WriteAllText(Path.Combine(dir.FullName, "Glow.js"), "effect({ name: \"Glow\", light() { return 0xff0000; } });");
            File.WriteAllText(Path.Combine(dir.FullName, "Broken.js"), "effect({ name: ");
            File.WriteAllText(Path.Combine(dir.FullName, "notes.txt"), "not an effect");
            folder.Load();
            Assert.Equal(builtIn + 1, registry.All.Count);
            Assert.Equal("Glow", registry.Find("script:glow")!.Name);
            Assert.Single(folder.Problems);
            Assert.Equal("Broken.js", folder.Problems[0].File);

            File.WriteAllText(Path.Combine(dir.FullName, "Glow.js"), "effect({ name: \"Glow Two\", light() { return 0xff0000; } });");
            File.Delete(Path.Combine(dir.FullName, "Broken.js"));
            folder.Load();
            Assert.Equal("Glow Two", registry.Find("script:glow")!.Name);
            Assert.Empty(folder.Problems);

            File.Delete(Path.Combine(dir.FullName, "Glow.js"));
            folder.Load();
            Assert.Null(registry.Find("script:glow"));
            Assert.Equal(builtIn, registry.All.Count);
            Assert.Equal(4, changes);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Remembers_why_a_running_effect_stopped()
    {
        var dir = Directory.CreateTempSubdirectory("myrgb-effects-");
        try
        {
            var registry = new EffectRegistry();
            var folder = new EffectFolder(registry, dir.FullName);
            File.WriteAllText(Path.Combine(dir.FullName, "Bad.js"), "effect({ name: \"Bad\", light() { return oops; } });");
            folder.Load();
            string? heard = null;
            folder.EffectStopped += (_, why) => heard = why;
            RenderFrames(registry.Find("script:bad")!, 1);
            Assert.Contains("oops", folder.ProblemFor("script:bad"));
            Assert.Equal(folder.ProblemFor("script:bad"), heard);

            folder.Load(); // reloading gives it a fresh start
            Assert.Null(folder.ProblemFor("script:bad"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Making_the_folder_puts_the_examples_and_guide_in_it()
    {
        var root = Directory.CreateTempSubdirectory("myrgb-effects-");
        try
        {
            var path = Path.Combine(root.FullName, "MyRGB Effects");
            var folder = new EffectFolder(new EffectRegistry(), path);
            folder.Create();
            Assert.True(File.Exists(Path.Combine(path, "Northern Lights.js")));
            Assert.True(File.Exists(Path.Combine(path, "How to make effects.txt")));

            // Never overwrites what the user already has.
            File.WriteAllText(Path.Combine(path, "Northern Lights.js"), "mine");
            folder.Create();
            Assert.Equal("mine", File.ReadAllText(Path.Combine(path, "Northern Lights.js")));
        }
        finally { root.Delete(true); }
    }
}
