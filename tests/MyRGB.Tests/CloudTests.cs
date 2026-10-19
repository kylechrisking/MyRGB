using System.Text.Json;
using MyRGB.Cloud;
using MyRGB.Core.Presets;

namespace MyRGB.Tests;

public class CloudTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static Preset P(string id, string color = "#FF0000") =>
        new() { Id = id, Name = id, EffectId = "static", Settings = new() { ["color"] = color } };

    private static RemotePreset R(Preset p) => new(p.Id, p, false, Now.AddHours(-1));
    private static RemotePreset Gone(string id) => new(id, null, true, Now.AddHours(-1));

    private static PresetSync.SyncPlan Plan(Preset[] local, RemotePreset[] remote, Dictionary<string, string>? baseline = null) =>
        PresetSync.Plan(local, remote.ToDictionary(r => r.PresetId), baseline ?? [], Now);

    [Fact]
    public void First_sync_uploads_local_and_downloads_remote()
    {
        var plan = Plan([P("a")], [R(P("b"))]);
        Assert.Equal(["a"], plan.Upload.Select(u => u.PresetId));
        Assert.Equal(["b"], plan.SaveLocally.Select(p => p.Id));
        Assert.Empty(plan.DeleteLocally);
    }

    [Fact]
    public void Identical_on_both_sides_does_nothing()
    {
        var a = P("a");
        var plan = Plan([a], [R(a)], new() { ["a"] = PresetSync.Hash(a) });
        Assert.Empty(plan.Upload);
        Assert.Empty(plan.SaveLocally);
    }

    [Fact]
    public void Edit_on_another_pc_comes_down_and_local_edit_goes_up()
    {
        var a = P("a"); var b = P("b");
        var baseline = new Dictionary<string, string> { ["a"] = PresetSync.Hash(a), ["b"] = PresetSync.Hash(b) };
        var plan = Plan([a, P("b", "#00FF00")], [R(P("a", "#0000FF")), R(b)], baseline);

        Assert.Equal("#0000FF", plan.SaveLocally.Single().Settings["color"]);
        Assert.Equal("#00FF00", plan.Upload.Single().Data!.Settings["color"]);
    }

    [Fact]
    public void Deleting_here_deletes_there_and_vice_versa()
    {
        var a = P("a"); var b = P("b");
        var baseline = new Dictionary<string, string> { ["a"] = PresetSync.Hash(a), ["b"] = PresetSync.Hash(b) };
        var plan = Plan([b], [R(a), Gone("b")], baseline);

        Assert.True(plan.Upload.Single().Deleted);          // a: deleted here
        Assert.Equal("a", plan.Upload.Single().PresetId);
        Assert.Equal(["b"], plan.DeleteLocally);            // b: deleted on the other PC
        Assert.Equal("", plan.NewState["a"]);
    }

    [Fact]
    public void An_edit_beats_a_deletion()
    {
        var a = P("a");
        var baseline = new Dictionary<string, string> { ["a"] = PresetSync.Hash(a) };

        var editedHere = Plan([P("a", "#123456")], [Gone("a")], baseline);
        Assert.Empty(editedHere.DeleteLocally);
        Assert.False(editedHere.Upload.Single().Deleted);

        var editedThere = Plan([], [R(P("a", "#123456"))], baseline);
        Assert.Equal("#123456", editedThere.SaveLocally.Single().Settings["color"]);
        Assert.Empty(editedThere.Upload);
    }

    [Fact]
    public void Both_edited_keeps_this_pcs_version()
    {
        var a = P("a");
        var plan = Plan([P("a", "#111111")], [R(P("a", "#222222"))], new() { ["a"] = PresetSync.Hash(a) });
        Assert.Equal("#111111", plan.Upload.Single().Data!.Settings["color"]);
        Assert.Empty(plan.SaveLocally);
    }

    [Fact]
    public void Hash_ignores_setting_order()
    {
        var x = new Preset { Id = "x", Name = "x", EffectId = "e", Settings = new() { ["a"] = "1", ["b"] = "2" } };
        var y = x with { Settings = new() { ["b"] = "2", ["a"] = "1" } };
        Assert.Equal(PresetSync.Hash(x), PresetSync.Hash(y));
    }

    [Fact]
    public void Pkce_challenge_matches_rfc_7636_example()
    {
        // Appendix B of RFC 7636.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var (verifier, challenge) = Pkce.Create();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain('=', challenge);
    }

    [Theory]
    [InlineData("neon wave", "neon wave")]
    [InlineData("a*,b)(or=1", "abor1")]
    [InlineData("   ", null)]
    [InlineData("*", null)]
    public void Search_terms_are_safe_for_postgrest(string input, string? expected) =>
        Assert.Equal(expected, SupabaseCloud.SearchTerm(input));

    [Fact]
    public void Shared_presets_are_validated()
    {
        var ok = JsonSerializer.SerializeToElement(new { id = "x", name = "Nice", effectId = "plasma", settings = new { palette = "Neon" } });
        Assert.NotNull(PresetJson.Read(ok));

        var noEffect = JsonSerializer.SerializeToElement(new { id = "x", name = "Bad", effectId = "" });
        Assert.Null(PresetJson.Read(noEffect));

        var huge = JsonSerializer.SerializeToElement(new { id = "x", name = "Big", effectId = "static", settings = new { color = new string('a', 1000) } });
        Assert.Null(PresetJson.Read(huge));

        Assert.Null(PresetJson.Read(JsonSerializer.SerializeToElement(new[] { 1, 2 })));
    }
}
