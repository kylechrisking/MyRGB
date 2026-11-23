namespace MyRGB.Core;

/// <summary>A nightly stretch of time, e.g. 23:00 to 07:00, which may run past midnight.</summary>
public readonly record struct NightHours(TimeOnly From, TimeOnly To)
{
    /// <summary>True when <paramref name="now"/> falls inside: from <see cref="From"/> up to, not including, <see cref="To"/>.</summary>
    public bool Contains(TimeOnly now) =>
        From == To ? false
        : From < To ? now >= From && now < To
        : now >= From || now < To;

    /// <summary>Every half hour of the day, for pickers: "00:00" … "23:30".</summary>
    public static IReadOnlyList<string> Choices { get; } =
        Enumerable.Range(0, 48).Select(i => new TimeOnly(i / 2, i % 2 * 30).ToString("HH:mm")).ToList();

    public static TimeOnly Parse(string? text, TimeOnly fallback) =>
        TimeOnly.TryParseExact(text, "HH:mm", out var t) ? t : fallback;
}
