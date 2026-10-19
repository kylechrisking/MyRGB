using MyRGB.Core.Devices;

namespace MyRGB.Providers.Corsair;

/// <summary>
/// Which of the Vanguard Pro 96's 137 color slots are real LEDs, and where each sits on the board.
/// </summary>
/// <remarks>
/// From a USB capture of iCUE: slots follow USB HID keyboard usage codes (4 = A, 41 = Esc, 58 = F1…),
/// and exactly these 102 slots carry color. Slots 105–111, 122 and 130–136 are outside the standard
/// codes; their key names below are a best guess (modifiers in HID order, then Fn and extra LEDs).
/// Positions are key centers in key units on a 96% board.
/// </remarks>
internal static class VanguardPro96Layout
{
    public const int SlotCount = 137;

    public sealed record Key(int Slot, string Name, float X, float Y);

    public static readonly Key[] Keys =
    [
        // Function row
        new(41, "Esc", 0.5f, 0),
        new(58, "F1", 2, 0), new(59, "F2", 3, 0), new(60, "F3", 4, 0), new(61, "F4", 5, 0),
        new(62, "F5", 6.5f, 0), new(63, "F6", 7.5f, 0), new(64, "F7", 8.5f, 0), new(65, "F8", 9.5f, 0),
        new(66, "F9", 11, 0), new(67, "F10", 12, 0), new(68, "F11", 13, 0), new(69, "F12", 14, 0),
        new(70, "Print Screen", 15.5f, 0), new(76, "Delete", 16.5f, 0),

        // Number row + numpad top
        new(53, "`", 0.5f, 1),
        new(30, "1", 1.5f, 1), new(31, "2", 2.5f, 1), new(32, "3", 3.5f, 1), new(33, "4", 4.5f, 1), new(34, "5", 5.5f, 1),
        new(35, "6", 6.5f, 1), new(36, "7", 7.5f, 1), new(37, "8", 8.5f, 1), new(38, "9", 9.5f, 1), new(39, "0", 10.5f, 1),
        new(45, "-", 11.5f, 1), new(46, "=", 12.5f, 1), new(42, "Backspace", 14, 1),
        new(83, "Num Lock", 16, 1), new(84, "Num /", 17, 1), new(85, "Num *", 18, 1), new(86, "Num -", 19, 1),

        // QWERTY row
        new(43, "Tab", 0.75f, 2),
        new(20, "Q", 2, 2), new(26, "W", 3, 2), new(8, "E", 4, 2), new(21, "R", 5, 2), new(23, "T", 6, 2),
        new(28, "Y", 7, 2), new(24, "U", 8, 2), new(12, "I", 9, 2), new(18, "O", 10, 2), new(19, "P", 11, 2),
        new(47, "[", 12, 2), new(48, "]", 13, 2), new(49, "\\", 14.25f, 2),
        new(95, "Num 7", 16, 2), new(96, "Num 8", 17, 2), new(97, "Num 9", 18, 2), new(87, "Num +", 19, 2.5f),

        // Home row
        new(57, "Caps Lock", 0.9f, 3),
        new(4, "A", 2.25f, 3), new(22, "S", 3.25f, 3), new(7, "D", 4.25f, 3), new(9, "F", 5.25f, 3), new(10, "G", 6.25f, 3),
        new(11, "H", 7.25f, 3), new(13, "J", 8.25f, 3), new(14, "K", 9.25f, 3), new(15, "L", 10.25f, 3),
        new(51, ";", 11.25f, 3), new(52, "'", 12.25f, 3), new(40, "Enter", 13.9f, 3),
        new(92, "Num 4", 16, 3), new(93, "Num 5", 17, 3), new(94, "Num 6", 18, 3),

        // Shift row
        new(106, "Left Shift", 1.1f, 4),
        new(29, "Z", 2.75f, 4), new(27, "X", 3.75f, 4), new(6, "C", 4.75f, 4), new(25, "V", 5.75f, 4), new(5, "B", 6.75f, 4),
        new(17, "N", 7.75f, 4), new(16, "M", 8.75f, 4), new(54, ",", 9.75f, 4), new(55, ".", 10.75f, 4), new(56, "/", 11.75f, 4),
        new(110, "Right Shift", 13, 4), new(82, "Up", 14.75f, 4.15f),
        new(89, "Num 1", 16, 4), new(90, "Num 2", 17, 4), new(91, "Num 3", 18, 4), new(88, "Num Enter", 19, 4.5f),

        // Bottom row
        new(105, "Left Ctrl", 0.6f, 5), new(108, "Left Win", 1.9f, 5), new(107, "Left Alt", 3.1f, 5),
        new(44, "Space", 6.9f, 5),
        new(111, "Right Alt", 10.6f, 5), new(122, "Fn", 11.75f, 5),
        new(80, "Left", 13.75f, 5.15f), new(81, "Down", 14.75f, 5.15f), new(79, "Right", 15.75f, 5.15f),
        new(98, "Num 0", 16.75f, 5), new(99, "Num .", 18, 5),

        // Unidentified extra LEDs (likely the media/indicator area), shown as a strip above the function row.
        new(130, "Extra 1", 13, -1), new(131, "Extra 2", 14, -1), new(132, "Extra 3", 15, -1), new(133, "Extra 4", 16, -1),
        new(134, "Extra 5", 17, -1), new(135, "Extra 6", 18, -1), new(136, "Extra 7", 19, -1),
    ];

    /// <summary>LEDs in <see cref="Keys"/> order, positioned in the device's unit square.</summary>
    public static IReadOnlyList<LedInfo> Leds()
    {
        float minX = Keys.Min(k => k.X), maxX = Keys.Max(k => k.X);
        float minY = Keys.Min(k => k.Y), maxY = Keys.Max(k => k.Y);
        return Keys.Select(k => new LedInfo(k.Name, (k.X - minX) / (maxX - minX), (k.Y - minY) / (maxY - minY))).ToArray();
    }
}
