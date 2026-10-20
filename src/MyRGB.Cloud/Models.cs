using MyRGB.Core.Presets;

namespace MyRGB.Cloud;

/// <summary>Where the account service lives. Both values are public by design; neither is a secret.</summary>
/// <param name="Url">The Supabase project URL, e.g. https://abcd.supabase.co.</param>
/// <param name="PublishableKey">The project's publishable (anon) key. Row-level security decides what it can do.</param>
public sealed record CloudConfig(string Url, string PublishableKey)
{
    /// <summary>
    /// Release builds carry the values in assembly metadata (set from CI secrets at build time);
    /// MYRGB_SUPABASE_URL / MYRGB_SUPABASE_KEY override them for development. Null when neither is
    /// set, in which case MyRGB runs without account features.
    /// </summary>
    public static CloudConfig? Load()
    {
        string? url = Environment.GetEnvironmentVariable("MYRGB_SUPABASE_URL") ?? BuildValue("SupabaseUrl");
        string? key = Environment.GetEnvironmentVariable("MYRGB_SUPABASE_KEY") ?? BuildValue("SupabaseKey");
        return string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key) ? null : new CloudConfig(url.TrimEnd('/'), key);
    }

    private static string? BuildValue(string name) =>
        typeof(CloudConfig).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == name)?.Value;
}

public sealed record CloudUser(Guid Id, string DisplayName, string? Email, string? Provider);

/// <summary>A preset in the user's private synced copy.</summary>
public sealed record RemotePreset(string PresetId, Preset? Data, bool Deleted, DateTimeOffset UpdatedAt);

/// <summary>A preset someone shared with the community.</summary>
public sealed record SharedPreset(
    Guid Id, string Name, string Description, string EffectId, Preset Data,
    IReadOnlyList<string> Swatch, int Downloads, DateTimeOffset CreatedAt, Guid AuthorId, string AuthorName,
    bool AuthorSupporter = false);

/// <summary>Whether the signed-in user supports MyRGB, until when (null: no end date), and if their badge shows.</summary>
public sealed record SupporterStatus(bool Active, DateTimeOffset? Until, bool ShowBadge);

public enum CommunitySort { Popular, Newest }

/// <summary>Sign-in providers MyRGB offers. The id is the one Supabase expects.</summary>
public sealed record SignInProvider(string Id, string Name)
{
    public static readonly IReadOnlyList<SignInProvider> All =
    [
        new("google", "Google"),
        new("github", "GitHub"),
        new("discord", "Discord"),
    ];
}

/// <summary>A server-side refusal with a message fit to show the user.</summary>
public sealed class CloudException(string message, Exception? inner = null) : Exception(message, inner);
