using MyRGB.Providers.Corsair;

namespace MyRGB.Tests;

/// <summary>
/// Expected bytes are USB captures of iCUE talking to a Vanguard Pro 96, with Windows' report id 0
/// in front (the capture shows the 1024 bytes on the wire; our buffers are 1025).
/// </summary>
public class CorsairTests
{
    /// <summary>The session number iCUE was given in the captures.</summary>
    private const byte Session = 0x02;

    [Fact]
    public void Open_session_matches_capture() =>
        Assert.Equal(Wire("0001001b012681ad17"), Bragi.OpenSession(0x2681ad17)[..10]);

    [Fact]
    public void Close_session_matches_capture() =>
        Assert.Equal(Wire("0001021b020000000002"), Bragi.CloseSession(Session)[..11]);

    [Fact]
    public void Session_id_from_reply() =>
        Assert.Equal(1, Bragi.SessionId(Wire("0000001b002681ad1701")));

    [Fact]
    public void Software_mode_matches_capture() =>
        Assert.Equal(Wire("0001020103000200000000"), Bragi.Set(Session, Bragi.PropertyMode, Bragi.ModeSoftware)[..12]);

    [Fact]
    public void Hardware_mode_matches_capture() =>
        Assert.Equal(Wire("0001020103000100000000"), Bragi.Set(Session, Bragi.PropertyMode, Bragi.ModeHardware)[..12]);

    [Fact]
    public void Open_lighting_matches_capture() =>
        Assert.Equal(Wire("0001020d01220000"), Bragi.Open(Session, Bragi.LightingHandle, Bragi.LightingResource)[..9]);

    [Fact]
    public void File_read_commands_have_web_hub_layout()
    {
        Assert.Equal(Wire("0001020d003d00"), Bragi.Open(Session, Bragi.FileBuffer, ScreenFiles.ResourceMapFile)[..8]);
        Assert.Equal(Wire("00010209" + "00"), Bragi.Describe(Session, Bragi.FileBuffer)[..6]);
        Assert.Equal(Wire("00010208" + "00"), Bragi.Read(Session, Bragi.FileBuffer)[..6]);
        Assert.Equal(Wire("000102020701"), Bragi.Get(Session, ScreenFiles.PropertyScreenIndex)[..7]);
    }

    [Fact]
    public void Described_size_and_property_value_from_reply()
    {
        // [id] 00 00 <session> 09 <status> d0 d1 <u32 size>
        Assert.Equal(0x01_4C3A, Bragi.DescribedSize(Wire("000002090000003a4c0100")));
        Assert.Equal(0x00A0_0000u, Bragi.PropertyValue(Wire("00000202000000a000")));
    }

    [Fact]
    public void Parses_profiles_list() =>
        Assert.Equal(new ushort[] { 0x6D60, 0x6D61 }, ScreenFiles.ParseProfilesList(Hex("13000200606d616d")).ProfileFileIds);

    [Fact]
    public void Parses_profile_screen_layout_file()
    {
        // 30 bytes of fields, name length 3, "abc\0", then four u16s; the fourth is the layout file.
        var f = new byte[30].Concat(Hex("0300")).Concat("abc\0"u8.ToArray()).Concat(Hex("0100020003003412")).ToArray();
        var p = ScreenFiles.ParseProfile(f)!;
        Assert.Equal("abc", p.Name);
        Assert.Equal(0x1234, p.ScreenLayoutFile);
    }

    [Fact]
    public void Parses_profile_properties_file()
    {
        // The real profile file read off the keyboard on 2026-11-14 (profile 28000).
        var f = Hex("1EF1000000000000616D0000626D636D0000646D00000000686D696D0000" + "2100")
            .Concat("VANGUARD PRO 96 Default Profile 1\0"u8.ToArray()).Concat(Hex("6A6D6B6D6C6D666D")).ToArray();
        var p = ScreenFiles.ParseProfile(f)!;
        Assert.Equal("VANGUARD PRO 96 Default Profile 1", p.Name);
        Assert.Equal(28001, p.PropertiesFile);
        Assert.Equal(28006, p.ScreenLayoutFile);
    }

    [Fact]
    public void Parses_properties_file()
    {
        var p = ScreenFiles.ParseProperties(Hex("3DD4" + "0200" + "0701" + "02000000" + "4900" + "00000000"));
        Assert.Equal(0xD43D, p.Header);
        Assert.Equal(2u, p[263]);
        Assert.Equal(0u, p[73]);
        Assert.Null(p[62]);
    }

    [Fact]
    public void Properties_file_stops_at_its_end() =>
        Assert.Single(ScreenFiles.ParseProperties(Hex("3DD4" + "0500" + "070102000000" + "4900")).Entries);

