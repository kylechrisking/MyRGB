using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using MyRGB.Core.Wallpaper;

namespace MyRGB.App.Services.Wallpaper;

/// <summary>
/// Shows <see cref="WallpaperRenderer"/>'s frames on the desktop: one borderless window per screen, put
/// behind the desktop icons. Each window draws its own part of the shared grid, scaled up smoothly.
/// Every second it checks that Explorer still holds the windows (it drops them when it restarts) and
/// whether a game or full-screen app hides each screen, so nothing is drawn that nobody can see.
/// </summary>
public sealed class WallpaperDisplay : IDisposable
{
    private readonly WallpaperRenderer _renderer;
    private readonly Func<IReadOnlyList<Screen>> _screens;
    private readonly ILogger _log;
    private readonly List<WallpaperWindow> _windows = [];
    private readonly DispatcherTimer _watch;
    private WriteableBitmap? _bitmap;
    private int _posted;

    public WallpaperDisplay(WallpaperRenderer renderer, Func<IReadOnlyList<Screen>> screens, ILogger log)
    {
        _renderer = renderer;
        _screens = screens;
        _log = log;
        _renderer.FrameReady += OnFrame;
        _renderer.Changed += () => Dispatcher.UIThread.Post(Update);
        _watch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Watch());
        Update();
    }

    /// <summary>True while windows are up on the desktop.</summary>
    public bool Showing => _windows.Count > 0;

    /// <summary>Raised when <see cref="Showing"/> or the reason it isn't changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Why the wallpaper couldn't be put on the desktop, if it couldn't.</summary>
    public string? Problem { get; private set; }

    /// <summary>Open or close the windows to match the settings, and lay them out on today's screens.</summary>
    public void Update()
    {
        Close();
        if (!_renderer.On) { _watch.Stop(); StatusChanged?.Invoke(); return; }

        var screens = _screens();
        if (screens.Count == 0) return;
        var all = screens.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b));
        _renderer.SetCanvas(all);
        Problem = null;
        foreach (var screen in screens)
        {
            var window = new WallpaperWindow(this, screen.Bounds, all);
            window.Open();
            if (window.DesktopParent is null)
            {
                window.CloseNow();
                Problem = "Windows' desktop couldn't be found to draw on. Restarting Explorer (or signing out and in) usually fixes this.";
                _log.LogWarning("Couldn't put the wallpaper behind the desktop icons");
                Close();
                break;
            }
            _windows.Add(window);
        }
        if (_windows.Count > 0)
        {
            _log.LogInformation("Wallpaper showing on {Count} screen(s), source {Source}", _windows.Count, _renderer.Source);
            _watch.Start();
        }
        StatusChanged?.Invoke();
    }

    internal IImage? Image => _renderer.Source switch
    {
        WallpaperSource.Picture => _renderer.Picture,
        WallpaperSource.Video => _videoBitmap,
        _ => _bitmap,
    };

    /// <summary>Pictures and videos fill each screen whole; effects span all the screens as one canvas.</summary>
    internal bool FillsScreen => _renderer.Source is WallpaperSource.Picture or WallpaperSource.Video;

    private WriteableBitmap? _videoBitmap;
    private int _videoShown = -1;

    private void OnFrame()
    {
        // One frame in flight at a time; a busy UI thread skips frames rather than queue them.
        if (Interlocked.Exchange(ref _posted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _posted, 0);
            if (_windows.Count == 0 || _windows.TrueForAll(w => w.Hidden)) return;
            if (_renderer.Source == WallpaperSource.Video)
            {
                if (!CopyVideo()) return;
            }
            else if (_renderer.Latest is { } frame) Copy(frame);
            else return;
            foreach (var w in _windows) w.Redraw();
        }, DispatcherPriority.Render);
    }

    /// <summary>Copy the decoder's newest frame into the bitmap the screens draw; false if there's nothing new.</summary>
    private bool CopyVideo()
    {
        int number = _renderer.VideoFrameNumber;
        var size = _renderer.VideoSize;
        if (number == _videoShown || size.Width <= 0) return false;
        _videoShown = number;
        if (_videoBitmap is null || _videoBitmap.PixelSize != size)
        {
            _videoBitmap?.Dispose();
            _videoBitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using var target = _videoBitmap.Lock();
        _renderer.CopyVideoFrame(bgra =>
        {
            int rowBytes = size.Width * 4;
            if (bgra.Length < rowBytes * size.Height) return; // a frame of the previous video
            if (target.RowBytes == rowBytes) Marshal.Copy(bgra, 0, target.Address, rowBytes * size.Height);
            else for (int y = 0; y < size.Height; y++) Marshal.Copy(bgra, y * rowBytes, target.Address + y * target.RowBytes, rowBytes);
        });
        return true;
    }

    private void Copy(WallpaperFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using var buffer = _bitmap.Lock();
        var row = new int[frame.Width];
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                var c = frame.Pixels[y * frame.Width + x];
                row[x] = unchecked((int)(0xFF000000u | (uint)c.R << 16 | (uint)c.G << 8 | c.B));
            }
            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, frame.Width);
        }
    }

    private void Watch()
    {
        bool allHidden = true;
        foreach (var w in _windows)
        {
            if (!w.StillAttached)
            {
                _log.LogInformation("The desktop was rebuilt (Explorer restarted?); putting the wallpaper back");
                Update();
                return;
            }
            w.Hidden = DesktopLayer.CoveredByFullScreen(w.Area.X, w.Area.Y, w.Area.Width, w.Area.Height);
            allHidden &= w.Hidden;
        }
        _renderer.Hidden = allHidden;
    }

    private void Close()
    {
        if (_windows.Count == 0) return;
        foreach (var w in _windows) w.CloseNow();
        _windows.Clear();
        _renderer.Hidden = false;
        DesktopLayer.RepaintWallpaper();
    }

    public void Dispose()
    {
        _watch.Stop();
        _renderer.FrameReady -= OnFrame;
        Close();
        _bitmap?.Dispose();
        _videoBitmap?.Dispose();
    }

    /// <summary>One screen's wallpaper window.</summary>
    internal sealed class WallpaperWindow : Window
    {
        private readonly WallpaperDisplay _display;
        private readonly PixelRect _all;
        private readonly Surface _surface;
        private IntPtr _handle;

        public WallpaperWindow(WallpaperDisplay display, PixelRect area, PixelRect all)
        {
            _display = display;
            Area = area;
            _all = all;
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            Background = Brushes.Black;
            Title = "MyRGB wallpaper";
            // Off every screen until it is inside the desktop, so it never flashes up as a normal window.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-32000, -32000);
            Width = 64;
            Height = 64;
            Content = _surface = new Surface(this);
        }

        public PixelRect Area { get; }
        public IntPtr? DesktopParent { get; private set; }
        public bool Hidden { get; set; }
        public bool StillAttached => DesktopParent is { } p && DesktopLayer.StillAttached(_handle, p);

        public void Open()
        {
            Show();
            if (TryGetPlatformHandle()?.Handle is not { } handle) return;
            _handle = handle;
            DesktopParent = DesktopLayer.Attach(handle, Area.X, Area.Y, Area.Width, Area.Height);
        }

        public void Redraw()
        {
            if (!Hidden) _surface.InvalidateVisual();
        }

        public void CloseNow()
        {
            if (_handle != IntPtr.Zero) DesktopLayer.Detach(_handle);
            Close();
        }

        /// <summary>Draws this screen's part of the frame (or the whole picture, filling the screen).</summary>
        private sealed class Surface(WallpaperWindow owner) : Control
        {
            public override void Render(DrawingContext context)
            {
                var bounds = new Rect(Bounds.Size);
                context.FillRectangle(Brushes.Black, bounds);
                if (owner._display.Image is not { } image) return;
                var size = image.Size;
                Rect source;
                if (owner._display.FillsScreen)
                {
                    // Fill the screen, cropping the picture or video evenly on its longer side.
                    double scale = Math.Max(bounds.Width / size.Width, bounds.Height / size.Height);
                    double w = bounds.Width / scale, h = bounds.Height / scale;
                    source = new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
                }
                else
                {
                    // This screen's share of the grid that spans all of them.
                    var all = owner._all;
                    double sx = size.Width / all.Width, sy = size.Height / all.Height;
                    source = new Rect((owner.Area.X - all.X) * sx, (owner.Area.Y - all.Y) * sy, owner.Area.Width * sx, owner.Area.Height * sy);
                }
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                    context.DrawImage(image, source, bounds);
            }
        }
    }
}
