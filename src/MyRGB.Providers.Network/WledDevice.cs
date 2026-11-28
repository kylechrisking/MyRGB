using System.Net;
using System.Net.Sockets;
using MyRGB.Core;
using MyRGB.Core.Devices;

namespace MyRGB.Providers.Network;

/// <summary>A WLED controller (an LED strip or matrix run by an ESP board) driven over WLED's realtime UDP.</summary>
internal sealed class WledDevice : IRgbDevice, IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _target;
    private readonly byte[] _packet = new byte[WledPackets.BufferSize];

    public WledDevice(IPAddress address, WledInfo info)
    {
        _target = new IPEndPoint(address, WledPackets.Port);
        _udp = new UdpClient(address.AddressFamily);
        Info = info;
        Name = info.Name;
        string key = info.Mac.Length > 0 ? info.Mac.ToLowerInvariant() : address.ToString();
        Id = $"wled:{key}";
        HardwareKey = Id;
        Type = info.LedCount == 1 ? DeviceType.Light : DeviceType.LedStrip;
        Leds = Layout(info);
        Zones = [new ZoneInfo(info.MatrixWidth > 0 ? "Matrix" : "Strip", 0, info.LedCount)];
    }

    public WledInfo Info { get; }
    public IPAddress Address => _target.Address;
    public string Id { get; }
    public string Name { get; }
    public string Vendor => "WLED";
    public DeviceType Type { get; }
    public string ProviderId => NetworkLightsProvider.ProviderId;
    public string? HardwareKey { get; }
    public IReadOnlyList<LedInfo> Leds { get; }
    public IReadOnlyList<ZoneInfo> Zones { get; }

    public async ValueTask ApplyAsync(ReadOnlyMemory<Rgb> colors, CancellationToken ct = default)
    {
        for (int p = 0, n = WledPackets.PacketCount(colors.Length); p < n; p++)
        {
            int len = WledPackets.Write(_packet, colors.Span, p);
            await _udp.SendAsync(_packet.AsMemory(0, len), _target, ct);
        }
    }

    /// <summary>A matrix as its grid (WLED handles serpentine wiring itself), anything else as one line left to right.</summary>
    internal static IReadOnlyList<LedInfo> Layout(WledInfo info)
    {
        var leds = new LedInfo[info.LedCount];
        if (info.MatrixWidth > 0 && info.MatrixHeight > 0)
        {
            int w = info.MatrixWidth, h = info.MatrixHeight;
            for (int i = 0; i < leds.Length; i++)
            {
                int col = i % w, row = i / w;
                leds[i] = new LedInfo($"LED {i + 1}", w == 1 ? 0.5f : col / (float)(w - 1), h == 1 ? 0.5f : row / (float)(h - 1));
            }
        }
        else
        {
            for (int i = 0; i < leds.Length; i++)
                leds[i] = new LedInfo($"LED {i + 1}", leds.Length == 1 ? 0.5f : i / (float)(leds.Length - 1), 0.5f);
        }
        return leds;
    }

    public void Dispose() => _udp.Dispose();
}
