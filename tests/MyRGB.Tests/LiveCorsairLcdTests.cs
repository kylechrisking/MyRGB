using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Hid;
using MyRGB.Providers.Corsair;

namespace MyRGB.Tests;

/// <summary>
/// Read-only probe of the Vanguard Pro 96's screen storage, to confirm docs/LCD-PROTOCOL.txt before
/// anything is written. Reads the profiles, their screen layouts, the resource map and the images they
/// point at, prints a summary, and saves every file it read under artifacts/lcd-probe/&lt;time&gt;/ so they
/// can be put back if a later upload goes wrong. It never creates, writes or deletes a file.
/// Explicit: MyRGB must be closed first (it holds the keyboard), and the lights go dark until it ends.
/// </summary>
public class LiveCorsairLcdTests(ITestOutputHelper output)
{
    private const int MaxResources = 40;

    [Fact(Explicit = true)]
    public async Task Probes_screen_files()
    {
        var iface = HidDevices.Find("vid_1b1c&pid_2b0e&mi_02").FirstOrDefault(i => i.UsagePage == 0xFF42 && i.OutputReportLength == Bragi.ReportLength);
        Assert.True(iface is not null, "Vanguard Pro 96 command interface not found. Is it plugged in?");

        var dir = Path.Combine(RepoRoot(), "artifacts", "lcd-probe", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        void Say(string s) { output.WriteLine(s); log.AppendLine(s); }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        await using var kb = await CorsairKeyboard.OpenAsync(iface, NullLogger.Instance, ct);
        var saved = new HashSet<ushort>();

        async Task<byte[]?> Read(ushort id, string what)
        {
            byte[]? f;
            try { f = await kb.ReadFileAsync(id, ct); }
            catch (Exception ex) when (ex is IOException or TimeoutException) { Say($"  file {id} ({what}): read failed: {ex.Message}"); return null; }
            if (f is null) { Say($"  file {id} ({what}): does not exist"); return null; }
            if (saved.Add(id)) await File.WriteAllBytesAsync(Path.Combine(dir, $"file-{id}.bin"), f, ct);
            Say($"  file {id} ({what}): {f.Length} bytes, starts {Convert.ToHexString(f, 0, Math.Min(f.Length, 24))}");
            return f;
        }

        Say("Properties");
        foreach (var (id, name) in new (ushort, string)[] { (Bragi.PropertyVendorId, "vendor id"), (Bragi.PropertyProductId, "product id"), (Bragi.PropertyMode, "mode"), (ScreenFiles.PropertyFreeStorage, "free storage"), (ScreenFiles.PropertyScreenIndex, "screen index") })
        {
            var v = await kb.GetPropertyAsync(id, ct);
            Say($"  {name} ({id}): {(v is null ? "rejected" : $"{v} (0x{v:X})")}");
        }

        Say("Profiles");
        var resources = new SortedSet<ushort>();
        var listFile = await Read(ScreenFiles.ProfilesListFile, "profiles list");
        var list = listFile is null ? null : ScreenFiles.ParseProfilesList(listFile);
        foreach (var pid in list?.ProfileFileIds ?? [])
        {
            var pf = await Read(pid, "profile");
            var profile = pf is null ? null : ScreenFiles.ParseProfile(pf);
            if (profile is null) continue;
            Say($"  profile {pid} \"{profile.Name}\": properties file {profile.PropertiesFile}, screen layout file {profile.ScreenLayoutFile}");
            if (profile.PropertiesFile != 0 && await Read(profile.PropertiesFile, "properties") is { } propFile)
            {
                var props = ScreenFiles.ParseProperties(propFile);
                Say($"    properties header 0x{props.Header:X4}, declared {props.DeclaredCount}, read {props.Entries.Count}: {string.Join(" ", props.Entries.Select(e => $"{e.Id}=0x{e.Value:X}"))}");
                Say($"    screen index (263): {props[ScreenFiles.PropertyScreenIndex]?.ToString() ?? "not set"}");
                var rebuilt = ScreenFiles.BuildProperties(props.Entries);
                Say($"    rebuilds byte-for-byte: {rebuilt.AsSpan().SequenceEqual(propFile)}");
            }
            if (profile.ScreenLayoutFile == 0) continue;

            var lf = await Read(profile.ScreenLayoutFile, "screen layout");
            if (lf is null) continue;
            var layout = ScreenFiles.ParseScreenLayout(lf);
            Say($"    layout header 0x{layout.Header:X4}, rows: {string.Join(" | ", layout.Rows.Select(r => string.Join(",", r)))}");
            foreach (var cid in layout.Rows.SelectMany(r => r).Distinct())
            {
                var cf = await Read(cid, "screen config");
                var config = cf is null ? null : ScreenFiles.ParseScreenConfig(cf);
                if (config is null) continue;
                Say($"    config {cid}: kind 0x{config.Kind:X2}, delay {config.FrameDelayMs} ms, resources {string.Join(",", config.ResourceIds)}");
                foreach (var r in config.ResourceIds) resources.Add(r);
            }
        }

        Say("Resource map");
        var mapFile = await Read(ScreenFiles.ResourceMapFile, "resource map");
        if (mapFile is not null)
        {
            var map = ScreenFiles.ParseResourceMap(mapFile);
            Say($"  header 0x{map.Header:X4}, declared {map.DeclaredCount}, entries: {string.Join(" ", map.Entries.Select(e => $"{e.ResourceId}@{e.Address}#{e.Hash:X8}"))}");
            foreach (var e in map.Entries) resources.Add(e.ResourceId);
        }

        Say($"Resources ({resources.Count}, reading up to {MaxResources})");
        foreach (var rid in resources.Take(MaxResources))
        {
            var rf = await Read(rid, "resource");
            if (rf is null) continue;
            var bmp = ScreenFiles.ParseBmpResource(rf);
            Say(bmp is null
                ? $"    not a BMP resource (marker 0x{rf[0]:X2})"
                : $"    BMP {bmp.Width}x{bmp.Height}, {bmp.BitsPerPixel} bpp, compression {bmp.Compression}");
        }

        Say($"Saved {saved.Count} files to {dir}");
        await File.WriteAllTextAsync(Path.Combine(dir, "summary.txt"), log.ToString(), ct);
    }

    /// <summary>
    /// WRITES to the keyboard: puts a test card on the screen (top third red, middle green, bottom blue,
    /// a white square top-left, so colour order and orientation are both visible). Backs up every file it
    /// changes to artifacts/lcd-upload/&lt;time&gt;/ first, then reads each written file back to check it.
    /// Undo with <see cref="Rolls_back_last_upload"/>. MyRGB must be closed.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Uploads_test_image()
    {
        await using var kb = await OpenAsync();
        var dir = Path.Combine(RepoRoot(), "artifacts", "lcd-upload", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var log = new StringBuilder();
        void Say(string s) { output.WriteLine(s); log.AppendLine(s); }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        try
        {
            var r = await ScreenUpload.UploadStaticImageAsync(kb, TestCard(), dir, Say, cts.Token);
            Say($"Done: resource {r.ResourceFile}, config {r.ConfigFile}, screen index {r.ScreenIndex}");

            Say("Read back");
            foreach (var id in new[] { ScreenFiles.ResourceMapFile, r.ConfigFile, r.ResourceFile })
            {
                var f = await kb.ReadFileAsync(id, cts.Token);
                Say($"  file {id}: {(f is null ? "MISSING" : $"{f.Length} bytes, starts {Convert.ToHexString(f, 0, Math.Min(24, f.Length))}")}");
                Assert.NotNull(f);
            }
            var profile = ScreenFiles.ParseProfile((await kb.ReadFileAsync(r.ProfileFile, cts.Token))!)!;
            var layout = ScreenFiles.ParseScreenLayout((await kb.ReadFileAsync(profile.ScreenLayoutFile, cts.Token))!);
            Say($"  layout {profile.ScreenLayoutFile}: rows {string.Join(" | ", layout.Rows.Select(row => string.Join(",", row)))}");
            Assert.Contains(r.ConfigFile, layout.Rows[0]);
            var props = ScreenFiles.ParseProperties((await kb.ReadFileAsync(profile.PropertiesFile, cts.Token))!);
            Say($"  screen index (263): {props[ScreenFiles.PropertyScreenIndex]}");
            Say($"  free storage now: {await kb.GetPropertyAsync(ScreenFiles.PropertyFreeStorage, cts.Token)}");
        }
        finally
        {
            if (Directory.Exists(dir)) await File.WriteAllTextAsync(Path.Combine(dir, "summary.txt"), log.ToString(), CancellationToken.None);
        }
    }

    /// <summary>WRITES to the keyboard: undoes the newest upload under artifacts/lcd-upload/ that isn't undone yet.</summary>
    [Fact(Explicit = true)]
    public async Task Rolls_back_last_upload()
    {
        var root = Path.Combine(RepoRoot(), "artifacts", "lcd-upload");
        var dir = Directory.GetDirectories(root).OrderDescending()
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "upload.txt")) && !File.Exists(Path.Combine(d, "rolled-back.txt")));
        Assert.True(dir is not null, "No upload left to roll back.");

        await using var kb = await OpenAsync();
        var log = new StringBuilder();
        void Say(string s) { output.WriteLine(s); log.AppendLine(s); }
        Say($"Rolling back {dir}");
        var ct = TestContext.Current.CancellationToken;
        await ScreenUpload.RollBackAsync(kb, dir, Say, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "rolled-back.txt"), log.ToString(), ct);
    }

    private static async Task<CorsairKeyboard> OpenAsync()
    {
        var iface = HidDevices.Find("vid_1b1c&pid_2b0e&mi_02").FirstOrDefault(i => i.UsagePage == 0xFF42 && i.OutputReportLength == Bragi.ReportLength);
        Assert.True(iface is not null, "Vanguard Pro 96 command interface not found. Is it plugged in?");
        return await CorsairKeyboard.OpenAsync(iface, NullLogger.Instance, CancellationToken.None);
    }

    /// <summary>Red / green / blue thirds, top to bottom, with a 40×40 white square in the top-left corner.</summary>
    private static byte[] TestCard()
    {
        const int w = ScreenFiles.Width, h = ScreenFiles.Height;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                if (x < 40 && y < 40) { rgb[o] = rgb[o + 1] = rgb[o + 2] = 255; continue; }
                rgb[o + (y * 3 / h)] = 255; // 0 = R, 1 = G, 2 = B
            }
        return rgb;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
