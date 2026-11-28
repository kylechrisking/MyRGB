using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MyRGB.Providers.Network;

/// <summary>A light that answered, ready to add.</summary>
public sealed record FoundLight(NetworkLightKind Kind, string Address, string Name, int LedCount)
{
    public NetworkLight ToSetting() => new(Kind, Address, Name);
}

/// <summary>
/// Finds and checks lights on the home network. Only runs when the user asks ("Find lights" or "Add"),
/// never in the background.
/// </summary>
public static class LightFinder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>Ask the light at <paramref name="address"/> what it is. Null if nothing answers like WLED.</summary>
    public static async Task<FoundLight?> CheckAsync(string address, CancellationToken ct = default)
    {
        address = address.Trim();
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) address = address[7..];
        address = address.TrimEnd('/');
        if (address.Length == 0) return null;
        var info = await ReadWledInfoAsync(address, ct);
        return info is null ? null : new FoundLight(NetworkLightKind.Wled, address, info.Name, info.LedCount);
    }

    internal static async Task<WledInfo?> ReadWledInfoAsync(string address, CancellationToken ct)
    {
        try
        {
            var host = address.Contains(':') && IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]" : address;
            var json = await Http.GetStringAsync($"http://{host}/json/info", ct);
            return WledInfo.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException
                                   && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Look for WLED lights for a few seconds. WLED announces itself over mDNS as "_wled._tcp.local";
    /// ask every network adapter, then check each address that answers.
    /// </summary>
    public static async Task<IReadOnlyList<FoundLight>> FindAsync(TimeSpan listenFor, CancellationToken ct = default)
    {
        var query = MdnsQuery("_wled._tcp.local");
        var answered = new HashSet<IPAddress>();
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var local in LocalIPv4Addresses())
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(local, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    sockets.Add(udp);
                }
                catch (SocketException) { }
            }

            using var listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
            listen.CancelAfter(listenFor);
            var receivers = sockets.Select(udp => ReceiveAsync(udp, answered, listen.Token)).ToList();
            // Ask twice: mDNS is UDP and a single question is easily lost on Wi-Fi.
            for (int attempt = 0; attempt < 2 && !listen.IsCancellationRequested; attempt++)
            {
                foreach (var udp in sockets)
                {
                    try { await udp.SendAsync(query, MdnsGroup, ct); }
                    catch (SocketException) { }
                }
                try { await Task.Delay(TimeSpan.FromMilliseconds(800), listen.Token); }
                catch (OperationCanceledException) { }
            }
            await Task.WhenAll(receivers);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
        ct.ThrowIfCancellationRequested();

        List<IPAddress> addresses;
        lock (answered) addresses = [.. answered];
        var checks = await Task.WhenAll(addresses.Select(a => CheckAsync(a.ToString(), ct)));
        return checks.OfType<FoundLight>().OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task ReceiveAsync(UdpClient udp, HashSet<IPAddress> answered, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                if (MentionsWled(result.Buffer))
                    lock (answered) answered.Add(result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>A DNS query packet for one PTR record, asking for a direct (unicast) reply.</summary>
    internal static byte[] MdnsQuery(string service)
    {
        var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }; // id 0, flags 0, one question
        foreach (var label in service.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)b.Length);
            bytes.AddRange(b);
        }
        bytes.AddRange([0, 0, 12, 0x80, 1]); // end of name, type PTR, class IN with the unicast-reply bit
        return [.. bytes];
    }

    /// <summary>True for an mDNS response (QR bit set) that carries the "_wled" label anywhere in it.</summary>
    internal static bool MentionsWled(ReadOnlySpan<byte> packet) =>
        packet.Length > 12 && (packet[2] & 0x80) != 0 && packet.IndexOf("\u0005_wled"u8) >= 0;

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    yield return a.Address;
        }
    }
}
