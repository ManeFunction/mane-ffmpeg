using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>One project's export: preset, output file, the ffmpeg command preview and the running export.</summary>
public sealed partial class ExportViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly AppServices _app = AppServices.Instance;

    private readonly DispatcherTimer _planTimer;
    private ExportSession? _session;
    private string? _lastOutputPath;

    /// <summary>The running export's latest progress, or what it's doing before ffmpeg starts.</summary>
    private ExportProgress? _progress;
    private string? _preparing;

    /// <summary>An existing output file the user agreed to replace (in the save dialog or when asked).</summary>
    private string? _confirmedOutputPath;

    /// <summary>
    /// Each mode's preset and explicit output path; <see cref="SelectedPreset"/> and <see cref="OutputPath"/> show the
    /// current mode's. The project saves the video ones; the audio ones last while the window is open.
    /// </summary>
    private readonly ModeChoice[] _modes = [new(), new()];

    /// <summary>A mode's preset and explicit output path (null: automatic).</summary>
    private sealed class ModeChoice
    {
        public Preset? Preset;
        public string? OutputPath;
    }

    private ModeChoice Choice(PresetKind kind) => _modes[(int)kind];

    /// <summary>The current mode's choice.</summary>
    private ModeChoice Mode => Choice(Kind);

    /// <summary>Set while the menu is refilled, when its selection changing isn't the user's choice.</summary>
    private bool _refilling;

    /// <summary>
    /// Set while the code selects a preset (a project's, or the mode's after the menu was refilled), which isn't
    /// remembered as the user's last choice.
    /// </summary>
    private bool _selectingFromCode;

    private ProjectState State => _owner.State;
    private SourceCatalog Catalog => _owner.Catalog;
    private IMainWindowView? View => _owner.View;

    /// <summary>The current mode's presets: video presets for Export video, audio presets for Extract audio.</summary>
    public ObservableCollection<Preset> Presets { get; } = [];

    /// <summary>The export button saves the timeline's audio alone (and turns green) instead of exporting the video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(OutputExtensionHint))]
    public partial bool IsAudioMode { get; set; }

    public PresetKind Kind => IsAudioMode ? PresetKind.Audio : PresetKind.Video;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputExtensionHint))]
    public partial Preset? SelectedPreset { get; set; }

    /// <summary>Explicit output path; null means "derive from the first video".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDisplay))]
    public partial string? OutputPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(CancelExportCommand), nameof(PauseExportCommand), nameof(UseVideoModeCommand), nameof(UseAudioModeCommand))]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseExportCommand), nameof(ResumeExportCommand))]
    public partial bool IsExportPaused { get; private set; }

    [ObservableProperty]
    public partial double ExportProgress { get; private set; }

    [ObservableProperty]
    public partial string? ExportProgressText { get; private set; }

    [ObservableProperty]
    public partial string? ExportStatus { get; private set; }

    [ObservableProperty]
    public partial bool ExportFailed { get; private set; }

    [ObservableProperty]
    public partial bool CanReveal { get; private set; }

    [ObservableProperty]
    public partial string? ExportLog { get; private set; }

    [ObservableProperty]
    public partial string? CommandPreview { get; private set; }

    [ObservableProperty]
    public partial bool ShowCommand { get; set; }

    public bool IsIdle => !IsExporting;

    private bool CanPauseExport => IsExporting && !IsExportPaused;

    public string OutputDisplay => OutputPath ?? (DefaultOutputPath() is { } p ? $"{p}  (automatic)" : "Choose where to save…");

    public string OutputExtensionHint => SelectedPreset is { Extension.Length: > 0 } p ? $".{p.Extension}"
        : IsAudioMode ? "by audio codec" : "same as source";

    public ExportViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        _planTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, OnPlanTimer);
        ReloadPresets();
        IsAudioMode = _app.Settings.Current.ExtractAudioMode;
        _app.Presets.Changed += OnPresetsChanged;
        owner.StateChanged += (_, _) => InvalidatePlanPreview();
        owner.Catalog.Changed += _ => InvalidatePlanPreview();
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.FilePath))
                OnPropertyChanged(nameof(OutputDisplay));
        };
    }

    private void OnPlanTimer(object? sender, EventArgs e)
    {
        _planTimer.Stop();
        _ = UpdatePlanPreviewAsync();
    }

    private void OnPresetsChanged(object? sender, EventArgs e) => ReloadPresets();

    /// <summary>Refills the menu with the current mode's presets, keeping each mode's choice where it still exists.</summary>
    private void ReloadPresets()
    {
        foreach (var kind in Enum.GetValues<PresetKind>())
            Choice(kind).Preset = Current(Choice(kind).Preset, kind);
        _refilling = true;
        try
        {
            Presets.Clear();
            foreach (var preset in _app.Presets.Presets.Where(p => p.Kind == Kind))
                Presets.Add(preset);
        }
        finally
        {
            _refilling = false;
        }
        SelectFromCode(Mode.Preset);
    }

    private void SelectFromCode(Preset? preset)
    {
        _selectingFromCode = true;
        try
        {
            SelectedPreset = preset;
        }
        finally
        {
            _selectingFromCode = false;
        }
    }

    /// <summary>The store's version of <paramref name="chosen"/>, or the default preset of that kind.</summary>
    private Preset? Current(Preset? chosen, PresetKind kind) =>
        _app.Presets.Presets.FirstOrDefault(p => p.Id == chosen?.Id && p.Kind == kind) ?? DefaultPreset(kind);

    /// <summary>
    /// The preset last chosen for this kind (kept between sessions), else "for storage" for video and "extract audio
    /// (as is)" for saving audio.
    /// </summary>
    private Preset? DefaultPreset(PresetKind kind)
    {
        var presets = _app.Presets.Presets;
        if (LastPresetId(kind) is { } last && presets.FirstOrDefault(p => p.Id == last && p.Kind == kind) is { } remembered)
            return remembered;
        var id = kind == PresetKind.Audio ? Preset.ExtractAudio.Id : Preset.ForStorage.Id;
        return presets.FirstOrDefault(p => p.Id == id) ?? presets.FirstOrDefault(p => p.Kind == kind);
    }

    private string? LastPresetId(PresetKind kind) =>
        kind == PresetKind.Audio ? _app.Settings.Current.LastAudioPresetId : _app.Settings.Current.LastVideoPresetId;

    private void RememberPreset(PresetKind kind, Preset preset)
    {
        if (LastPresetId(kind) == preset.Id)
            return;
        _app.Settings.Update(s => kind == PresetKind.Audio ? s with { LastAudioPresetId = preset.Id } : s with { LastVideoPresetId = preset.Id });
    }

    partial void OnIsAudioModeChanged(bool value)
    {
        if (_app.Settings.Current.ExtractAudioMode != value)
            _app.Settings.Update(s => s with { ExtractAudioMode = value });
        OutputPath = Mode.OutputPath;
        ReloadPresets();
        ExportStatus = null;
        CanReveal = false;
        ExportLog = null;
        InvalidatePlanPreview();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void UseVideoMode() => IsAudioMode = false;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void UseAudioMode() => IsAudioMode = true;

    partial void OnSelectedPresetChanged(Preset? value)
    {
        // Refilling the menu clears its selection for a moment; that isn't the user's choice.
        if (_refilling)
            return;
        Mode.Preset = value;
        if (value is not null && !_selectingFromCode)
            RememberPreset(Kind, value);
        if (!IsAudioMode && value is not null && PresetAnalysis.Of(value).Arguments is { } args)
            _owner.SnapToKeyframes = args.VideoCopy;
        OnPropertyChanged(nameof(OutputDisplay));
        _owner.RefreshDirty();
        InvalidatePlanPreview();
    }

    partial void OnOutputPathChanged(string? value)
    {
        Mode.OutputPath = value;
        _owner.RefreshDirty();
        InvalidatePlanPreview();
    }

    partial void OnShowCommandChanged(bool value)
    {
        if (value)
            InvalidatePlanPreview();
    }

    internal ExportSettings CurrentExportSettings() =>
        new() { PresetId = Choice(PresetKind.Video).Preset?.Id, PresetSnapshot = Choice(PresetKind.Video).Preset, OutputPath = Choice(PresetKind.Video).OutputPath };

    /// <summary>
    /// The project's preset, or one that runs exactly like it. One this machine doesn't have comes from the project file and its arguments go
    /// straight to ffmpeg, so it's only added when the user agrees, and never in place of an existing preset.
    /// </summary>
    internal async Task<Preset?> ResolvePresetAsync(ExportSettings settings)
    {
        if (_app.Presets.FindFor(settings.PresetId, settings.PresetSnapshot) is { Kind: PresetKind.Video } known)
            return known;
        if (settings.PresetSnapshot is not { Kind: PresetKind.Video } snapshot || snapshot.Id != settings.PresetId || View is null)
            return null;
        var answer = await View.AskAsync($"Add the preset “{snapshot.Name}”?",
            $"This project was exported with a preset you don't have:\n\n{snapshot.Arguments}\n\n" +
            "Only add presets from projects you trust: ffmpeg runs these options as they are.",
            "Add preset", "Use my default");
        if (answer != 0)
            return null;
        _app.Presets.Save(snapshot.WithDefaults());
        return _app.Presets.Find(snapshot.Id);
    }

    internal void ApplyExportSettings(ExportSettings settings, Preset? preset)
    {
        var output = settings.OutputPath is { } path && Path.IsPathFullyQualified(path) ? path : null;
        if (IsAudioMode)
        {
            Choice(PresetKind.Video).Preset = preset is { Kind: PresetKind.Video } ? preset : DefaultPreset(PresetKind.Video);
            Choice(PresetKind.Video).OutputPath = output;
            _owner.RefreshDirty();
            return;
        }
        SelectFromCode(preset is { Kind: PresetKind.Video } ? preset : DefaultPreset(PresetKind.Video));
        OutputPath = output;
    }

    private string? DefaultOutputPath()
    {
        if (State.Sources.FirstOrDefault() is not { } first)
            return null;
        var dir = Path.GetDirectoryName(first.Path)!;
        var baseName = _owner.FilePath is not null ? _owner.ProjectName
            : Path.GetFileNameWithoutExtension(first.Path) + (IsAudioMode ? "-audio" : "-edit");
        // Never one of the project's own files, even one that's missing right now.
        return OutputNaming.FreePath(dir, baseName, OutputNaming.Extension(SelectedPreset, Kind, State, Catalog.Media, first.Path),
            c => State.Sources.Any(s => s.Path == c) || State.AudioFiles.Any(s => s.Path == c));
    }

    internal void InvalidatePlanPreview()
    {
        OnPropertyChanged(nameof(OutputDisplay));
        if (!ShowCommand)
            return;
        _planTimer.Stop();
        _planTimer.Start();
    }

    private async Task UpdatePlanPreviewAsync()
    {
        if (_app.Tools is not { } tools)
        {
            CommandPreview = _app.ToolsError;
            return;
        }
        try
        {
            var plan = await PlanAsync(OutputPath ?? DefaultOutputPath() ?? "out.mp4", Path.Combine(AppPaths.TempDirectory, "preview"),
                status => CommandPreview = status);
            var notes = plan.Notes.Count > 0 ? string.Join("\n", plan.Notes.Select(n => "• " + n)) + "\n\n" : "";
            CommandPreview = notes + plan.CommandText(tools.Ffmpeg);
        }
        catch (Exception ex)
        {
            CommandPreview = ex.Message;
        }
    }

    /// <param name="status">Shows what planning is waiting for (the export's progress row, or the command panel).</param>
    private async Task<ExportPlan> PlanAsync(string output, string workDir, Action<string> status)
    {
        if (SelectedPreset is not { } preset)
            throw new ExportPlanException("Choose a preset.");
        var keyframes = new Dictionary<string, KeyframeIndex>();
        if (PresetAnalysis.Of(preset).VideoCopy)
        {
            foreach (var source in State.Sources.Where(s => State.Timeline.Clips.Any(c => c.SourceId == s.Id)))
            {
                if (Catalog.Info(source.Id) is null)
                    continue;
                if (Catalog.Keyframes(source) is not { } index)
                {
                    status($"Finding keyframes in {source.DisplayName}…");
                    index = await Catalog.KeyframesAsync(source);
                }
                keyframes[source.Id] = index;
            }
        }
        return ExportPlanner.Plan(new ExportJob
        {
            State = State,
            Media = Catalog.Media,
            Preset = preset,
            OutputPath = output,
            WorkDirectory = workDir,
            Keyframes = keyframes,
        });
    }

    [RelayCommand]
    private async Task ChooseOutput()
    {
        if (View is null)
            return;
        var path = await View.PickOutputPathAsync(OutputPath ?? DefaultOutputPath() ?? "export.mp4");
        if (path is null)
            return;
        // The save dialog has already asked about replacing an existing file.
        _confirmedOutputPath = path;
        OutputPath = path;
    }

    [RelayCommand]
    private void UseAutomaticOutput() => OutputPath = null;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Export()
    {
        if (_app.Tools is not { } tools)
        {
            ShowExportResult(_app.ToolsError ?? "ffmpeg not found", failed: true);
            return;
        }
        var output = OutputPath ?? DefaultOutputPath();
        if (output is null)
        {
            ShowExportResult("Add videos to the timeline first.", failed: true);
            return;
        }
        var audio = IsAudioMode;
        var what = audio ? "Extracting audio" : "Export";
        // An output path restored from a project was never confirmed in the save dialog.
        if (output != _confirmedOutputPath && File.Exists(output) && View is not null)
        {
            var answer = await View.AskAsync($"Replace “{Path.GetFileName(output)}”?",
                $"A file with this name already exists in {Path.GetDirectoryName(output)}. Exporting will replace it.",
                "Replace", "Cancel");
            if (answer != 0)
                return;
            _confirmedOutputPath = output;
        }

        IsExporting = true;
        ExportFailed = false;
        CanReveal = false;
        ExportLog = null;
        ExportStatus = null;
        ExportProgress = 0;
        _progress = null;
        ShowPreparing("Preparing…");
        // Started before planning, so a pause while finding keyframes holds the export too.
        var session = _session = new ExportSession($"{(audio ? "Extracting audio to" : "Exporting")} {Path.GetFileName(output)}", "Export");
        try
        {
            var plan = await PlanAsync(output, ExportSession.NewWorkDirectory("export"), ShowPreparing);
            var result = await session.RunAsync(tools, plan, new Progress<ExportProgress>(OnExportProgress));
            _lastOutputPath = output;
            switch (result.Outcome)
            {
                case ExportOutcome.Succeeded:
                    ShowExportResult($"✓ {result.Message} in {ExportText.Duration(result.Elapsed)}", failed: false);
                    CanReveal = true;
                    await _app.NotifyAsync(audio ? "Audio extracted" : "Export finished", $"{Path.GetFileName(output)} is ready ({ExportText.Duration(result.Elapsed)}).");
                    break;
                case ExportOutcome.Cancelled:
                    ShowExportResult($"{what} cancelled.", failed: false);
                    break;
                default:
                    ShowExportResult($"{what} failed: {result.Message}", failed: true);
                    ExportLog = result.Log;
                    await _app.NotifyAsync($"{what} failed", $"{Path.GetFileName(output)}: {result.Message}");
                    break;
            }
        }
        catch (ExportPlanException ex)
        {
            ShowExportResult(ex.Message, failed: true);
        }
        catch (Exception ex)
        {
            ShowExportResult($"{what} failed: {ex.Message}", failed: true);
            ExportLog = ex.ToString();
        }
        finally
        {
            IsExporting = false;
            IsExportPaused = false;
            session.Dispose();
            _session = null;
        }
    }

    private void ShowPreparing(string status)
    {
        _preparing = status;
        ShowProgressText();
    }

    private void OnExportProgress(ExportProgress p)
    {
        ExportProgress = p.Fraction * 100;
        _progress = p;
        ShowProgressText();
    }

    private void ShowProgressText() => ExportProgressText = ExportText.Progress(_progress, IsExportPaused, _preparing);

    private void ShowExportResult(string message, bool failed)
    {
        ExportStatus = message;
        ExportFailed = failed;
        ExportProgressText = null;
    }

    [RelayCommand(CanExecute = nameof(CanPauseExport))]
    private void PauseExport()
    {
        _session?.Pause();
        IsExportPaused = true;
        ShowProgressText();
    }

    [RelayCommand(CanExecute = nameof(IsExportPaused))]
    private void ResumeExport()
    {
        IsExportPaused = false;
        _session?.Resume();
        ShowProgressText();
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport() => _session?.Cancel();

    [RelayCommand]
    private void RevealOutput()
    {
        if (_lastOutputPath is not null && File.Exists(_lastOutputPath))
            View?.RevealInFileManager(_lastOutputPath);
    }

    public void Dispose()
    {
        _app.Presets.Changed -= OnPresetsChanged;
        _planTimer.Stop();
        _session?.Cancel();
    }
}
