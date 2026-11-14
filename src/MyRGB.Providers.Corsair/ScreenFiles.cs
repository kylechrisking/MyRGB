using System.Buffers.Binary;

namespace MyRGB.Providers.Corsair;

/// <summary>
/// The onboard files behind the Vanguard Pro 96's LCD. Formats come from CORSAIR Web Hub's code;
/// docs/LCD-PROTOCOL.txt has the details and what is still unverified. Every parser here is lenient: it
/// returns what it could read and never throws on a short or odd file, because these parse files we did
/// not write. The builders reproduce Web Hub's bytes exactly, quirks included.
/// </summary>
internal static class ScreenFiles
{
    /// <summary>The screen, in pixels (Web Hub's Vanguard ScreenService hard-codes it).</summary>
    public const int Width = 248, Height = 170;

    /// <summary>Host-made file ids come from this range. Web Hub never hands out 28049.</summary>
    public const ushort FirstHostFileId = 28000, LastHostFileId = 32767, SkippedFileId = 28049;
    /// <summary>The list of profile file ids.</summary>
    public const ushort ProfilesListFile = 15;

    /// <summary>The registry of every image resource on the device.</summary>
    public const ushort ResourceMapFile = 61;

    /// <summary>
    /// What the screen shows: a copy of the selected layout entry's config file. Web Hub's selectWidget
    /// writes it; adding an entry to the layout alone doesn't change the screen. (Not to be confused with
    /// property 62, free storage.)
    /// </summary>
    public const ushort CurrentScreenFile = 62;

    /// <summary>Bytes free for files. Web Hub keeps 5 MiB of it in reserve on this keyboard.</summary>
    public const ushort PropertyFreeStorage = 62;

    /// <summary>Which entry of the active profile's screen layout is showing.</summary>
    public const ushort PropertyScreenIndex = 263;

    /// <summary>First bytes of a resource file, saying what follows.</summary>
    public const byte BmpMarker = 0x48, GifMarker = 0x45;

    /// <summary>First bytes of a layout entry's config file: a static image, or a frame list (animation).</summary>
    public const byte StaticConfigMarker = 0x38, AnimationConfigMarker = 0x66;

