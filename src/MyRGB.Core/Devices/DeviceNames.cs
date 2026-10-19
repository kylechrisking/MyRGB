namespace MyRGB.Core.Devices;

public static class DeviceNames
{
    /// <summary>
    /// Names to show people. Identical devices get numbered so they can be told apart:
    /// four RAM sticks become "… (stick 1)" to "… (stick 4)".
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
                result[d.Id] = items.Count == 1 ? d.Name
                    : d.Type == DeviceType.Memory ? $"{d.Name} (stick {i + 1})"
                    : $"{d.Name} ({i + 1})";
            }
        }
        return result;
    }
}
