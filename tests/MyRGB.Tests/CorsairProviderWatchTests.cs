using MyRGB.Hid;
using MyRGB.Providers.Corsair;

namespace MyRGB.Tests;

/// <summary>
/// The watch that brings the keyboard back without a manual rescan. On 2026-11-16 and 11-17 the keyboard
/// dropped off USB at ~07:50 with the other USB devices, came back in the afternoon, and MyRGB didn't pick
/// it up for hours because nothing asked it to look.
/// </summary>
public class CorsairProviderWatchTests
{
    private static readonly HidInterface Unopenable = new(@"\\?\hid#vid_1b1c&pid_2b0e&mi_02#not-a-real-device", 0xFF42, 1, 65, 65, 0);

    private sealed class Clock
    {
        public DateTime Now = new(2026, 11, 17, 16, 0, 0, DateTimeKind.Utc);
    }

    private static (CorsairProvider Provider, Clock Clock, List<int> Scans) Make(Func<HidInterface?> find)
    {
        var clock = new Clock();
        var provider = new CorsairProvider(null, null, find, () => clock.Now, startWatch: false);
        var scans = new List<int>();
        provider.DevicesChanged += (_, _) => scans.Add(1);
        return (provider, clock, scans);
    }

    [Fact]
    public void Asks_for_a_scan_when_the_keyboard_is_plugged_in_and_not_held()
    {
        var (provider, _, scans) = Make(() => Unopenable);
        Assert.True(provider.CheckForKeyboard());
        Assert.Single(scans);
    }

    [Fact]
    public void Stays_quiet_while_the_keyboard_is_unplugged()
    {
        var (provider, _, scans) = Make(() => null);
        Assert.False(provider.CheckForKeyboard());
        Assert.Empty(scans);
    }

    [Fact]
    public void Asks_once_until_that_scan_has_run()
    {
        var (provider, _, scans) = Make(() => Unopenable);
        provider.CheckForKeyboard();
        Assert.False(provider.CheckForKeyboard());
        Assert.Single(scans);
    }

    [Fact]
    public async Task Backs_off_from_a_keyboard_that_will_not_open()
    {
        var (provider, clock, _) = Make(() => Unopenable);

        Assert.Empty(await provider.DiscoverAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MyRGB.Core.Devices.ProviderStatus.Error, provider.Status);
        clock.Now += TimeSpan.FromSeconds(4);
        Assert.False(provider.CheckForKeyboard()); // first retry waits 5 s
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.True(provider.CheckForKeyboard());

        await provider.DiscoverAsync(TestContext.Current.CancellationToken); // fails again: 10 s now
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.False(provider.CheckForKeyboard());
        clock.Now += TimeSpan.FromSeconds(5);
        Assert.True(provider.CheckForKeyboard());
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(7, 300)]
    [InlineData(40, 300)]
    public void Retry_delay_doubles_up_to_five_minutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CorsairProvider.RetryDelay(failures));
}
