using System.Diagnostics;
using MyRGB.Core.Sensors;
using MyRGB.Sensors;

namespace MyRGB.Tests;

/// <summary>
/// Starts the real helper exe and talks to it over the real pipe. Explicit, so they only run
/// when asked for: MyRGB.Tests.exe -explicit only -showLiveOutput
/// </summary>
public class LiveHelperTests(ITestOutputHelper output)
{
    [Fact(Explicit = true)]
    public async Task The_real_helper_serves_readings()
    {
        var ct = TestContext.Current.CancellationToken;
        var exe = FindHelper();
        Assert.True(File.Exists(exe), $"The helper isn't built; run dotnet build src/MyRGB.App (expected at {exe}).");

        using var helper = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        Assert.NotNull(helper);
        try
        {
            // No fallback: anything that arrives came over the pipe from the helper.
            using var client = new PipeSensors(retryAfter: TimeSpan.FromMilliseconds(200), fallback: new NoSensors());
            IReadOnlyList<SensorReading> readings = [];
            for (var i = 0; i < 60 && readings.Count == 0; i++)
            {
                await Task.Delay(250, ct);
                readings = client.Read();
            }

            Assert.NotEmpty(readings);
            Assert.True(client.Connected);
            Assert.NotNull(client.HelperVersion);
            output.WriteLine($"helper {client.HelperVersion}: {readings.Count} readings, {client.Fans.Count} fan(s)");
            foreach (var r in readings.Where(r => r.Value is not null).Take(12))
                output.WriteLine($"  {r.Hardware,-32} {r.Name,-26} {MonitoringToString(r)}");
        }
        finally
        {
            if (!helper.HasExited) helper.Kill(entireProcessTree: true);
        }
    }

    private static string MonitoringToString(SensorReading r) => r.Kind switch
    {
        SensorKind.Temperature => $"{r.Value:0.0} °C",
        SensorKind.Load or SensorKind.Control => $"{r.Value:0} %",
        SensorKind.Fan => $"{r.Value:0} RPM",
        SensorKind.Power => $"{r.Value:0.0} W",
        SensorKind.Clock => $"{r.Value:0} MHz",
        _ => r.Value?.ToString("0.##") ?? "–",
    };

    /// <summary>Walks up from the test folder to the repository, where the app build stages the helper.</summary>
    private static string FindHelper()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MyRGB.slnx")))
            dir = dir.Parent;
        if (dir is null) return "";
        return Path.Combine(dir.FullName, "src", "MyRGB.App", "bin", "Debug", "net10.0", "helper", "MyRGB.Sensors.Helper.exe");
    }
}
