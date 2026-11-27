using CommunityToolkit.Mvvm.ComponentModel;
using MyRGB.App.Services;
using MyRGB.Core.Localization;

namespace MyRGB.App.ViewModels;

/// <summary>One choice in Settings' Language list. A null code follows Windows.</summary>
public sealed record LanguageOption(string? Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Settings' Language picker. Only shown once MyRGB has a second language; the pick is saved
/// right away and used the next time MyRGB starts (pages look their words up once, as they're built).
/// </summary>
public sealed partial class LanguageViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly string _running = Strings.Current;

    public LanguageViewModel(AppHost host)
    {
        _host = host;
        Options = [new LanguageOption(null, Strings.Get("settings.language.followWindows")),
                   .. Strings.Available().Select(l => new LanguageOption(l.Code, l.Name))];
        _selected = Options.FirstOrDefault(o => o.Code is not null && string.Equals(o.Code, host.Settings.Language, StringComparison.OrdinalIgnoreCase))
                    ?? Options[0];
    }

    public IReadOnlyList<LanguageOption> Options { get; }

    /// <summary>English plus at least one translation; with English alone there's nothing to pick.</summary>
    public bool ShowPicker => Options.Count > 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartNote))]
    private LanguageOption _selected;

    /// <summary>"Takes effect next start" while the pick differs from the language on screen; empty otherwise.</summary>
    public string RestartNote
    {
        get
        {
            var wanted = Strings.Resolve(Selected.Code);
            if (string.Equals(wanted, _running, StringComparison.OrdinalIgnoreCase)) return "";
            var name = Options.FirstOrDefault(o => string.Equals(o.Code, wanted, StringComparison.OrdinalIgnoreCase))?.Name ?? wanted;
            return Strings.Format("settings.language.restartNote", name);
        }
    }

    partial void OnSelectedChanged(LanguageOption value)
    {
        if (value is null) return;
        _host.Settings.Language = value.Code;
        _host.ScheduleSave();
    }
}
