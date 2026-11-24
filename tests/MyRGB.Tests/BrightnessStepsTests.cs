using MyRGB.Core;

namespace MyRGB.Tests;

public class BrightnessStepsTests
{
    [Theory]
    [InlineData(0.5, 1, 0.6)]
    [InlineData(0.5, -1, 0.4)]
    [InlineData(0.65, 1, 0.7)]
    [InlineData(0.65, -1, 0.6)]
    [InlineData(1.0, 1, 1.0)]
    [InlineData(0.1, -1, 0.1)]
    [InlineData(0.03, 1, 0.1)]
    [InlineData(0.7000000001, 1, 0.8)]
    public void A_press_moves_to_the_next_tenth(double from, int direction, double to) =>
        Assert.Equal(to, BrightnessSteps.Next(from, direction), 3);
}
