using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyRGB.Providers.Network;

/// <summary>What a Govee light says about itself when asked on the network.</summary>
internal sealed record GoveeInfo(IPAddress Address, string DeviceId, string Model);

/// <summary>
/// Govee's local network control ("LAN Control", switched on per light in the Govee Home app).
/// Questions go to the light on UDP 4003 (or to everyone on 239.255.255.250:4001), and answers come
/// back to this PC on UDP 4002. One listener on 4002 serves every question, so it is opened once,
/// the first time a Govee light is added or looked for.
/// </summary>
internal static class GoveeLan
{
    public const int ScanPort = 4001, ReplyPort = 4002, ControlPort = 4003;
    private static readonly IPEndPoint ScanGroup = new(IPAddress.Parse("239.255.255.250"), ScanPort);

    private static readonly object Gate = new();
    private static UdpClient? _listener;
    private static readonly ConcurrentDictionary<Guid, Action<IPAddress, string, JsonNode?>> Waiters = new();

    private static UdpClient Sender { get; } = new(AddressFamily.InterNetwork);

    public static byte[] Message(string cmd, JsonObject data) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["msg"] = new JsonObject { ["cmd"] = cmd, ["data"] = data } }.ToJsonString());

    public static byte[] Scan() => Message("scan", new JsonObject { ["account_topic"] = "reserve" });
    public static byte[] Status() => Message("devStatus", new JsonObject());
    public static byte[] Turn(bool on) => Message("turn", new JsonObject { ["value"] = on ? 1 : 0 });

    public static byte[] Color(MyRGB.Core.Rgb c) => Message("colorwc", new JsonObject
    {
        ["color"] = new JsonObject { ["r"] = c.R, ["g"] = c.G, ["b"] = c.B },
        ["colorTemInKelvin"] = 0,
    });

    /// <summary>Read {"msg":{"cmd":..., "data":{...}}}. Null if it isn't one.</summary>
    public static (string Cmd, JsonNode? Data)? Parse(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (JsonNode.Parse(packet) is not JsonObject root || root["msg"] is not JsonObject msg) return null;
            return msg["cmd"]?.GetValue<string>() is { } cmd ? (cmd, msg["data"]) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A scan answer as a light: {"ip":"192.168.1.23","device":"1F:80:...","sku":"H6199",...}.</summary>
    public static GoveeInfo? ReadScan(JsonNode? data)
    {
        try
        {
            if (data?["ip"]?.GetValue<string>() is not { } ip || !IPAddress.TryParse(ip, out var address)) return null;
            return new GoveeInfo(address, data["device"]?.GetValue<string>() ?? ip, data["sku"]?.GetValue<string>() ?? "");
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static Task SendAsync(IPAddress to, byte[] packet, CancellationToken ct = default) =>
        Sender.SendAsync(packet, new IPEndPoint(to, ControlPort), ct).AsTask();

    /// <summary>Ask every Govee light with LAN Control on to say where it is.</summary>
    public static async Task<IReadOnlyList<GoveeInfo>> ScanAsync(TimeSpan listenFor, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<IPAddress, GoveeInfo>();
        using var _ = Listen((from, cmd, data) =>
        {
            if (cmd == "scan" && ReadScan(data) is { } info) found[info.Address] = info;
        });
        var packet = Scan();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            foreach (var local in LocalIPv4Addresses())
            {
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(local, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    await udp.SendAsync(packet, ScanGroup, ct);
                }
                catch (SocketException) { }
            }
            await Task.Delay(listenFor / 2, ct);
        }
        return [.. found.Values];
    }

    /// <summary>True if a Govee light at <paramref name="address"/> answers a status question within a couple of seconds.</summary>
    public static async Task<bool> AnswersAsync(IPAddress address, CancellationToken ct = default)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = Listen((from, cmd, _) =>
        {
            if (cmd == "devStatus" && from.Equals(address)) answered.TrySetResult();
        });
        for (int attempt = 0; attempt < 3 && !answered.Task.IsCompleted; attempt++)
        {
            try { await SendAsync(address, Status(), ct); }
            catch (SocketException) { return false; }
            await Task.WhenAny(answered.Task, Task.Delay(700, ct));
        }
        return answered.Task.IsCompleted;
    }

    /// <summary>Hear every answer while the returned handle is alive.</summary>
    private static IDisposable Listen(Action<IPAddress, string, JsonNode?> onMessage)
    {
        EnsureListening();
        var id = Guid.NewGuid();
        Waiters[id] = onMessage;
        return new Unlisten(id);
    }

    private sealed class Unlisten(Guid id) : IDisposable
    {
        public void Dispose() => Waiters.TryRemove(id, out _);
    }

    private static void EnsureListening()
    {
        lock (Gate)
        {
            if (_listener is not null) return;
            var udp = new UdpClient(AddressFamily.InterNetwork);
            // Other Govee tools on this PC may hold 4002 too; share it rather than fail.
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, ReplyPort));
            _listener = udp;
            _ = Task.Run(() => ReceiveLoop(udp));
        }
    }

    private static async Task ReceiveLoop(UdpClient udp)
    {
        while (true)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. a "port unreachable" echo from an earlier send
            if (Parse(result.Buffer) is not { } msg) continue;
            foreach (var waiter in Waiters.Values) waiter(result.RemoteEndPoint.Address, msg.Cmd, msg.Data);
        }
    }

    internal static IEnumerable<IPAddress> LocalIPv4Addresses()
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
