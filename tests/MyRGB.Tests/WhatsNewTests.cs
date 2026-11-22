using MyRGB.Core;

namespace MyRGB.Tests;

public class WhatsNewTests
{
    [Fact]
    public void Shows_every_release_since_the_one_last_seen_newest_first()
    {
        var shown = WhatsNew.Since(new Version(0, 6, 2), new Version(0, 6, 5, 0), firstRun: false);
        Assert.Equal([new Version(0, 6, 5), new(0, 6, 4), new(0, 6, 3)], shown.Select(r => r.Version));
    }

    [Fact]
    public void Shows_nothing_once_seen() =>
        Assert.Empty(WhatsNew.Since(new Version(0, 6, 5), new Version(0, 6, 5, 0), firstRun: false));

    [Fact]
    public void A_first_install_gets_the_tour_instead() =>
        Assert.Empty(WhatsNew.Since(null, new Version(0, 6, 5), firstRun: true));

    [Fact]
    public void An_update_from_before_it_remembered_shows_just_this_release() =>
        Assert.Equal([new Version(0, 6, 5)], WhatsNew.Since(null, new Version(0, 6, 5), firstRun: false).Select(r => r.Version));

    [Fact]
    public void Releases_are_listed_newest_first() =>
        Assert.Equal(WhatsNew.Releases.OrderByDescending(r => r.Version).Select(r => r.Version), WhatsNew.Releases.Select(r => r.Version));

    [Fact]
    public void The_current_version_has_an_entry()
    {
        var version = WhatsNew.Trim(typeof(WhatsNew).Assembly.GetName().Version!);
        Assert.Contains(WhatsNew.Releases, r => r.Version == version);
    }
}
