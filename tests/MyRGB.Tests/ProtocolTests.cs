using System.Buffers.Binary;
using System.Text;
using MyRGB.Core;
using MyRGB.Providers.OpenRgb;

namespace MyRGB.Tests;

public class ProtocolTests
{
    [Fact]
    public void Header_round_trips()
    {
        var packet = Protocol.Packet(3, PacketId.RequestControllerData, [1, 2, 3, 4]);
        var (dev, id, size) = Protocol.ReadHeader(packet);
        Assert.Equal(3u, dev);
        Assert.Equal(PacketId.RequestControllerData, id);
        Assert.Equal(4u, size);
        Assert.Equal("ORGB"u8.ToArray(), packet[..4]);
    }

    [Fact]
    public void UpdateLeds_packet_layout()
    {
        Rgb[] colors = [new(1, 2, 3), new(4, 5, 6)];
        var buf = new byte[Protocol.UpdateLedsSize(colors.Length)];
        Protocol.WriteUpdateLeds(buf, 7, colors);

        var (dev, id, size) = Protocol.ReadHeader(buf);
        Assert.Equal(7u, dev);
        Assert.Equal(PacketId.UpdateLeds, id);
        Assert.Equal((uint)(4 + 2 + 8), size);
        Assert.Equal(size, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(20)));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 }, buf[22..]);
    }

    [Fact]
    public void ResizeZone_packet_layout()
    {
        var packet = Protocol.ResizeZone(2, 1, 32);

        var (dev, id, size) = Protocol.ReadHeader(packet);
        Assert.Equal(2u, dev);
        Assert.Equal(1000u, (uint)id);
        Assert.Equal(8u, size); // the server rejects anything but 2 * sizeof(int)
        Assert.Equal(24, packet.Length);
        Assert.Equal(new byte[] { 1, 0, 0, 0, 32, 0, 0, 0 }, packet[16..]);
    }

    [Fact]
    public void ResizeZone_encodes_little_endian_int32()
    {
        var packet = Protocol.ResizeZone(0, 0x0102, 0x01020304);
        Assert.Equal(0x0102, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(16)));
        Assert.Equal(0x01020304, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(20)));
    }

    [Fact]
    public void Zone_min_max_parsed_and_resizable_flagged()
    {
        var c = Protocol.ParseController(new ControllerBuilder(4).Build(), 4);
        Assert.Equal((5u, 5u, false), (c.Zones[0].LedsMin, c.Zones[0].LedsMax, c.Zones[0].Resizable));
        Assert.Equal((0u, 204u, true), (c.Zones[1].LedsMin, c.Zones[1].LedsMax, c.Zones[1].Resizable));
    }

    [Fact]
    public void Device_lists_resizable_zones_and_id_ignores_led_count()
    {
        var data = Protocol.ParseController(new ControllerBuilder(4).Build(), 4);
        var device = new OpenRgbDevice(null!, 0, data);
        Assert.Equal([new MyRGB.Core.Devices.ResizableZone("Logo", 3, 0, 204)], device.ResizableZones);
        Assert.Equal(OpenRgbDevice.MakeId(data), device.Id);
        Assert.Equal(device.Id, OpenRgbDevice.MakeId(data with { Zones = [], LedNames = [] }));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void Parses_controller_at_each_version(uint version)
    {
        var data = new ControllerBuilder(version).Build();
        var c = Protocol.ParseController(data, version);

        Assert.Equal("Test Keyboard", c.Name);
        Assert.Equal(version >= 1 ? "Razer" : "", c.Vendor);
        Assert.Equal(5, c.Type);
        Assert.Equal(["Direct", "Static"], c.Modes.Select(m => m.Name));
        Assert.Equal(2, c.Zones.Count);
        Assert.Equal(2u, c.Zones[0].MatrixHeight);
        Assert.Equal(3u, c.Zones[0].MatrixWidth);
        Assert.Null(c.Zones[1].Matrix);
        Assert.Equal(8, c.LedNames.Count);
    }

    [Fact]
    public void Matrix_zone_becomes_a_grid()
    {
        var c = Protocol.ParseController(new ControllerBuilder(4).Build(), 4);
        var (leds, zones) = LedLayout.Build(c);

        Assert.Equal(8, leds.Count);
        Assert.Equal([("Keys", 0, 5), ("Logo", 5, 3)], zones.Select(z => (z.Name, z.StartIndex, z.Count)));
        // Matrix (2 rows of 3, one empty cell): LED 0 top-left, LED 2 top-right, LED 3 bottom-left.
        Assert.Equal((0f, 0f), (leds[0].X, leds[0].Y));
        Assert.Equal(1f, leds[2].X);
        Assert.True(leds[3].Y > leds[0].Y);
        // Linear zone sits below the matrix and spans the width.
        Assert.True(leds[5].Y > leds[3].Y);
        Assert.Equal(0f, leds[5].X);
        Assert.Equal(1f, leds[7].X);
    }

    [Theory]
    [InlineData(@"HID: \\?\hid#vid_1532&pid_00ab&mi_02#7&abc", "usb:1532:00ab")]
    [InlineData(@"HID: \\?\HID#VID_1B1C&PID_0C1A#xyz", "usb:1b1c:0c1a")]
    [InlineData("I2C: PIIX4, address 0x58", null)]
    public void Usb_key_from_location(string location, string? expected) =>
        Assert.Equal(expected, OpenRgbDevice.UsbKey(location));

    /// <summary>Writes a controller description the way the OpenRGB server serializes it.</summary>
    private sealed class ControllerBuilder(uint version)
    {
        private readonly MemoryStream _ms = new();

        public byte[] Build()
        {
            using var w = new BinaryWriter(_ms, Encoding.UTF8, leaveOpen: true);
            void Str(string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); w.Write((ushort)b.Length); w.Write(b); }

            w.Write(0u);              // size, patched below
            w.Write(5);               // keyboard
            Str("Test Keyboard");
            if (version >= 1) Str("Razer");
            Str("desc"); Str("1.0"); Str("SN123"); Str(@"HID: \\?\hid#vid_1532&pid_0001");

            w.Write((ushort)2);       // modes
            w.Write(0);               // active mode
            foreach (var mode in new[] { "Direct", "Static" })
            {
                Str(mode);
                w.Write(1); w.Write(0u);
                w.Write(0u); w.Write(0u);
                if (version >= 3) { w.Write(0u); w.Write(100u); }
                w.Write(0u); w.Write(0u);
                w.Write(0u);
                if (version >= 3) w.Write(100u);
                w.Write(0u); w.Write(0u);
                w.Write((ushort)1); w.Write(0xFF0000u);
            }

            w.Write((ushort)2);       // zones
            Str("Keys");
            w.Write(2); w.Write(5u); w.Write(5u); w.Write(5u);
            uint[] map = [0, 1, 2, 3, 4, 0xFFFFFFFF];
            w.Write((ushort)(8 + map.Length * 4));
            w.Write(2u); w.Write(3u);
            foreach (var m in map) w.Write(m);
            if (version >= 4) { w.Write((ushort)1); Str("seg"); w.Write(1); w.Write(0u); w.Write(5u); }

            Str("Logo");
            w.Write(1); w.Write(0u); w.Write(204u); w.Write(3u);   // resizable: min 0, max 204
            w.Write((ushort)0);
            if (version >= 4) w.Write((ushort)0);

            w.Write((ushort)8);
            for (int i = 0; i < 8; i++) { Str($"LED {i}"); w.Write((uint)i); }
            w.Write((ushort)8);
            for (int i = 0; i < 8; i++) w.Write(0u);

            w.Flush();
            var bytes = _ms.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
            return bytes;
        }
    }
}
