using MyRGB.Core.Games;

namespace MyRGB.Core.Effects.BuiltIn;

/// <summary>
/// Lights that follow your game: a health bar or your team's color, a red flash when you're hit,
/// white when a flashbang gets you, and a quickening blink once the bomb is down. Fed by
/// <see cref="GameFeed"/>, which Settings > Game lighting fills from Counter-Strike 2 and Dota 2.
/// Outside a match (or with no game sending anything) it shows one calm color.
/// </summary>
internal sealed class GameEventsEffect : Effect
{
    public const string Id = "game-events";

    /// <summary>A planted Counter-Strike 2 bomb goes off after this long.</summary>
    public const double BombSeconds = 40;

    private static readonly Rgb Hurt = new(255, 0, 0);
    private static readonly Rgb Amber = new(255, 150, 0);
    private static readonly Rgb Fire = new(255, 70, 0);
    private static readonly Rgb Stun = new(150, 60, 255);
    private static readonly Rgb Mana = new(30, 90, 255);
    private static readonly Rgb Defused = new(0, 120, 255);
    private static readonly Rgb Boom = new(255, 170, 60);

    public static readonly EffectDefinition Definition = new(
        Id, "Game Events", "Your lights follow your game: health, hits, flashbangs and the bomb. Works with Counter-Strike 2 and Dota 2 once Game lighting is on in Settings.", "Games",
        [
            new ChoiceParameter("base", "Shows", ["Health", "Team color", "One color"], "Health"),
            new ColorParameter("color", "One color", new Rgb(0, 140, 255)),
            new ColorParameter("idle", "Outside a match", new Rgb(90, 0, 255)),
            new BoolParameter("hurt", "Flash red when you're hit", true),
            new BoolParameter("low", "Pulse when health is low", true),
            new BoolParameter("flash", "Go white when flashed", true),
            new BoolParameter("bomb", "Bomb timer", true),
            new BoolParameter("ammo", "Warn when your clip runs low", true),
            new BoolParameter("kills", "Flash on a kill", true),
            new ColorParameter("killColor", "Kill color", new Rgb(255, 190, 0)),
        ],
        () => new GameEventsEffect());

    /// <summary>Milliseconds, the same clock <see cref="GameFeed"/> stamps with. Tests replace it.</summary>
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        long now = Clock();
        var snap = GameFeed.Current;
        var g = snap.State;

        if (!snap.Fresh(now) || !g.InMatch)
        {
            output.Fill(s.GetColor("idle"));
            return;
        }

        string look = s.GetString("base");
        Rgb one = look == "Team color" ? TeamColor(g.Team) ?? s.GetColor("idle") : s.GetColor("color");
        bool meter = look == "Health";
        double t = frame.Time;

        // Overlays, worked out once per frame: how strongly each one shows (0 = not at all).
        double low = s.GetBool("low") && g.Alive && g.Health <= 0.25 ? 0.6 * Wave(t, 1.6) : 0;
        double ammo = s.GetBool("ammo") && g.Alive && g.Ammo is <= 0.2 ? 0.45 * Wave(t, 2.5) : 0;
        double fire = g.Alive ? g.Burning * 0.6 : 0;
        double stun = g.Disabled ? 0.5 : 0;
        double hurt = s.GetBool("hurt") ? Fade(now - snap.HurtAt, 350) : 0;
        double kill = s.GetBool("kills") ? Fade(now - snap.KillAt, 700) : 0;
        double flashed = s.GetBool("flash") ? g.Flashed : 0;
        var (bombColor, bomb) = s.GetBool("bomb") ? BombOverlay(snap, now) : (Rgb.Black, 0);
        Rgb killColor = s.GetColor("killColor");

        for (int i = 0; i < leds.Length; i++)
        {
            var p = leds[i];
            Rgb c;
            if (meter)
            {
                // A bar across the desk: full and green at full health, short and red when nearly dead.
                // Dota 2 puts mana along the bottom edge.
                bool manaRow = g.Game == GameKind.Dota2 && p.Y >= 0.8f;
                double level = manaRow ? g.Mana : g.Health;
                Rgb hue = manaRow ? Mana : Rgb.LerpHue(Hurt, new Rgb(0, 255, 40), g.Health);
                c = p.X <= level || level >= 0.999 ? hue : hue.Scale(0.06);
            }
            else c = one;

            if (!g.Alive) c = c.Scale(0.08);
            if (low > 0) c = Rgb.Lerp(c, Hurt, low);
            if (ammo > 0) c = Rgb.Lerp(c, Amber, ammo);
            if (fire > 0) c = Rgb.Lerp(c, Fire, fire * (0.7 + 0.3 * Flicker(t, i)));
            if (stun > 0) c = Rgb.Lerp(c, Stun, stun);
            if (hurt > 0) c = Rgb.Lerp(c, Hurt, hurt);
            if (kill > 0) c = Rgb.Lerp(c, killColor, kill);
            if (bomb > 0) c = Rgb.Lerp(c, bombColor, bomb);
            if (flashed > 0) c = Rgb.Lerp(c, Rgb.White, flashed);
            output[i] = c;
        }
    }

    /// <summary>
    /// The bomb blinks red, slowly at first and faster as it nears going off, like the beep in game.
    /// When the round ends on it, a blue wash for a defuse or a bright burst for the blast.
    /// </summary>
    internal static (Rgb Color, double Strength) BombOverlay(GameFeed.Snapshot snap, long now)
    {
        var bomb = snap.State.Bomb;
        if (bomb == BombState.Planted && snap.BombPlantedAt != long.MinValue)
        {
            double elapsed = (now - snap.BombPlantedAt) / 1000.0;
            if (elapsed < 0 || elapsed > BombSeconds + 1) return (Rgb.Black, 0);
            double progress = Math.Clamp(elapsed / BombSeconds, 0, 1);
            double period = 1.0 - 0.85 * progress; // one blink a second down to about seven
            double phase = elapsed % period / period;
            return (Hurt, phase < 0.3 ? 0.85 : 0);
        }
        if (bomb is BombState.Defused or BombState.Exploded)
            return (bomb == BombState.Defused ? Defused : Boom, Fade(now - snap.BombEndedAt, 2000));
        return (Rgb.Black, 0);
    }

    internal static Rgb? TeamColor(string? team) => team?.ToLowerInvariant() switch
    {
        "ct" => new Rgb(0, 90, 255),
        "t" => new Rgb(255, 130, 0),
        "radiant" => new Rgb(40, 220, 60),
        "dire" => new Rgb(220, 30, 20),
        _ => null,
    };

    /// <summary>1 right at the moment, falling to 0 after <paramref name="ms"/>.</summary>
    private static double Fade(long age, double ms) => age < 0 || age >= ms ? 0 : 1 - age / ms;

    private static double Wave(double t, double hz) => 0.5 + 0.5 * Math.Sin(t * hz * Math.Tau);

    private static double Flicker(double t, int i) => 0.5 + 0.5 * Math.Sin(t * 11 + i * 1.7) * Math.Sin(t * 7.3 + i * 0.9);
}
