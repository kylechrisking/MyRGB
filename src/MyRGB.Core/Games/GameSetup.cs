using System.Text.RegularExpressions;

namespace MyRGB.Core.Games;

/// <summary>A game MyRGB can listen to, and where its Game State Integration file goes.</summary>
public sealed record GsiGame(GameKind Kind, string Name, string InstallFolder, string ConfigFolder, string[] Data)
{
    public static readonly GsiGame CounterStrike2 = new(
        GameKind.CounterStrike2, "Counter-Strike 2", "Counter-Strike Global Offensive",
        Path.Combine("game", "csgo", "cfg"),
        ["provider", "map", "round", "player_id", "player_state", "player_weapons"]);

    /// <summary>Dota 2 also needs "-gamestateintegration" in its Steam launch options.</summary>
    public static readonly GsiGame Dota2 = new(
        GameKind.Dota2, "Dota 2", "dota 2 beta",
        Path.Combine("game", "dota", "cfg", "gamestate_integration"),
        ["provider", "map", "player", "hero"]);

    public static readonly IReadOnlyList<GsiGame> All = [CounterStrike2, Dota2];
}

/// <summary>
/// Finds Counter-Strike 2 and Dota 2 in the user's Steam libraries and adds (or removes) the small
/// file that tells each game to send its state to MyRGB. Only runs when the user asks.
/// </summary>
public sealed class GameSetup(IReadOnlyList<string> steamFolders)
{
    public const string FileName = "gamestate_integration_myrgb.cfg";

    /// <summary>Uses the Steam install Windows knows about, plus every library folder it lists.</summary>
    public static GameSetup ForThisPc() => new(SteamLibraries(SteamRoot()));

    public IReadOnlyList<string> SteamFolders { get; } = steamFolders;

    /// <summary>Where <paramref name="game"/> is installed, or null if it isn't in any Steam library.</summary>
    public string? FindGame(GsiGame game) =>
        SteamFolders
            .Select(lib => Path.Combine(lib, "steamapps", "common", game.InstallFolder))
            .FirstOrDefault(Directory.Exists);

    public string? ConfigPath(GsiGame game) =>
        FindGame(game) is { } dir ? Path.Combine(dir, game.ConfigFolder, FileName) : null;

    public bool IsInstalled(GsiGame game) => ConfigPath(game) is { } p && File.Exists(p);

    /// <summary>Write the file for <paramref name="game"/>. Returns false when the game isn't installed.</summary>
    public bool Install(GsiGame game, int port, string token)
    {
        if (ConfigPath(game) is not { } path) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Config(game, port, token));
        return true;
    }

    public void Remove(GsiGame game)
    {
        if (ConfigPath(game) is { } path && File.Exists(path)) File.Delete(path);
    }

    /// <summary>The file itself, in Valve's KeyValues format.</summary>
    public static string Config(GsiGame game, int port, string token)
    {
        var data = string.Join("\n", game.Data.Select(d => $"        \"{d}\" \"1\""));
        // Throttle and buffer keep posts to about ten a second, quick enough for a hit flash.
        return $$"""
            "MyRGB"
            {
                "uri" "http://localhost:{{port}}/"
                "timeout" "1.0"
                "buffer" "0.0"
                "throttle" "0.1"
                "heartbeat" "10.0"
                "auth"
                {
                    "token" "{{token}}"
                }
                "data"
                {
            {{data}}
                }
            }

            """.Replace("\r\n", "\n");
    }

    private static string? SteamRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
            }
            catch (Exception) { /* fall through to the usual place */ }
        }
        string usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(usual) ? usual : null;
    }

    /// <summary>The Steam folder and every extra library listed in its libraryfolders.vdf.</summary>
    internal static IReadOnlyList<string> SteamLibraries(string? root)
    {
        if (root is null) return [];
        var found = new List<string> { root };
        string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    found.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch (IOException) { }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
