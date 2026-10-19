using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MyRGB.Core.Presets;

namespace MyRGB.Cloud;

/// <summary>
/// <see cref="ICloud"/> over Supabase's REST endpoints (GoTrue for auth, PostgREST for data).
/// The app holds only the public key and the user's own session; row-level security in
/// supabase/migrations decides what each request may touch.
/// </summary>
public sealed partial class SupabaseCloud : ICloud, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CloudConfig _config;
    private readonly SessionStore _store;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private StoredSession? _session;

    public SupabaseCloud(CloudConfig config, string dataDirectory, HttpMessageHandler? handler = null)
    {
        _config = config;
        _store = new SessionStore(Path.Combine(dataDirectory, "session.bin"));
        _http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = new Uri(config.Url + "/"), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("apikey", config.PublishableKey);
        _session = _store.Load();
    }

    public bool IsConfigured => true;
    public CloudUser? User => _session?.User;
    public event EventHandler? UserChanged;

    // ---- Auth -------------------------------------------------------------

    public async Task SignInAsync(string provider, CancellationToken ct)
    {
        var (verifier, challenge) = Pkce.Create();
        string code = await BrowserSignIn.GetCodeAsync(redirect =>
            $"{_config.Url}/auth/v1/authorize?provider={Uri.EscapeDataString(provider)}" +
            $"&redirect_to={Uri.EscapeDataString(redirect)}&code_challenge={challenge}&code_challenge_method=s256", ct);

        var reply = await _http.PostAsJsonAsync("auth/v1/token?grant_type=pkce", new { auth_code = code, code_verifier = verifier }, ct);
        SetSession(await ReadSession(reply, ct));
    }

    public async Task SignOutAsync()
    {
        if (_session is { } s)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "auth/v1/logout");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AccessToken);
                using var _ = await _http.SendAsync(req);
            }
            catch (HttpRequestException) { } // offline: forgetting the session locally is enough
        }
        SetSession(null);
    }

    public async Task DeleteAccountAsync(CancellationToken ct)
    {
        await Send(HttpMethod.Post, "rest/v1/rpc/delete_my_account", new { }, ct);
        SetSession(null);
    }

    private void SetSession(StoredSession? session)
    {
        _session = session;
        if (session is null) _store.Clear(); else _store.Save(session);
        UserChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<StoredSession> ReadSession(HttpResponseMessage reply, CancellationToken ct)
    {
        var body = await reply.Content.ReadFromJsonAsync<JsonObject>(Json, ct);
        if (!reply.IsSuccessStatusCode || body?["access_token"] is null)
            throw new CloudException(ErrorText(body) ?? "Sign-in failed. Please try again.");

        var user = body["user"]!;
        var meta = user["user_metadata"];
        string? email = user["email"]?.GetValue<string>();
        string name = meta?["full_name"]?.GetValue<string>() ?? meta?["name"]?.GetValue<string>()
                      ?? meta?["user_name"]?.GetValue<string>() ?? email?.Split('@')[0] ?? "MyRGB user";
        return new StoredSession(
            body["access_token"]!.GetValue<string>(),
            body["refresh_token"]!.GetValue<string>(),
            DateTimeOffset.UtcNow.AddSeconds(body["expires_in"]?.GetValue<int>() ?? 3600),
            new CloudUser(Guid.Parse(user["id"]!.GetValue<string>()), name, email, user["app_metadata"]?["provider"]?.GetValue<string>()));
    }

    /// <summary>A valid access token, refreshed when it's about to expire. Throws when signed out.</summary>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var s = _session ?? throw new CloudException("Sign in first.");
        if (s.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return s.AccessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            s = _session ?? throw new CloudException("Sign in first.");
            if (s.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return s.AccessToken;

            var reply = await _http.PostAsJsonAsync("auth/v1/token?grant_type=refresh_token", new { refresh_token = s.RefreshToken }, ct);
            if (reply.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                SetSession(null); // revoked or expired for good
                throw new CloudException("Your session expired. Please sign in again.");
            }
            var fresh = await ReadSession(reply, ct);
            SetSession(fresh);
            return fresh.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // ---- Synced presets ---------------------------------------------------

    private sealed record SyncedRow(string Preset_id, JsonElement Data, bool Deleted, DateTimeOffset Updated_at);

    public async Task<IReadOnlyList<RemotePreset>> GetSyncedPresetsAsync(CancellationToken ct)
    {
        var rows = await Get<List<SyncedRow>>("rest/v1/synced_presets?select=preset_id,data,deleted,updated_at", ct);
        return rows.Select(r => new RemotePreset(r.Preset_id, r.Deleted ? null : PresetJson.Read(r.Data), r.Deleted, r.Updated_at)).ToList();
    }

    public async Task PutSyncedPresetsAsync(IReadOnlyCollection<RemotePreset> presets, CancellationToken ct)
    {
        if (presets.Count == 0) return;
        var user = User ?? throw new CloudException("Sign in first.");
        var rows = presets.Select(p => new
        {
            user_id = user.Id,
            preset_id = p.PresetId,
            data = p.Data is null ? (JsonNode)new JsonObject() : PresetJson.Write(p.Data),
            deleted = p.Deleted,
            updated_at = p.UpdatedAt,
        });
        await Send(HttpMethod.Post, "rest/v1/synced_presets?on_conflict=user_id,preset_id", rows, ct,
                   prefer: "resolution=merge-duplicates,return=minimal");
    }

    // ---- Community --------------------------------------------------------

    private sealed record CommunityRow(
        Guid Id, string Name, string Description, string Effect_id, JsonElement Data, string[] Swatch,
        int Downloads, DateTimeOffset Created_at, Guid Author_id, string Author_name);

    public async Task<IReadOnlyList<SharedPreset>> BrowseAsync(string? search, CommunitySort sort, CancellationToken ct)
    {
        string order = sort == CommunitySort.Newest ? "created_at.desc" : "downloads.desc,created_at.desc";
        string url = $"rest/v1/community_presets?select=*&order={order}&limit=60";
        if (SearchTerm(search) is { } term) url += $"&name=ilike.*{Uri.EscapeDataString(term)}*";

        // Browsing works signed out; the public key is enough to read visible presets.
        var rows = await Get<List<CommunityRow>>(url, ct, requireUser: false);
        return rows
            .Select(r => PresetJson.Read(r.Data) is { } data
                ? new SharedPreset(r.Id, r.Name, r.Description, r.Effect_id, data, r.Swatch, r.Downloads, r.Created_at, r.Author_id, r.Author_name)
                : null)
            .OfType<SharedPreset>()
            .ToList();
    }

    public Task ShareAsync(Preset preset, IReadOnlyList<string> swatch, string description, CancellationToken ct) =>
        Send(HttpMethod.Post, "rest/v1/shared_presets", new
        {
            name = preset.Name.Trim(),
            description = description.Trim(),
            effect_id = preset.EffectId,
            data = PresetJson.Write(preset),
            swatch = swatch.Take(8).ToArray(),
        }, ct, prefer: "return=minimal");

    public Task DeleteSharedAsync(Guid id, CancellationToken ct) =>
        Send(HttpMethod.Delete, $"rest/v1/shared_presets?id=eq.{id}", null, ct);

    public Task CountInstallAsync(Guid id, CancellationToken ct) =>
        Send(HttpMethod.Post, "rest/v1/rpc/count_install", new { preset = id }, ct);

    public Task ReportAsync(Guid id, string reason, CancellationToken ct) =>
        Send(HttpMethod.Post, "rest/v1/preset_reports", new { preset_id = id, reason = reason.Trim() }, ct, prefer: "return=minimal");

    public Task SendSupportMessageAsync(string subject, string body, string appVersion, CancellationToken ct) =>
        Send(HttpMethod.Post, "rest/v1/support_messages", new { subject = subject.Trim(), body = body.Trim(), app_version = appVersion }, ct, prefer: "return=minimal");

    // ---- Plumbing ---------------------------------------------------------

    /// <summary>Letters, digits, spaces and dashes only: the term goes into a PostgREST filter.</summary>
    internal static string? SearchTerm(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var clean = SearchChars().Replace(search, "").Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(clean.Length, 40)];
    }

    [GeneratedRegex(@"[^\p{L}\p{N} \-]")]
    private static partial Regex SearchChars();

    private async Task<T> Get<T>(string url, CancellationToken ct, bool requireUser = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (requireUser || _session is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        using var reply = await _http.SendAsync(req, ct);
        await EnsureOk(reply, ct);
        return (await reply.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task Send(HttpMethod method, string url, object? body, CancellationToken ct, string? prefer = null)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (prefer is not null) req.Headers.Add("Prefer", prefer);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var reply = await _http.SendAsync(req, ct);
        await EnsureOk(reply, ct);
    }

    private static async Task EnsureOk(HttpResponseMessage reply, CancellationToken ct)
    {
        if (reply.IsSuccessStatusCode) return;
        JsonObject? body = null;
        try { body = await reply.Content.ReadFromJsonAsync<JsonObject>(Json, ct); }
        catch (JsonException) { }
        throw new CloudException(ErrorText(body) ?? $"The MyRGB service answered {(int)reply.StatusCode}. Please try again later.");
    }

    private static string? ErrorText(JsonObject? body) =>
        body?["message"]?.GetValue<string>() ?? body?["msg"]?.GetValue<string>() ?? body?["error_description"]?.GetValue<string>();

    public void Dispose() => _http.Dispose();
}

/// <summary>Preset (de)serialization for the service, validating what comes back from it.</summary>
internal static class PresetJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonNode Write(Preset preset) => JsonSerializer.SerializeToNode(preset, Options)!;

    /// <summary>Null for anything that isn't a well-formed preset (shared presets are other people's data).</summary>
    public static Preset? Read(JsonElement json)
    {
        try
        {
            var p = json.Deserialize<Preset>(Options);
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.EffectId)) return null;
            if (p.Name.Length > 60 || p.Settings.Count > 64 || p.Overrides.Count > 64) return null;
            if (p.Settings.Any(kv => kv.Key.Length > 64 || kv.Value.Length > 256)) return null;
            return p;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
