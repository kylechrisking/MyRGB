using System.Buffers.Binary;
using System.Text;
using MyRGB.Core;

namespace MyRGB.Providers.OpenRgb;

internal enum PacketId : uint
{
    RequestControllerCount = 0,
    RequestControllerData = 1,
    RequestProtocolVersion = 40,
    SetClientName = 50,
    DeviceListUpdated = 100,
    ResizeZone = 1000,
    UpdateLeds = 1050,
    SetCustomMode = 1100,
}

internal sealed record ModeData(string Name, int Value, uint Flags);

internal sealed record ZoneData(
    string Name, int Type, uint LedsMin, uint LedsMax, uint LedCount, uint MatrixHeight, uint MatrixWidth, uint[]? Matrix)
{
    public const int Single = 0, Linear = 1, MatrixType = 2;

    /// <summary>The user sets the LED count (ARGB headers, fan channels); OpenRGB can't detect it.</summary>
    public bool Resizable => LedsMin < LedsMax;
}

internal sealed record ControllerData(
    int Type,
    string Name,
    string Vendor,
    string Description,
    string Version,
    string Serial,
    string Location,
    int ActiveMode,
    IReadOnlyList<ModeData> Modes,
    IReadOnlyList<ZoneData> Zones,
    IReadOnlyList<string> LedNames,
    IReadOnlyList<Rgb> Colors);

/// <summary>
/// The OpenRGB SDK wire format (TCP, little-endian). Every packet is a 16-byte header
/// ("ORGB", device index, packet id, payload size) followed by the payload.
/// </summary>
internal static class Protocol
{
    public const int HeaderSize = 16;

    /// <summary>
    /// Highest protocol version this client understands. Version 4 adds zone segments; later
    /// versions add fields we don't parse, so we ask the server to talk version 4 to us.
    /// </summary>
    public const uint ClientVersion = 4;

    private static ReadOnlySpan<byte> Magic => "ORGB"u8;

    public static byte[] Packet(uint deviceIndex, PacketId id, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        WriteHeader(buf, deviceIndex, id, (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static void WriteHeader(Span<byte> dest, uint deviceIndex, PacketId id, uint payloadSize)
    {
        Magic.CopyTo(dest);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], deviceIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], (uint)id);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[12..], payloadSize);
    }

    public static (uint DeviceIndex, PacketId Id, uint Size) ReadHeader(ReadOnlySpan<byte> header)
    {
        if (!header[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Bad OpenRGB packet magic.");
        return (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
                (PacketId)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));
    }

    public static byte[] UInt32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    public static byte[] NullTerminated(string s) => [.. Encoding.ASCII.GetBytes(s), 0];

    /// <summary>
    /// RGBCONTROLLER_RESIZEZONE: payload is exactly two int32s, zone index then new LED count.
    /// The server ignores any other payload size, saves its "sizes" profile, and sends no reply.
    /// </summary>
    public static byte[] ResizeZone(uint deviceIndex, int zone, int newSize)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, zone);
        BinaryPrimitives.WriteInt32LittleEndian(payload[4..], newSize);
        return Packet(deviceIndex, PacketId.ResizeZone, payload);
    }

    /// <summary>Total packet size for an UpdateLeds call with <paramref name="ledCount"/> colors.</summary>
    public static int UpdateLedsSize(int ledCount) => HeaderSize + 4 + 2 + 4 * ledCount;

    /// <summary>Write an UpdateLeds packet into <paramref name="dest"/> (sized by <see cref="UpdateLedsSize"/>).</summary>
    public static void WriteUpdateLeds(Span<byte> dest, uint deviceIndex, ReadOnlySpan<Rgb> colors)
    {
        int payload = 4 + 2 + 4 * colors.Length;
        WriteHeader(dest, deviceIndex, PacketId.UpdateLeds, (uint)payload);
        var p = dest[HeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)payload);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], (ushort)colors.Length);
        var c = p[6..];
        for (int i = 0; i < colors.Length; i++)
        {
            c[i * 4] = colors[i].R;
            c[i * 4 + 1] = colors[i].G;
            c[i * 4 + 2] = colors[i].B;
            c[i * 4 + 3] = 0;
        }
    }

    /// <summary>Parse a RequestControllerData response serialized at <paramref name="version"/>.</summary>
    public static ControllerData ParseController(ReadOnlySpan<byte> data, uint version)
    {
        var r = new Reader(data);
        uint size = r.U32();
        int type = r.I32();
        string name = r.Str();
        string vendor = version >= 1 ? r.Str() : "";
        string description = r.Str();
        string ver = r.Str();
        string serial = r.Str();
        string location = r.Str();

        ushort modeCount = r.U16();
        int activeMode = r.I32();
        var modes = new List<ModeData>(modeCount);
        for (int i = 0; i < modeCount; i++)
        {
            string modeName = r.Str();
            int value = r.I32();
            uint flags = r.U32();
            r.Skip(8);                        // speed min, max
            if (version >= 3) r.Skip(8);      // brightness min, max
            r.Skip(8);                        // colors min, max
            r.Skip(4);                        // speed
            if (version >= 3) r.Skip(4);      // brightness
            r.Skip(8);                        // direction, color mode
            r.Skip(r.U16() * 4);              // mode colors
            modes.Add(new ModeData(modeName, value, flags));
        }

        ushort zoneCount = r.U16();
        var zones = new List<ZoneData>(zoneCount);
        for (int i = 0; i < zoneCount; i++)
        {
            string zoneName = r.Str();
            int zoneType = r.I32();
            uint ledsMin = r.U32();
            uint ledsMax = r.U32();
            uint ledCount = r.U32();
            ushort matrixBytes = r.U16();
            uint h = 0, w = 0;
            uint[]? matrix = null;
            if (matrixBytes > 0)
            {
                h = r.U32();
                w = r.U32();
                matrix = new uint[h * w];
                for (int k = 0; k < matrix.Length; k++) matrix[k] = r.U32();
            }
            if (version >= 4)
            {
                ushort segments = r.U16();
                for (int s = 0; s < segments; s++)
                {
                    r.Str();                  // segment name
                    r.Skip(12);               // type, start, count
                }
            }
            zones.Add(new ZoneData(zoneName, zoneType, ledsMin, ledsMax, ledCount, h, w, matrix));
        }

        ushort ledTotal = r.U16();
        var ledNames = new List<string>(ledTotal);
        for (int i = 0; i < ledTotal; i++)
        {
            ledNames.Add(r.Str());
            r.Skip(4);                        // led value
        }
        ushort colorCount = r.U16();
        var colors = new Rgb[colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            uint c = r.U32();
            colors[i] = new Rgb((byte)c, (byte)(c >> 8), (byte)(c >> 16));
        }

        if (r.Position != size)
            throw new InvalidDataException($"Controller '{name}': parsed {r.Position} of {size} bytes (protocol v{version}).");

        return new ControllerData(type, name, vendor, description, ver, serial, location, activeMode, modes, zones, ledNames, colors);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public int Position { get; private set; }

        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]); Position += 4; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_data[Position..]); Position += 4; return v; }
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]); Position += 2; return v; }
        public void Skip(int n) => Position += n;

        /// <summary>Length-prefixed (u16, includes the terminating NUL) string.</summary>
        public string Str()
        {
            int len = U16();
            var bytes = _data.Slice(Position, len);
            Position += len;
            int nul = bytes.IndexOf((byte)0);
            return Encoding.UTF8.GetString(nul >= 0 ? bytes[..nul] : bytes);
        }
    }
}
