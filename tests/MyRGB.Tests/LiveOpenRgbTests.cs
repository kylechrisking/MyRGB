using MyRGB.Providers.OpenRgb;

namespace MyRGB.Tests;

/// <summary>
/// Talks to a real OpenRGB server on 127.0.0.1:6742. Explicit, so they only run when asked for:
/// MyRGB.Tests.exe -explicit only -showLiveOutput
/// </summary>
public class LiveOpenRgbTests(ITestOutputHelper output)
{
    [Fact(Explicit = true)]
    public async Task Lists_real_devices()
    {
        await using var provider = new OpenRgbProvider(new OpenRgbOptions { AutoStart = false });
        var devices = await provider.DiscoverAsync(TestContext.Current.CancellationToken);

        output.WriteLine(provider.StatusMessage ?? "");
        foreach (var d in devices)
            output.WriteLine($"{d.Type,-12} {d.Vendor,-10} {d.Name,-40} leds={d.Leds.Count,-4} zones={string.Join(", ", d.Zones.Select(z => $"{z.Name}({z.Count})"))}  hw={d.HardwareKey}  id={d.Id}");
        Assert.NotEmpty(devices);
    }

    /// <summary>Read back what the server last sent to each device, e.g. to confirm MyRGB's frames arrive.</summary>
    [Fact(Explicit = true)]
    public async Task Reads_current_colors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await OpenRgbClient.ConnectAsync("127.0.0.1", 6742, "MyRGB tests", ct);
        int count = await client.GetControllerCountAsync(ct);
        for (uint i = 0; i < count; i++)
        {
            var c = await client.GetControllerAsync(i, ct);
            output.WriteLine($"{c.Name,-36} mode={c.Modes.ElementAtOrDefault(c.ActiveMode)?.Name,-8} {string.Join(" ", c.Colors.Take(8).Select(x => x.ToHex()))}");
        }
    }
}
