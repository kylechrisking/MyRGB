namespace MyRGB.Core;

/// <summary>A preset that also sets the monitors to a brightness, 0..100.</summary>
public sealed record PresetMonitorLevel(string PresetId, int Level);

/// <summary>What the monitor options say; see <see cref="MonitorLevels.Target"/>.</summary>
/// <param name="FollowLights">Turn the monitors down to <paramref name="OffLevel"/> while the lights are off.</param>
/// <param name="NightDim">Turn them down to <paramref name="NightLevel"/> during night hours.</param>
/// <param name="PresetLevels">Presets that set their own monitor level.</param>
public sealed record MonitorRules(
    bool FollowLights = false, int OffLevel = 10,
    bool NightDim = false, int NightLevel = 25,
    IReadOnlyList<PresetMonitorLevel>? PresetLevels = null);

public static class MonitorLevels
{
    /// <summary>The fixed levels the tray and Settings offer.</summary>
    public static readonly int[] Steps = [100, 75, 50, 25, 10, 0];

    /// <summary>
    /// The brightness the monitors should be at now. Night beats lights-off, which beats the active
    /// preset's own level, which beats the level picked by hand (<paramref name="manual"/>).
    /// </summary>
    public static int Target(MonitorRules rules, int manual, bool lightsOn, bool night, string? presetId)
    {
        if (rules.NightDim && night) return Clamp(rules.NightLevel);
        if (rules.FollowLights && !lightsOn) return Clamp(rules.OffLevel);
        if (presetId is not null && rules.PresetLevels?.FirstOrDefault(p => p.PresetId == presetId) is { } p) return Clamp(p.Level);
        return Clamp(manual);
    }

    public static int Clamp(int level) => Math.Clamp(level, 0, 100);
}

/// <summary>Monitor brightness as the local API sees it (Twinkle Tray in the app).</summary>
public interface IMonitorBrightness
{
    /// <summary>False when the program that drives the monitors isn't installed or is switched off in Settings.</summary>
    bool Available { get; }

    /// <summary>0..100, what MyRGB last set or read.</summary>
    int Level { get; }

    /// <summary>The monitors' names, as last read.</summary>
    IReadOnlyList<string> Monitors { get; }

    /// <summary>Set every monitor, the same as the slider: stays until the next rule change.</summary>
    void SetLevel(int level);
}
