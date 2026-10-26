using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.App.ViewModels;

/// <summary>The preset editor: a list on the left, the selected preset's fields on the right.</summary>
public sealed partial class PresetsViewModel : ViewModelBase
{
    private readonly AppServices _app = AppServices.Instance;
    private bool _loading;
    private bool _reloading;

    public PresetsViewModel()
    {
        Reload(null);
    }

    public ObservableCollection<Preset> Presets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ListSelection))]
    public partial Preset? Selected { get; set; }

    /// <summary>
    /// Asks what to do with unsaved edits before they'd be lost: 0 saves them, 1 discards them, anything else
    /// keeps editing. Without it, edits are discarded.
    /// </summary>
    public Func<Task<int>>? AskAboutEdits { get; set; }

    /// <summary>Asks before the built-in presets are restored; true goes ahead. Without it, they're restored at once.</summary>
    public Func<Task<bool>>? ConfirmRestore { get; set; }

    /// <summary>The list's selection. Choosing another preset with unsaved edits asks first.</summary>
    public Preset? ListSelection
    {
        get => Selected;
        set
        {
            // Refilling the list clears its selection; that isn't the user choosing another preset.
            if (value == Selected || _reloading)
                return;
            if (!IsModified || AskAboutEdits is null)
                Selected = value;
            else
                _ = SwitchAsync(value);
        }
    }

    private async Task SwitchAsync(Preset? target)
    {
        if (await ResolveEditsAsync())
            Selected = Presets.FirstOrDefault(p => p.Id == target?.Id);
        else
            // The list moved to the target already; put it back on the preset being edited.
            OnPropertyChanged(nameof(ListSelection));
    }

    /// <summary>Saves or discards unsaved edits as the user chooses; false when they'd rather keep editing.</summary>
    public async Task<bool> ResolveEditsAsync()
    {
        if (!IsModified || AskAboutEdits is null)
            return true;
        switch (await AskAboutEdits())
        {
            case 0:
                Save();
                return true;
            case 1:
                return true;
            default:
                return false;
        }
    }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Arguments { get; set; } = "";

    [ObservableProperty]
    public partial string Extension { get; set; } = "";

    [ObservableProperty]
    public partial string AudioFallback { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    public partial bool IsModified { get; private set; }

    [ObservableProperty]
    public partial string Analysis { get; private set; } = "";

    [ObservableProperty]
    public partial string Warnings { get; private set; } = "";

    public bool HasSelection => Selected is not null;

    public IReadOnlyList<string> ExtensionSuggestions { get; } = ["", "mp4", "mkv", "mov", "webm", "m4v", "ts"];

    private void Reload(string? selectId)
    {
        _reloading = true;
        Presets.Clear();
        foreach (var preset in _app.Presets.Presets)
            Presets.Add(preset);
        _reloading = false;
        Selected = Presets.FirstOrDefault(p => p.Id == selectId) ?? Presets.FirstOrDefault();
        // The list lost its selection while refilling; an unchanged Selected wouldn't tell it to select again.
        OnPropertyChanged(nameof(ListSelection));
    }

    partial void OnSelectedChanged(Preset? value)
    {
        _loading = true;
        Name = value?.Name ?? "";
        Arguments = value?.Arguments ?? "";
        Extension = value?.Extension ?? "";
        AudioFallback = value?.AudioFallbackArguments ?? "";
        _loading = false;
        IsModified = false;
        Analyze();
    }

    partial void OnNameChanged(string value) => Edited();
    partial void OnArgumentsChanged(string value) => Edited();
    partial void OnExtensionChanged(string value) => Edited();
    partial void OnAudioFallbackChanged(string value) => Edited();

    private void Edited()
    {
        if (_loading)
            return;
        IsModified = Selected is not null;
        Analyze();
    }

    private void Analyze()
    {
        var analysis = PresetAnalysis.Of(Arguments, AudioFallback);
        if (analysis.Arguments is { } args)
        {
            var filters = (args.VideoFilter, args.AudioFilter) switch
            {
                (null, null) => "",
                _ => $"\nFilters applied after joining clips: {string.Join(", ", new[] { args.VideoFilter, args.AudioFilter }.OfType<string>())}",
            };
            var extension = string.IsNullOrWhiteSpace(Extension) ? "Output: same container as the first video." : $"Output: .{Extension.Trim().TrimStart('.')} file.";
            Analysis = $"{analysis.VideoSummary}\n{analysis.AudioSummary(AudioFallback)}\n{extension}{filters}";
        }
        else
        {
            Analysis = "";
        }
        Warnings = string.Join("\n", new[] { _app.Presets.SaveError }.OfType<string>().Concat(analysis.Errors).Concat(analysis.Warnings));
    }

    [RelayCommand(CanExecute = nameof(IsModified))]
    private void Save()
    {
        if (Selected is not { } preset)
            return;
        var updated = preset with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Untitled preset" : Name.Trim(),
            Arguments = Arguments.Trim(),
            Extension = Extension.Trim().TrimStart('.'),
            AudioFallbackArguments = string.IsNullOrWhiteSpace(AudioFallback) ? Preset.DefaultAudioFallback : AudioFallback.Trim(),
        };
        _app.Presets.Save(updated);
        Reload(updated.Id);
    }

    [RelayCommand(CanExecute = nameof(IsModified))]
    private void Revert() => OnSelectedChanged(Selected);

    [RelayCommand]
    private async Task Add()
    {
        if (!await ResolveEditsAsync())
            return;
        var preset = new Preset
        {
            Id = Ids.New(),
            Name = "New preset",
            Arguments = "-c:v libx264 -crf 20 -preset medium -c:a aac -b:a 192k",
            Extension = "mp4",
        };
        _app.Presets.Save(preset);
        Reload(preset.Id);
    }

    [RelayCommand]
    private async Task Duplicate()
    {
        if (!await ResolveEditsAsync() || Selected is not { } preset)
            return;
        var copy = preset with { Id = Ids.New(), Name = preset.Name + " copy" };
        _app.Presets.Save(copy);
        Reload(copy.Id);
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } preset)
            return;
        var index = Presets.IndexOf(preset);
        _app.Presets.Delete(preset.Id);
        Reload(Presets.ElementAtOrDefault(Math.Max(0, index - 1))?.Id);
        if (index > 0 && index - 1 < Presets.Count)
            Selected = Presets[index - 1];
    }

    [RelayCommand]
    private async Task RestoreBuiltIns()
    {
        if (ConfirmRestore is not null && !await ConfirmRestore())
            return;
        if (!await ResolveEditsAsync())
            return;
        _app.Presets.RestoreBuiltIns();
        Reload(Selected?.Id);
    }
}
