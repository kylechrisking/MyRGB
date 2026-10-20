using System.Collections.ObjectModel;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MyRGB.App.ViewModels;

public sealed record SupportedDevice(string Name, string Connection);

/// <summary>
/// "Will MyRGB work with my device?": a search over everything MyRGB can drive, its own drivers
/// plus the ~1,900 devices of the bundled OpenRGB (Assets/supported-devices.tsv, made by
/// tools/make-supported-devices.ps1).
/// </summary>
public sealed partial class SupportedDevicesViewModel : ObservableObject
{
    private const int MaxResults = 40;

    /// <summary>Devices MyRGB drives itself rather than through OpenRGB.</summary>
    private static readonly SupportedDevice[] Native = [new("Corsair Vanguard Pro 96", "MyRGB native driver")];

    private readonly Lazy<IReadOnlyList<SupportedDevice>> _all = new(Load);

    public ObservableCollection<SupportedDevice> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _summary = "";

    public int Count => _all.Value.Count;

    partial void OnQueryChanged(string value) => Search();

    public void Search()
    {
        Results.Clear();
        var words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            Summary = $"MyRGB works with {Count:N0} devices. Type a brand or model to check yours.";
            return;
        }
        var matches = _all.Value.Where(d => words.All(w => d.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var d in matches.Take(MaxResults)) Results.Add(d);
        Summary = matches.Count switch
        {
            0 => $"Nothing matches \"{Query.Trim()}\". Try just the brand, or fewer words.",
            1 => "1 match.",
            <= MaxResults => $"{matches.Count} matches.",
            _ => $"{matches.Count} matches; showing the first {MaxResults}. Add a word to narrow it down.",
        };
    }

    private static IReadOnlyList<SupportedDevice> Load()
    {
        var list = new List<SupportedDevice>(Native);
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://MyRGB/Assets/supported-devices.tsv"));
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length == 2) list.Add(new SupportedDevice(parts[0], $"Through OpenRGB · {parts[1]}"));
            }
        }
        catch (Exception) { /* missing list: search just the native drivers */ }
        return list.DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Name).ToList();
    }
}
