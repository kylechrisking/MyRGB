using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;
using MyRGB.App.Services.Wallpaper;
using MyRGB.Core.Effects;
using MyRGB.Core.Wallpaper;
using MyRGB.Core.Presets;

namespace MyRGB.App.ViewModels;

/// <summary>One preset offered as a wallpaper scene.</summary>
public sealed partial class WallpaperSceneViewModel(Preset preset, EffectDefinition? effect, WallpapersViewModel owner) : ObservableObject
{
    public string Id => preset.Id;
    public string Name => preset.Name;
    public EffectDefinition? Effect => effect;
    public IReadOnlyDictionary<string, string> Settings => preset.Settings;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand] private void Choose() => owner.ChooseScene(Id);
}

/// <summary>
/// The Wallpapers page: MyRGB's moving wallpaper behind the desktop icons, what it plays, and lights
/// that take their colors from it.
/// </summary>
public sealed partial class WallpapersViewModel : ObservableObject
{
    private readonly AppHost _host;

    public WallpapersViewModel(AppHost host)
    {
        _host = host;
        _on = host.Settings.WallpaperOn;
        _source = host.Settings.WallpaperSource;
        _picture = host.Settings.WallpaperPicture;
        _video = host.Settings.WallpaperVideo;
        _videoUseGpu = host.Settings.VideoUseGpu;
        host.Wallpaper.StatusChanged += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(DecodingNote)));
        RefreshScenes();
        host.Presets.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshScenes);
        host.Lights.StateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(LightsMatch));
            OnPropertyChanged(nameof(LightsLabel));
            OnPropertyChanged(nameof(Status));
        });
        host.Wallpaper.Changed += () => Dispatcher.UIThread.Post(Sync);
    }

    /// <summary>Pick up a change made elsewhere (the API), without writing it back.</summary>
    private void Sync()
    {
        if (_on != _host.Settings.WallpaperOn) { _on = _host.Settings.WallpaperOn; OnPropertyChanged(nameof(On)); }
        if (_source != _host.Settings.WallpaperSource)
        {
            _source = _host.Settings.WallpaperSource;
            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(FromLights));
            OnPropertyChanged(nameof(FromPreset));
            OnPropertyChanged(nameof(FromPicture));
            OnPropertyChanged(nameof(FromVideo));
        }
        foreach (var s in Scenes) s.IsSelected = s.Id == _host.Settings.WallpaperPresetId;
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>Follow the desktop windows once the app has made them.</summary>
    public void Attach(WallpaperDisplay display) =>
        display.StatusChanged += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Status)));

    public List<WallpaperSceneViewModel> Scenes { get; private set; } = [];

    [ObservableProperty] private bool _on;
    [ObservableProperty] private WallpaperSource _source;
    [ObservableProperty] private string? _picture;

    public bool FromLights { get => Source == WallpaperSource.Lights; set { if (value) Source = WallpaperSource.Lights; } }
    public bool FromPreset { get => Source == WallpaperSource.Preset; set { if (value) Source = WallpaperSource.Preset; } }
    public bool FromPicture { get => Source == WallpaperSource.Picture; set { if (value) Source = WallpaperSource.Picture; } }
    public bool FromVideo { get => Source == WallpaperSource.Video; set { if (value) Source = WallpaperSource.Video; } }

    public string PictureName => Picture is { } p ? Path.GetFileName(p) : "No picture chosen";

    [ObservableProperty] private string? _video;

    public string VideoName => Video is { } v ? Path.GetFileName(v) : "No video chosen";

    /// <summary>Settings: video wallpapers decoded by the graphics card.</summary>
    [ObservableProperty] private bool _videoUseGpu;

    partial void OnVideoUseGpuChanged(bool value)
    {
        _host.Settings.VideoUseGpu = value;
        _host.ScheduleSave();
        OnPropertyChanged(nameof(DecodingNote)); // the playing video reopens with the new choice on its next frame
    }

    /// <summary>How the video wallpaper is being decoded right now, in a sentence.</summary>
    public string DecodingNote
    {
        get
        {
            var w = _host.Wallpaper;
            if (!On || Source != WallpaperSource.Video || w.VideoSize.Width == 0)
                return VideoUseGpu ? "No video is playing right now. When one is, your graphics card will play it." : "No video is playing right now. When one is, your processor will play it.";
            if (w.VideoOnGpu) return "Right now: your graphics card is playing the video.";
            return VideoUseGpu && w.VideoGpuProblem is { } why
                ? $"Right now: your processor is playing the video, because the graphics card couldn't ({why.TrimEnd('.')})."
                : "Right now: your processor is playing the video.";
        }
    }

    /// <summary>True while the lights play Match Wallpaper.</summary>
    public bool LightsMatch => _host.Engine.ActivePreset?.EffectId == WallpaperFeed.EffectId;

    public string LightsLabel => LightsMatch
        ? "Your lights are taking their colors from the wallpaper."
        : $"Your lights are playing {_host.Engine.ActivePreset?.Name ?? "nothing"}.";

    /// <summary>One line on what the desktop shows right now.</summary>
    public string Status
    {
        get
        {
            if (!On) return LightsMatch
                ? "MyRGB's wallpaper is off. Your lights are matching Windows' own wallpaper picture."
                : "MyRGB's wallpaper is off. Windows shows its own.";
            if (App.Wallpaper?.Problem is { } p) return p;
            if (_host.Wallpaper.Problem is { } q) return q;
            int screens = App.Wallpaper is { Showing: true } ? Math.Max(1, CountScreens()) : 0;
            string what = Source switch
            {
                WallpaperSource.Lights => $"whatever your lights play ({_host.Engine.ActivePreset?.Name ?? "nothing yet"})",
                WallpaperSource.Picture => PictureName,
                WallpaperSource.Video => VideoName,
                _ => Scenes.FirstOrDefault(s => s.IsSelected)?.Name ?? "a preset",
            };
            return screens > 0 ? $"Showing {what} behind your desktop icons." : $"Set to show {what}.";
        }
    }

    private static int CountScreens() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Screens.All.Count ?? 1;

    partial void OnOnChanged(bool value)
    {
        _host.Settings.WallpaperOn = value;
        Apply();
    }

    partial void OnSourceChanged(WallpaperSource value)
    {
        _host.Settings.WallpaperSource = value;
        OnPropertyChanged(nameof(FromLights));
        OnPropertyChanged(nameof(FromPreset));
        OnPropertyChanged(nameof(FromPicture));
        OnPropertyChanged(nameof(FromVideo));
        Apply();
    }

    partial void OnVideoChanged(string? value)
    {
        _host.Settings.WallpaperVideo = value;
        OnPropertyChanged(nameof(VideoName));
        Apply();
    }

    partial void OnPictureChanged(string? value)
    {
        _host.Settings.WallpaperPicture = value;
        OnPropertyChanged(nameof(PictureName));
        Apply();
    }

    internal void ChooseScene(string id)
    {
        _host.Settings.WallpaperPresetId = id;
        foreach (var s in Scenes) s.IsSelected = s.Id == id;
        if (Source != WallpaperSource.Preset) Source = WallpaperSource.Preset;
        else Apply();
    }

    private void Apply()
    {
        _host.Wallpaper.Refresh();
        _host.ScheduleSave();
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DecodingNote));
    }

    private void RefreshScenes()
    {
        Scenes = _host.Presets.All
            .Where(p => p.EffectId != WallpaperFeed.EffectId && p.EffectId != MyRGB.Core.Screen.ScreenFeed.EffectId && p.Id != "lights-off")
            .Select(p => new WallpaperSceneViewModel(p, _host.Effects.Find(p.EffectId), this) { IsSelected = p.Id == _host.Settings.WallpaperPresetId })
            .ToList();
        OnPropertyChanged(nameof(Scenes));
    }

    [RelayCommand]
    private void SetSource(string? source)
    {
        if (Enum.TryParse<WallpaperSource>(source, out var s)) Source = s;
    }

    [RelayCommand]
    private void MatchLights() => _host.Lights.ActivatePreset("wallpaper");

    [RelayCommand]
    private async Task ChooseVideo()
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a wallpaper video",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Videos") { Patterns = ["*.mp4", "*.m4v", "*.mov", "*.wmv", "*.avi", "*.webm", "*.mkv"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        Video = path;
        Source = WallpaperSource.Video;
    }

    [RelayCommand]
    private async Task ChoosePicture()
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a wallpaper picture",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        Picture = path;
        Source = WallpaperSource.Picture;
    }
}
