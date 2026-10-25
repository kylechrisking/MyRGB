using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyRGB.App.Services;
using MyRGB.Cloud;
using MyRGB.Core;

namespace MyRGB.App.ViewModels;

/// <summary>
/// The strip under the top bar when a newer release is out, and the Updates part of Settings.
/// One click downloads that release's installer, checks it is the exact file published on GitHub
/// and runs it quietly — per-user, no Windows prompts — and the installer restarts MyRGB itself.
/// </summary>
public sealed partial class UpdateViewModel(AppHost host) : ObservableObject
{
    /// <summary>Stream Deck's plugin and the RAM/SMBus task stay out of an in-app update, so it never needs a Windows prompt.</summary>
    private const string InstallerArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=\"!streamdeck,!smbus,!desktopicon\" /UPDATED";

    private readonly AppHost _host = host;
    private readonly ILogger _log = host.Logs.CreateLogger<UpdateViewModel>();

    /// <summary>What this build is; installers are built with the same number.</summary>
    public static Version CurrentVersion { get; } = ReadVersion();

    /// <summary>The release that is waiting, when there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStrip))]
    private UpdateInfo? _available;

    /// <summary>The release the strip was dismissed for; a different (newer) one shows the strip again.</summary>
    private Version? _dismissed;

    /// <summary>What the strip and Settings say while a check, download or install is under way — or why one failed.</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty] private bool _busy;

    /// <summary>0..1 while an installer downloads.</summary>
    [ObservableProperty] private double _progress;

    public bool ShowStrip => Available is not null && _dismissed != Available.Version;
    public string VersionText => $"MyRGB {CurrentVersion}";

    /// <summary>The quiet first look a little after startup, then hourly while MyRGB runs; a check that finds nothing says nothing.</summary>
    public void CheckOnStart()
    {
        if (_host.Settings.CheckForUpdates && _host.Updates is not null)
            _ = CheckAsync(notify: false);
    }

    [RelayCommand]
    private Task Check() => CheckAsync(notify: true);

    private async Task CheckAsync(bool notify)
    {
        if (_host.Updates is not { } feed || Busy) return;
        if (notify)
        {
            Busy = true;
            Status = "Checking…";
        }
        var found = await feed.FindUpdateAsync(CurrentVersion);
        Busy = false;
        if (found is { } update)
        {
            Available = update;
            Status = null;
        }
        else Status = notify ? $"MyRGB {CurrentVersion} is up to date." : null;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Available is not { } update || Busy) return;
        if (_host.Updates is not { } feed) return;

        // Without a published checksum the download can't be verified, so it never runs: hand
        // that release to the browser instead.
        if (update.InstallerSha256 is null)
        {
            Status = "This release publishes no checksum, so it opens in your browser instead.";
            AccountViewModel.OpenLink(update.ReleaseUrl.ToString());
            return;
        }

        try
        {
            Busy = true;
            Progress = 0;
            Status = "Downloading…";
            var installer = Path.Combine(Path.GetTempPath(), update.InstallerName);
            await feed.DownloadAsync(update, installer, new Progress<double>(p => Progress = p));

            Status = "Installing…";
            _host.SaveNow();
            Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, Arguments = InstallerArgs });
            QuitForInstaller();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Updating to {Version} failed", update.Version);
            Status = ex is InvalidOperationException ? ex.Message : "The download didn't complete. Check your connection and try again.";
            Busy = false;
        }
    }

    [RelayCommand]
    private void OpenNotes()
    {
        if (Available is { } update) AccountViewModel.OpenLink(update.ReleaseUrl.ToString());
    }

    [RelayCommand]
    private void Dismiss()
    {
        _dismissed = Available?.Version;
        OnPropertyChanged(nameof(ShowStrip));
    }

    /// <summary>Quit now so setup can replace MyRGB's files; it starts the new build itself (/UPDATED).</summary>
    private static void QuitForInstaller()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            (desktop.MainWindow as MainWindow)?.AllowClose = true;
            desktop.Shutdown();
        }
    }

    private static Version ReadVersion()
    {
        var v = typeof(App).Assembly.GetName().Version!;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}
