using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>The preset editor: Video and Audio tabs, each a list on the left and the selected preset's fields on the right.</summary>
public sealed partial class PresetsViewModel : ViewModelBase
{
    private readonly AppServices _app = AppServices.Instance;
    private bool _loading;
    private bool _reloading;

    public PresetsViewModel()
    {
        Reload(null);
    }

    /// <summary>The presets of the open tab.</summary>
    public ObservableCollection<Preset> Presets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindIndex), nameof(IsAudio), nameof(ExtensionSuggestions), nameof(ExtensionPlaceholder),
        nameof(ArgumentsPlaceholder), nameof(ArgumentsHelp))]
    public partial PresetKind Kind { get; private set; }

    public bool IsAudio => Kind == PresetKind.Audio;

    /// <summary>The open tab: 0 for video presets, 1 for audio presets. Switching with unsaved edits asks first.</summary>
    public int KindIndex
    {
        get => IsAudio ? 1 : 0;
        set
        {
            var kind = value == 1 ? PresetKind.Audio : PresetKind.Video;
            if (kind == Kind)
                return;
            if (!IsModified || AskAboutEdits is null)
                Kind = kind;
            else
                _ = SwitchKindAsync(kind);
        }
    }

    private async Task SwitchKindAsync(PresetKind kind)
    {
        if (await ResolveEditsAsync())
            Kind = kind;
        else
            // The tabs moved already; put them back on the preset being edited.
            OnPropertyChanged(nameof(KindIndex));
    }

    partial void OnKindChanged(PresetKind value) => Reload(null);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ListSelection))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand))]
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

    public IReadOnlyList<string> ExtensionSuggestions => IsAudio
        ? ["", "m4a", "mp3", "flac", "wav", "opus", "ogg", "mka"]
        : ["", "mp4", "mkv", "mov", "webm", "m4v", "ts"];

    public string ExtensionPlaceholder => IsAudio ? "empty = picked from the audio codec" : "empty = same as the first video";

    public string ArgumentsPlaceholder => IsAudio ? "-c:a aac -b:a 256k" : "-c:v libx265 -crf 28 -preset slow -c:a copy";

    public string ArgumentsHelp => IsAudio
        ? "Everything ffmpeg accepts between the inputs and the output file. Only the audio is saved: inputs, -map, -ss/-t and the output name are filled in by the editor; -af is applied after the clips are joined."
        : "Everything ffmpeg accepts between the inputs and the output file. Inputs, -map, -ss/-t and the output name are filled in by the editor; -vf/-af are applied after the clips are joined.";

    private void Reload(string? selectId)
    {
        _reloading = true;
        Presets.Clear();
        foreach (var preset in _app.Presets.Presets.Where(p => p.Kind == Kind))
            Presets.Add(preset);
        _reloading = false;
        Selected = Presets.FirstOrDefault(p => p.Id == selectId) ?? Presets.FirstOrDefault();
        // The list lost its selection while refilling; an unchanged Selected wouldn't tell it to select again.
        OnPropertyChanged(nameof(ListSelection));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
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
        var analysis = PresetAnalysis.Of(Arguments, AudioFallback, Kind);
        if (analysis.Arguments is { } args)
        {
            var filters = (args.VideoFilter, args.AudioFilter) switch
            {
                (null, null) => "",
                _ => $"\nFilters applied after joining clips: {string.Join(", ", new[] { args.VideoFilter, args.AudioFilter }.OfType<string>())}",
            };
            var extension = !string.IsNullOrWhiteSpace(Extension) ? $"Output: .{Extension.Trim().TrimStart('.')} file."
                : IsAudio ? "Output: the file type of the audio codec (.m4a for AAC and ALAC, .mp3, .flac, .opus, .wav…; .mka for others)."
                : "Output: same container as the first video.";
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
        var preset = IsAudio
            ? new Preset { Id = Ids.New(), Name = "New audio preset", Kind = PresetKind.Audio, Arguments = "-c:a aac -b:a 256k", Extension = "m4a" }
            : new Preset
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

    private bool CanMoveUp() => Selected is not null && Presets.IndexOf(Selected) > 0;

    private bool CanMoveDown() => Selected is not null && Presets.IndexOf(Selected) is var i && i >= 0 && i < Presets.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => MoveSelectedTo(Presets.IndexOf(Selected!) - 1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => MoveSelectedTo(Presets.IndexOf(Selected!) + 1);

    /// <summary>Puts the selected preset at <paramref name="index"/> in the list. Unsaved edits stay in the editor.</summary>
    public void MoveSelectedTo(int index)
    {
        if (Selected is not { } preset)
            return;
        _app.Presets.Move(preset.Id, index);
        Reload(preset.Id);
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
