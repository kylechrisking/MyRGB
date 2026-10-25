using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyRGB.Core;

/// <summary>A newer MyRGB release. InstallerSha256 is the digest GitHub holds for the installer
/// (lowercase hex), or null when the release publishes none.</summary>
public sealed record UpdateInfo(Version Version, Uri InstallerUrl, long InstallerSize, string? InstallerSha256, Uri ReleaseUrl)
{
    public string InstallerName => Path.GetFileName(InstallerUrl.AbsolutePath);
}

/// <summary>
/// Reads new releases from the project's GitHub repository, which is where every installer is
/// published; pre-releases included, since while MyRGB is in beta those are the releases. Any
/// failure — offline, rate-limited, an answer that isn't understood — reads as "nothing new",
/// so a check MyRGB couldn't make never turns into a message about it.
/// </summary>
public sealed class UpdateFeed : IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/kylechrisking/MyRGB/releases?per_page=10";

    private readonly HttpClient _http;

    public UpdateFeed(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyRGB");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>The newest release that ships an installer, when there is one newer than <paramref name="current"/>.</summary>
    public async Task<UpdateInfo?> FindUpdateAsync(Version current, CancellationToken ct = default)
    {
        try { return Find(await _http.GetStringAsync(ReleasesUrl, ct), current); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Download the installer to <paramref name="path"/>, reporting 0..1 as it goes, and check it is the exact file GitHub holds.</summary>
    public async Task DownloadAsync(UpdateInfo update, string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var download = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        long copied = 0;
        var total = response.Content.Headers.ContentLength ?? update.InstallerSize;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await download.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            if (total > 0) progress?.Report(Math.Min(1d, (double)copied / total));
        }
        await file.DisposeAsync();

        // An installer MyRGB runs for the user has to be bit for bit what the release published.
        if (update.InstallerSha256 is not { } expected)
            throw new InvalidOperationException("The release publishes no checksum to verify the download against.");
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("The downloaded installer did not match the file published on GitHub.");
        }
        progress?.Report(1d);
    }

    /// <summary>The newest usable release in GitHub's releases JSON, when one is newer than <paramref name="current"/>. Newest first is how GitHub lists them.</summary>
    public static UpdateInfo? Find(string releasesJson, Version current)
    {
        using var json = JsonDocument.Parse(releasesJson);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)) continue;
            if (release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
                && ParseTag(tag.GetString()!) is { } version && version > current
                && InstallerOf(release) is { } installer)
                return new UpdateInfo(version, installer.Url, installer.Size, installer.Sha256,
                    release.TryGetProperty("html_url", out var page) && page.ValueKind == JsonValueKind.String
                        && Uri.TryCreate(page.GetString(), UriKind.Absolute, out var releasePage) ? releasePage : installer.Url);
        }
        return null;
    }

    /// <summary>"v0.3.0-beta" is version 0.3.0; anything that isn't a plain number is skipped rather than guessed.</summary>
    public static Version? ParseTag(string tag)
    {
        var numbers = tag.Trim().TrimStart('v', 'V');
        var end = numbers.IndexOfAny(['-', '+']);
        if (end >= 0) numbers = numbers[..end];
        return Version.TryParse(numbers, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>The release's MyRGB-Setup asset, with its checksum when GitHub publishes one.</summary>
    private static (Uri Url, long Size, string? Sha256)? InstallerOf(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out var name) || name.GetString() is not { } fileName
                || !fileName.StartsWith("MyRGB-Setup-", StringComparison.Ordinal)
                || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { } address
                && Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                var sha = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d
                    && d.StartsWith("sha256:", StringComparison.Ordinal) ? d["sha256:".Length..].ToLowerInvariant() : null;
                return (uri, size, sha);
            }
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
