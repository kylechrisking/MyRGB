using MyRGB.Core.Presets;

namespace MyRGB.Cloud;

/// <summary>Everything MyRGB does online. Every feature is optional: the app works fully offline.</summary>
public interface ICloud
{
    /// <summary>False in builds without a configured backend; the UI hides account features then.</summary>
    bool IsConfigured { get; }

    CloudUser? User { get; }

    /// <summary>Raised (any thread) on sign-in, sign-out and session expiry.</summary>
    event EventHandler? UserChanged;

    /// <summary>Opens the browser to sign in and completes when the user comes back.</summary>
    Task SignInAsync(string provider, CancellationToken ct);

    Task SignOutAsync();

    /// <summary>Permanently deletes the account and everything stored with it.</summary>
    Task DeleteAccountAsync(CancellationToken ct);

    Task<IReadOnlyList<RemotePreset>> GetSyncedPresetsAsync(CancellationToken ct);
    Task PutSyncedPresetsAsync(IReadOnlyCollection<RemotePreset> presets, CancellationToken ct);

    Task<IReadOnlyList<SharedPreset>> BrowseAsync(string? search, CommunitySort sort, CancellationToken ct);
    Task ShareAsync(Preset preset, IReadOnlyList<string> swatch, string description, CancellationToken ct);
    Task DeleteSharedAsync(Guid id, CancellationToken ct);
    Task CountInstallAsync(Guid id, CancellationToken ct);
    Task ReportAsync(Guid id, string reason, CancellationToken ct);

    /// <summary>The signed-in user's supporter status; null when signed out or the service doesn't track it.</summary>
    Task<SupporterStatus?> GetSupportAsync(CancellationToken ct);

    /// <summary>Show or hide the supporter badge on presets the user shares.</summary>
    Task SetBadgeVisibleAsync(bool visible, CancellationToken ct);
}
