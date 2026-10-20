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
}
