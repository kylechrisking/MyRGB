using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyRGB.Core.Presets;

namespace MyRGB.Cloud;

/// <summary>
/// Keeps the user's own presets the same on every PC they sign in on. A three-way merge: for
/// each preset we remember what it looked like at the last sync, so we can tell "changed here"
/// from "changed on another PC" and never overwrite an edit with a stale copy. When both sides
/// changed the same preset, the newer edit wins.
/// </summary>
public sealed class PresetSync
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ICloud _cloud;
    private readonly PresetLibrary _library;
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PresetSync(ICloud cloud, PresetLibrary library, string stateDirectory)
    {
        _cloud = cloud;
        _library = library;
        _stateDirectory = stateDirectory;
    }

    public DateTimeOffset? LastSynced { get; private set; }

    /// <summary>Raised after a sync finishes (successfully or not). Any thread.</summary>
    public event EventHandler<string?>? Synced;

    /// <summary>Merge local and remote presets. Returns null on success, or an error to show.</summary>
    public async Task<string?> SyncAsync(CancellationToken ct = default)
    {
        if (_cloud.User is not { } user) return null;
        await _gate.WaitAsync(ct);
        string? error = null;
        try
        {
            var state = LoadState(user.Id);
            var remote = (await _cloud.GetSyncedPresetsAsync(ct)).ToDictionary(r => r.PresetId);
            var plan = Plan(_library.UserPresets, remote, state, DateTimeOffset.UtcNow);

            await _cloud.PutSyncedPresetsAsync(plan.Upload, ct);
            foreach (var p in plan.SaveLocally) _library.Save(p);
            foreach (var id in plan.DeleteLocally) _library.Delete(id);

            SaveState(user.Id, plan.NewState);
            LastSynced = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or TaskCanceledException)
        {
            error = ex is CloudException ? ex.Message : "Couldn't reach the MyRGB service. Your presets will sync next time.";
        }
        finally
        {
            _gate.Release();
        }
        Synced?.Invoke(this, error);
        return error;
    }

    internal sealed record SyncPlan(
        IReadOnlyCollection<RemotePreset> Upload,
        IReadOnlyCollection<Preset> SaveLocally,
        IReadOnlyCollection<string> DeleteLocally,
        Dictionary<string, string> NewState);

    /// <param name="lastSynced">Preset id → content hash at the last sync ("" for a known deletion).</param>
    internal static SyncPlan Plan(
        IReadOnlyList<Preset> local, IReadOnlyDictionary<string, RemotePreset> remote,
        IReadOnlyDictionary<string, string> lastSynced, DateTimeOffset now)
    {
        var upload = new List<RemotePreset>();
        var save = new List<Preset>();
        var delete = new List<string>();
        var state = new Dictionary<string, string>();
        var localById = local.ToDictionary(p => p.Id);

        foreach (var id in localById.Keys.Union(remote.Keys).Union(lastSynced.Keys))
        {
            localById.TryGetValue(id, out var mine);
            remote.TryGetValue(id, out var theirs);
            string? baseline = lastSynced.GetValueOrDefault(id);

            // Each side's version: a content hash, "" for deleted, null for never existed.
            string? here = mine is not null ? Hash(mine) : baseline is null ? null : "";
            string? there = theirs is null ? baseline : theirs.Deleted ? "" : Hash(theirs.Data!);

            bool changedHere = here != baseline;
            bool changedThere = there != baseline;

            string? result = (changedHere, changedThere) switch
            {
                (false, false) => baseline,
                (true, false) => here,
                (false, true) => there,
                // Both changed: an edit beats a deletion, and between two edits this PC wins.
                _ => here == "" ? there : here,
            };
            if (result is null) continue;

            if (result != there)
                upload.Add(result == "" ? new RemotePreset(id, null, true, now) : new RemotePreset(id, mine, false, now));
            if (result != here)
            {
                if (result == "") { if (mine is not null) delete.Add(id); }
                else save.Add(theirs!.Data! with { BuiltIn = false });
            }
            state[id] = result;
        }
        return new SyncPlan(upload, save, delete, state);
    }

    internal static string Hash(Preset p)
    {
        // Canonical form: sorted keys, so dictionary order never looks like an edit.
        var canonical = JsonSerializer.Serialize(new
        {
            p.Name,
            p.EffectId,
            Settings = p.Settings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
            Overrides = p.Overrides.OrderBy(o => o.DeviceId).Select(o => new
            {
                o.DeviceId, o.Off, o.EffectId,
                Settings = o.Settings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
            }),
        }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    private string StatePath(Guid user) => Path.Combine(_stateDirectory, $"sync-{user:N}.json");

    private Dictionary<string, string> LoadState(Guid user)
    {
        try
        {
            return File.Exists(StatePath(user))
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatePath(user))) ?? []
                : [];
        }
        catch (JsonException)
        {
            return []; // treat as a first sync: nothing is lost, it just merges by content
        }
    }

    private void SaveState(Guid user, Dictionary<string, string> state)
    {
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(StatePath(user), JsonSerializer.Serialize(state));
    }
}