    // The keyboard's own properties file for profile 28000, read on 2026-11-14: not sorted by id.
    private const string RealProperties =
        "3DD40F00" + "4900FFFFFF00" + "4A0001000000" + "5D000000FF00" + "5E00FFFFFF00" + "CF00FFFFFF00" + "040000000000" +
        "D60000000000" + "6C0000000000" + "FB0000000000" + "FC0001000000" + "FD0000BEFF00" + "FE0004000000" + "FF0007000000" +
        "060100000000" + "070100000000";

    [Fact]
    public void Real_properties_file_round_trips_unchanged()
    {
        var bytes = Hex(RealProperties);
        var p = ScreenFiles.ParseProperties(bytes);
        Assert.Equal(15, p.Entries.Count);
        Assert.Equal(0u, p[ScreenFiles.PropertyScreenIndex]);
        Assert.Equal(bytes, ScreenFiles.BuildProperties(p.Entries));
    }

    [Fact]
    public void Setting_screen_index_changes_only_its_value()
    {
        var bytes = Hex(RealProperties);
        var after = ScreenFiles.BuildProperties(ScreenFiles.WithProperty(ScreenFiles.ParseProperties(bytes).Entries, ScreenFiles.PropertyScreenIndex, 2));
        Assert.Equal(bytes.Length, after.Length);
        var changed = Enumerable.Range(0, bytes.Length).Where(i => bytes[i] != after[i]).ToArray();
        Assert.Equal(new[] { bytes.Length - 4 }, changed);
        Assert.Equal(2, after[^4]);
    }

    [Fact]
    public void Setting_a_missing_property_appends_it() =>
        Assert.Equal(Hex("3DD4" + "0200" + "4900" + "00000000" + "0701" + "03000000"),
            ScreenFiles.BuildProperties(ScreenFiles.WithProperty([new(73, 0)], 263, 3)));

    [Fact]
    public void Profile_too_short_has_no_layout() =>
        Assert.Equal(0, ScreenFiles.ParseProfile(new byte[40])!.ScreenLayoutFile);

    [Fact]
    public void Parses_screen_layout_rows()
    {
        var l = ScreenFiles.ParseScreenLayout(Hex("370002" + "02606d616d" + "01626d"));
        Assert.Equal(0x37, l.Header);
        Assert.Equal(new ushort[] { 0x6D60, 0x6D61 }, l.Rows[0]);
        Assert.Equal(new ushort[] { 0x6D62 }, l.Rows[1]);
    }

    [Fact]
    public void Parses_resource_map_and_skips_empty_slots()
    {
        var m = ScreenFiles.ParseResourceMap(Hex("00000300" + "606d606d00000000" + "0000000000000000" + "616d616d01020304"));
        Assert.Equal(3, m.DeclaredCount);
        Assert.Equal(new ushort[] { 0x6D60, 0x6D61 }, m.Entries.Select(e => e.ResourceId));
        Assert.Equal(0x04030201u, m.Entries[1].Hash);
    }

    [Fact]
    public void Parses_static_and_animated_configs()
    {
        var s = ScreenFiles.ParseScreenConfig(Hex("3800606d000000000000000000000000"))!;
        Assert.Equal(new ushort[] { 0x6D60 }, s.ResourceIds);
        var a = ScreenFiles.ParseScreenConfig(Hex("66006400" + "0200" + "606d616d"))!;
        Assert.Equal(100, a.FrameDelayMs);
        Assert.Equal(new ushort[] { 0x6D60, 0x6D61 }, a.ResourceIds);
        Assert.Empty(ScreenFiles.ParseScreenConfig(Hex("01000000"))!.ResourceIds);
    }

    [Fact]
    public void Reads_bmp_resource_header()
    {
        var f = new byte[2 + 54];
        f[0] = ScreenFiles.BmpMarker;
        f[2] = (byte)'B'; f[3] = (byte)'M';
        BitConverter.TryWriteBytes(f.AsSpan(2 + 18), 248);
        BitConverter.TryWriteBytes(f.AsSpan(2 + 22), 170);
        BitConverter.TryWriteBytes(f.AsSpan(2 + 28), (ushort)16);
        BitConverter.TryWriteBytes(f.AsSpan(2 + 30), 3);
        Assert.Equal(new ScreenFiles.BmpInfo(248, 170, 16, 3), ScreenFiles.ParseBmpResource(f));
        Assert.Null(ScreenFiles.ParseBmpResource(new byte[10]));
    }

    [Fact]
    public void Close_matches_capture() =>
        Assert.Equal(Wire("0001020501020000"), Bragi.Close(Session, 0x02)[..9]);

    [Fact]
    public void Reports_are_full_length() =>
        Assert.Equal(1025, Bragi.Get(Session, Bragi.PropertyVendorId).Length);

