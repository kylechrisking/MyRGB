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
        new(new(0, 8, 4), ["Uninstalling MyRGB now also removes the temperature and fan helper if you turned it on, instead of leaving it to start at every sign-in."]),
        new(new(0, 8, 3), ["Safer settings: MyRGB keeps a daily backup of its settings for a week and restores the newest one if the file is ever damaged. A PC that lost its presets now gets them back from your account instead of deleting them on your other PCs."]),
        new(new(0, 8, 2), ["RAM lighting no longer depends on VBScript, which Windows is removing: OpenRGB now starts straight from its task, in the background. If yours still uses the old way, Home offers to update it with one Windows prompt."]),
        new(new(0, 8, 1), ["MyRGB no longer asks to update the temperature and fan helper after every update, only when the helper itself changes. The \"Restart as administrator\" button is gone: the helper does that job with one permission prompt."]),
        new(new(0, 8, 0), ["A tidier window: Layout is part of the Studio (switch on Arrange devices), Cooling is part of Monitoring, and Vendor apps sit in Settings, only while one is installed.",
                           "Home's Lighting card has the preset buttons; the music-player bar is gone.",
                           "Devices can show as a grid, with a drawing of each device lit the way it is right now.",
                           "Vote community presets up or down, and suggest your favourites for MyRGB's built-in presets.",
                           "Looks: Original is the default again, sharpened; Match Windows now uses plain Light and Dark; Bench and Chalkboard were retired."]),
        new(new(0, 7, 9), ["A wireless headset that was asleep when MyRGB started (the G733, for one) now lights up by itself once you wake it."]),
        new(new(0, 7, 8), ["Scan again (on Devices) now asks OpenRGB to look for hardware too, so a wireless headset or mouse switched on after OpenRGB started turns up without a restart."]),
        new(new(0, 7, 7), ["Safer RAM lighting: the task that runs OpenRGB as administrator could be pointed at a copy in your own folder. If yours was, Home offers to fix it with one Windows prompt."]),
        new(new(0, 7, 6), ["More keyboard shortcuts in Settings > Global hotkeys: previous preset, lights brighter or dimmer, and screens brighter or dimmer. Settings now says if another app already has one."]),
        new(new(0, 7, 5), ["Match your monitors in Settings > Monitors: give each one its own darkest and brightest, and the Screens slider runs between them."]),
        new(new(0, 7, 4), ["Monitor brightness no longer needs Twinkle Tray: MyRGB talks to your monitors itself, over DDC/CI. Switch it on in Settings > Monitors."]),
        new(new(0, 7, 3), ["A Screen Brightness key for Stream Deck: brighter, dimmer or a set level for your monitors, or turn the dial on a Stream Deck +."]),
        new(new(0, 7, 2), ["Monitor brightness in Settings > Monitors: a Screens slider up top and in the tray, dim the screens with the lights or at night, and a level per preset."]),
        new(new(0, 7, 1), ["Turn the lights off for 15 minutes to 2 hours from the tray; they come back on by themselves."]),
        new(new(0, 7, 0), ["Switching presets blends from one look to the next instead of cutting: from the app, the tray, a schedule, an app profile or a Stream Deck."]),
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
