using MyRGB.Core;
using MyRGB.Core.Effects;
using MyRGB.Core.Effects.BuiltIn;
using MyRGB.Core.Games;

namespace MyRGB.Tests;

[Collection("GameFeed")] // the feed is shared, so these never run alongside each other
public class GameTests : IDisposable
{
    private const string Token = "secret";

    private static string Cs(int health = 100, int flashed = 0, int kills = 0, string? bomb = null,
        string me = "7656", string shown = "7656", string activity = "playing", string phase = "live", int clip = 30) => $$"""
        {
          "provider": { "name": "Counter-Strike 2", "appid": 730, "steamid": "{{me}}" },
          "map": { "mode": "competitive", "name": "de_dust2", "phase": "{{phase}}" },
          "round": { "phase": "live"{{(bomb is null ? "" : $", \"bomb\": \"{bomb}\"")}} },
          "player": {
            "steamid": "{{shown}}", "team": "CT", "activity": "{{activity}}",
            "state": { "health": {{health}}, "armor": 50, "flashed": {{flashed}}, "burning": 0, "round_kills": {{kills}} },
            "weapons": {
              "weapon_0": { "name": "weapon_knife", "type": "Knife", "state": "holstered" },
              "weapon_1": { "name": "weapon_m4a1", "type": "Rifle", "ammo_clip": {{clip}}, "ammo_clip_max": 30, "state": "active" }
            }
          },
          "auth": { "token": "{{Token}}" }
        }
        """;

    private const string Dota = """
        {
          "provider": { "name": "Dota 2", "appid": 570 },
          "map": { "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" },
          "player": { "team_name": "dire", "kills": 3 },
          "hero": { "alive": true, "health_percent": 40, "mana_percent": 75, "stunned": true, "silenced": false, "hexed": false },
          "auth": { "token": "secret" }
        }
        """;

    public GameTests() => GameFeed.Clear();

    public void Dispose() => GameFeed.Clear();

    [Fact]
    public void Counter_strike_post_is_read()
    {
        var s = GsiParser.Parse(Cs(health: 60, flashed: 255, kills: 2, clip: 6), Token)!;

        Assert.Equal(GameKind.CounterStrike2, s.Game);
        Assert.True(s.InMatch);
        Assert.True(s.Alive);
        Assert.Equal(0.6, s.Health, 3);
        Assert.Equal(0.5, s.Armor, 3);
        Assert.Equal(1, s.Flashed, 3);
        Assert.Equal(2, s.Kills);
        Assert.Equal(0.2, s.Ammo!.Value, 3);
        Assert.Equal("CT", s.Team);
    }

    [Fact]
    public void A_wrong_token_or_unknown_game_is_ignored()
    {
        Assert.Null(GsiParser.Parse(Cs(), "other"));
        Assert.Null(GsiParser.Parse("""{ "provider": { "appid": 440 }, "auth": { "token": "secret" } }""", Token));
        Assert.Null(GsiParser.Parse("not json", Token));
    }

    [Fact]
    public void Watching_a_teammate_after_dying_is_not_your_health()
    {
        var s = GsiParser.Parse(Cs(health: 100, shown: "999"), Token)!;
        Assert.False(s.Alive);
        Assert.Equal(0, s.Health);
    }

    [Fact]
    public void The_menu_is_not_a_match()
    {
        Assert.False(GsiParser.Parse(Cs(activity: "menu"), Token)!.InMatch);
        Assert.False(GsiParser.Parse(Cs(phase: "gameover"), Token)!.InMatch);
    }

    [Fact]
    public void Bomb_states_are_read()
    {
        Assert.Equal(BombState.Planted, GsiParser.Parse(Cs(bomb: "planted"), Token)!.Bomb);
        Assert.Equal(BombState.Defused, GsiParser.Parse(Cs(bomb: "defused"), Token)!.Bomb);
        Assert.Equal(BombState.None, GsiParser.Parse(Cs(), Token)!.Bomb);
    }

    [Fact]
    public void Dota_post_is_read()
    {
        var s = GsiParser.Parse(Dota, Token)!;

        Assert.Equal(GameKind.Dota2, s.Game);
        Assert.True(s.InMatch);
        Assert.Equal(0.4, s.Health, 3);
        Assert.Equal(0.75, s.Mana, 3);
        Assert.True(s.Disabled);
        Assert.Equal("dire", s.Team);
        Assert.Equal(3, s.Kills);
    }

    [Fact]
    public void Feed_marks_hits_kills_and_the_bomb()
    {
        var full = GsiParser.Parse(Cs(), Token)!;
        var snap = GameFeed.Advance(GameFeed.Snapshot.Empty, full, 1000);
        Assert.Equal(long.MinValue, snap.HurtAt); // the first post is not a hit

        snap = GameFeed.Advance(snap, full with { Health = 0.7 }, 2000);
        Assert.Equal(2000, snap.HurtAt);

        snap = GameFeed.Advance(snap, full with { Health = 0.7, Kills = 1 }, 3000);
        Assert.Equal(3000, snap.KillAt);
        Assert.Equal(2000, snap.HurtAt);

        snap = GameFeed.Advance(snap, full with { Bomb = BombState.Planted }, 4000);
        snap = GameFeed.Advance(snap, full with { Bomb = BombState.Planted }, 5000);
        Assert.Equal(4000, snap.BombPlantedAt); // still the moment it went down

        snap = GameFeed.Advance(snap, full with { Bomb = BombState.Defused }, 6000);
        Assert.Equal(6000, snap.BombEndedAt);
    }

