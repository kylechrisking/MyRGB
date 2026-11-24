namespace MyRGB.Core;

/// <summary>A preset that also sets the monitors to a brightness, 0..100.</summary>
public sealed record PresetMonitorLevel(string PresetId, int Level);

/// <summary>One monitor's own span for the shared level, so it can be matched to the others. By EDID name.</summary>
public sealed record MonitorRange(string Name, int Darkest = 0, int Brightest = 100);

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

    /// <summary>
    /// What one monitor should be sent for the shared <paramref name="level"/>: 0..100 is spread over the
    /// monitor's own darkest..brightest, so two panels that differ can be matched by eye.
    /// </summary>
    public static int Map(int level, MonitorRange? range)
    {
        level = Clamp(level);
        if (range is null) return level;
        int lo = Clamp(range.Darkest), hi = Clamp(range.Brightest);
        if (hi <= lo) return lo;
        return lo + (int)Math.Round(level * (hi - lo) / 100.0);
    }

    /// <summary>The shared level a monitor reading <paramref name="raw"/> stands for; the reverse of <see cref="Map"/>.</summary>
    public static int Unmap(int raw, MonitorRange? range)
    {
        raw = Clamp(raw);
        if (range is null) return raw;
        int lo = Clamp(range.Darkest), hi = Clamp(range.Brightest);
        if (hi <= lo) return 100;
        return Clamp((int)Math.Round((raw - lo) * 100.0 / (hi - lo)));
    }

    public static int Clamp(int level) => Math.Clamp(level, 0, 100);
}

/// <summary>Monitor brightness as the local API sees it (DDC/CI in the app).</summary>
public interface IMonitorBrightness
{
    /// <summary>False when it's switched off in Settings or this PC can't reach monitors.</summary>
    bool Available { get; }

    /// <summary>0..100, what MyRGB last set or read.</summary>
    int Level { get; }

    /// <summary>The monitors' names, as last read.</summary>
    IReadOnlyList<string> Monitors { get; }

    /// <summary>Set every monitor, the same as the slider: stays until the next rule change.</summary>
    void SetLevel(int level);
}
