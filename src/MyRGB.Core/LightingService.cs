using MyRGB.Core.Devices;
using MyRGB.Core.Effects;
using MyRGB.Core.Engine;
using MyRGB.Core.Presets;

namespace MyRGB.Core;

public sealed record LightingState(bool Enabled, double Brightness, string? ActivePresetId, string? ActivePresetName, bool Paused = false);

public sealed record PresetSummary(string Id, string Name, string EffectId, bool BuiltIn, IReadOnlyList<string> Swatch);

public sealed record DeviceSummary(string Id, string Name, string Vendor, string Type, string Provider, int LedCount);

/// <summary>
/// The control surface everything outside the engine uses: the desktop UI, the local HTTP API,
/// and through it the Stream Deck plugin.
/// </summary>
public interface ILightingController
{
    LightingState State { get; }
    IReadOnlyList<PresetSummary> GetPresets();
    IReadOnlyList<DeviceSummary> GetDevices();
    bool ActivatePreset(string id);
    void StepPreset(int delta);
    void SetEnabled(bool enabled);
    void SetBrightness(double brightness);

    /// <summary>Freeze or resume the animation.</summary>
    void SetPaused(bool paused);

    /// <summary>Switch to a random preset other than the current one.</summary>
    void Shuffle();

    /// <summary>Pulse one device (or all, when null) so it can be spotted. Returns false for an unknown device.</summary>
    bool Flash(string? deviceId, Rgb? color = null);

    /// <summary>Raised when power, brightness, the active preset, or the preset list changes. Any thread.</summary>
    event EventHandler? StateChanged;
}

public sealed class LightingService : ILightingController
{
    private readonly LightingEngine _engine;
    private readonly PresetLibrary _presets;
    private readonly EffectRegistry _effects;
    private readonly DeviceManager _devices;
    private readonly Dictionary<string, string[]> _swatchCache = new();

    public LightingService(LightingEngine engine, PresetLibrary presets, EffectRegistry effects, DeviceManager devices)
    {
        _engine = engine;
        _presets = presets;
        _effects = effects;
        _devices = devices;
        _presets.Changed += (_, _) =>
        {
            lock (_swatchCache) _swatchCache.Clear();
            // If the active preset was edited, pick up the new version.
            if (_engine.ActivePreset is { } active && _presets.Find(active.Id) is { } updated && !ReferenceEquals(updated, active))
                _engine.Activate(updated);
            Raise();
        };
    }

    public event EventHandler? StateChanged;

    public LightingState State =>
        new(_engine.Enabled, _engine.Brightness, _engine.ActivePreset?.Id, _engine.ActivePreset?.Name, _engine.Paused);

    /// <summary>How long a flash lasts unless stopped.</summary>
    public static readonly TimeSpan FlashLength = TimeSpan.FromSeconds(4);

    /// <summary>The default flash color: white reads on every LED and stands out from most effects.</summary>
    public static readonly Rgb FlashColor = new(255, 255, 255);

    public void SetPaused(bool paused)
    {
        _engine.Paused = paused;
        Raise();
    }

    public void Shuffle()
    {
        var others = _presets.All.Where(p => p.Id != _engine.ActivePreset?.Id).ToList();
        if (others.Count > 0) ActivatePreset(others[Random.Shared.Next(others.Count)].Id);
    }

    public bool Flash(string? deviceId, Rgb? color = null) => Flash(deviceId is null ? null : [deviceId], color, FlashLength);

    /// <summary>Pulse several devices at once (all when null), for as long as asked.</summary>
    public bool Flash(IReadOnlyCollection<string>? deviceIds, Rgb? color, TimeSpan length)
    {
        if (deviceIds is not null && !deviceIds.All(id => _devices.Devices.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            return false;
        _engine.Flash(deviceIds, color ?? FlashColor, length);
        return true;
    }

    public void StopFlash() => _engine.StopFlash();

    public IReadOnlyList<PresetSummary> GetPresets() =>
        _presets.All.Select(p => new PresetSummary(p.Id, p.Name, p.EffectId, p.BuiltIn, SwatchFor(p))).ToList();

    public IReadOnlyList<DeviceSummary> GetDevices() =>
        _devices.Devices.Select(d => new DeviceSummary(d.Id, d.Name, d.Vendor, d.Type.ToString(), d.ProviderId, d.Leds.Count)).ToList();

    public bool ActivatePreset(string id)
    {
        var preset = _presets.Find(id);
        if (preset is null) return false;
        _engine.Activate(preset);
        Raise();
        return true;
    }

    /// <summary>Run a preset that isn't in the library (the Studio's live draft).</summary>
    public void Preview(Preset preset)
    {
        _engine.Activate(preset);
        Raise();
    }

    public void StepPreset(int delta)
    {
        var all = _presets.All;
        if (all.Count == 0) return;
        int current = _engine.ActivePreset is { } a ? all.ToList().FindIndex(p => p.Id == a.Id) : -1;
        int next = ((current + delta) % all.Count + all.Count) % all.Count;
        ActivatePreset(all[next].Id);
    }

    public void SetEnabled(bool enabled)
    {
        _engine.Enabled = enabled;
        Raise();
    }

    public void SetBrightness(double brightness)
    {
        _engine.Brightness = brightness;
        Raise();
    }

    private string[] SwatchFor(Preset p)
    {
        lock (_swatchCache)
        {
            if (!_swatchCache.TryGetValue(p.Id, out var s))
                _swatchCache[p.Id] = s = PresetLibrary.Swatch(p, _effects).Select(c => c.ToHex()).ToArray();
            return s;
        }
    }

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
}
