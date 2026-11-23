using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MyRGB.App.Services;

public sealed record MonitorInfo(string Name, int Brightness);

/// <summary>Something that can read and set monitor brightness. Twinkle Tray in the app, a fake in snapshots.</summary>
public interface IMonitorControl
{
    /// <summary>Whether the program is installed at all.</summary>
    bool Installed { get; }

    Task<IReadOnlyList<MonitorInfo>> ListAsync(CancellationToken ct);

    Task SetAllAsync(int level, CancellationToken ct);
}

/// <summary>
/// Drives Twinkle Tray (twinkletray.com) through its command line: <c>--List</c> to read the monitors,
/// <c>--All --Set=N</c> to set them. No window opens; the running Twinkle Tray does the work over DDC/CI.
/// </summary>
public sealed partial class TwinkleTray : IMonitorControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string? _exe = Find();

    public bool Installed => _exe is not null;

    /// <summary>The Microsoft Store install's command alias, else a normal install.</summary>
    public static string? Find()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(local, "Microsoft", "WindowsApps", "Twinkle-Tray.exe"),
            Path.Combine(local, "Programs", "twinkle-tray", "Twinkle Tray.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Twinkle Tray", "Twinkle Tray.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<IReadOnlyList<MonitorInfo>> ListAsync(CancellationToken ct) =>
        _exe is null ? [] : Parse(await RunAsync("--List", ct));

    public async Task SetAllAsync(int level, CancellationToken ct)
    {
        if (_exe is null) return;
        await RunAsync($"--All --Set={Math.Clamp(level, 0, 100)}", ct);
    }

    /// <summary>Reads <c>--List</c> output: blocks of "Name: …" and "Brightness: …", coloured with ANSI codes.</summary>
    public static IReadOnlyList<MonitorInfo> Parse(string output)
    {
        var list = new List<MonitorInfo>();
        string? name = null;
        foreach (var raw in Ansi().Replace(output, "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("MonitorNum:", StringComparison.OrdinalIgnoreCase)) name = null;
            else if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase)) name = line[5..].Trim();
            else if (line.StartsWith("Brightness:", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(line[11..].Trim(), out int b))
                list.Add(new MonitorInfo(string.IsNullOrEmpty(name) ? $"Monitor {list.Count + 1}" : name, Math.Clamp(b, 0, 100)));
        }
        return list;
    }

    private async Task<string> RunAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exe!, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Twinkle Tray didn't start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var output = p.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = p.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch (Exception) { /* already gone */ }
            throw;
        }
        return await output;
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex Ansi();
}
