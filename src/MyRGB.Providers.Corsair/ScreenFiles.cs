using System.Buffers.Binary;

namespace MyRGB.Providers.Corsair;

/// <summary>
/// The onboard files behind the Vanguard Pro 96's LCD, read-only for now. Formats come from CORSAIR Web
/// Hub's code; docs/LCD-PROTOCOL.txt has the details and what is still unverified. Every parser here is
/// lenient: it returns what it could read and never throws on a short or odd file, because these
/// parse files we did not write.
/// </summary>
internal static class ScreenFiles
{
    /// <summary>The list of profile file ids.</summary>
    public const ushort ProfilesListFile = 15;

    /// <summary>The registry of every image resource on the device.</summary>
    public const ushort ResourceMapFile = 61;

    /// <summary>Bytes free for files. Web Hub keeps 5 MiB of it in reserve on this keyboard.</summary>
    public const ushort PropertyFreeStorage = 62;

    /// <summary>Which entry of the active profile's screen layout is showing.</summary>
    public const ushort PropertyScreenIndex = 263;

    /// <summary>First bytes of a resource file, saying what follows.</summary>
    public const byte BmpMarker = 0x48, GifMarker = 0x45;

    /// <summary>First bytes of a layout entry's config file: a static image, or a frame list (animation).</summary>
    public const byte StaticConfigMarker = 0x38, AnimationConfigMarker = 0x66;

    public sealed record ProfilesList(ushort Header, IReadOnlyList<ushort> ProfileFileIds);

    /// <summary><c>u16 header | u16 count | count × u16 profile file id</c>.</summary>
    public static ProfilesList ParseProfilesList(ReadOnlySpan<byte> f)
    {
        if (f.Length < 4) return new(0, []);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        var ids = new List<ushort>();
        for (int i = 0, o = 4; i < count && o + 2 <= f.Length; i++, o += 2)
            ids.Add(BinaryPrimitives.ReadUInt16LittleEndian(f[o..]));
        return new(BinaryPrimitives.ReadUInt16LittleEndian(f), ids);
    }

    public sealed record Profile(string Name, ushort ScreenLayoutFile);

    /// <summary>
    /// A profile file: fixed u16 fields up to a name length at 30, the name from 32 (with a trailing
    /// zero), then four u16s, and the fourth is the screen layout file. Null if the file is too short.
    /// </summary>
    public static Profile? ParseProfile(ReadOnlySpan<byte> f)
    {
        if (f.Length < 32) return null;
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(f[30..]);
        int after = 33 + nameLength; // Web Hub: name spans 32 .. 33+n (the name plus its terminator)
        string name = System.Text.Encoding.UTF8.GetString(f.Slice(32, Math.Min(nameLength, f.Length - 32))).TrimEnd('\0');
        ushort layout = after + 8 <= f.Length ? BinaryPrimitives.ReadUInt16LittleEndian(f[(after + 6)..]) : (ushort)0;
        return new(name, layout);
    }

    public sealed record ScreenLayout(ushort Header, IReadOnlyList<IReadOnlyList<ushort>> Rows);

    /// <summary><c>u16 header | u8 rows | rows × { u8 count, count × u16 config file id }</c>.</summary>
    public static ScreenLayout ParseScreenLayout(ReadOnlySpan<byte> f)
    {
        if (f.Length < 3) return new(0, []);
        var rows = new List<IReadOnlyList<ushort>>();
        int o = 3;
        for (int r = 0; r < f[2] && o < f.Length; r++)
        {
            int count = f[o++];
            var ids = new List<ushort>();
            for (int i = 0; i < count && o + 2 <= f.Length; i++, o += 2)
                ids.Add(BinaryPrimitives.ReadUInt16LittleEndian(f[o..]));
            rows.Add(ids);
        }
        return new(BinaryPrimitives.ReadUInt16LittleEndian(f), rows);
    }

    public sealed record ResourceEntry(ushort ResourceId, ushort Address, uint Hash);

    public sealed record ResourceMap(ushort Header, int DeclaredCount, IReadOnlyList<ResourceEntry> Entries);

    /// <summary>
    /// <c>u16 header | u16 count | count × { u16 id, u16 address, u8[4] hash }</c>. Entries with id 0 are
    /// empty slots and are left out.
    /// </summary>
    public static ResourceMap ParseResourceMap(ReadOnlySpan<byte> f)
    {
        if (f.Length < 4) return new(0, 0, []);
        // Web Hub reads the count as a single byte at offset 2 and writes it back as a u16; read the u16.
        int count = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        var entries = new List<ResourceEntry>();
        for (int i = 0, o = 4; i < count && o + 8 <= f.Length; i++, o += 8)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(f[o..]);
            if (id == 0) continue;
            entries.Add(new(id, BinaryPrimitives.ReadUInt16LittleEndian(f[(o + 2)..]), BinaryPrimitives.ReadUInt32LittleEndian(f[(o + 4)..])));
        }
        return new(BinaryPrimitives.ReadUInt16LittleEndian(f), count, entries);
    }

    /// <summary>
    /// What a layout entry's config file points at. A static image is <c>38 00 &lt;u16 resource&gt;</c>;
    /// an animation is <c>66 00 &lt;u16 delay ms&gt; &lt;u16 frames&gt; frames × u16 resource</c>.
    /// </summary>
    public sealed record ScreenConfig(byte Kind, int FrameDelayMs, IReadOnlyList<ushort> ResourceIds);

    public static ScreenConfig? ParseScreenConfig(ReadOnlySpan<byte> f)
    {
        if (f.Length < 4) return null;
        switch (f[0])
        {
            case StaticConfigMarker:
                return new(f[0], 0, [BinaryPrimitives.ReadUInt16LittleEndian(f[2..])]);
            case AnimationConfigMarker when f.Length >= 6:
                int delay = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
                int frames = BinaryPrimitives.ReadUInt16LittleEndian(f[4..]);
                var ids = new List<ushort>();
                for (int i = 0, o = 6; i < frames && o + 2 <= f.Length; i++, o += 2)
                    ids.Add(BinaryPrimitives.ReadUInt16LittleEndian(f[o..]));
                return new(f[0], delay, ids);
            default:
                return new(f[0], 0, []); // a built-in screen mode we don't know yet
        }
    }

    /// <summary>The BMP header of an image resource (<c>48 00 BM…</c>), enough to check size and format.</summary>
    public sealed record BmpInfo(int Width, int Height, int BitsPerPixel, int Compression);

    public static BmpInfo? ParseBmpResource(ReadOnlySpan<byte> f)
    {
        if (f.Length < 2 + 54 || f[0] != BmpMarker || f[2] != (byte)'B' || f[3] != (byte)'M') return null;
        var b = f[2..];
        return new(BinaryPrimitives.ReadInt32LittleEndian(b[18..]), BinaryPrimitives.ReadInt32LittleEndian(b[22..]),
            BinaryPrimitives.ReadUInt16LittleEndian(b[28..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(b[30..]));
    }
}
