using System.Text.Json;

namespace MyRGB.Core.Games;

public enum GameKind { None, CounterStrike2, Dota2 }

public enum BombState { None, Planted, Defused, Exploded }

/// <summary>
/// What a game last told MyRGB about you, boiled down to what lighting can use. Every level runs 0..1.
/// Counter-Strike 2 and Dota 2 send this through Valve's Game State Integration: the game posts
/// JSON to MyRGB a few times a second while you play.
/// </summary>
public sealed record GameState
{
    public static readonly GameState None = new();

    public GameKind Game { get; init; }

    /// <summary>In a match and playing, not in the menus or the scoreboard between maps.</summary>
    public bool InMatch { get; init; }

    public bool Alive { get; init; } = true;
    public double Health { get; init; } = 1;

    /// <summary>Counter-Strike armor.</summary>
    public double Armor { get; init; }

    /// <summary>Dota 2 mana.</summary>
    public double Mana { get; init; }

    /// <summary>Rounds left in the gun you are holding; null for knives, grenades and the bomb.</summary>
    public double? Ammo { get; init; }

    /// <summary>How blinded by a flashbang you are right now.</summary>
    public double Flashed { get; init; }

    /// <summary>Standing in fire.</summary>
    public double Burning { get; init; }

    /// <summary>Dota 2: stunned, hexed or silenced.</summary>
    public bool Disabled { get; init; }

    /// <summary>"CT" or "T" in Counter-Strike, "radiant" or "dire" in Dota 2.</summary>
    public string? Team { get; init; }

    public BombState Bomb { get; init; }

    /// <summary>Kills this round (Counter-Strike) or this match (Dota 2); a rise means you just got one.</summary>
    public int Kills { get; init; }
}

/// <summary>Reads Game State Integration posts from Counter-Strike 2 and Dota 2.</summary>
public static class GsiParser
{
    public const int CounterStrikeAppId = 730;
    public const int DotaAppId = 570;

    /// <summary>
    /// The state in <paramref name="json"/>, or null when it isn't a game post MyRGB knows, or its
    /// token doesn't match <paramref name="token"/> (so another program on the PC can't fake one).
    /// </summary>
    public static GameState? Parse(string json, string? token)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (token is not null && Str(root, "auth", "token") != token) return null;

