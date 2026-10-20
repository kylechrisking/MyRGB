using MyRGB.Core.Presets;

namespace MyRGB.Cloud;

/// <summary>Stands in when a build has no account service configured. Everything online is unavailable.</summary>
public sealed class OfflineCloud : ICloud
{
    private static CloudException Unavailable() => new("Accounts aren't available in this version of MyRGB.");

    public bool IsConfigured => false;
    public CloudUser? User => null;
    public event EventHandler? UserChanged { add { } remove { } }

    public Task SignInAsync(string provider, CancellationToken ct) => throw Unavailable();
    public Task SignOutAsync() => Task.CompletedTask;
    public Task DeleteAccountAsync(CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<RemotePreset>> GetSyncedPresetsAsync(CancellationToken ct) => throw Unavailable();
    public Task PutSyncedPresetsAsync(IReadOnlyCollection<RemotePreset> presets, CancellationToken ct) => throw Unavailable();
    public Task<IReadOnlyList<SharedPreset>> BrowseAsync(string? search, CommunitySort sort, CancellationToken ct) => throw Unavailable();
    public Task ShareAsync(Preset preset, IReadOnlyList<string> swatch, string description, CancellationToken ct) => throw Unavailable();
    public Task DeleteSharedAsync(Guid id, CancellationToken ct) => throw Unavailable();
    public Task CountInstallAsync(Guid id, CancellationToken ct) => throw Unavailable();
    public Task ReportAsync(Guid id, string reason, CancellationToken ct) => throw Unavailable();
    public Task SendSupportMessageAsync(string subject, string body, string appVersion, CancellationToken ct) => throw Unavailable();
    public Task<SupporterStatus?> GetSupportAsync(CancellationToken ct) => Task.FromResult<SupporterStatus?>(null);
    public Task SetBadgeVisibleAsync(bool visible, CancellationToken ct) => Task.CompletedTask;
}
