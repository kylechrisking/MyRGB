using MyRGB.Core;
using MyRGB.Providers.Network;

namespace MyRGB.Tests;

public class NetworkLightsTests
{
    [Fact]
    public void Reads_wled_info()
    {
        var info = WledInfo.Parse("""
            {"ver":"0.15.0","leds":{"count":60,"lc":1,"rgbw":false},"name":"Desk strip","mac":"A1B2C3D4E5F6"}
            """);
        Assert.NotNull(info);
        Assert.Equal("Desk strip", info.Name);
        Assert.Equal(60, info.LedCount);
        Assert.Equal("A1B2C3D4E5F6", info.Mac);
        Assert.Equal(0, info.MatrixWidth);
    }

    [Fact]
    public void Reads_a_matrix_only_when_it_covers_every_led()
    {
        Assert.Equal(16, WledInfo.Parse("""{"leds":{"count":256,"matrix":{"w":16,"h":16}}}""")!.MatrixWidth);
        Assert.Equal(0, WledInfo.Parse("""{"leds":{"count":250,"matrix":{"w":16,"h":16}}}""")!.MatrixWidth);
    }

    [Theory]
    [InlineData("""{"name":"router"}""")]
    [InlineData("""{"leds":{"count":0}}""")]
    [InlineData("<html>not json</html>")]
    public void Anything_else_is_not_wled(string json) => Assert.Null(WledInfo.Parse(json));

    [Fact]
    public void Splits_a_long_strip_into_dnrgb_packets()
    {
        var colors = Enumerable.Range(0, 600).Select(i => new Rgb((byte)i, (byte)(i >> 8), 7)).ToArray();
        Assert.Equal(2, WledPackets.PacketCount(colors.Length));
        var buf = new byte[WledPackets.BufferSize];

        int len = WledPackets.Write(buf, colors, 0);
        Assert.Equal(4 + 489 * 3, len);
        Assert.Equal(new byte[] { 4, WledPackets.TimeoutSeconds, 0, 0, 0, 0, 7 }, buf[..7]);

        len = WledPackets.Write(buf, colors, 1);
        Assert.Equal(4 + (600 - 489) * 3, len);
        Assert.Equal((byte)(489 >> 8), buf[2]);
        Assert.Equal((byte)(489 & 0xFF), buf[3]);
        Assert.Equal(new byte[] { 489 & 0xFF, 489 >> 8, 7 }, buf[4..7]);
    }

    [Fact]
    public void Lays_a_strip_out_left_to_right_and_a_matrix_as_its_grid()
    {
        var strip = WledDevice.Layout(new WledInfo("s", "", "", 3, 0, 0, false));
        Assert.Equal(new[] { 0f, 0.5f, 1f }, strip.Select(l => l.X));
        Assert.All(strip, l => Assert.Equal(0.5f, l.Y));

        var grid = WledDevice.Layout(new WledInfo("m", "", "", 6, 3, 2, false));
        Assert.Equal((1f, 0f), (grid[2].X, grid[2].Y));
        Assert.Equal((0f, 1f), (grid[3].X, grid[3].Y));
    }

    [Fact]
    public void Mdns_query_asks_for_wled_and_spots_answers()
    {
        var q = LightFinder.MdnsQuery("_wled._tcp.local");
        Assert.Equal(1, q[5]); // one question
        Assert.False(LightFinder.MentionsWled(q)); // a question, not an answer

        var answer = (byte[])q.Clone();
        answer[2] = 0x84; // response, authoritative
        Assert.True(LightFinder.MentionsWled(answer));
    }
}
