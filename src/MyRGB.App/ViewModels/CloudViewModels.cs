using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;
using MyRGB.Cloud;
using MyRGB.Core;
using MyRGB.Core.Effects;
using MyRGB.Core.Presets;

namespace MyRGB.App.ViewModels;

public sealed record ProviderButton(string Name, IAsyncRelayCommand Command)
{
    public string Label => $"Continue with {Name}";
}

public sealed record LinkButton(string Label, IRelayCommand Command);

/// <summary>Signing in and out, preset sync, and deleting the account. Lives in Settings › Account.</summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly AppHost _host;
    private CancellationTokenSource? _signIn;

    public AccountViewModel(AppHost host)
    {
        _host = host;
        _syncPresets = host.Settings.SyncPresets;
        host.Cloud.UserChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        host.Sync.Synced += (_, error) => Dispatcher.UIThread.Post(() =>
        {
            SyncStatus = error ?? $"Synced {DateTime.Now:t}";
        });
        Providers = SignInProvider.All.Select(p => new ProviderButton(p.Name, new AsyncRelayCommand(() => SignIn(p.Id)))).ToList();
        _ = LoadSupport();
    }

    public bool IsConfigured => _host.Cloud.IsConfigured;
    public bool IsSignedIn => _host.Cloud.User is not null;
    public bool IsSignedOut => IsConfigured && !IsSignedIn;
    public string DisplayName => _host.Cloud.User?.DisplayName ?? "";
    public string Email => _host.Cloud.User?.Email ?? "";
    public string Initial => DisplayName.Length > 0 ? char.ToUpperInvariant(DisplayName[0]).ToString() : "?";
    public string ProviderText => _host.Cloud.User?.Provider is { } p ? $"Signed in with {char.ToUpperInvariant(p[0])}{p[1..]}" : "";
    public IReadOnlyList<ProviderButton> Providers { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _syncStatus = "";
    [ObservableProperty] private bool _syncPresets;
    [ObservableProperty] private bool _confirmingDelete;

    partial void OnSyncPresetsChanged(bool value)
    {
        _host.Settings.SyncPresets = value;
        _host.ScheduleSave();
        if (value) _host.ScheduleSync(TimeSpan.Zero);
    }

    private async Task SignIn(string provider)
    {
        _signIn?.Cancel();
        _signIn = new CancellationTokenSource();
        IsBusy = true;
        Status = "Finish signing in in your browser…";
        try
        {
            await _host.Cloud.SignInAsync(provider, _signIn.Token);
            Status = "";
        }
        catch (OperationCanceledException) { Status = ""; }
        catch (CloudException ex) { Status = ex.Message; }
        catch (HttpRequestException) { Status = "Couldn't reach the MyRGB service. Check your internet connection."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        _signIn?.Cancel();
        IsBusy = false;
        Status = "";
    }

    [RelayCommand]
    private async Task SignOut()
    {
        await _host.Cloud.SignOutAsync();
        Status = "Signed out. Your presets stay on this PC.";
    }

    [RelayCommand]
    private async Task SyncNow()
    {
        SyncStatus = "Syncing…";
        await _host.Sync.SyncAsync();
    }

    [RelayCommand] private void AskDelete() => ConfirmingDelete = true;
    [RelayCommand] private void CancelDelete() => ConfirmingDelete = false;

    [RelayCommand]
    private async Task DeleteAccount()
    {
        ConfirmingDelete = false;
        try
        {
            await _host.Cloud.DeleteAccountAsync(CancellationToken.None);
            Status = "Your account and everything stored with it have been deleted. Your presets stay on this PC.";
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            Status = ex is CloudException ? ex.Message : "Couldn't reach the MyRGB service. Please try again.";
        }
    }

    private void Refresh()
    {
        foreach (var name in new[] { nameof(IsSignedIn), nameof(IsSignedOut), nameof(DisplayName), nameof(Email), nameof(Initial), nameof(ProviderText) })
            OnPropertyChanged(name);
        SignedInChanged?.Invoke();
        _ = LoadSupport();
    }

    // ---- Supporting MyRGB ----

    public IReadOnlyList<LinkButton> SupportLinks { get; } = Funding.Links
        .Select(l => new LinkButton($"Support on {l.Name}", new RelayCommand(() => OpenLink(l.Url))))
        .ToList();
    public bool HasSupportLinks => SupportLinks.Count > 0;

    [ObservableProperty] private bool _isSupporter;
    [ObservableProperty] private string _supporterText = "";
    [ObservableProperty] private bool _showBadge = true;
    private bool _loadingSupport;

    public async Task LoadSupport()
    {
        SupporterStatus? status = null;
        try
        {
            if (_host.Cloud.User is not null) status = await _host.Cloud.GetSupportAsync(CancellationToken.None);
        }
        catch (HttpRequestException) { /* offline: show the free tier until the next sign-in */ }
        _loadingSupport = true;
        IsSupporter = status?.Active == true;
        ShowBadge = status?.ShowBadge ?? true;
        SupporterText = status switch
        {
            { Active: true, Until: null } => "Thank you for supporting MyRGB.",
            { Active: true, Until: { } until } => $"Thank you for supporting MyRGB. Your supporter perks run until {until.LocalDateTime:d}.",
            _ => "",
        };
        _loadingSupport = false;
    }

    partial void OnShowBadgeChanged(bool value)
    {
        if (_loadingSupport) return;
        _ = _host.Cloud.SetBadgeVisibleAsync(value, CancellationToken.None).ContinueWith(
            t => Dispatcher.UIThread.Post(() => Status = "Couldn't save your badge setting. Please try again."),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    internal static void OpenLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { /* no browser registered */ }
    }

    /// <summary>Lets other pages (Community, Contact) update their signed-in-only parts.</summary>
    public event Action? SignedInChanged;
}

/// <summary>The Community page: browse, install, share and report presets.</summary>
public sealed partial class CommunityViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly AccountViewModel _account;
    private CancellationTokenSource? _searchDebounce;

    public CommunityViewModel(AppHost host, AccountViewModel account)
    {
        _host = host;
        _account = account;
        account.SignedInChanged += () =>
        {
            OnPropertyChanged(nameof(IsSignedIn));
            RefreshShareable();
            _ = Load();
        };
        host.Presets.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshShareable);
        RefreshShareable();
    }

    public ObservableCollection<SharedPresetViewModel> Items { get; } = [];
    public ObservableCollection<Preset> Shareable { get; } = [];
    public IReadOnlyList<string> SortOptions { get; } = ["Most popular", "Newest"];
    public bool IsSignedIn => _host.Cloud.User is not null;
    public bool HasShareable => Shareable.Count > 0;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sort = "Most popular";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _loadedOnce;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShareCommand))]
    private Preset? _toShare;

    [ObservableProperty] private string _shareDescription = "";
    [ObservableProperty] private string _shareStatus = "";

    public bool IsEmpty => LoadedOnce && Items.Count == 0 && !IsLoading;

    partial void OnSearchChanged(string value) => Debounced();
    partial void OnSortChanged(string value) => _ = Load();

    private void Debounced()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _searchDebounce, cts)?.Cancel();
        _ = Task.Delay(400, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => _ = Load()); }, TaskScheduler.Default);
    }

    [RelayCommand]
    public async Task Load()
    {
        if (!_host.Cloud.IsConfigured) return;
        IsLoading = true;
        Status = "";
        try
        {
            var sort = Sort == "Newest" ? CommunitySort.Newest : CommunitySort.Popular;
            var found = await _host.Cloud.BrowseAsync(Search, sort, CancellationToken.None);
            Items.Clear();
            foreach (var s in found) Items.Add(new SharedPresetViewModel(s, this, _host.Cloud.User?.Id == s.AuthorId, _host.Effects.Find(s.EffectId)));
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or TaskCanceledException)
        {
            Status = ex is CloudException ? ex.Message : "Couldn't reach the MyRGB service. Check your internet connection.";
        }
        finally
        {
            IsLoading = false;
            LoadedOnce = true;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    internal async Task Install(SharedPresetViewModel item)
    {
        var copy = item.Shared.Data with
        {
            Id = PresetLibrary.NewId(item.Shared.Name),
            Name = item.Shared.Name,
            BuiltIn = false,
        };
        _host.Presets.Save(copy);
        _host.Lights.ActivatePreset(copy.Id);
        item.Status = "Added to your presets and playing now.";
        if (IsSignedIn)
        {
            try { await _host.Cloud.CountInstallAsync(item.Shared.Id, CancellationToken.None); }
            catch (Exception ex) when (ex is CloudException or HttpRequestException) { } // the count is a nicety
        }
    }

    internal async Task Remove(SharedPresetViewModel item)
    {
        try
        {
            await _host.Cloud.DeleteSharedAsync(item.Shared.Id, CancellationToken.None);
            Items.Remove(item);
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException) { item.Status = ex.Message; }
    }

    internal async Task Report(SharedPresetViewModel item, string reason)
    {
        try
        {
            await _host.Cloud.ReportAsync(item.Shared.Id, reason, CancellationToken.None);
            item.Status = "Thanks. Root Labs will take a look.";
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException) { item.Status = ex.Message; }
    }

    private bool CanShare() => ToShare is not null && IsSignedIn;

    [RelayCommand(CanExecute = nameof(CanShare))]
    private async Task Share()
    {
        if (ToShare is not { } preset) return;
        ShareStatus = "Sharing…";
        try
        {
            var swatch = PresetLibrary.Swatch(preset, _host.Effects).Select(c => c.ToHex()).ToArray();
            await _host.Cloud.ShareAsync(preset, swatch, ShareDescription, CancellationToken.None);
            ShareStatus = $"\"{preset.Name}\" is now in the community gallery.";
            ShareDescription = "";
            await Load();
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            ShareStatus = ex is CloudException ? ex.Message : "Couldn't reach the MyRGB service.";
        }
    }

    private void RefreshShareable()
    {
        var keep = ToShare?.Id;
        Shareable.Clear();
        foreach (var p in _host.Presets.UserPresets) Shareable.Add(p);
        ToShare = Shareable.FirstOrDefault(p => p.Id == keep) ?? Shareable.FirstOrDefault();
        OnPropertyChanged(nameof(HasShareable));
        ShareCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class SharedPresetViewModel(SharedPreset shared, CommunityViewModel owner, bool isMine, EffectDefinition? effect) : ObservableObject
{
    public SharedPreset Shared => shared;
    public string Name => shared.Name;
    public string Author => $"by {shared.AuthorName}";
    public bool AuthorSupporter => shared.AuthorSupporter;
    public string Description => shared.Description;
    public bool HasDescription => shared.Description.Length > 0;
    public string Downloads => shared.Downloads == 1 ? "1 install" : $"{shared.Downloads} installs";
    public bool IsMine => isMine;
    public bool CanReport => !isMine;
    public bool Supported => effect is not null;
    public string? Unsupported => Supported ? null : "Needs a newer version of MyRGB";

    /// <summary>For the live preview tile; a preset whose effect this version lacks shows its colors instead.</summary>
    public EffectDefinition? Effect => effect;
    public IReadOnlyDictionary<string, string> Settings => shared.Data.Settings;

    public IBrush Swatch { get; } = MakeBrush(shared.Swatch);

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _reporting;
    [ObservableProperty] private string _reportReason = "";

    [RelayCommand(CanExecute = nameof(Supported))] private Task Install() => owner.Install(this);
    [RelayCommand] private Task Remove() => owner.Remove(this);
    [RelayCommand] private void AskReport() => Reporting = true;
    [RelayCommand] private void CancelReport() => Reporting = false;

    [RelayCommand]
    private async Task SendReport()
    {
        Reporting = false;
        await owner.Report(this, string.IsNullOrWhiteSpace(ReportReason) ? "Inappropriate" : ReportReason);
    }

    private static IBrush MakeBrush(IReadOnlyList<string> hex)
    {
        var colors = hex.Select(h => Rgb.TryParse(h, out var c) ? Color.FromRgb(c.R, c.G, c.B) : Colors.Black).ToList();
        if (colors.Count == 0) return Brushes.Black;
        if (colors.Count == 1) return new SolidColorBrush(colors[0]);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        for (int i = 0; i < colors.Count; i++) brush.GradientStops.Add(new GradientStop(colors[i], i / (double)(colors.Count - 1)));
        return brush;
    }
}

/// <summary>Contacting Root Labs. Requires an account, so the team can reply and the form can't be spammed.</summary>
public sealed partial class ContactViewModel : ObservableObject
{
    private readonly AppHost _host;

    public ContactViewModel(AppHost host, AccountViewModel account)
    {
        _host = host;
        account.SignedInChanged += () =>
        {
            OnPropertyChanged(nameof(IsSignedIn));
            SendCommand.NotifyCanExecuteChanged();
        };
    }

    public bool IsSignedIn => _host.Cloud.User is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _subject = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _message = "";

    [ObservableProperty] private string _status = "";

    private bool CanSend() => IsSignedIn && Subject.Trim().Length > 0 && Message.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        Status = "Sending…";
        try
        {
            string version = typeof(ContactViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
            await _host.Cloud.SendSupportMessageAsync(Subject, Message, version, CancellationToken.None);
            Subject = "";
            Message = "";
            Status = "Sent. Root Labs will reply to the email on your account.";
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException)
        {
            Status = ex is CloudException ? ex.Message : "Couldn't reach the MyRGB service. Please try again.";
        }
    }
}
