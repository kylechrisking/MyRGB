using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;
using MyRGB.Core.Effects;
using MyRGB.Core.Presets;

namespace MyRGB.App.ViewModels;

public sealed record EffectItem(EffectDefinition Definition)
{
    public string Name => Definition.Name;
    public string Category => Definition.Category;
    public string Description => Definition.Description;
    public override string ToString() => Name;
}

/// <summary>"All devices", or one device whose override is being edited.</summary>
public sealed record TargetItem(string? DeviceId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The effect editor. Edits a draft preset that runs live on the hardware; Save writes it to the
/// library. Slider moves update the running effect in place so animations don't restart.
/// </summary>
public sealed partial class StudioViewModel : ObservableObject
{
    public const string FollowMode = "Follow main effect";
    public const string OwnMode = "Own effect";
    public const string OffMode = "Off";

    private readonly AppHost _host;
    private string _baseEffectId = "rainbow-wave";
    private Dictionary<string, string> _baseSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _editingId;
    private bool _loading;

    public StudioViewModel(AppHost host)
    {
        _host = host;
        Effects = new(host.Effects.All.OrderBy(e => e.Category).ThenBy(e => e.Name).Select(e => new EffectItem(e)));
        Targets.Add(new TargetItem(null, "All devices"));
        _selectedTarget = Targets[0];
    }

    public ObservableCollection<EffectItem> Effects { get; }
    public ObservableCollection<TargetItem> Targets { get; } = [];
    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];
    public IReadOnlyList<string> Modes { get; } = [FollowMode, OwnMode, OffMode];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeviceTarget), nameof(ShowEffectEditor), nameof(TargetHint))]
    private TargetItem _selectedTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEffectEditor), nameof(TargetHint))]
    private string _targetMode = FollowMode;

    /// <summary>One line explaining what the current "Edit for" choice means.</summary>
    public string TargetHint => !IsDeviceTarget
        ? "These settings apply to every device. Choose a single device to give it a look of its own."
        : TargetMode switch
        {
            OwnMode => "This device runs its own effect. Pick one on the left and tune it below.",
            OffMode => "This device stays dark while this preset plays.",
            _ => "This device shows the main effect, like everything else.",
        };

    [ObservableProperty]
    private EffectItem? _selectedEffect;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsNewCommand))]
    private string _presetName = "My preset";

    [ObservableProperty]
    private string _status = "";

    public bool IsDeviceTarget => SelectedTarget.DeviceId is not null;
    public bool ShowEffectEditor => !IsDeviceTarget || TargetMode == OwnMode;

    /// <summary>Saving overwrites the preset being edited; false for built-ins and new drafts.</summary>
    public bool IsEditingUserPreset => _editingId is not null;

    public void SetDevices(IEnumerable<(string Id, string Name)> devices)
    {
        var current = SelectedTarget.DeviceId;
        while (Targets.Count > 1) Targets.RemoveAt(1);
        foreach (var (id, name) in devices) Targets.Add(new TargetItem(id, name));
        SelectedTarget = Targets.FirstOrDefault(t => t.DeviceId == current) ?? Targets[0];
    }

    /// <summary>Open a preset for editing. Built-ins are copied; saving makes a new user preset.</summary>
    public void Load(Preset preset)
    {
        _baseEffectId = preset.EffectId;
        _baseSettings = new(preset.Settings, StringComparer.OrdinalIgnoreCase);
        _overrides.Clear();
        foreach (var o in preset.Overrides) _overrides[o.DeviceId] = o;
        _editingId = preset.BuiltIn || preset.Id == AppHost.DraftPresetId ? null : preset.Id;
        PresetName = preset.BuiltIn ? $"My {preset.Name}" : preset.Name;
        OnPropertyChanged(nameof(IsEditingUserPreset));
        SelectedTarget = Targets[0];
        RefreshEditor();
        ApplyDraft();
        Status = preset.BuiltIn ? "Editing a copy of a built-in preset." : $"Editing \"{preset.Name}\".";
    }

    partial void OnSelectedTargetChanged(TargetItem value) => RefreshEditor();

    partial void OnTargetModeChanged(string value)
    {
        if (_loading || SelectedTarget.DeviceId is not { } id) return;
        _overrides.TryGetValue(id, out var existing);
        switch (value)
        {
            case OffMode:
                _overrides[id] = new DeviceOverride { DeviceId = id, Off = true };
                break;
            case OwnMode:
                _overrides[id] = new DeviceOverride { DeviceId = id, EffectId = existing?.EffectId ?? _baseEffectId, Settings = existing?.Settings ?? new(_baseSettings) };
                break;
            default:
                _overrides.Remove(id);
                break;
        }
        RefreshEditor();
        ApplyDraft();
    }

    partial void OnSelectedEffectChanged(EffectItem? value)
    {
        if (_loading || value is null) return;
        if (SelectedTarget.DeviceId is { } id)
        {
            if (!_overrides.TryGetValue(id, out var o) || o.EffectId == value.Definition.Id) return;
            _overrides[id] = o with { EffectId = value.Definition.Id, Settings = new(StringComparer.OrdinalIgnoreCase) };
        }
        else
        {
            if (_baseEffectId == value.Definition.Id) return;
            _baseEffectId = value.Definition.Id;
            _baseSettings = new(StringComparer.OrdinalIgnoreCase);
        }
        RebuildParameters();
        ApplyDraft();
    }

    private void RefreshEditor()
    {
        _loading = true;
        try
        {
            if (SelectedTarget.DeviceId is { } id)
            {
                _overrides.TryGetValue(id, out var o);
                TargetMode = o is null ? FollowMode : o.Off ? OffMode : OwnMode;
            }
            SelectedEffect = Effects.FirstOrDefault(e => e.Definition.Id == CurrentEffectId());
        }
        finally
        {
            _loading = false;
        }
        RebuildParameters();
    }

    private string CurrentEffectId() =>
        SelectedTarget.DeviceId is { } id && _overrides.TryGetValue(id, out var o) && o.EffectId is { } e ? e : _baseEffectId;

    private Dictionary<string, string> CurrentSettings() =>
        SelectedTarget.DeviceId is { } id && _overrides.TryGetValue(id, out var o) && o.EffectId is not null ? o.Settings : _baseSettings;

    private void RebuildParameters()
    {
        Parameters.Clear();
        if (!ShowEffectEditor || _host.Effects.Find(CurrentEffectId()) is not { } def) return;
        var values = CurrentSettings();
        foreach (var spec in def.Parameters)
            Parameters.Add(ParameterViewModel.Create(spec, values.GetValueOrDefault(spec.Key) ?? spec.DefaultValue, OnParameterChanged));
    }

    private void OnParameterChanged(string key, string value)
    {
        var settings = CurrentSettings();
        settings[key] = value;
        // Live update in place; the draft preset itself picks the value up on the next ApplyDraft/Save.
        _host.Engine.UpdateLiveSettings(SelectedTarget.DeviceId is { } id && _overrides.ContainsKey(id) ? id : null, new EffectSettings(settings));
        if (_host.Engine.ActivePreset?.Id != AppHost.DraftPresetId) ApplyDraft();
    }

    private Preset BuildPreset(string id) => new()
    {
        Id = id,
        Name = PresetName.Trim(),
        EffectId = _baseEffectId,
        Settings = new(_baseSettings, StringComparer.OrdinalIgnoreCase),
        Overrides = _overrides.Values.Select(o => o with { Settings = new(o.Settings, StringComparer.OrdinalIgnoreCase) }).ToList(),
    };

    private void ApplyDraft() => _host.Lights.Preview(BuildPreset(AppHost.DraftPresetId) with { Name = $"{PresetName} (unsaved)" });

    private bool CanSave() => !string.IsNullOrWhiteSpace(PresetName);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Store(_editingId ?? PresetLibrary.NewId(PresetName));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveAsNew() => Store(PresetLibrary.NewId(PresetName));

    [RelayCommand]
    private void Revert()
    {
        if (_host.Settings.ActivePresetId is { } id && _host.Presets.Find(id) is { } p)
        {
            _host.Lights.ActivatePreset(p.Id);
            Status = $"Back to \"{p.Name}\". Unsaved changes are kept here.";
        }
    }

    private void Store(string id)
    {
        var saved = _host.Presets.Save(BuildPreset(id));
        _editingId = saved.Id;
        OnPropertyChanged(nameof(IsEditingUserPreset));
        _host.Lights.ActivatePreset(saved.Id);
        Status = $"Saved \"{saved.Name}\".";
    }
}
