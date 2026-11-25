using MyRGB.Core;
using MyRGB.Core.Effects;
using MyRGB.Core.Effects.BuiltIn;
using MyRGB.Core.Wallpaper;

namespace MyRGB.Tests;

[Collection("WallpaperFeed")] // the feed is process-wide
public class WallpaperTests
{
    // Left half red, right half blue.
    private static WallpaperFrame Split() => new(4, 2,
        [new(255, 0, 0), new(255, 0, 0), new(0, 0, 255), new(0, 0, 255),
         new(255, 0, 0), new(255, 0, 0), new(0, 0, 255), new(0, 0, 255)]);

    [Fact]
    public void Each_light_takes_the_color_of_its_part_of_the_screen()
    {
        WallpaperFeed.Publish(Split());
        try
        {
            var effect = WallpaperEffect.Definition.Create(new EffectSettings(new Dictionary<string, string> { ["vivid"] = "100", ["area"] = "1", ["smooth"] = "0" }));
            var output = new Rgb[2];
            effect.Render(new FrameContext(0, 1 / 30.0), [LedPoint.Whole(0.1f, 0.5f), LedPoint.Whole(0.9f, 0.5f)], output);
            Assert.Equal(new Rgb(255, 0, 0), output[0]);
            Assert.Equal(new Rgb(0, 0, 255), output[1]);
            Assert.True(WallpaperFeed.Wanted(TimeSpan.FromSeconds(5)));
        }
        finally { WallpaperFeed.Publish(null); }
    }

    [Fact]
    public void No_wallpaper_glows_dimly_and_sampling_averages_the_area()
    {
        WallpaperFeed.Publish(null);
        var effect = WallpaperEffect.Definition.Create(EffectSettings.Empty);
        var output = new Rgb[1];
        effect.Render(new FrameContext(0, 1), [LedPoint.Whole(0.5f, 0.5f)], output);
        Assert.Equal(WallpaperEffect.Waiting, output[0]);

        // A box across the middle sees both halves.
        var mixed = Split().Sample(0.5, 0.5, 0.5);
        Assert.True(mixed.R > 60 && mixed.B > 60, $"got {mixed}");
    }

    [Fact]
    public void Vividness_pushes_colors_away_from_grey()
    {
        var dull = new Rgb(140, 110, 110);
        var vivid = WallpaperEffect.Vivid(dull, 2);
        Assert.True(vivid.R - vivid.G > dull.R - dull.G);
        Assert.Equal(dull, WallpaperEffect.Vivid(dull, 1));
    }
}

public class VideoDecoderTests
{
    /// <summary>Set MYRGB_TEST_VIDEO to any MP4 to run this; it reads the file only.</summary>
    [Fact]
    public void Decodes_scaled_frames_and_loops()
    {
        string? path = Environment.GetEnvironmentVariable("MYRGB_TEST_VIDEO");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Skip("MYRGB_TEST_VIDEO isn't set to a video file.");
        if (!OperatingSystem.IsWindows()) Assert.Skip("Media Foundation is Windows only.");

        using var video = new VideoDecoder(path, 640, 360);
        Assert.InRange(video.Width, 2, 640);
        Assert.InRange(video.Height, 2, 360);
        var frame = new byte[video.Width * video.Height * 4];
        TimeSpan? first = video.ReadFrame(frame), later = null;
        for (int i = 0; i < 10; i++) later = video.ReadFrame(frame);
        Assert.NotNull(first);
        Assert.True(later > first, $"frames should move forward: {first} then {later}");
        Assert.Contains(frame, b => b != 0); // a picture, not a black buffer

        video.Rewind();
        Assert.True(video.ReadFrame(frame) < TimeSpan.FromSeconds(1));
        Console.WriteLine($"{video.Width}x{video.Height}, {video.Duration}");
    }

    [Fact]
    public void A_file_that_is_not_a_video_says_so()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Media Foundation is Windows only.");
        string path = Path.Combine(Path.GetTempPath(), $"myrgb-not-a-video-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(path, "hello");
        try { Assert.Throws<VideoException>(() => new VideoDecoder(path, 640, 360)); }
        finally { File.Delete(path); }
    }
}

public class VideoGpuTests
{
    /// <summary>With MYRGB_TEST_VIDEO set: the graphics card decodes the same frames as the processor, for a fraction of the processor time.</summary>
    [Fact]
    public void The_graphics_card_decodes_and_the_processor_is_the_fallback()
    {
        string? path = Environment.GetEnvironmentVariable("MYRGB_TEST_VIDEO");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Skip("MYRGB_TEST_VIDEO isn't set to a video file.");
        if (!OperatingSystem.IsWindows()) Assert.Skip("Media Foundation is Windows only.");

        (TimeSpan Cpu, bool OnGpu, string? Why, byte[] Frame, int W, int H) Run(bool gpu)
        {
            using var video = new VideoDecoder(path, 1920, 1080, gpu);
            var frame = new byte[video.Width * video.Height * 4];
            var before = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            for (int i = 0; i < 300; i++) if (video.ReadFrame(frame) is null) video.Rewind();
            var used = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - before;
            return (used, video.OnGpu, video.GpuProblem, frame, video.Width, video.Height);
        }

        var cpu = Run(gpu: false);
        var gpu = Run(gpu: true);
        Console.WriteLine($"processor: {cpu.Cpu.TotalMilliseconds:N0} ms for 300 frames at {cpu.W}x{cpu.H}");
        Console.WriteLine($"graphics card: {gpu.Cpu.TotalMilliseconds:N0} ms, on GPU {gpu.OnGpu}, problem {gpu.Why ?? "none"}, {gpu.W}x{gpu.H}");

        Assert.False(cpu.OnGpu);
        Assert.Equal((cpu.W, cpu.H), (gpu.W, gpu.H));
        Assert.Contains(gpu.Frame, b => b != 0);
        // Same frame both ways, give or take colour rounding: compare the average brightness.
        double Mean(byte[] f) { long s = 0; for (int i = 0; i < f.Length; i += 4) s += f[i] + f[i + 1] + f[i + 2]; return s / (f.Length / 4.0 * 3); }
        Assert.InRange(Mean(gpu.Frame) - Mean(cpu.Frame), -12, 12);
    }
}
