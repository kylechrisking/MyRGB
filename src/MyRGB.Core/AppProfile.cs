namespace MyRGB.Core;

/// <summary>Switch to a preset while a program is in the foreground, e.g. your game's look while gaming.</summary>
public sealed record AppProfile(string ProcessName, string PresetId)
{
    /// <summary>Matches the start of the foreground process's name, so "cs2" or "cs2.exe" both catch "cs2".</summary>
    public bool Matches(string? foreground) =>
        !string.IsNullOrWhiteSpace(foreground) &&
        !string.IsNullOrWhiteSpace(ProcessName) &&
        foreground.StartsWith(ProcessName.Trim(), StringComparison.OrdinalIgnoreCase);
}
