using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyRGB.Core.Wallpaper;

namespace MyRGB.Core.Screen;

/// <summary>
/// Takes small copies of what the main screen shows right now, games and films included.
/// The graphics card copies the screen and shrinks it (Windows' desktop duplication), so a 4K
/// screen costs about as little as a small one. Where that isn't available, Windows' older way of
/// copying the screen is used instead, which costs the processor more. Not thread-safe: one thread reads.
/// </summary>
/// <remarks>
/// Some games that take the screen over completely ("exclusive full screen") may come out black with
/// the older way; desktop duplication sees them. Protected video (some streaming apps) comes out black
/// with either, as Windows intends.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class ScreenCapture : IDisposable
{
    private readonly int _maxWidth;
    private Duplication? _gpu;
    private DateTime _retryGpuAt;
    private WallpaperFrame? _last;

    /// <param name="maxWidth">The widest copy wanted; the copy keeps the screen's shape.</param>
    public ScreenCapture(int maxWidth = 96) => _maxWidth = maxWidth;

    /// <summary>True while the graphics card is doing the copying.</summary>
    public bool OnGpu => _gpu is not null;

    /// <summary>Why the graphics card isn't copying the screen, when it isn't.</summary>
    public string? GpuProblem { get; private set; }

    /// <summary>
    /// The screen as it is now, or the last copy when nothing on it has changed. Null when the screen
    /// can't be read at all, such as while Windows shows its lock screen or a UAC prompt.
    /// </summary>
    public WallpaperFrame? Grab()
    {
        if (_gpu is null && DateTime.UtcNow >= _retryGpuAt)
        {
            try { _gpu = new Duplication(_maxWidth); GpuProblem = null; }
            catch (Exception ex) when (ex is ScreenCaptureException or DllNotFoundException or EntryPointNotFoundException)
            {
                GpuProblem = ex.Message;
                _retryGpuAt = DateTime.UtcNow.AddSeconds(30);
            }
        }

        if (_gpu is { } gpu)
        {
            try
            {
                if (gpu.Next(out var frame)) _last = frame;
                return _last;
            }
            catch (ScreenCaptureException ex)
            {
                // The screen changed mode, a game took it over, or Windows switched to the secure desktop:
                // start over on the next grab.
                GpuProblem = ex.Message;
                gpu.Dispose();
                _gpu = null;
                _retryGpuAt = ex.Lost ? DateTime.UtcNow : DateTime.UtcNow.AddSeconds(2);
                return _last;
            }
        }

        _last = Gdi.Grab(_maxWidth);
        return _last;
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _gpu = null;
    }

    // ---- Desktop duplication ------------------------------------------------------------------------

    /// <summary>One duplication of the main screen, with the textures it shrinks frames through.</summary>
    private sealed class Duplication : IDisposable
    {
        private IntPtr _device, _context, _dupl, _mips, _mipsView, _staging;
        private readonly int _level, _w, _h;

        public Duplication(int maxWidth)
        {
            try
            {
                var adapter = FindMainScreen(out var output);
                try
                {
                    // On laptops with two graphics cards the screen belongs to one of them; the device
                    // has to be made on that one or duplication refuses.
                    Check(D3D11CreateDevice(adapter, 0 /* unknown: the adapter decides */, IntPtr.Zero, BgraSupport,
                        IntPtr.Zero, 0, 7, out _device, out _, out _context), "set up the graphics card");

                    var iid = IidOutput1;
                    Check(Marshal.QueryInterface(output, in iid, out var output1), "read the screen (Windows 8 or later is needed)");
                    try { Check(Call<IntPtr, IntPtr>(output1, 22, _device, out _dupl), "copy the screen"); }
                    finally { Marshal.Release(output1); }
                }
                finally
                {
                    Marshal.Release(output);
                    Marshal.Release(adapter);
                }

                var desc = default(OutduplDesc);
                VoidCall(_dupl, 7, &desc);
                int w = (int)desc.Width, h = (int)desc.Height;

                // A texture with every smaller size below it: the graphics card halves the frame down
                // to the first size no wider than asked for, and only that small copy comes back.
                var mips = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 0, ArraySize = 1, Format = Bgra, SampleCount = 1, BindFlags = 0x20 | 0x8, MiscFlags = 0x1 };
                Check(CreateTexture(_device, &mips, out _mips), "make room on the graphics card");
                IntPtr view;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)VTable(_device)[7])(_device, _mips, IntPtr.Zero, &view), "make room on the graphics card");
                _mipsView = view;

                while ((w >> _level) > maxWidth && (w >> _level) > 1) _level++;
                _w = Math.Max(1, w >> _level);
                _h = Math.Max(1, h >> _level);
                var staging = new Texture2DDesc { Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = Bgra, SampleCount = 1, Usage = 3, CpuAccessFlags = 0x20000 };
                Check(CreateTexture(_device, &staging, out _staging), "make room on the graphics card");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>The next frame, if the screen changed within a short wait; false when it didn't.</summary>
        public bool Next(out WallpaperFrame? frame)
        {
            frame = null;
            var info = stackalloc byte[64];
            IntPtr resource;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, IntPtr*, int>)VTable(_dupl)[8])(_dupl, 20, info, &resource);
            if (hr == WaitTimeout) return false;
            Check(hr, "copy the screen");
            try
            {
                var iid = IidTexture2D;
                Check(Marshal.QueryInterface(resource, in iid, out var texture), "copy the screen");
                try
                {
                    var ctx = VTable(_context);
                    var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)ctx[46];
                    copy(_context, _mips, 0, 0, 0, 0, texture, 0, IntPtr.Zero);
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)ctx[54])(_context, _mipsView);
                    copy(_context, _staging, 0, 0, 0, 0, _mips, (uint)_level, IntPtr.Zero);
                }
                finally { Marshal.Release(texture); }
            }
            finally
            {
                Marshal.Release(resource);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)VTable(_dupl)[14])(_dupl);
            }

            var mapped = default(Mapped);
            var vt = VTable(_context);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)vt[14])(_context, _staging, 0, 1 /* read */, 0, &mapped), "read the screen copy");
            try
            {
                var pixels = new Rgb[_w * _h];
                for (int y = 0; y < _h; y++)
                {
                    var row = (byte*)mapped.Data + y * mapped.RowPitch;
                    for (int x = 0; x < _w; x++, row += 4) pixels[y * _w + x] = new Rgb(row[2], row[1], row[0]);
                }
                frame = new WallpaperFrame(_w, _h, pixels);
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vt[15])(_context, _staging, 0); }
            return true;
        }

        public void Dispose()
        {
            Release(ref _staging);
            Release(ref _mipsView);
            Release(ref _mips);
            Release(ref _dupl);
            Release(ref _context);
            Release(ref _device);
        }

        private static void Release(ref IntPtr com)
        {
            if (com != IntPtr.Zero) Marshal.Release(com);
            com = IntPtr.Zero;
        }

        private static int CreateTexture(IntPtr device, Texture2DDesc* desc, out IntPtr texture)
        {
            IntPtr t;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)VTable(device)[5])(device, desc, IntPtr.Zero, &t);
            texture = t;
            return hr;
        }

        /// <summary>The graphics card and output that show the main screen (the one at 0,0).</summary>
        private static IntPtr FindMainScreen(out IntPtr output)
        {
            var iid = IidFactory1;
            Check(CreateDXGIFactory1(ref iid, out var factory), "find the graphics card");
            try
            {
                for (uint a = 0; ; a++)
                {
                    if (Call<uint, IntPtr>(factory, 7, a, out var adapter) < 0) break;
                    for (uint o = 0; ; o++)
                    {
                        if (Call<uint, IntPtr>(adapter, 7, o, out var candidate) < 0) break;
                        var desc = default(OutputDesc);
                        VoidCallHr(candidate, 7, &desc);
                        if (desc.Attached != 0 && desc.Left <= 0 && desc.Top <= 0 && desc.Right > 0 && desc.Bottom > 0)
                        {
                            output = candidate;
                            return adapter;
                        }
                        Marshal.Release(candidate);
                    }
                    Marshal.Release(adapter);
                }
            }
            finally { Marshal.Release(factory); }
            throw new ScreenCaptureException("No graphics card says it shows the main screen.", 0);
        }
    }

    // ---- Windows' older screen copy, for when duplication isn't available -----------------------------

    private static class Gdi
    {
        public static WallpaperFrame? Grab(int maxWidth)
        {
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1); // the main screen
            if (sw <= 0 || sh <= 0) return null;
            int w = Math.Min(maxWidth, sw), h = Math.Max(1, (int)Math.Round((double)sh * w / sw));

            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            var mem = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
            var bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            try
            {
                if (mem == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
                var old = SelectObject(mem, bitmap);
                SetStretchBltMode(mem, 4 /* HALFTONE: averages, rather than picking stray pixels */);
                SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
                bool ok = StretchBlt(mem, 0, 0, w, h, screen, 0, 0, sw, sh, 0x00CC0020 /* SRCCOPY */);
                SelectObject(mem, old);
                if (!ok) return null;

                var pixels = new Rgb[w * h];
                var p = (byte*)bits;
                for (int i = 0; i < pixels.Length; i++, p += 4) pixels[i] = new Rgb(p[2], p[1], p[0]);
                return new WallpaperFrame(w, h, pixels);
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
            public int Colors; // one RGBQUAD, unused for 32-bit
        }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    }

    // ---- COM by vtable slot --------------------------------------------------------------------------
    // Direct3D and DXGI are called through their vtables, slot numbers from the Windows SDK headers
    // (IUnknown takes 0-2). Only the handful of calls used here.

    private const uint Bgra = 87 /* DXGI_FORMAT_B8G8R8A8_UNORM */, BgraSupport = 0x20;
    private const int WaitTimeout = unchecked((int)0x887A0027), AccessLost = unchecked((int)0x887A0026);
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static IntPtr* VTable(IntPtr com) => *(IntPtr**)com;

    private static int Call<TIn, TOut>(IntPtr com, int slot, TIn arg, out TOut result) where TIn : unmanaged where TOut : unmanaged
    {
        TOut r;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, TIn, TOut*, int>)VTable(com)[slot])(com, arg, &r);
        result = r;
        return hr;
    }

    private static void VoidCall<T>(IntPtr com, int slot, T* arg) where T : unmanaged =>
        ((delegate* unmanaged[Stdcall]<IntPtr, T*, void>)VTable(com)[slot])(com, arg);

    private static int VoidCallHr<T>(IntPtr com, int slot, T* arg) where T : unmanaged =>
        ((delegate* unmanaged[Stdcall]<IntPtr, T*, int>)VTable(com)[slot])(com, arg);

    private static void Check(int hr, string doing)
    {
        if (hr < 0) throw new ScreenCaptureException($"Windows couldn't {doing} (0x{hr:X8}).", hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    /// <summary>DXGI_OUTPUT_DESC: the screen's name, where it sits on the desktop, and whether it's in use.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct OutputDesc
    {
        public fixed char Name[32];
        public int Left, Top, Right, Bottom;
        public int Attached;
        public int Rotation;
        public IntPtr Monitor;
    }

    /// <summary>DXGI_OUTDUPL_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct OutduplDesc
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling;
        public int Rotation, InSystemMemory;
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint levels, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);

    internal static bool IsLost(int hr) => hr == AccessLost;
}

[SupportedOSPlatform("windows")]
public sealed class ScreenCaptureException(string message, int hResult) : Exception(message)
{
    /// <summary>The screen was taken away for a moment (a mode change, a game, the secure desktop); try again at once.</summary>
    public bool Lost => ScreenCapture.IsLost(hResult);
}
