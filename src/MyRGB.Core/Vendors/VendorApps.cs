using System.Text.RegularExpressions;

namespace MyRGB.Core.Vendors;

/// <summary>An entry from Windows' installed-programs list.</summary>
public sealed record InstalledProgram(string Name, string? Publisher, string? Version, string? UninstallCommand);

/// <summary>
/// A vendor's lighting app that MyRGB can stand in for, with an honest list of what else it does,
/// so people know what they give up by uninstalling it.
/// </summary>
/// <param name="Vendor">Matched against <c>IRgbDevice.Vendor</c> to find the devices it used to run.</param>
/// <param name="Main">Matches the program's own entry in Installed apps.</param>
/// <param name="Components">Matches the drivers and services it installs alongside (removed after it).</param>
/// <param name="YouWouldLose">Non-lighting features MyRGB does not replace.</param>
/// <param name="Tip">Vendor-specific advice for uninstalling cleanly.</param>
public sealed record VendorApp(
    string Id,
    string Name,
    string? Vendor,
    Regex Main,
    Regex? Components,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> YouWouldLose,
    string? Tip = null);

/// <summary>A vendor app found on this PC.</summary>
public sealed record DetectedVendorApp(VendorApp App, InstalledProgram Program, IReadOnlyList<InstalledProgram> Components, bool Running);

public static class VendorAppCatalog
{
    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<VendorApp> All =
    [
        new("icue", "Corsair iCUE", "Corsair",
            R(@"^(Corsair )?iCUE\s*\d*( Software)?$"),
            R(@"^Corsair (Device Control Service|.*VHID Drivers|.*USBXp Drivers)"),
            ["iCUE"], // its background services and updaters do not drive lighting
            [
                "Fan and pump speed curves for Corsair Commander and liquid coolers",
                "Key remapping and macros on Corsair keyboards",
                "Mouse DPI and button settings",
                "Firmware updates for Corsair devices",
            ],
            "iCUE's drivers are listed separately (VHID, USBXp, Device Control Service). Remove iCUE first, then those. Keep the USBXp drivers if you use a Corsair wireless (Slipstream) receiver."),

        new("ghub", "Logitech G HUB", "Logitech",
            R(@"^Logitech G HUB$"), null,
            ["lghub", "lghub_agent"],
            [
                "Headset EQ, sidetone and Blue VO!CE microphone filters",
                "Mouse DPI and button mapping",
                "Firmware updates for Logitech devices",
            ],
            "Settings saved to a device's onboard memory keep working after G HUB is gone. Check that the headset sounds right before you uninstall."),

        new("synapse", "Razer Synapse", "Razer",
            R(@"^Razer (Synapse|Central|Chroma)"), R(@"^Razer (Cortex|Chroma SDK)"),
            ["Razer Synapse", "Razer Synapse 3", "RazerAppEngine", "RzChromaSDKService"],
            [
                "Mouse DPI stages and button mapping (unless saved to onboard memory)",
                "Firmware updates for Razer devices",
            ],
            "Save your DPI and button settings to the mouse's onboard memory first; they keep working without Synapse."),

        new("armoury", "ASUS Armoury Crate", "ASUS",
            R(@"^(ARMOURY CRATE|Armoury Crate)( Service| Lite Service)?$"), R(@"^(AURA|ASUS Aura|AURA lighting effect add-on|ROG Live Service)"),
            ["ArmouryCrate", "LightingService"],
            [
                "Motherboard fan profiles (set them in the BIOS instead)",
                "ASUS driver and BIOS update notifications",
            ],
            "ASUS's own \"Armoury Crate Uninstall Tool\" removes it more completely than Windows' uninstaller, which leaves services behind."),

        new("msi", "MSI Center / Mystic Light", "MSI",
            R(@"^(MSI Center|Mystic Light|MSI Dragon Center|Dragon Center)"), null,
            ["MSI Center", "LEDKeeper2", "MysticLight"],
            ["Fan control and performance modes", "MSI driver and BIOS updates"]),

        new("gigabyte", "GIGABYTE Control Center / RGB Fusion", "Gigabyte",
            R(@"^(GIGABYTE Control Center|RGB Fusion|APP Center)"), null,
            ["GCC", "RGBFusion"],
            ["Smart Fan control from Windows (set fan curves in the BIOS instead)", "GIGABYTE driver and BIOS updates"]),

        new("nzxt", "NZXT CAM", "NZXT",
            R(@"^NZXT CAM"), null,
            ["NZXT CAM"],
            ["Fan and pump control for NZXT Kraken and controllers", "Temperature monitoring overlay"],
            "If an NZXT Kraken cools your CPU, keep CAM or set a pump curve another way first."),

        new("lianli", "Lian Li L-Connect", "Lian Li",
            R(@"^L-Connect"), null,
            ["L-Connect 3", "L-Connect"],
            ["Fan speed control for Lian Li UNI FAN hubs"]),

        new("steelseries", "SteelSeries GG", "SteelSeries",
            R(@"^SteelSeries (GG|Engine)"), null,
            ["SteelSeriesGG", "SteelSeriesEngine"],
            ["Headset Sonar EQ", "Mouse and keyboard settings"]),

        new("hyperx", "HyperX NGENUITY", "HyperX",
            R(@"^(HyperX )?NGENUITY"), null,
            ["NGENUITY"],
            ["Mouse and keyboard settings", "Firmware updates"]),

        new("coolermaster", "Cooler Master MasterPlus", "Cooler Master",
            R(@"^(MasterPlus|Cooler Master MasterPlus|MasterCTRL)"), null,
            ["MasterPlus"],
            ["Fan control for Cooler Master controllers"]),

        new("signalrgb", "SignalRGB", null,
            R(@"^SignalRGB"), null,
            ["SignalRgb", "SignalRgbLauncher"],
            [],
            "MyRGB does everything SignalRGB was doing for lighting. Running both makes them fight over every device."),
    ];

    /// <summary>
    /// Match installed programs against the catalog. Programs that merely share a publisher are
    /// ignored: Elgato Stream Deck is published by Corsair but has nothing to do with iCUE.
    /// </summary>
    public static IReadOnlyList<DetectedVendorApp> Detect(IEnumerable<InstalledProgram> installed, IEnumerable<string> runningProcesses)
    {
        var programs = installed.DistinctBy(p => p.Name).ToList();
        var running = runningProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<DetectedVendorApp>();

        foreach (var app in All)
        {
            var main = programs.FirstOrDefault(p => app.Main.IsMatch(p.Name));
            if (main is null) continue;
            var components = app.Components is null ? [] : programs.Where(p => p != main && app.Components.IsMatch(p.Name)).ToList();
            found.Add(new DetectedVendorApp(app, main, components, app.Processes.Any(running.Contains)));
        }
        return found;
    }

    /// <summary>
    /// The command to run to uninstall. MSI entries often register "/I{guid}" (which opens the
    /// repair/modify dialog); "/X{guid}" goes straight to uninstall.
    /// </summary>
    public static string? UninstallCommand(InstalledProgram program)
    {
        var cmd = program.UninstallCommand?.Trim();
        if (string.IsNullOrEmpty(cmd)) return null;
        return Regex.Replace(cmd, @"(?i)(msiexec(\.exe)?\s+)/I\s*(\{[0-9A-F-]+\})", "$1/X$3");
    }
}