    [Fact]
    public void Color_frame_header_matches_capture()
    {
        var rgb = new byte[VanguardPro96Layout.SlotCount * 3];
        rgb[41 * 3] = rgb[41 * 3 + 1] = rgb[41 * 3 + 2] = 0xFF; // Esc white
        var report = new byte[Bragi.ReportLength];
        Bragi.WriteColors(report, Session, Bragi.LightingHandle, rgb);

        Assert.Equal(Wire("00010206019d0100001200"), report[..Bragi.ColorOffset]); // length 0x19d = 2 + 137 * 3
        int esc = Bragi.ColorOffset + 41 * 3;
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, report[esc..(esc + 3)]);
        Assert.All(report[(Bragi.ColorOffset + rgb.Length)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Reply_matching()
    {
        var reply = Wire("0000020d00");
        Assert.True(Bragi.IsReplyTo(reply, Session, Bragi.OpenHandle));
        Assert.False(Bragi.IsReplyTo(reply, Session, Bragi.Write));
        Assert.Equal(0, Bragi.Status(reply));
        Assert.Equal(6, Bragi.Status(Wire("0000010d06")));
    }

    [Fact]
    public void Layout_has_the_102_lit_slots_from_the_capture()
    {
        int[] lit =
        [
            4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
            35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64,
            65, 66, 67, 68, 69, 70, 76, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99,
            105, 106, 107, 108, 110, 111, 122, 130, 131, 132, 133, 134, 135, 136,
        ];
        Assert.Equal(lit, VanguardPro96Layout.Keys.Select(k => k.Slot).Order());
        Assert.Equal("Esc", VanguardPro96Layout.Keys.Single(k => k.Slot == 41).Name);

        var leds = VanguardPro96Layout.Leds();
        Assert.All(leds, l => Assert.InRange(l.X, 0f, 1f));
        Assert.All(leds, l => Assert.InRange(l.Y, 0f, 1f));
    }

    [Fact]
    public void Keyboard_controls_follow_the_captured_reports()
    {
        // Captured from the Vanguard Pro 96 in software mode (report id included), padded to 65 bytes.
        static byte[] Report(string hex) => [.. Convert.FromHexString(hex), .. new byte[65 - hex.Length / 2]];
        var keys = new KeyboardControls.KeyState();
        KeyboardControls.Control[] Controls(string hex) => KeyboardControls.Parse(Report(hex), keys).Select(a => a.Control).ToArray();

        // Wheel: one click up, one down.
        Assert.Equal([(KeyboardControls.Control.VolumeSteps, 1)], KeyboardControls.Parse(Report("00000000056000010000000000000000000000000000000000"), keys));
        Assert.Equal([(KeyboardControls.Control.VolumeSteps, -1)], KeyboardControls.Parse(Report("00000000056000FFFFFFFF0000000000000000000000000000"), keys));

        // Wheel pressed (byte 21 bit 1), held, then released: one mute.
        Assert.Equal([KeyboardControls.Control.Mute], Controls("000000000200000000000000000000000000000000020000"));
        Assert.Empty(Controls("000000000200000000000000000000000000000000020000"));
        Assert.Empty(Controls("000000000200000000000000000000000000000000000000"));

        // Fn (byte 20 bit 2) with F3, F4 and F12.
        Assert.Equal([KeyboardControls.Control.BrightnessDown], Controls("000000000200000000000000100000000000000004000000"));
        Assert.Equal([KeyboardControls.Control.BrightnessUp], Controls("000000000200000000000000200000000000000004000000"));
        Assert.Equal([KeyboardControls.Control.DialMode], Controls("000000000200000000000000002000000000000004000000"));

        // F3 without Fn is just F3, and Fn+F5 (stop) the keyboard handles itself.
        Assert.Empty(Controls("000000000200000000000000000000000000000000000000"));
        Assert.Empty(Controls("000000000200000000000000100000000000000000000000"));
        Assert.Empty(Controls("000000000200000000000000400000000000000004000000"));
    }

    /// <summary>Captured wire bytes with the report id Windows adds in front.</summary>
    private static byte[] Wire(string hex) => [0, .. Convert.FromHexString(hex)];

    /// <summary>Plain bytes, for file contents (no report id).</summary>
    [Fact]
    public void Create_and_delete_file_commands()
    {
        Assert.Equal(Wire("00010B0B606D"), Bragi.Create(0x0B, 28000)[..7]);
        Assert.Equal(Wire("00010B0C616D"), Bragi.Delete(0x0B, 28001)[..7]);
    }

    [Fact]
    public void Small_file_write_is_one_begin_report()
    {
        var reports = Bragi.FileWrite(2, Bragi.FileBuffer, Hex("440001006A6D")).ToList();
        var r = Assert.Single(reports);
        Assert.Equal(Wire("0001020600" + "06000000" + "440001006A6D" + "00"), r[..17]);
        Assert.Equal(Bragi.ReportLength, r.Length);
    }

    [Fact]
    public void Large_file_write_splits_into_begin_and_continues()
    {
        var data = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var reports = Bragi.FileWrite(2, Bragi.FileBuffer, data).ToList();
        Assert.Equal(3, reports.Count); // 1015 + 1019 + 966
        Assert.Equal(Bragi.Write, reports[0][4]);
        Assert.Equal(3000u, BitConverter.ToUInt32(reports[0], 6));
        Assert.All(reports.Skip(1), r => Assert.Equal(Bragi.WriteContinue, r[4]));
        var joined = reports[0][10..].Concat(reports[1][6..]).Concat(reports[2][6..(6 + 966)]).ToArray();
        Assert.Equal(data, joined);
    }

    [Fact]
    public void Static_config_points_at_resource() =>
        Assert.Equal(Hex("3800616D000000000000000000000000"), ScreenFiles.BuildStaticConfig(28001));

    [Fact]
    public void Resource_map_gets_new_entry() =>
        Assert.Equal(Hex("44000100" + "6D6D6D6D00000000"),
            ScreenFiles.BuildResourceMap(0x44, [.. ScreenFiles.ParseResourceMap(Hex("44000000")).Entries, new(28013, 28013, 0)]));

    [Fact]
    public void Empty_layout_gets_row_zero()
    {
        var l = ScreenFiles.WithConfigInRow0(ScreenFiles.ParseScreenLayout(Hex("370000")), 28014);
        Assert.Equal(Hex("370001016E6D"), ScreenFiles.BuildScreenLayout(l));
    }

    [Fact]
    public void Layout_appends_to_row_zero_and_keeps_other_rows()
    {
        var l = ScreenFiles.WithConfigInRow0(ScreenFiles.ParseScreenLayout(Hex("370002" + "01606D" + "01616D")), 28014);
        Assert.Equal(Hex("370002" + "02606D6E6D" + "01616D"), ScreenFiles.BuildScreenLayout(l));
    }

    [Fact]
    public void Static_image_matches_web_hub_layout()
    {
        var rgb = new byte[ScreenFiles.Width * ScreenFiles.Height * 3];
        rgb[0] = 255;                                        // top-left pixel red
        rgb[^2] = 255;                                       // bottom-right pixel green
        var f = ScreenFiles.BuildStaticImage(rgb, 0x12345678);

        Assert.Equal(54 + 248 * 2 * 170 + 6, f.Length);      // 84380
        Assert.Equal(Hex("4800424D"), f[..4]);
        Assert.Equal((uint)f.Length, BitConverter.ToUInt32(f, 4));
        Assert.Equal(54u, BitConverter.ToUInt32(f, 12));
        Assert.Equal(248, BitConverter.ToInt32(f, 20));
        Assert.Equal(170, BitConverter.ToInt32(f, 24));      // positive, yet rows go top-down
        Assert.Equal(16, BitConverter.ToUInt16(f, 30));
        Assert.Equal(3u, BitConverter.ToUInt32(f, 32));
        Assert.Equal(Hex("00F8"), f[54..56]);                // first row first: red in RGB565 LE
        int last = 54 + 248 * 2 * 170 - 2;
        Assert.Equal(Hex("E007"), f[last..(last + 2)]);      // green
        Assert.Equal(Hex("0000" + "78563412"), f[^6..]);     // two zeros, then the time
        var bmp = ScreenFiles.ParseBmpResource(f)!;
        Assert.Equal((248, 170, 16, 3), (bmp.Width, bmp.Height, bmp.BitsPerPixel, bmp.Compression));
    }

    [Fact]
    public void Static_image_rejects_wrong_size() =>
        Assert.Throws<ArgumentException>(() => ScreenFiles.BuildStaticImage(new byte[30], 0));

    [Fact]
    public void Finds_ids_a_profile_points_at()
    {
        // From the real profile 28000: includes 28001..28012 minus the gaps, at even and odd offsets.
        var f = Hex("1EF1000000000000616D0000626D636D0000646D00000000686D696D0000" + "2100")
            .Concat("VANGUARD PRO 96 Default Profile 1\0"u8.ToArray()).Concat(Hex("6A6D6B6D6C6D666D")).ToArray();
        var ids = ScreenFiles.MentionedHostIds(f).ToHashSet();
        Assert.Superset(new HashSet<ushort> { 28001, 28002, 28003, 28004, 28006, 28008, 28009, 28010, 28011, 28012 }, ids);
        Assert.DoesNotContain((ushort)28005, ids);
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);
}
