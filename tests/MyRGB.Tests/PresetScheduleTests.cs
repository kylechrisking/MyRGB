using MyRGB.Core;

namespace MyRGB.Tests;

public class PresetScheduleTests
{
    private static readonly ScheduledPreset[] Day =
    [
        new("08:00", "ocean"),
        new("18:00", "aurora"),
        new("22:30", "fire"),
    ];

    private static DateTime At(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0);

    [Theory]
    [InlineData(9, 0, "ocean")]
    [InlineData(18, 0, "aurora")]
    [InlineData(23, 0, "fire")]
    [InlineData(3, 0, "fire")] // yesterday's last one still holds in the early hours
    public void Current_is_the_last_time_that_came_round(int h, int m, string preset) =>
        Assert.Equal(preset, PresetSchedule.Current(Day, At(22, h, m))!.PresetId);

    [Fact]
    public void Due_only_when_a_time_is_crossed()
    {
        Assert.Null(PresetSchedule.Due(Day, At(22, 17, 58), At(22, 17, 59)));
        Assert.Equal("aurora", PresetSchedule.Due(Day, At(22, 17, 59), At(22, 18, 0))!.PresetId);
        Assert.Null(PresetSchedule.Due(Day, At(22, 18, 0), At(22, 18, 1)));
    }

    [Fact]
    public void Due_across_midnight_and_after_a_long_gap()
    {
        Assert.Equal("ocean", PresetSchedule.Due(Day, At(22, 23, 0), At(23, 8, 1))!.PresetId);
        Assert.Equal("fire", PresetSchedule.Due(Day, At(20, 12, 0), At(22, 23, 0))!.PresetId); // asleep for days: the latest wins
        Assert.Null(PresetSchedule.Due(Day, At(22, 9, 0), At(22, 9, 0)));
    }

    [Fact]
    public void Empty_or_bad_times()
    {
        Assert.Null(PresetSchedule.Current([], At(22, 12)));
        Assert.Equal(new TimeOnly(0, 0), new ScheduledPreset("noon", "x").Time);
    }
}
