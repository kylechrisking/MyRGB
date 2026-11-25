using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using MyRGB.Core;
using MyRGB.Core.Effects;
using MyRGB.Core.Wallpaper;
using MyRGB.Core.Presets;

namespace MyRGB.App.Services.Wallpaper;

/// <summary>What the desktop wallpaper shows.</summary>
public enum WallpaperSource
{
    /// <summary>Whatever preset the lights are playing, across the screens.</summary>
    Lights,
    /// <summary>One chosen preset, whatever the lights do.</summary>
    Preset,
    /// <summary>A still picture from a file.</summary>
    Picture,
}

/// <summary>
/// Draws MyRGB's wallpaper: the chosen preset's effect on a coarse grid that spans every screen,
/// about 30 times a second on its own thread. Each frame goes two ways: to the screens (the
/// display scales the grid up smoothly behind the desktop icons) and to <see cref="WallpaperFeed"/>,
/// where the Match Wallpaper effect lights the devices from it. While MyRGB shows no wallpaper of its
/// own, the feed gets a still of Windows' own wallpaper instead, so Match Wallpaper works either way.
/// </summary>
public sealed class WallpaperRenderer : IDisposable
{
    /// <summary>About this many grid cells across all screens: enough for soft light, cheap to draw.</summary>
    private const int TargetCells = 24_000;

    private readonly Func<Preset?> _lightsPreset;
    private readonly PresetLibrary _presets;
    private readonly EffectRegistry _effects;
    private readonly AppSettings _settings;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private readonly AutoResetEvent _wake = new(false);

    private PixelRect _canvas = new(0, 0, 1920, 1080);
    private int _cols, _rows;
    private LedPoint[] _points = [];
    private Effect? _effect;
    private Preset? _effectFor;
    private string? _pictureFor;
    private WallpaperFrame? _pictureFrame;
    private string? _windowsPicture;
    private DateTime _windowsCheckedAt;
    private volatile bool _windowsStale = true;

