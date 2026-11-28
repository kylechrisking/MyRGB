using System.Net;
using MyRGB.Core;
using MyRGB.Core.Devices;

namespace MyRGB.Providers.Network;

/// <summary>
/// A Govee light over LAN Control. Govee's local control sets one color for the whole light, so it is
/// one LED to MyRGB; it takes the color of wherever it sits on the canvas.
/// </summary>
internal sealed class GoveeDevice : IRgbDevice
{
    /// <summary>Govee lights fall behind when sent more than about ten changes a second.</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(100);

    private readonly IPAddress _address;
    private Rgb? _sent;
    private bool? _on; // unknown until MyRGB first switches it
    private DateTime _sentAt = DateTime.MinValue;

    public GoveeDevice(IPAddress address, string deviceId, string model, string? name)
    {
        _address = address;
        Id = $"govee:{deviceId.Replace(":", "").ToLowerInvariant()}";
        HardwareKey = Id;
        Name = !string.IsNullOrWhiteSpace(name) ? name : model.Length > 0 ? $"Govee {model}" : "Govee light";
    }

    public string Id { get; }
    public string Name { get; }
    public string Vendor => "Govee";
    public DeviceType Type => DeviceType.Light;
    public string ProviderId => NetworkLightsProvider.ProviderId;
    public string? HardwareKey { get; }
    public IReadOnlyList<LedInfo> Leds { get; } = [new LedInfo("Light", 0.5f, 0.5f)];
    public IReadOnlyList<ZoneInfo> Zones { get; } = [new ZoneInfo("Light", 0, 1)];

    public async ValueTask ApplyAsync(ReadOnlyMemory<Rgb> colors, CancellationToken ct = default)
    {
        if (colors.IsEmpty) return;
        var c = colors.Span[0];
        var now = DateTime.UtcNow;
        if (c == _sent && now - _sentAt < TimeSpan.FromSeconds(5)) return;
        if (now - _sentAt < MinGap) return; // the engine sends again soon; the latest color wins
        _sent = c;
        _sentAt = now;

        // Black means off: a Govee light at color 0,0,0 can still glow, so switch it off instead.
        if (c == Rgb.Black)
        {
            if (_on != false) await GoveeLan.SendAsync(_address, GoveeLan.Turn(false), ct);
            _on = false;
            return;
        }
        if (_on != true)
        {
            await GoveeLan.SendAsync(_address, GoveeLan.Turn(true), ct);
            _on = true;
        }
        await GoveeLan.SendAsync(_address, GoveeLan.Color(c), ct);
    }
}
