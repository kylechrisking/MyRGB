using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using MyRGB.Core;

namespace MyRGB.Tests;

public class UpdateTests : IDisposable
{
    private const string Releases = """
        [
          {"tag_name":"v0.3.0-beta","draft":false,"prerelease":true,
           "html_url":"https://github.com/kylechrisking/MyRGB/releases/tag/v0.3.0-beta",
           "assets":[
             {"name":"MyRGB-Setup-0.3.0.exe","size":74681712,"state":"uploaded",
              "digest":"sha256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
              "browser_download_url":"https://github.com/kylechrisking/MyRGB/releases/download/v0.3.0-beta/MyRGB-Setup-0.3.0.exe"}]},
          {"tag_name":"v0.2.1-beta","draft":false,"prerelease":true,
           "assets":[
             {"name":"MyRGB-Setup-0.2.1.exe","size":74681712,
              "browser_download_url":"https://github.com/kylechrisking/MyRGB/releases/download/v0.2.1-beta/MyRGB-Setup-0.2.1.exe"}]}
        ]
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "myrgb-updates-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Release_tags_parse_to_plain_versions()
    {
        Assert.Equal(new Version(0, 3, 0), UpdateFeed.ParseTag("v0.3.0-beta"));
        Assert.Equal(new Version(0, 2, 1), UpdateFeed.ParseTag("v0.2.1"));
        Assert.Equal(new Version(1, 2, 0), UpdateFeed.ParseTag("V1.2"));
        Assert.Null(UpdateFeed.ParseTag("nightly"));
    }

    [Fact]
    public void The_newest_release_with_an_installer_is_an_update()
    {
        var update = UpdateFeed.Find(Releases, new Version(0, 2, 1));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 3, 0), update.Version);
        Assert.Equal("MyRGB-Setup-0.3.0.exe", update.InstallerName);
        Assert.Equal("https://github.com/kylechrisking/MyRGB/releases/download/v0.3.0-beta/MyRGB-Setup-0.3.0.exe", update.InstallerUrl.ToString());
        Assert.Equal("https://github.com/kylechrisking/MyRGB/releases/tag/v0.3.0-beta", update.ReleaseUrl.ToString());
        Assert.Equal(74681712, update.InstallerSize);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", update.InstallerSha256);
    }

    [Fact]
    public void Nothing_newer_reads_as_no_update()
    {
        Assert.Null(UpdateFeed.Find(Releases, new Version(0, 3, 0)));
        Assert.Null(UpdateFeed.Find(Releases, new Version(0, 4, 0)));
    }

    [Fact]
    public void Drafts_are_skipped_and_a_release_without_a_checksum_still_counts()
    {
        var json = """
            [
              {"tag_name":"v0.9.9-beta","draft":true,
               "assets":[{"name":"MyRGB-Setup-0.9.9.exe","digest":"sha256:00",
                          "browser_download_url":"https://github.com/kylechrisking/MyRGB/releases/download/v0.9.9-beta/MyRGB-Setup-0.9.9.exe"}]},
              {"tag_name":"v0.3.0-beta","draft":false,
               "assets":[{"name":"MyRGB-Setup-0.3.0.exe",
                          "browser_download_url":"https://github.com/kylechrisking/MyRGB/releases/download/v0.3.0-beta/MyRGB-Setup-0.3.0.exe"}]}
            ]
            """;
        var update = UpdateFeed.Find(json, new Version(0, 2, 1));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 3, 0), update.Version);
        Assert.Null(update.InstallerSha256);
    }

    [Fact]
    public async Task A_download_is_kept_when_it_matches_the_published_checksum()
    {
        var body = "the shape of an installer"u8.ToArray();
        using var feed = new UpdateFeed(new FixedResponse(body));
        var update = new UpdateInfo(new Version(0, 3, 0), new Uri("https://example.com/MyRGB-Setup-0.3.0.exe"),
            body.Length, Sha256(body), new Uri("https://example.com/rel"));
        var path = Path.Combine(_dir, "MyRGB-Setup-0.3.0.exe");
        var seen = 0d;

        await feed.DownloadAsync(update, path, new Progress<double>(p => seen = Math.Max(seen, p)),
            TestContext.Current.CancellationToken);

        Assert.Equal(body, File.ReadAllBytes(path));
        Assert.True(seen >= 0.999);
    }

    [Fact]
    public async Task A_tampered_download_is_deleted_and_never_run()
    {
        var body = "the shape of an installer"u8.ToArray();
        using var feed = new UpdateFeed(new FixedResponse(body));
        var update = new UpdateInfo(new Version(0, 3, 0), new Uri("https://example.com/MyRGB-Setup-0.3.0.exe"),
            body.Length, Sha256("something else entirely"u8.ToArray()), new Uri("https://example.com/rel"));
        var path = Path.Combine(_dir, "MyRGB-Setup-0.3.0.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => feed.DownloadAsync(update, path, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedResponse(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