    public WallpaperRenderer(Func<Preset?> lightsPreset, PresetLibrary presets, EffectRegistry effects, AppSettings settings, ILogger log)
    {
        _lightsPreset = lightsPreset;
        _presets = presets;
        _effects = effects;
        _settings = settings;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "MyRGB wallpaper", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Raised after each new frame, on the wallpaper thread.</summary>
    public event Action? FrameReady;

    /// <summary>Raised when what should be on screen changes (on/off, source, picture), on the caller's thread.</summary>
    public event Action? Changed;

    /// <summary>The latest frame drawn for the screens; null while there is none (off, or a picture).</summary>
    public WallpaperFrame? Latest { get; private set; }

    /// <summary>The picture to show full size, while the source is a picture.</summary>
    public Bitmap? Picture { get; private set; }

    /// <summary>Why nothing moves, for the Wallpapers page; null when all is well.</summary>
    public string? Problem { get; private set; }

    /// <summary>Every screen's area together, in pixels; the grid spans it.</summary>
    public PixelRect Canvas => _canvas;

    /// <summary>Set by the display while games or full-screen apps hide every screen: drawing slows right down.</summary>
    public bool Hidden { get; set; }

    public bool On => _settings.WallpaperOn;
    public WallpaperSource Source => _settings.WallpaperSource;

    public void SetCanvas(PixelRect canvas)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || canvas == _canvas) return;
        _canvas = canvas;
        _wake.Set();
    }

    /// <summary>Settings changed (the page or the API): pick up the new source now.</summary>
    public void Refresh()
    {
        _windowsStale = true; // read Windows' own picture afresh when MyRGB's goes away
        LoadPictureIfNeeded();
        _wake.Set();
        Changed?.Invoke();
    }

    private void LoadPictureIfNeeded()
    {
        string? path = _settings.WallpaperSource == WallpaperSource.Picture ? _settings.WallpaperPicture : null;
        if (path == _pictureFor) return;
        _pictureFor = path;
        var old = Picture;
        Picture = null;
        _pictureFrame = null;
        Problem = null;
        if (path is null) { old?.Dispose(); return; }
        try
        {
            Picture = new Bitmap(path);
            _pictureFrame = Shrink(Picture, 96);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _log.LogWarning("Couldn't open the wallpaper picture {Path}: {Reason}", path, ex.Message);
            Problem = $"Couldn't open {Path.GetFileName(path)}.";
        }
        old?.Dispose();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = 0, effectTime = 0;
        while (_running)
        {
            double started = clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(started - last, 0, 0.25);
            last = started;
            double interval = 1.0 / 30;
            try
            {
                if (!_settings.WallpaperOn)
                {
                    Latest = null;
                    PublishWindowsPicture();
                    interval = 0.5;
                }
                else if (_settings.WallpaperSource == WallpaperSource.Picture)
                {
                    Latest = null;
                    WallpaperFeed.Publish(_pictureFrame);
                    interval = 0.5;
                }
                else if (Hidden && !WallpaperFeed.Wanted(TimeSpan.FromSeconds(2)))
                {
                    interval = 0.25; // nobody can see it and no lights follow it
                }
                else
                {
                    effectTime += dt;
                    DrawEffect(effectTime, dt);
                    if (Hidden) interval = 1.0 / 10; // only the lights see it
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Drawing the wallpaper failed");
                interval = 1;
            }
            double sleep = interval - (clock.Elapsed.TotalSeconds - started);
            if (sleep > 0) _wake.WaitOne(TimeSpan.FromSeconds(sleep));
        }
    }

    private void DrawEffect(double time, double dt)
    {
        var preset = _settings.WallpaperSource == WallpaperSource.Lights
            ? _lightsPreset()
            : _settings.WallpaperPresetId is { } id ? _presets.Find(id) : null;
        // Mirroring Match Wallpaper would be the wallpaper copying itself: nothing to show.
        if (preset is null || preset.EffectId == WallpaperFeed.EffectId || _effects.Find(preset.EffectId) is not { } def)
        {
            Problem = preset?.EffectId == WallpaperFeed.EffectId
                ? "Your lights are matching the wallpaper, so the wallpaper has nothing of theirs to show. Pick a preset for it instead."
                : null;
            Latest = null;
            WallpaperFeed.Publish(null);
            FrameReady?.Invoke();
            return;
        }
        Problem = null;

        BuildGrid();
        if (!ReferenceEquals(preset, _effectFor) || _effect is null)
        {
            _effect = def.Create(new EffectSettings(preset.Settings));
            _effectFor = preset;
        }

        var colors = new Rgb[_points.Length];
        _effect.Render(new FrameContext(time, dt), _points, colors);
        var frame = new WallpaperFrame(_cols, _rows, colors);
        Latest = frame;
        WallpaperFeed.Publish(frame);
        FrameReady?.Invoke();
    }

    private void BuildGrid()
    {
        double cell = Math.Sqrt((double)_canvas.Width * _canvas.Height / TargetCells);
        int cols = Math.Max(16, (int)Math.Round(_canvas.Width / cell)), rows = Math.Max(9, (int)Math.Round(_canvas.Height / cell));
        if (cols == _cols && rows == _rows) return;
        (_cols, _rows) = (cols, rows);
        _points = new LedPoint[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                _points[r * cols + c] = LedPoint.Whole((c + 0.5f) / cols, (r + 0.5f) / rows);
        _effect = null; // stateful effects (ripples, stars) start over on the new grid
    }

    /// <summary>A still of the picture Windows itself shows, while someone's lights follow it. Checked every 30 s.</summary>
    private void PublishWindowsPicture()
    {
        if (!WallpaperFeed.Wanted(TimeSpan.FromSeconds(5)) || !_windowsStale && DateTime.UtcNow - _windowsCheckedAt < TimeSpan.FromSeconds(30)) return;
        _windowsCheckedAt = DateTime.UtcNow;
        string? path = OperatingSystem.IsWindows() ? DesktopLayer.CurrentWallpaperPath() : null;
        if (!_windowsStale && path == _windowsPicture) return;
        _windowsStale = false;
        _windowsPicture = path;
        try
        {
            // No picture: Windows shows a plain color, so the lights take that.
            if (path is null) { WallpaperFeed.Publish(OperatingSystem.IsWindows() ? new WallpaperFrame(1, 1, [DesktopLayer.DesktopColor()]) : null); return; }
            using var bitmap = new Bitmap(path);
            WallpaperFeed.Publish(Shrink(bitmap, 64));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _log.LogDebug("Couldn't read the Windows wallpaper {Path}: {Reason}", path, ex.Message);
            WallpaperFeed.Publish(null);
        }
    }

    /// <summary>The picture scaled to <paramref name="width"/> cells across, for the lights to sample.</summary>
    internal static WallpaperFrame Shrink(Bitmap bitmap, int width)
    {
        int height = Math.Max(1, (int)Math.Round(width * bitmap.PixelSize.Height / (double)Math.Max(1, bitmap.PixelSize.Width)));
        using var small = bitmap.CreateScaledBitmap(new PixelSize(width, height), BitmapInterpolationMode.HighQuality);
        var bytes = new byte[width * height * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { small.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, width * 4); }
        finally { handle.Free(); }
        bool rgba = small.Format is { } f && f == PixelFormat.Rgba8888;
        var pixels = new Rgb[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            byte a = bytes[i * 4], b = bytes[i * 4 + 1], c = bytes[i * 4 + 2];
            pixels[i] = rgba ? new Rgb(a, b, c) : new Rgb(c, b, a);
        }
        return new WallpaperFrame(width, height, pixels);
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        WallpaperFeed.Publish(null);
        Picture?.Dispose();
        _wake.Dispose();
    }
}
