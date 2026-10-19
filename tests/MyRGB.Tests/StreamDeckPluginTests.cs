using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyRGB.Tests;

/// <summary>
/// Runs the real plugin exe against a fake Stream Deck app (a local WebSocket server), with the
/// MyRGB app running. Explicit: it changes your lights briefly, then restores them.
/// </summary>
public class StreamDeckPluginTests(ITestOutputHelper output)
{
    private static readonly string PluginExe = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MyRGB.StreamDeck", "bin", "Debug", "net10.0", "MyRGB.StreamDeck.exe"));

    [Fact(Explicit = true)]
    public async Task Plugin_drives_the_app_like_stream_deck_would()
    {
        var ct = TestContext.Current.CancellationToken;
        using var api = new HttpClient { BaseAddress = new Uri("http://localhost:6743/api/") };
        api.DefaultRequestHeaders.Add("X-MyRGB", "tests");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var before = await api.GetFromJsonAsync<JsonObject>("state", json, ct);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var plugin = Process.Start(new ProcessStartInfo(PluginExe, ["-port", port.ToString(), "-pluginUUID", "TEST", "-registerEvent", "registerPlugin", "-info", "{}"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct);
            using var ws = await AcceptWebSocket(tcp.GetStream(), ct);

            var register = await Receive(ws, ct);
            Assert.Equal("registerPlugin", register["event"]!.GetValue<string>());
            Assert.Equal("TEST", register["uuid"]!.GetValue<string>());

            // A preset key appears: the plugin should paint the swatch and title it.
            await Send(ws, Event("willAppear", "com.myrgb.streamdeck.preset", "k1", new JsonObject { ["presetId"] = "ocean" }), ct);
            var image = await ReceiveUntil(ws, m => m["event"]!.GetValue<string>() == "setImage" && m["context"]!.GetValue<string>() == "k1", ct);
            Assert.StartsWith("data:image/svg+xml", image["payload"]!["image"]!.GetValue<string>());
            var painted = await ReceiveUntil(ws, m => m["event"]!.GetValue<string>() == "setTitle" && m["context"]!.GetValue<string>() == "k1", ct);
            output.WriteLine(painted.ToJsonString());
            Assert.Equal("Ocean", painted["payload"]!["title"]!.GetValue<string>());

            // Press it.
            await Send(ws, Event("keyDown", "com.myrgb.streamdeck.preset", "k1", new JsonObject { ["presetId"] = "ocean" }), ct);
            await Task.Delay(500, ct);
            var state = await api.GetFromJsonAsync<JsonObject>("state", json, ct);
            Assert.Equal("ocean", state!["activePresetId"]!.GetValue<string>());

            // A brightness key set to "Set to 40%".
            var bright = new JsonObject { ["mode"] = "set", ["value"] = 40 };
            await Send(ws, Event("willAppear", "com.myrgb.streamdeck.brightness", "k2", bright), ct);
            await Send(ws, Event("keyDown", "com.myrgb.streamdeck.brightness", "k2", bright), ct);
            var title = await ReceiveUntil(ws, m => m["event"]!.GetValue<string>() == "setTitle" && m["context"]!.GetValue<string>() == "k2"
                                                    && m["payload"]!["title"]!.GetValue<string>() == "40%", ct);
            Assert.NotNull(title);

            // The property inspector asks for the preset list.
            await Send(ws, new JsonObject { ["event"] = "sendToPlugin", ["action"] = "com.myrgb.streamdeck.preset", ["context"] = "k1", ["payload"] = new JsonObject { ["type"] = "getPresets" } }, ct);
            var list = await ReceiveUntil(ws, m => m["event"]!.GetValue<string>() == "sendToPropertyInspector", ct);
            Assert.True(list["payload"]!["presets"]!.AsArray().Count >= 10);
        }
        finally
        {
            if (!plugin.HasExited) plugin.Kill();
            listener.Stop();
            // Put the lights back how they were.
            await api.PostAsync($"presets/{before!["activePresetId"]}/activate", null, ct);
            await api.PostAsJsonAsync("brightness", new { value = before["brightness"]!.GetValue<double>() }, ct);
        }
    }

    private static JsonObject Event(string evt, string action, string context, JsonObject settings) => new()
    {
        ["event"] = evt,
        ["action"] = action,
        ["context"] = context,
        ["device"] = "dev",
        ["payload"] = new JsonObject { ["settings"] = settings.DeepClone(), ["controller"] = "Keypad" },
    };

    /// <summary>Minimal server side of the RFC 6455 handshake.</summary>
    private static async Task<WebSocket> AcceptWebSocket(NetworkStream stream, CancellationToken ct)
    {
        var request = new StringBuilder();
        var buf = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n"))
        {
            await stream.ReadExactlyAsync(buf, ct);
            request.Append((char)buf[0]);
        }
        var key = request.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, TimeSpan.FromSeconds(30));
    }

    private static Task Send(WebSocket ws, JsonObject msg, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonObject> Receive(WebSocket ws, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buf = new byte[256 * 1024];
        var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, timeout.Token);
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonNode.Parse(ms.ToArray())!.AsObject();
    }

    private static async Task<JsonObject> ReceiveUntil(WebSocket ws, Func<JsonObject, bool> match, CancellationToken ct)
    {
        while (true)
        {
            var m = await Receive(ws, ct);
            if (match(m)) return m;
        }
    }
}
