using System.Buffers.Binary;

namespace MyRGB.Providers.Corsair;

/// <summary>
/// The command protocol newer Corsair peripherals speak on their vendor HID interface (usage page 0xFF42).
/// Worked out from USB captures of iCUE driving a Vanguard Pro 96; nothing here is taken from other drivers.
/// </summary>
/// <remarks>
/// On the wire every packet is 1024 bytes, <c>00 01 &lt;target&gt; &lt;command&gt; args…</c>; Windows adds
/// report id 0 in front, so our buffers are 1025 bytes. Replies come back on the same interface as
/// <c>00 00 &lt;target&gt; &lt;command&gt; &lt;status&gt; data…</c> (after the report id).
/// </remarks>
internal static class Bragi
{
    public const int ReportLength = 1025;
    public const byte HostToDevice = 0x01;

    /// <summary>
    /// Sessions are opened on target 0; every other command goes to the session number the device hands
    /// back (iCUE's sessions happened to be 1 and 2). Commands to an unopened session get status 0x0F.
    /// </summary>
    public const byte SessionTarget = 0x00;

    public const byte SetProperty = 0x01, GetProperty = 0x02, CloseHandle = 0x05, Write = 0x06, OpenHandle = 0x0D, Session = 0x1B;

    /// <summary>Reading a file opened on a buffer: describe gives its size, read returns the next chunk.</summary>
    public const byte ReadBuffer = 0x08, DescribeBuffer = 0x09;

    /// <summary>
    /// The handle slot used for onboard file I/O, kept apart from <see cref="LightingHandle"/>. CORSAIR
    /// Web Hub uses buffer 0 for files (see docs/LCD-PROTOCOL.txt).
    /// </summary>
    public const byte FileBuffer = 0x00;

    /// <summary>Where a reply's data starts: after the report id, the 4-byte header and the status.</summary>
    public const int ReplyDataOffset = 6;

    public const byte PropertyMode = 0x03;
    public const uint ModeHardware = 1, ModeSoftware = 2;
    public const byte PropertyVendorId = 0x11, PropertyProductId = 0x12;

    /// <summary>The per-LED color resource, opened on <see cref="LightingHandle"/> like iCUE does.</summary>
    public const ushort LightingResource = 0x22;
    public const byte LightingHandle = 0x01;
    public const ushort ColorDataType = 0x0012;

    public static byte[] Command(byte target, byte command, ReadOnlySpan<byte> args)
    {
        var r = new byte[ReportLength];
        r[2] = HostToDevice;
        r[3] = target;
        r[4] = command;
        args.CopyTo(r.AsSpan(5));
        return r;
    }

    /// <summary><c>1B 01 &lt;4-byte client token&gt;</c> on target 0. The reply carries the new session number.</summary>
    public static byte[] OpenSession(uint token)
    {
        Span<byte> a = stackalloc byte[5];
        a[0] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(a[1..], token);
        return Command(SessionTarget, Session, a);
    }

    /// <summary>Session number from an <see cref="OpenSession"/> reply: after status and the echoed token.</summary>
    public static byte SessionId(ReadOnlySpan<byte> reply) => reply[10];

    /// <summary><c>1B 02 00 00 00 00 &lt;session&gt;</c>, sent on the session being closed.</summary>
    public static byte[] CloseSession(byte session) => Command(session, Session, [0x02, 0, 0, 0, 0, session]);

    /// <summary><c>01 &lt;prop&gt; 00 &lt;u32 value&gt;</c>.</summary>
    public static byte[] Set(byte target, byte property, uint value)
    {
        Span<byte> a = stackalloc byte[6];
        a[0] = property;
        BinaryPrimitives.WriteUInt32LittleEndian(a[2..], value);
        return Command(target, SetProperty, a);
    }

    /// <summary><c>02 &lt;u16 prop LE&gt;</c>. Property ids go past 255 (the screen index is 263).</summary>
    public static byte[] Get(byte target, ushort property) => Command(target, GetProperty, [(byte)property, (byte)(property >> 8)]);

    /// <summary>The u32 value from a <see cref="Get"/> reply.</summary>
    public static uint PropertyValue(ReadOnlySpan<byte> reply) => BinaryPrimitives.ReadUInt32LittleEndian(reply[ReplyDataOffset..]);

    /// <summary><c>09 &lt;buffer&gt;</c>: the size of the file opened on that buffer.</summary>
    public static byte[] Describe(byte target, byte buffer) => Command(target, DescribeBuffer, [buffer]);

    /// <summary>File size from a <see cref="Describe"/> reply: a u32 two bytes into the data.</summary>
    public static int DescribedSize(ReadOnlySpan<byte> reply) => (int)BinaryPrimitives.ReadUInt32LittleEndian(reply[(ReplyDataOffset + 2)..]);

    /// <summary><c>08 &lt;buffer&gt;</c>: the next chunk of the file. The chunk fills the rest of the reply.</summary>
    public static byte[] Read(byte target, byte buffer) => Command(target, ReadBuffer, [buffer]);

    /// <summary><c>0D &lt;handle&gt; &lt;u16 resource&gt;</c>.</summary>
    public static byte[] Open(byte target, byte handle, ushort resource) =>
        Command(target, OpenHandle, [handle, (byte)resource, (byte)(resource >> 8)]);

    /// <summary><c>05 01 &lt;handle&gt;</c>.</summary>
    public static byte[] Close(byte target, byte handle) => Command(target, CloseHandle, [1, handle]);

    /// <summary>
    /// One full color frame: <c>06 &lt;handle&gt; &lt;u32 length&gt; 12 00 RGB RGB…</c>, where length counts
    /// the 2-byte data type plus the colors. <paramref name="rgb"/> is one R,G,B triple per slot.
    /// </summary>
    public static void WriteColors(Span<byte> report, byte target, byte handle, ReadOnlySpan<byte> rgb)
    {
        if (report.Length < ReportLength) throw new ArgumentException("Report buffer too small.", nameof(report));
        if (ColorOffset + rgb.Length > ReportLength) throw new ArgumentException("Too many colors for one report.", nameof(rgb));
        report[..ReportLength].Clear();
        report[2] = HostToDevice;
        report[3] = target;
        report[4] = Write;
        report[5] = handle;
        BinaryPrimitives.WriteUInt32LittleEndian(report[6..], (uint)(2 + rgb.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(report[10..], ColorDataType);
        rgb.CopyTo(report[ColorOffset..]);
    }

    /// <summary>Where the first color byte sits in a <see cref="WriteColors"/> report.</summary>
    public const int ColorOffset = 12;

    /// <summary>A reply is <c>[report id] 00 00 &lt;target&gt; &lt;command&gt; &lt;status&gt;</c>; status 0 means success.</summary>
    public static bool IsReplyTo(ReadOnlySpan<byte> reply, byte target, byte command) =>
        reply.Length > 5 && reply[2] == 0 && reply[3] == target && reply[4] == command;

    public static byte Status(ReadOnlySpan<byte> reply) => reply[5];
}
