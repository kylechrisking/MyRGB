using MyRGB.Core;

namespace MyRGB.Providers.Network;

/// <summary>
/// WLED's realtime UDP protocol (port 21324), DNRGB flavour: [4, timeout, start hi, start lo, r, g, b, ...].
/// Each packet carries up to 489 LEDs from a start index, so any length of strip fits in a few packets.
/// </summary>
internal static class WledPackets
{
    public const int Port = 21324;
    public const byte Dnrgb = 4;
    public const int MaxLedsPerPacket = 489;
    public const int HeaderSize = 4;

    /// <summary>
    /// Seconds WLED waits after the last packet before going back to its own effect. A few seconds:
    /// long enough to ride out a dropped packet (MyRGB resends at least once a second), short enough
    /// that the light goes back to normal soon after MyRGB closes.
    /// </summary>
    public const byte TimeoutSeconds = 3;

    public static int PacketCount(int ledCount) => (ledCount + MaxLedsPerPacket - 1) / MaxLedsPerPacket;

    /// <summary>Write packet <paramref name="index"/> of a frame into <paramref name="buffer"/>; returns its length.</summary>
    public static int Write(Span<byte> buffer, ReadOnlySpan<Rgb> colors, int index)
    {
        int start = index * MaxLedsPerPacket;
        int count = Math.Min(MaxLedsPerPacket, colors.Length - start);
        buffer[0] = Dnrgb;
        buffer[1] = TimeoutSeconds;
        buffer[2] = (byte)(start >> 8);
        buffer[3] = (byte)start;
        var body = buffer[HeaderSize..];
        for (int i = 0; i < count; i++)
        {
            var c = colors[start + i];
            body[i * 3] = c.R;
            body[i * 3 + 1] = c.G;
            body[i * 3 + 2] = c.B;
        }
        return HeaderSize + count * 3;
    }

    public static int BufferSize => HeaderSize + MaxLedsPerPacket * 3;
}
