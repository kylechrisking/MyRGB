using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MyRGB.Core.Devices;

namespace MyRGB.App.Services;

/// <summary>Turns a picture file into what a device screen takes.</summary>
public static class ScreenImage
{
    /// <summary>
    /// Loads a picture (PNG, JPEG, BMP...) and scales it to fill a <paramref name="width"/> × <paramref name="height"/>
    /// screen, cropping the edges that don't fit rather than stretching. Transparent parts come out black.
    /// Call on the UI thread.
    /// </summary>
    public static RenderTargetBitmap Render(string path, int width, int height)
    {
        using var src = new Bitmap(path);
        var (x, y, w, h) = ScreenFit.Cover(src.Size.Width, src.Size.Height, width, height);
        var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, width, height));
            ctx.DrawImage(src, new Rect(x, y, w, h), new Rect(0, 0, width, height));
        }
        return target;
    }

    /// <summary>The bitmap's pixels as R,G,B triples, top row first.</summary>
    public static byte[] ToRgb(Bitmap bitmap)
    {
        int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height, stride = w * 4;
        var pixels = new byte[stride * h];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { pin.Free(); }

        if (bitmap.Format == PixelFormat.Rgba8888)
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return ScreenFit.PremultipliedBgraToRgb(pixels, w, h, stride);
    }

    public static byte[] ToPng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }
}