    [Fact]
    public void Feed_goes_stale_when_the_game_stops_sending()
    {
        var snap = GameFeed.Advance(GameFeed.Snapshot.Empty, GsiParser.Parse(Cs(), Token)!, 1000);
        Assert.True(snap.Fresh(1000 + 5000));
        Assert.False(snap.Fresh(1000 + GameFeed.StaleAfterMs));
    }

    [Fact]
    public void Effect_shows_the_idle_color_without_a_game()
    {
        var output = Render(new GameEventsEffect(), 0);
        Assert.All(output, c => Assert.Equal(new Rgb(90, 0, 255), c));
    }

    [Fact]
    public void Effect_fills_a_health_bar()
    {
        GameFeed.Update(GsiParser.Parse(Cs(health: 50), Token)!, 1000);
        var output = Render(new GameEventsEffect { Clock = () => 1000 }, 0);

        // LEDs spread left to right: the left half lit, the right half dim.
        Assert.True(Brightness(output[0]) > 100);
        Assert.True(Brightness(output[^1]) < 30);
    }

    [Fact]
    public void Effect_goes_white_when_flashed()
    {
        GameFeed.Update(GsiParser.Parse(Cs(flashed: 255), Token)!, 1000);
        var output = Render(new GameEventsEffect { Clock = () => 1000 }, 0);
        Assert.All(output, c => Assert.Equal(Rgb.White, c));
    }

    [Fact]
    public void Effect_flashes_red_on_a_hit_and_then_settles()
    {
        GameFeed.Update(GsiParser.Parse(Cs(), Token)!, 1000);
        GameFeed.Update(GsiParser.Parse(Cs(health: 90), Token)!, 2000);

        var hit = Render(new GameEventsEffect { Clock = () => 2000 }, 0);
        Assert.Equal(new Rgb(255, 0, 0), hit[0]);

        var later = Render(new GameEventsEffect { Clock = () => 3000 }, 0);
        Assert.NotEqual(new Rgb(255, 0, 0), later[0]);
    }

    [Fact]
    public void Bomb_blinks_faster_as_it_runs_down()
    {
        static int Blinks(double fromSeconds, double toSeconds)
        {
            var snap = new GameFeed.Snapshot(GameState.None with { Game = GameKind.CounterStrike2, Bomb = BombState.Planted },
                0, long.MinValue, long.MinValue, 0, long.MinValue);
            int count = 0;
            bool wasOn = false;
            for (double t = fromSeconds; t < toSeconds; t += 0.01)
            {
                bool on = GameEventsEffect.BombOverlay(snap, (long)(t * 1000)).Strength > 0;
                if (on && !wasOn) count++;
                wasOn = on;
            }
            return count;
        }

        Assert.True(Blinks(35, 40) > Blinks(0, 5) * 2);
    }

    [Fact]
    public void Config_file_points_at_myrgb_with_the_token()
    {
        string cfg = GameSetup.Config(GsiGame.CounterStrike2, 6744, "abc");
        Assert.Contains("\"uri\" \"http://localhost:6744/\"", cfg);
        Assert.Contains("\"token\" \"abc\"", cfg);
        Assert.Contains("\"player_state\" \"1\"", cfg);
    }

    [Fact]
    public void Connect_writes_into_the_game_folder_and_disconnect_removes_it()
    {
        string lib = Path.Combine(Path.GetTempPath(), "myrgb-steam-" + Guid.NewGuid().ToString("N"));
        string other = Path.Combine(Path.GetTempPath(), "myrgb-steam-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(lib, "steamapps"));
            Directory.CreateDirectory(Path.Combine(other, "steamapps", "common", "dota 2 beta"));
            File.WriteAllText(Path.Combine(lib, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{other.Replace(@"\", @"\\")}\"\n\t}}\n}}\n");

            var setup = new GameSetup(GameSetup.SteamLibraries(lib));
            Assert.Null(setup.FindGame(GsiGame.CounterStrike2));
            Assert.False(setup.Install(GsiGame.CounterStrike2, 6744, "abc"));

            Assert.True(setup.Install(GsiGame.Dota2, 6744, "abc"));
            Assert.True(setup.IsInstalled(GsiGame.Dota2));
            Assert.True(File.Exists(Path.Combine(other, "steamapps", "common", "dota 2 beta", "game", "dota", "cfg", "gamestate_integration", GameSetup.FileName)));

            setup.Remove(GsiGame.Dota2);
            Assert.False(setup.IsInstalled(GsiGame.Dota2));
        }
        finally
        {
            try { Directory.Delete(lib, true); } catch { }
            try { Directory.Delete(other, true); } catch { }
        }
    }

    private static Rgb[] Render(GameEventsEffect effect, double time)
    {
        effect.Settings = GameEventsEffect.Definition.Bind(EffectSettings.Empty);
        var leds = Enumerable.Range(0, 10).Select(i => LedPoint.Whole(i / 9f, 0.5f)).ToArray();
        var output = new Rgb[leds.Length];
        effect.Render(new FrameContext(time, 0), leds, output);
        return output;
    }

    private static int Brightness(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B));
}
