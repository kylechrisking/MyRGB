namespace MyRGB.Core;

public static class BrightnessSteps
{
    /// <summary>
    /// One press of a brightness key: to the next tenth up or down (so 0.65 goes to 0.7 or 0.6), never
    /// below 10%, since turning the lights off is its own key.
    /// </summary>
    public static double Next(double current, int direction)
    {
        double tenths = direction > 0 ? Math.Floor(current * 10 + 1e-6) + 1 : Math.Ceiling(current * 10 - 1e-6) - 1;
        return Math.Round(Math.Clamp(tenths / 10, 0.1, 1), 2);
    }
}
