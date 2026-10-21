using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;

namespace MyRGB.App.ViewModels;

/// <summary>
/// The small "buy the devs a coffee" note in the corner of the window. It waits until MyRGB has
/// been used on a week's worth of different days, never shows to supporters or during the tour,
/// and once answered stays away for two months ("Don't ask again" for good).
/// </summary>
public sealed partial class SupportNudgeViewModel : ObservableObject
{
    public const int DaysBeforeAsking = 7;
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(60);

    private readonly AppHost _host;
    private readonly AccountViewModel _account;

    [ObservableProperty] private bool _isOpen;

    public SupportNudgeViewModel(AppHost host, AccountViewModel account)
    {
        _host = host;
        _account = account;
        _account.PropertyChanged += OnAccountChanged;
    }

    private static string? Link => Funding.BuyMeACoffeeUrl ?? Funding.Links.Select(l => l.Url).FirstOrDefault();

    /// <summary>Count today as a day of use, then decide whether to show the note. UI thread.</summary>
    public void Check(DateTime now)
    {
        var s = _host.Settings;
        var today = DateOnly.FromDateTime(now);
        if (s.LastUsedDay != today)
        {
            s.LastUsedDay = today;
            s.DaysUsed++;
            _host.ScheduleSave();
        }
        IsOpen = ShouldAsk(s.DaysUsed, s.SupportAskedAt, s.SupportAskOff, now)
            && s.HasSeenWelcome && !_account.IsSupporter && Link is not null;
    }

    internal static bool ShouldAsk(int daysUsed, DateTime? askedAt, bool off, DateTime now) =>
        !off && daysUsed >= DaysBeforeAsking && (askedAt is not { } at || now - at >= AskAgainAfter);

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountViewModel.IsSupporter) && _account.IsSupporter) IsOpen = false;
    }

    [RelayCommand]
    private void Support()
    {
        if (Link is { } url) AccountViewModel.OpenLink(url);
        Answer();
    }

    [RelayCommand]
    private void Later() => Answer();

    [RelayCommand]
    private void Never()
    {
        _host.Settings.SupportAskOff = true;
        Answer();
    }

    private void Answer()
    {
        _host.Settings.SupportAskedAt = DateTime.Now;
        _host.ScheduleSave();
        IsOpen = false;
    }
}
