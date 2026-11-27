using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyRGB.App.Services;
using MyRGB.Core.Games;
using MyRGB.Core.Localization;

namespace MyRGB.App.ViewModels;

/// <summary>
/// Settings > Game lighting: listen to Counter-Strike 2 and Dota 2, and add or remove the small file
/// each game needs before it will send anything. Nothing is written into a game folder until the
/// user presses Connect.
/// </summary>
public sealed partial class GameLightingViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;
    private GameSetup? _setup;

    public GameLightingViewModel(AppHost host)
    {
        _host = host;
        _log = host.Logs.CreateLogger<GameLightingViewModel>();
        _on = host.Settings.GameLightingOn;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateStatus();
        if (_on)
        {
            _timer.Start();
            FindGames();
        }
        UpdateStatus();
    }

    [ObservableProperty] private bool _on;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<GameRow> Games { get; } = [];

    async partial void OnOnChanged(bool value)
    {
        try { await _host.SetGameLightingAsync(value); }
        catch (Exception ex) { _log.LogWarning(ex, "Turning game lighting {State} failed", value ? "on" : "off"); }
        if (value)
        {
            _timer.Start();
            if (Games.Count == 0) FindGames();
        }
        else _timer.Stop();
        UpdateStatus();
    }

    /// <summary>Look through the Steam libraries again (after installing a game, say).</summary>
    [RelayCommand]
    private void FindGames()
    {
        try { _setup = GameSetup.ForThisPc(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Looking for Steam games failed");
            _setup = new GameSetup([]);
        }
        Games.Clear();
        foreach (var g in GsiGame.All) Games.Add(new GameRow(this, g));
    }

    internal GameSetup Setup => _setup ??= GameSetup.ForThisPc();
    internal int Port => _host.Settings.GamePort;
    internal string Token => _host.GameToken;
    internal ILogger Log => _log;

    private void UpdateStatus()
    {
        if (!On) { Status = Strings.Get("settings.games.off"); return; }
        if (_host.GameListenerError is { } error) { Status = Strings.Format("settings.games.portProblem", Port, error); return; }
        var snap = GameFeed.Current;
        long now = Environment.TickCount64;
        Status = !snap.Fresh(now)
            ? Strings.Get("settings.games.waiting")
            : snap.State.Game switch
            {
                GameKind.CounterStrike2 => Strings.Get(snap.State.InMatch ? "settings.games.csMatch" : "settings.games.csMenu"),
                GameKind.Dota2 => Strings.Get(snap.State.InMatch ? "settings.games.dotaMatch" : "settings.games.dotaMenu"),
                _ => Strings.Get("settings.games.listening"),
            };
    }
}

/// <summary>One game in Settings > Game lighting: found or not, connected or not.</summary>
public sealed partial class GameRow : ObservableObject
{
    private readonly GameLightingViewModel _owner;
    private readonly GsiGame _game;

    public GameRow(GameLightingViewModel owner, GsiGame game)
    {
        _owner = owner;
        _game = game;
        Refresh();
    }

    public string Name => _game.Name;

    /// <summary>Dota 2 ignores the file unless Steam starts it with this option.</summary>
    public string? Note => _game.Kind == GameKind.Dota2
        ? Strings.Get("settings.games.dotaNote")
        : null;

    public bool HasNote => Note is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText), nameof(Detail))]
    private bool _found;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText), nameof(Detail))]
    private bool _connected;

    [ObservableProperty] private string? _problem;

    public string ButtonText => Strings.Get(Connected ? "settings.games.disconnect" : "settings.games.connect");

    public string Detail =>
        !Found ? Strings.Get("settings.games.notFound")
        : Connected ? Strings.Get("settings.games.connected")
        : Strings.Get("settings.games.found");

    private void Refresh()
    {
        Found = _owner.Setup.FindGame(_game) is not null;
        Connected = Found && _owner.Setup.IsInstalled(_game);
    }

    [RelayCommand]
    private void Toggle()
    {
        Problem = null;
        try
        {
            if (Connected) _owner.Setup.Remove(_game);
            else if (!_owner.Setup.Install(_game, _owner.Port, _owner.Token)) Problem = Strings.Get("settings.games.folderGone");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owner.Log.LogWarning(ex, "Changing {Game}'s game state file failed", _game.Name);
            Problem = Strings.Format("settings.games.notAllowed", _owner.Setup.ConfigPath(_game), ex.Message);
        }
        Refresh();
    }
}