            return Int(root, "provider", "appid") switch
            {
                CounterStrikeAppId => CounterStrike(root),
                DotaAppId => Dota(root),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GameState CounterStrike(JsonElement root)
    {
        var state = new GameState { Game = GameKind.CounterStrike2 };
        bool live = Str(root, "map", "phase") is "live" or "warmup" or "intermission";
        state = state with
        {
            Bomb = Str(root, "round", "bomb") switch
            {
                "planted" => BombState.Planted,
                "defused" => BombState.Defused,
                "exploded" => BombState.Exploded,
                _ => BombState.None,
            },
        };

        if (!root.TryGetProperty("player", out var player)) return state with { InMatch = false };

        // While you're dead the game sends whoever you're watching. Their health isn't yours.
        string? me = Str(root, "provider", "steamid");
        string? shown = Str(player, "steamid");
        bool isMe = me is null || shown is null || me == shown;
        bool playing = Str(player, "activity") is not "menu";

        if (!player.TryGetProperty("state", out var s) || !isMe)
            return state with { InMatch = live && playing, Alive = false, Health = 0, Team = Str(player, "team") };

        int health = Int(s, "health") ?? 100;
        return state with
        {
            InMatch = live && playing,
            Team = Str(player, "team"),
            Alive = health > 0,
            Health = Math.Clamp(health / 100.0, 0, 1),
            Armor = Math.Clamp((Int(s, "armor") ?? 0) / 100.0, 0, 1),
            Flashed = Math.Clamp((Int(s, "flashed") ?? 0) / 255.0, 0, 1),
            Burning = Math.Clamp((Int(s, "burning") ?? 0) / 255.0, 0, 1),
            Kills = Int(s, "round_kills") ?? 0,
            Ammo = ActiveAmmo(player),
        };
    }

    private static double? ActiveAmmo(JsonElement player)
    {
        if (!player.TryGetProperty("weapons", out var weapons) || weapons.ValueKind != JsonValueKind.Object) return null;
        foreach (var w in weapons.EnumerateObject())
        {
            if (Str(w.Value, "state") != "active") continue;
            int? clip = Int(w.Value, "ammo_clip"), max = Int(w.Value, "ammo_clip_max");
            return clip is { } c && max is > 0 and { } m ? Math.Clamp((double)c / m, 0, 1) : null;
        }
        return null;
    }

    private static GameState Dota(JsonElement root)
    {
        var state = new GameState { Game = GameKind.Dota2 };
        // Hero picks, strategy time and the walk to the river count; the end screen and menus don't.
        bool live = Str(root, "map", "game_state") is "DOTA_GAMERULES_STATE_PRE_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
        state = state with
        {
            InMatch = live,
            Team = Str(root, "player", "team_name"),
            Kills = Int(root, "player", "kills") ?? 0,
        };
        if (!root.TryGetProperty("hero", out var hero) || hero.ValueKind != JsonValueKind.Object) return state;

        bool alive = Bool(hero, "alive") ?? true;
        return state with
        {
            Alive = alive,
            Health = alive ? Math.Clamp((Int(hero, "health_percent") ?? 100) / 100.0, 0, 1) : 0,
            Mana = Math.Clamp((Int(hero, "mana_percent") ?? 0) / 100.0, 0, 1),
            Disabled = Bool(hero, "stunned") == true || Bool(hero, "hexed") == true || Bool(hero, "silenced") == true,
        };
    }

    private static JsonElement? Walk(JsonElement e, string[] path)
    {
        foreach (var key in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e)) return null;
        }
        return e;
    }

    private static string? Str(JsonElement e, params string[] path) =>
        Walk(e, path) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static int? Int(JsonElement e, params string[] path) =>
        Walk(e, path) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int i) ? i : null;

    private static bool? Bool(JsonElement e, params string[] path) =>
        Walk(e, path) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;
}

/// <summary>
/// The latest game state, plus the moments worth reacting to (a hit, a kill, the bomb going down),
/// for effects to read on the render thread. Times are <see cref="Environment.TickCount64"/> milliseconds.
/// </summary>
public static class GameFeed
{
    /// <summary>Games send a heartbeat every few seconds; this long without one means the game closed.</summary>
    public const long StaleAfterMs = 30_000;

    private static volatile Snapshot _now = Snapshot.Empty;

    public sealed record Snapshot(GameState State, long ReceivedAt, long HurtAt, long KillAt, long BombPlantedAt, long BombEndedAt)
    {
        public static readonly Snapshot Empty = new(GameState.None, long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue);

        public bool Fresh(long now) => State.Game != GameKind.None && now - ReceivedAt < StaleAfterMs;
    }

    public static Snapshot Current => _now;

    /// <summary>Take a new post from a game. Safe from any thread.</summary>
    public static void Update(GameState next, long? at = null) => _now = Advance(_now, next, at ?? Environment.TickCount64);

    public static void Clear() => _now = Snapshot.Empty;

    internal static Snapshot Advance(Snapshot prev, GameState next, long now)
    {
        var old = prev.State;
        bool sameGame = old.Game == next.Game && prev.ReceivedAt != long.MinValue;
        long hurt = prev.HurtAt, kill = prev.KillAt, planted = prev.BombPlantedAt, ended = prev.BombEndedAt;

        if (sameGame && next.Alive && next.Health < old.Health - 0.001) hurt = now;
        if (sameGame && next.Kills > old.Kills) kill = now;
        if (next.Bomb == BombState.Planted && old.Bomb != BombState.Planted) planted = now;
        if (next.Bomb is BombState.Defused or BombState.Exploded && old.Bomb != next.Bomb) ended = now;
        if (next.Bomb == BombState.None) planted = long.MinValue;

        return new Snapshot(next, now, hurt, kill, planted, ended);
    }
}
