using MyRGB.Hid;

namespace MyRGB.Tests;

/// <summary>
/// Listens to every HID interface of the Vanguard Pro 96 and prints what it sends, to learn the
/// reports behind keys and the volume wheel. Read-only: it never writes to the keyboard. Explicit.
/// </summary>
public class LiveCorsairTests(ITestOutputHelper output)
{
    [Fact(Explicit = true)]
    public async Task Prints_keyboard_input_reports()
    {
        var ifaces = HidDevices.Find("vid_1b1c&pid_2b0e");
        foreach (var i in ifaces) output.WriteLine($"iface page=0x{i.UsagePage:X4} usage=0x{i.Usage:X2} in={i.InputReportLength} path={i.Path}");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var seen = new HashSet<string>();
        var tasks = ifaces.Where(i => i.InputReportLength > 0).Select(i => Task.Run(async () =>
        {
            FileStream s;
            try { s = HidDevices.Open(i.Path); }
            catch (Exception ex) { output.WriteLine($"page=0x{i.UsagePage:X4}: can't open ({ex.Message})"); return; }
            await using var _ = s;
            var buf = new byte[i.InputReportLength];
            while (!stop.IsCancellationRequested)
            {
                int n;
                try { n = await s.ReadAsync(buf, stop.Token); } catch { break; }
                // Skip replies to MyRGB's own colour frames (00 00 <session> 06 ...).
                if (i.UsagePage == 0xFF42 && buf[1] == 0 && buf[4] == 0x06) continue;
                string hex = Convert.ToHexString(buf, 0, Math.Min(n, 24));
                lock (seen)
                    if (seen.Add($"{i.UsagePage}:{hex}"))
                        output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} page=0x{i.UsagePage:X4} usage=0x{i.Usage:X2} {hex}");
            }
        })).ToArray();
        await Task.WhenAll(tasks);
    }
}
