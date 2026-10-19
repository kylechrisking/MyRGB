using MyRGB.StreamDeck;

// Stream Deck launches the plugin as:
//   MyRGB.StreamDeck.exe -port 28196 -pluginUUID <id> -registerEvent registerPlugin -info {...}
var arg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i + 1 < args.Length; i += 2) arg[args[i].TrimStart('-')] = args[i + 1];

if (!arg.TryGetValue("port", out var portText) || !int.TryParse(portText, out int port)
    || !arg.TryGetValue("pluginUUID", out var uuid) || !arg.TryGetValue("registerEvent", out var registerEvent))
{
    Log.Write($"Bad launch arguments: {string.Join(' ', args)}");
    return 1;
}

Log.Write($"Starting (Stream Deck port {port})");
using var cts = new CancellationTokenSource();
using var rgb = new MyRgbClient();
await using var deck = new StreamDeckConnection(port, uuid, registerEvent);
var plugin = new Plugin(deck, rgb);

deck.MessageReceived += plugin.OnMessage;
rgb.StateChanged += plugin.OnStateChanged;

var follow = rgb.FollowAsync(cts.Token);
try
{
    await deck.RunAsync(cts.Token);
}
catch (Exception ex)
{
    Log.Write($"Stream Deck connection ended: {ex.Message}");
}
cts.Cancel();
try { await follow; } catch (OperationCanceledException) { }
Log.Write("Stopped");
return 0;
