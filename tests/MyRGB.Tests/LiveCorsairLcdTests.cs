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
            Say($"  profile {pid} \"{profile.Name}\": screen layout file {profile.ScreenLayoutFile}");
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

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
