using MyRGB.Battery;
using MyRGB.Core.Devices;
using MyRGB.Core.Vendors;

namespace MyRGB.Tests;

public class VendorAndBatteryTests
{
    // A real Installed apps list from a PC with iCUE, G HUB and Stream Deck.
    private static readonly InstalledProgram[] ThisPc =
    [
        new("Elgato Stream Deck", "Corsair Memory, Inc.", "7.6.0", "MsiExec.exe /I{187BBE1D-5634-411D-A397-EEC5E7A87487}"),
        new("Corsair Device Control Service", "Corsair", "1.9.18", "MsiExec.exe /X{38D4DBC1-C1E3-4664-8363-43BE072DD228}"),
        new("Logitech G HUB", "Logitech", "2026.4", "\"C:\\Program Files\\LGHUB\\lghub_updater.exe\" --uninstall"),
        new("Corsair iCUE5 Software", "Corsair", "5.52.93", "MsiExec.exe /I{A9B0B2D7-8C59-4413-A2FB-99EDBE65A608}"),
        new("Corsair iCUE VHID Drivers v3.0.88.9", "Corsair Memory", "3.0.88.9", null),
        new("Corsair USBXp Drivers v3.3.0.15", "Corsair Memory", "3.3.0.15", null),
        new("Steam", "Valve", "2.10", null),
    ];

    [Fact]
    public void Detects_icue_and_ghub_but_not_stream_deck()
    {
        var found = VendorAppCatalog.Detect(ThisPc, ["lghub_agent", "lghub_updater", "Corsair.Service", "explorer"]);
        Assert.Equal(["icue", "ghub"], found.Select(f => f.App.Id));

        var icue = found[0];
        Assert.Equal("Corsair iCUE5 Software", icue.Program.Name);
        Assert.Equal(3, icue.Components.Count);
        Assert.False(icue.Running);
        Assert.True(found[1].Running);
    }

    [Theory]
    [InlineData("MsiExec.exe /I{A9B0B2D7-8C59-4413-A2FB-99EDBE65A608}", "MsiExec.exe /X{A9B0B2D7-8C59-4413-A2FB-99EDBE65A608}")]
    [InlineData("\"C:\\x\\uninst.exe\" /S", "\"C:\\x\\uninst.exe\" /S")]
    public void Msi_installs_switch_to_uninstall(string registered, string expected) =>
        Assert.Equal(expected, VendorAppCatalog.UninstallCommand(new InstalledProgram("x", null, null, registered)));

    [Fact]
    public void Razer_report_crc_and_reply()
    {
        var req = RazerReport.Build(0x1F, 0x07, 0x80, 0x02);
        Assert.Equal(91, req.Length);
        Assert.Equal(0x1F, req[2]);
        Assert.Equal(0x02 ^ 0x07 ^ 0x80, req[89]); // crc over bytes 2..87 of the report

        var reply = (byte[])req.Clone();
        reply[1] = 0x02;   // success
        reply[10] = 0xFF;  // arguments[1] = full
        var r = RazerReport.Parse(reply, 0x07, 0x80);
        Assert.True(r.Ok);
        Assert.Equal(0xFF, r.Argument1);

        reply[1] = 0x04;   // timeout: mouse asleep
        Assert.True(RazerReport.Parse(reply, 0x07, 0x80).NoResponse);
    }

    [Theory]
    [InlineData(4200, 100)]
    [InlineData(3790, 50)]
    [InlineData(3730, 35)]
    [InlineData(3300, 0)]
    public void Voltage_curve(int mv, int pct) => Assert.Equal(pct, LogitechBatterySource.VoltageToPercent(mv));

    [Fact]
    public async Task One_missed_read_keeps_the_last_battery_level()
    {
        var source = new FlakySource { Next = new BatteryStatus(70, false) };
        await using var monitor = new BatteryMonitor([new OneSource(source)]);
        await monitor.RefreshAsync();
        source.Next = null;
        await monitor.RefreshAsync();
        Assert.Equal(70, monitor.Readings["usb:test"].Status?.Percent);
    }

    private sealed class FlakySource : IBatterySource
    {
        public BatteryStatus? Next;
        public string HardwareKey => "usb:test";
        public string Name => "Test mouse";
        public Task<BatteryStatus?> ReadAsync(CancellationToken ct) => Task.FromResult(Next);
    }

    private sealed class OneSource(IBatterySource source) : IBatteryProvider
    {
        public IReadOnlyList<IBatterySource> Discover() => [source];
    }
}
