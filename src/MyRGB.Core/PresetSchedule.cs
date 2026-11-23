namespace MyRGB.Core;

/// <summary>Switch to <see cref="PresetId"/> every day at <see cref="At"/> ("HH:mm").</summary>
public sealed record ScheduledPreset(string At, string PresetId)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeOnly Time => NightHours.Parse(At, new TimeOnly(0, 0));
}

/// <summary>When a daily preset schedule says to switch.</summary>
public static class PresetSchedule
{
    /// <summary>
    /// The entry whose time came round most recently, at or before <paramref name="now"/> (yesterday's
    /// last one in the early hours). Null when there are no entries.
    /// </summary>
    public static ScheduledPreset? Current(IEnumerable<ScheduledPreset> entries, DateTime now) =>
        entries.Select(e => (Entry: e, At: LastOccurrence(e.Time, now)))
               .OrderByDescending(x => x.At)
               .Select(x => x.Entry)
               .FirstOrDefault();

    /// <summary>
    /// The entry to switch to if one of the times came round after <paramref name="since"/> and up to
    /// <paramref name="now"/>: the latest such, or null when none did.
    /// </summary>
    public static ScheduledPreset? Due(IEnumerable<ScheduledPreset> entries, DateTime since, DateTime now)
    {
        if (now <= since) return null;
        var current = Current(entries, now);
        return current is not null && LastOccurrence(current.Time, now) > since ? current : null;
    }

    private static DateTime LastOccurrence(TimeOnly time, DateTime now)
    {
        var at = now.Date + time.ToTimeSpan();
        return at > now ? at.AddDays(-1) : at;
    }
}
