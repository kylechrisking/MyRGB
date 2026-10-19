using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyRGB.StreamDeck;

/// <summary>
/// The plugin side of the Stream Deck SDK: a WebSocket to the Stream Deck app on the port it
/// passes us at launch. Receives key/dial events, sends titles, images, states and settings.
/// </summary>
internal sealed class StreamDeckConnection(int port, string pluginUuid, string registerEvent) : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Func<JsonObject, Task>? MessageReceived;

    public async Task RunAsync(CancellationToken ct)
    {
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), ct);
        await SendAsync(new JsonObject { ["event"] = registerEvent, ["uuid"] = pluginUuid });

        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (JsonNode.Parse(text) is JsonObject obj && MessageReceived is { } handler)
            {
                try { await handler(obj); }
                catch (Exception ex) { Log.Write($"Handling {obj["event"]} failed: {ex}"); }
            }
        }
    }

    public async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SetTitleAsync(string context, string? title) =>
        SendAsync(new JsonObject { ["event"] = "setTitle", ["context"] = context, ["payload"] = new JsonObject { ["title"] = title, ["target"] = 0 } });

    public Task SetImageAsync(string context, string? image) =>
        SendAsync(new JsonObject { ["event"] = "setImage", ["context"] = context, ["payload"] = new JsonObject { ["image"] = image, ["target"] = 0 } });

    public Task SetStateAsync(string context, int state) =>
        SendAsync(new JsonObject { ["event"] = "setState", ["context"] = context, ["payload"] = new JsonObject { ["state"] = state } });

    public Task SetFeedbackAsync(string context, JsonObject payload) =>
        SendAsync(new JsonObject { ["event"] = "setFeedback", ["context"] = context, ["payload"] = payload });

    public Task ShowAlertAsync(string context) => SendAsync(new JsonObject { ["event"] = "showAlert", ["context"] = context });

    public Task SendToPropertyInspectorAsync(string action, string context, JsonNode payload) =>
        SendAsync(new JsonObject { ["event"] = "sendToPropertyInspector", ["action"] = action, ["context"] = context, ["payload"] = payload });

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (WebSocketException) { }
        }
        _ws.Dispose();
    }
}

internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyRGB", "logs", "streamdeck.log");
    private static readonly object Gate = new();

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.Delete(Path);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