    /// <summary>
    /// Every u16, at any offset, that falls in the host file-id range: ids a file may point at. Odd offsets
    /// too, because a profile's ids sit after a variable-length name; a stray match only skips an id.
    /// </summary>
    public static IEnumerable<ushort> MentionedHostIds(byte[] f)
    {
        for (int o = 0; o + 2 <= f.Length; o++)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(o));
            if (v is >= FirstHostFileId and <= LastHostFileId) yield return v;
        }
    }

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

    public sealed record Profile(string Name, ushort PropertiesFile, ushort ScreenLayoutFile);

    /// <summary>
    /// A profile file: <c>u16 profile | u32 cookie | u16 assignments | u16 properties | …</c> fixed u16
    /// fields up to a name length at 30, the name from 32 (with a trailing zero), then four u16s, and
    /// the fourth is the screen layout file. Null if the file is too short.
    /// </summary>
    public static Profile? ParseProfile(ReadOnlySpan<byte> f)
    {
        if (f.Length < 32) return null;
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(f[30..]);
        int after = 33 + nameLength; // Web Hub: name spans 32 .. 33+n (the name plus its terminator)
        string name = System.Text.Encoding.UTF8.GetString(f.Slice(32, Math.Min(nameLength, f.Length - 32))).TrimEnd('\0');
        ushort layout = after + 8 <= f.Length ? BinaryPrimitives.ReadUInt16LittleEndian(f[(after + 6)..]) : (ushort)0;
        return new(name, BinaryPrimitives.ReadUInt16LittleEndian(f[8..]), layout);
    }

    /// <summary>The header Web Hub writes at the top of a profile's properties file.</summary>
    public const ushort PropertiesFileHeader = 0xD43D;

    public sealed record PropertyEntry(ushort Id, uint Value);

    public sealed record PropertiesFile(ushort Header, int DeclaredCount, IReadOnlyList<PropertyEntry> Entries)
    {
        /// <summary>The value of a property, or null if the file doesn't have it.</summary>
        public uint? this[ushort id] => Entries.FirstOrDefault(e => e.Id == id)?.Value;
    }

    /// <summary>
    /// A profile's properties file: <c>u16 header (0xD43D) | u16 count | count × { u16 id, u32 value }</c>,
    /// all little-endian. Holds per-profile settings the keyboard won't take through Set property, the
    /// screen index (263) among them. Entries are kept in file order, which the firmware doesn't sort.
    /// </summary>
    public static PropertiesFile ParseProperties(ReadOnlySpan<byte> f)
    {
        if (f.Length < 4) return new(0, 0, []);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        var entries = new List<PropertyEntry>();
        for (int i = 0, o = 4; i < count && o + 6 <= f.Length; i++, o += 6)
            entries.Add(new(BinaryPrimitives.ReadUInt16LittleEndian(f[o..]), BinaryPrimitives.ReadUInt32LittleEndian(f[(o + 2)..])));
        return new(BinaryPrimitives.ReadUInt16LittleEndian(f), count, entries);
    }

    /// <summary>
    /// Sets one property: replaced where it already is, or appended at the end, so every other byte of
    /// the file stays as the keyboard wrote it. (Web Hub instead rewrites the whole file sorted by id.)
    /// </summary>
    public static IReadOnlyList<PropertyEntry> WithProperty(IEnumerable<PropertyEntry> entries, ushort id, uint value)
    {
        var list = entries.ToList();
        int at = list.FindIndex(e => e.Id == id);
        if (at >= 0) list[at] = new(id, value); else list.Add(new(id, value));
        return list;
    }

    /// <summary>The bytes of a properties file, entries in the order given. Nothing writes this to the keyboard yet.</summary>
    public static byte[] BuildProperties(IReadOnlyCollection<PropertyEntry> entries)
    {
        var f = new byte[4 + entries.Count * 6];
        BinaryPrimitives.WriteUInt16LittleEndian(f, PropertiesFileHeader);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), (ushort)entries.Count);
        int o = 4;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(o), e.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(o + 2), e.Value);
            o += 6;
        }
        return f;
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

    /// <summary>
    /// A layout with <paramref name="configFile"/> added to the end of row 0. Row 0 is created when the
    /// layout has no rows, which is how this keyboard ships (Web Hub would throw there).
    /// </summary>
    public static ScreenLayout WithConfigInRow0(ScreenLayout layout, ushort configFile)
    {
        var rows = layout.Rows.Select(r => (IReadOnlyList<ushort>)r.ToList()).ToList();
        if (rows.Count == 0) rows.Add([configFile]);
        else rows[0] = [.. rows[0], configFile];
        return new(layout.Header == 0 ? (ushort)0x0037 : layout.Header, rows);
    }

    /// <summary><c>u16 header | u8 rows | rows × { u8 count, count × u16 }</c>, as Web Hub writes it.</summary>
    public static byte[] BuildScreenLayout(ScreenLayout layout)
    {
        var f = new List<byte> { (byte)layout.Header, (byte)(layout.Header >> 8), (byte)layout.Rows.Count };
        foreach (var row in layout.Rows)
        {
            f.Add((byte)row.Count);
            foreach (var id in row) { f.Add((byte)id); f.Add((byte)(id >> 8)); }
        }
        return [.. f];
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
    /// <c>u16 header | u16 count | entries</c>. A new resource is entered as <c>{id, id, 00000000}</c>, which
    /// is what Web Hub writes.
    /// </summary>
    public static byte[] BuildResourceMap(ushort header, IReadOnlyList<ResourceEntry> entries)
    {
        var f = new byte[4 + entries.Count * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(f, header);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), (ushort)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var o = f.AsSpan(4 + i * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(o, entries[i].ResourceId);
            BinaryPrimitives.WriteUInt16LittleEndian(o[2..], entries[i].Address);
            BinaryPrimitives.WriteUInt32LittleEndian(o[4..], entries[i].Hash);
        }
        return f;
    }

    /// <summary>A static image's config file: 16 bytes, <c>38 00 &lt;u16 resource&gt;</c> then zeros.</summary>
    public static byte[] BuildStaticConfig(ushort resourceId)
    {
        var f = new byte[16];
        f[0] = StaticConfigMarker;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), resourceId);
        return f;
    }

    /// <summary>
    /// A static image resource exactly as Web Hub's Vanguard ScreenService builds it from a 248×170 picture
    /// (<paramref name="rgb"/>: R,G,B per pixel, top row first). Its quirks are kept on purpose, since this is
    /// the layout the firmware is known to take:
    /// the file starts <c>48 00 'B' 'M'</c> and every header offset counts from the <c>48</c>; the header says
    /// bottom-up (positive height) but rows are written top-down; pixels start at byte 54, on top of the
    /// last header field and the RGB565 masks just written there; two zero bytes, then a u32 unix time end it.
    /// </summary>
    public static byte[] BuildStaticImage(ReadOnlySpan<byte> rgb, uint unixTime)
    {
        if (rgb.Length != Width * Height * 3) throw new ArgumentException($"Expected {Width}×{Height} RGB pixels.", nameof(rgb));
        const int pixelsAt = 54, stride = Width * 2; // 496: already a multiple of 4, so no row padding
        int pixelBytes = stride * Height, total = pixelsAt + pixelBytes + 6;
        var f = new byte[total];
        var s = f.AsSpan();
        s[0] = BmpMarker; s[2] = (byte)'B'; s[3] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], pixelsAt);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], Height);
        BinaryPrimitives.WriteUInt16LittleEndian(s[28..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[30..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 3); // BI_BITFIELDS
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)pixelBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0xF800); // overwritten by pixels below, as in Web Hub
        BinaryPrimitives.WriteUInt32LittleEndian(s[60..], 0x07E0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[64..], 0x001F);
        for (int p = 0, o = pixelsAt; p < Width * Height; p++, o += 2)
        {
            int r = rgb[p * 3], g = rgb[p * 3 + 1], b = rgb[p * 3 + 2];
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)((r >> 3) << 11 | (g >> 2) << 5 | b >> 3));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[(total - 4)..], unixTime);
        return f;
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
