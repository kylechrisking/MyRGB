using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyRGB.StreamDeck;

/// <summary>
/// All MyRGB actions. Each visible key/dial is tracked by its Stream Deck context so state
/// changes from anywhere (the app, a hotkey, another key) are reflected on every key at once.
/// </summary>
internal sealed class Plugin(StreamDeckConnection deck, MyRgbClient rgb)
{
    public const string PresetAction = "com.myrgb.streamdeck.preset";
    public const string PowerAction = "com.myrgb.streamdeck.power";
    public const string BrightnessAction = "com.myrgb.streamdeck.brightness";
    public const string CycleAction = "com.myrgb.streamdeck.cycle";
    public const string PauseAction = "com.myrgb.streamdeck.pause";
    public const string ShuffleAction = "com.myrgb.streamdeck.shuffle";

    private sealed record Instance(string Action, bool IsEncoder, JsonObject Settings);

    private readonly ConcurrentDictionary<string, Instance> _visible = new();
    private List<PresetSummary> _presets = [];
    private DateTime _presetsFetched = DateTime.MinValue;

    public async Task OnMessage(JsonObject msg)
    {
        string? evt = msg["event"]?.GetValue<string>();
        string? context = msg["context"]?.GetValue<string>();
        string? action = msg["action"]?.GetValue<string>();
        var payload = msg["payload"] as JsonObject;

        switch (evt)
        {
            case "willAppear" when context is not null && action is not null:
                _visible[context] = new Instance(action, payload?["controller"]?.GetValue<string>() == "Encoder", Settings(payload));
                if (!rgb.Online) await rgb.RefreshAsync(); // redraws every key if it comes online
                await Render(context);
                break;
            case "willDisappear" when context is not null:
                _visible.TryRemove(context, out _);
                break;
            case "didReceiveSettings" when context is not null && _visible.TryGetValue(context, out var inst):
                _visible[context] = inst with { Settings = Settings(payload) };
                await Render(context);
                break;
            case "keyDown" when context is not null && _visible.TryGetValue(context, out var key):
                if (!await Press(key)) await deck.ShowAlertAsync(context);
                break;
            case "dialRotate" when context is not null && _visible.TryGetValue(context, out var dial):
                int ticks = payload?["ticks"]?.GetValue<int>() ?? 0;
                if (dial.Action == BrightnessAction) await rgb.AdjustBrightnessAsync(ticks * Step(dial) / 100.0);
                else if (dial.Action == CycleAction) await rgb.StepAsync(ticks);
                break;
            case "dialDown" or "touchTap" when context is not null:
                await rgb.TogglePowerAsync();
                break;
            case "sendToPlugin" when context is not null && action is not null:
                await SendPresetList(action, context);
                break;
        }
    }

    /// <summary>The app's state changed (or it went offline): redraw every visible key.</summary>
    public async Task OnStateChanged(LightingState? state)
    {
        foreach (var context in _visible.Keys) await Render(context);
    }

    private async Task<bool> Press(Instance key)
    {
        switch (key.Action)
        {
            case PresetAction:
                return key.Settings["presetId"]?.GetValue<string>() is { Length: > 0 } id && await rgb.ActivateAsync(id);
            case PowerAction:
                return await rgb.TogglePowerAsync();
            case CycleAction:
                return await rgb.StepAsync(key.Settings["direction"]?.GetValue<string>() == "previous" ? -1 : 1);
            case PauseAction:
                return await rgb.TogglePauseAsync();
            case ShuffleAction:
                return await rgb.ShuffleAsync();
            case BrightnessAction:
                string mode = key.Settings["mode"]?.GetValue<string>() ?? "up";
                return mode switch
                {
                    "set" => await rgb.SetBrightnessAsync(Number(key.Settings, "value", 50) / 100),
                    "down" => await rgb.AdjustBrightnessAsync(-Step(key) / 100),
                    _ => await rgb.AdjustBrightnessAsync(Step(key) / 100),
                };
            default:
                return false;
        }
    }

