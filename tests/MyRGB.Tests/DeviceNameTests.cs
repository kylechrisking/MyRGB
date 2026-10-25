using MyRGB.Providers.OpenRgb;

namespace MyRGB.Tests;

public class DeviceNameTests
{
    [Theory]
    [InlineData("G733 Gaming HeadG733 Gaming Head", "G733 Gaming Head")]
    [InlineData("G733 gaming headG733 GAMING HEAD", "G733 gaming head")]
    [InlineData("Corsair VengeanceCorsair Vengeance", "Corsair Vengeance")]
    [InlineData("G733 Gaming Head", "G733 Gaming Head")]
    [InlineData("Keyboard", "Keyboard")]
    [InlineData("Fan Hub", "Fan Hub")] // halves differ, so nothing goes
    public void Doubled_names_show_one_copy(string raw, string expected)
    {
        Assert.Equal(expected, OpenRgbDevice.CleanName(raw));
    }

    [Fact]
    public void A_name_that_merely_repeats_a_word_stays_whole()
    {
        // Halves differ ("Boom" vs "box"), so nothing is removed.
        Assert.Equal("Boombox", OpenRgbDevice.CleanName("Boombox"));
    }
}
