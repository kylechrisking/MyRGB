using System.Text.Json.Serialization;

namespace MyRGB.Providers.Network;

/// <summary>What kind of light lives at an address. Nanoleaf and Hue come next.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetworkLightKind>))]
public enum NetworkLightKind { Wled, Govee }

/// <summary>
/// A light on the home network the user added (by hand or with "Find lights"). MyRGB only talks to
/// lights in this list, so nothing on the network changes until the user adds it.
/// </summary>
/// <param name="Address">IP address or host name, e.g. "192.168.1.40" or "wled-desk.local".</param>
/// <param name="Name">The name it reported when added; shown while the light is offline.</param>
public sealed record NetworkLight(NetworkLightKind Kind, string Address, string? Name = null);
