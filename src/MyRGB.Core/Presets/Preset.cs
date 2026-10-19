using System.Text.Json.Serialization;

namespace MyRGB.Core.Presets;

/// <summary>
/// A saved lighting look: one effect painted across the whole canvas, plus optional per-device
/// overrides (a different effect on one device, or that device turned off).
/// </summary>
public sealed record Preset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string EffectId { get; init; }
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DeviceOverride> Overrides { get; init; } = [];

    /// <summary>Shipped with the app. Built-ins are read-only; editing one saves a user copy.</summary>
    [JsonIgnore]
    public bool BuiltIn { get; init; }
}

public sealed record DeviceOverride
{
    public required string DeviceId { get; init; }

    /// <summary>True to keep this device dark while the preset is active.</summary>
    public bool Off { get; init; }

    /// <summary>When set, this device runs its own effect instead of the preset's main one.</summary>
    public string? EffectId { get; init; }

    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
