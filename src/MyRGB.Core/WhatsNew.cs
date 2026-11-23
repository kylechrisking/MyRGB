namespace MyRGB.Core;

/// <summary>
/// The short list Home shows after an update, built into the app so it works offline. Add a line for
/// each release that changes something a user would notice; the full notes stay on GitHub.
/// </summary>
public static class WhatsNew
{
    public sealed record Release(Version Version, IReadOnlyList<string> Lines);

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<Release> Releases { get; } =
    [
        new(new(0, 6, 9), ["The lights fade in and out instead of snapping: switching on or off, Lights out, and brightness changes."]),
        new(new(0, 6, 8), ["Change presets through the day: Settings > Schedule. Pick a time and a preset; add as many as you like."]),
        new(new(0, 6, 7), ["Set the brightness from the tray: right-click the icon, then Brightness."]),
        new(new(0, 6, 6), ["Lights out, in Settings: turn the lights off while Windows is locked or asleep, and every night between times you pick.",
                           "Turning the lights on in the meantime keeps them on until the next lock, sleep or night."]),
        new(new(0, 6, 5), ["After an update, this card shows what changed since the version you last used."]),
        new(new(0, 6, 4), ["If MyRGB can't save its settings, Home tells you, so changes don't quietly vanish when it closes."]),
        new(new(0, 6, 3), ["Fixed a crash when the look changed while MyRGB was running, e.g. Match Windows at sunrise.",
                           "\"Start with Windows\" always starts the installed MyRGB."]),
        new(new(0, 6, 2), ["Change the look from the tray: right-click the icon, then Appearance."]),
        new(new(0, 6, 1), ["Accounts, preset sync and Community work again, and presets sync every six hours."]),
        new(new(0, 6, 0), ["Seven looks in Settings > Appearance, plus Match Windows (Notebook by day, Chalkboard at night).",
                           "Switches that fit the look: tick boxes, a DIP switch, [x]."]),
    ];

    /// <summary>
    /// What to show to someone now on <paramref name="current"/> who last saw <paramref name="seen"/>:
    /// every release after <paramref name="seen"/> up to <paramref name="current"/>, newest first. Empty when
    /// nothing is new, or on a first install (<paramref name="seen"/> null and <paramref name="firstRun"/>), where the tour does the talking.
    /// </summary>
    public static IReadOnlyList<Release> Since(Version? seen, Version current, bool firstRun)
    {
        current = Trim(current);
        if (seen is null)
        {
            if (firstRun) return [];
            // Updated from a version that didn't remember what it showed: just this release.
            return Releases.Where(r => r.Version == current).ToList();
        }
        seen = Trim(seen);
        return Releases.Where(r => r.Version > seen && r.Version <= current).ToList();
    }

    /// <summary>Major.minor.patch only: assembly versions carry a fourth part.</summary>
    public static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
