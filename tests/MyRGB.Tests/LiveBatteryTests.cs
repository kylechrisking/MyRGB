using MyRGB.Battery;

namespace MyRGB.Tests;

/// <summary>Reads real batteries. Explicit: needs the wireless devices on and nearby.</summary>
public class LiveBatteryTests(ITestOutputHelper output)
{
    [Fact(Explicit = true)]
    public async Task Reads_wireless_batteries()
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = new RazerBatteryProvider().Discover().Concat(new LogitechBatteryProvider().Discover()).ToList();
        foreach (var s in sources)
        {
            var status = await s.ReadAsync(ct);
            output.WriteLine($"{s.Name,-30} {s.HardwareKey,-16} {(status is null ? "unreachable" : $"{status.Percent}%{(status.Charging ? " charging" : "")}{(status.Estimated ? " (est.)" : "")}")}");
        }
        Assert.NotEmpty(sources);
    }
}
