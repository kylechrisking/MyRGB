namespace MyRGB.Core.Devices;

public static class DeviceNames
{
    /// <summary>
    /// Names to show people. RAM sticks say which slot they're in, "… (DIMM A1)". Other identical
    /// devices get numbered so they can be told apart: "… (stick 1)" for RAM, "… (2)" for the rest.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Unique(IEnumerable<IRgbDevice> devices)
    {
        var list = devices.ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in list.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            for (int i = 0; i < items.Count; i++)
            {
                var d = items[i];
                result[d.Id] = d.Type == DeviceType.Memory && d.Slot is { } slot ? $"{d.Name} (DIMM {slot})"
                    : items.Count == 1 ? d.Name
                    : d.Type == DeviceType.Memory ? $"{d.Name} (stick {i + 1})"
                    : $"{d.Name} ({i + 1})";
            }
        }
        return result;
    }
}
