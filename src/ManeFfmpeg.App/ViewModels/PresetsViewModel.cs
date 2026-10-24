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

    public PresetsViewModel()
    {
        Reload(null);
    }

    public ObservableCollection<Preset> Presets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial Preset? Selected { get; set; }

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
        Presets.Clear();
        foreach (var preset in _app.Presets.Presets)
            Presets.Add(preset);
        Selected = Presets.FirstOrDefault(p => p.Id == selectId) ?? Presets.FirstOrDefault();
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
        Warnings = string.Join("\n", analysis.Errors.Concat(analysis.Warnings));
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
    private void Add()
    {
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
    private void Duplicate()
    {
        if (Selected is not { } preset)
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
    private void RestoreBuiltIns()
    {
        _app.Presets.RestoreBuiltIns();
        Reload(Selected?.Id);
    }
}