    private async Task Render(string context)
    {
        if (!_visible.TryGetValue(context, out var inst)) return;
        var s = rgb.State;
        if (s is null)
        {
            await deck.SetTitleAsync(context, "MyRGB\noffline");
            if (inst.IsEncoder) await deck.SetFeedbackAsync(context, new JsonObject { ["title"] = "MyRGB", ["value"] = "offline" });
            return;
        }

        switch (inst.Action)
        {
            case PresetAction:
            {
                string? id = inst.Settings["presetId"]?.GetValue<string>();
                var preset = (await Presets()).FirstOrDefault(p => p.Id == id);
                if (preset is null)
                {
                    await deck.SetImageAsync(context, null);
                    await deck.SetTitleAsync(context, "Pick a\npreset");
                    break;
                }
                await deck.SetImageAsync(context, KeyImages.Preset(preset.Swatch, preset.Id == s.ActivePresetId, s.Enabled));
                await deck.SetTitleAsync(context, Wrap(preset.Name));
                break;
            }
            case PowerAction:
                await deck.SetStateAsync(context, s.Enabled ? 0 : 1);
                await deck.SetTitleAsync(context, s.Enabled ? "On" : "Off");
                break;
            case BrightnessAction:
                string pct = $"{Math.Round(s.Brightness * 100)}%";
                await deck.SetImageAsync(context, KeyImages.Brightness(s.Brightness, s.Enabled));
                await deck.SetTitleAsync(context, pct);
                if (inst.IsEncoder)
                    await deck.SetFeedbackAsync(context, new JsonObject
                    {
                        ["title"] = s.Enabled ? "Brightness" : "Lights off",
                        ["value"] = pct,
                        ["indicator"] = new JsonObject { ["value"] = (int)Math.Round(s.Brightness * 100) },
                    });
                break;
            case PauseAction:
                await deck.SetStateAsync(context, s.Paused ? 1 : 0);
                await deck.SetTitleAsync(context, s.Paused ? "Paused" : "Playing");
                break;
            case ShuffleAction:
                await deck.SetTitleAsync(context, Wrap(s.ActivePresetName ?? ""));
                break;
            case CycleAction:
                await deck.SetTitleAsync(context, Wrap(s.ActivePresetName ?? ""));
                if (inst.IsEncoder)
                    await deck.SetFeedbackAsync(context, new JsonObject { ["title"] = "Preset", ["value"] = s.ActivePresetName ?? "" });
                break;
        }
    }

    private async Task SendPresetList(string action, string context)
    {
        _presetsFetched = DateTime.MinValue; // the inspector opening is a good moment to refresh
        var presets = await Presets();
        var list = new JsonArray(presets.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["builtIn"] = p.BuiltIn }).ToArray());
        await deck.SendToPropertyInspectorAsync(action, context, new JsonObject
        {
            ["type"] = "presets",
            ["online"] = rgb.Online,
            ["presets"] = list,
        });
    }

    private async Task<List<PresetSummary>> Presets()
    {
        if (DateTime.UtcNow - _presetsFetched < TimeSpan.FromSeconds(30) && _presets.Count > 0) return _presets;
        try
        {
            _presets = await rgb.GetPresetsAsync();
            _presetsFetched = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        return _presets;
    }

    private static JsonObject Settings(JsonObject? payload) =>
        payload?["settings"]?.DeepClone() as JsonObject ?? new JsonObject();

    private static double Step(Instance i) => Number(i.Settings, "step", 10);

    private static double Number(JsonObject settings, string key, double fallback)
    {
        var node = settings[key];
        if (node is null) return fallback;
        if (node.GetValueKind() == JsonValueKind.Number) return node.GetValue<double>();
        return double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    /// <summary>Stream Deck titles don't wrap on their own; split long names over two lines.</summary>
    private static string Wrap(string name)
    {
        if (name.Length <= 9 || !name.Contains(' ')) return name;
        int mid = name.Length / 2;
        int split = name.LastIndexOf(' ', mid) is var left and > 0 ? left : name.IndexOf(' ', mid);
        return split > 0 ? name[..split] + "\n" + name[(split + 1)..] : name;
    }
}
