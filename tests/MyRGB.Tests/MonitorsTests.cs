using MyRGB.Core;

namespace MyRGB.Tests;

public class MonitorsTests
{
    private static readonly MonitorRules All = new(
        FollowLights: true, OffLevel: 10,
        NightDim: true, NightLevel: 25,
        PresetLevels: [new("movie", 40)]);

    [Fact]
    public void With_no_rules_the_slider_level_is_used() =>
        Assert.Equal(80, MonitorLevels.Target(new MonitorRules(), 80, lightsOn: false, night: true, "movie"));

    [Fact]
    public void A_preset_level_beats_the_slider()
    {
        Assert.Equal(40, MonitorLevels.Target(All, 80, lightsOn: true, night: false, "movie"));
        Assert.Equal(80, MonitorLevels.Target(All, 80, lightsOn: true, night: false, "fire"));
        Assert.Equal(80, MonitorLevels.Target(All, 80, lightsOn: true, night: false, null));
    }

    [Fact]
    public void Lights_off_beats_a_preset_and_night_beats_both()
    {
        Assert.Equal(10, MonitorLevels.Target(All, 80, lightsOn: false, night: false, "movie"));
        Assert.Equal(25, MonitorLevels.Target(All, 80, lightsOn: false, night: true, "movie"));
        Assert.Equal(25, MonitorLevels.Target(All, 80, lightsOn: true, night: true, null));
    }

    [Fact]
    public void Rules_that_are_off_are_skipped()
    {
        var onlyPresets = All with { FollowLights = false, NightDim = false };
        Assert.Equal(40, MonitorLevels.Target(onlyPresets, 80, lightsOn: false, night: true, "movie"));
    }

    [Fact]
    public void Levels_are_kept_in_range()
    {
        Assert.Equal(100, MonitorLevels.Target(new MonitorRules(), 150, true, false, null));
        Assert.Equal(0, MonitorLevels.Target(All with { OffLevel = -5 }, 50, false, false, null));
    }
}
