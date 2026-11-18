using System.Net.Sockets;
using MyRGB.Cloud;

namespace MyRGB.Tests;

/// <summary>
/// The words shown when the service doesn't answer. A paused Supabase project has no DNS record,
/// which surfaces as HttpRequestException wrapping SocketException(HostNotFound).
/// </summary>
public class CloudErrorsTests
{
    private static HttpRequestException Wrapping(SocketError code) => new("send failed", new SocketException((int)code));

    [Fact]
    public void A_missing_address_says_the_service_is_offline()
    {
        var ex = Wrapping(SocketError.HostNotFound);
        Assert.Equal("The MyRGB service is offline right now. Try later.", CloudErrors.Unreachable(ex, "Try later."));
        Assert.StartsWith("HostNotFound", CloudErrors.Reason(ex));
    }

    [Fact]
    public void A_timeout_says_so()
    {
        var ex = new TaskCanceledException();
        Assert.StartsWith("The MyRGB service didn't answer in time.", CloudErrors.Unreachable(ex));
        Assert.Equal("timed out", CloudErrors.Reason(ex));
    }

    [Fact]
    public void No_network_blames_the_PC_not_the_service() =>
        Assert.StartsWith("This PC doesn't seem to be online.", CloudErrors.Unreachable(Wrapping(SocketError.NetworkUnreachable)));

    [Fact]
    public void Anything_else_keeps_the_general_wording() =>
        Assert.StartsWith("Couldn't reach the MyRGB service.", CloudErrors.Unreachable(Wrapping(SocketError.ConnectionRefused)));
}
