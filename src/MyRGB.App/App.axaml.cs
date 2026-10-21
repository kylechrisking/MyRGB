using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using MyRGB.App.Services;
using MyRGB.App.ViewModels;

namespace MyRGB.App;

public partial class App : Application
{
    private AppHost? _host;
    private MainWindow? _window;
    private NativeMenuItem? _powerItem;
    private NativeMenuItem? _pauseItem;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _host = new AppHost();
            var vm = new MainViewModel(_host);
            _window = new MainWindow { DataContext = vm };
            _host.ShowRequested += () => Dispatcher.UIThread.Post(ShowWindow);
            _host.Lights.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateTray);

            CreateTray(desktop);

            bool minimized = Program.StartMinimized || _host.Settings.StartMinimized;
            if (!minimized) _window.Show();

            desktop.ShutdownRequested += (_, _) => _window.AllowClose = true;
            desktop.Exit += (_, _) => _host.Shutdown();
            WatchForHandover(desktop);

            _ = _host.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Close when MyRGB starts as administrator, so that one gets the devices.</summary>
    private void WatchForHandover(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Program.Handover is not { } handover) return;
        new Thread(() =>
        {
            handover.WaitOne();
            Dispatcher.UIThread.Post(() =>
            {
                _window!.AllowClose = true;
                desktop.Shutdown();
            });
        }) { IsBackground = true, Name = "Handover" }.Start();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Open MyRGB");
        open.Click += (_, _) => ShowWindow();

        _powerItem = new NativeMenuItem("Turn lights off");
        _powerItem.Click += (_, _) => _host!.Lights.SetEnabled(!_host.Lights.State.Enabled);

        var next = new NativeMenuItem("Next preset");
        next.Click += (_, _) => _host!.Lights.StepPreset(1);

        var shuffle = new NativeMenuItem("Random preset");
        shuffle.Click += (_, _) => _host!.Lights.Shuffle();

        _pauseItem = new NativeMenuItem("Pause animation");
        _pauseItem.Click += (_, _) => _host!.Lights.SetPaused(!_host.Lights.State.Paused);

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            _window!.AllowClose = true;
            desktop.Shutdown();
        };

        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[] { open, _powerItem, next, shuffle, _pauseItem, new NativeMenuItemSeparator(), exit })
            menu.Items.Add(item);

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MyRGB/Assets/icon.ico"))),
            ToolTipText = "MyRGB",
            Menu = menu,
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private void UpdateTray()
    {
        if (_powerItem is not null && _host is not null)
            _powerItem.Header = _host.Lights.State.Enabled ? "Turn lights off" : "Turn lights on";
        if (_pauseItem is not null && _host is not null)
            _pauseItem.Header = _host.Lights.State.Paused ? "Resume animation" : "Pause animation";
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
