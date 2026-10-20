using System.Globalization;
using System.Text.RegularExpressions;

namespace MyRGB.Core.Devices;

/// <summary>Works out which DIMM slot a RAM stick's lighting controller sits in.</summary>
public static partial class MemorySlots
{
    /// <summary>
    /// The slot for an SMBus location such as "I2C: PawnIO SMBus i801 0, address 0x5A", or null.
    /// Corsair Vengeance RGB controllers answer at 0x58 plus the slot's index, the same order as the
    /// sticks' SPD chips (0x50 up): channel A's two slots, then channel B's, and so on. Boards label
    /// those A1, A2, B1, B2. Other makers move their controllers around, so they get no slot.
    /// </summary>
    public static string? FromLocation(string? location)
    {
        if (location is null || AddressPattern().Match(location) is not { Success: true } m) return null;
        int address = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (address is < 0x58 or > 0x5F) return null;
        int index = address - 0x58;
        return $"{(char)('A' + index / 2)}{index % 2 + 1}";
    }

    [GeneratedRegex(@"address 0x([0-9A-Fa-f]{2})\b")]
    private static partial Regex AddressPattern();
}
