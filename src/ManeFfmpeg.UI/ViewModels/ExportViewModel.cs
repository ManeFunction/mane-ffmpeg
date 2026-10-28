using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Notifications;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Power;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>One project's export: preset, output file, the ffmpeg command preview and the running export.</summary>
public sealed partial class ExportViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly AppServices _app = AppServices.Instance;

    private readonly DispatcherTimer _planTimer;
    private CancellationTokenSource? _exportCts;
    private ExportRunner? _runner;
    private SleepInhibitor? _keepAwake;
    private string? _keepAwakeReason;
    private string? _lastOutputPath;

    /// <summary>The running export's latest progress, or what it's doing before ffmpeg starts.</summary>
    private ExportProgress? _progress;
    private string? _preparing;

    /// <summary>An existing output file the user agreed to replace (in the save dialog or when asked).</summary>
    private string? _confirmedOutputPath;

    private ProjectState State => _owner.State;
    private SourceCatalog Catalog => _owner.Catalog;
    private IMainWindowView? View => _owner.View;

    public ObservableCollection<Preset> Presets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputExtensionHint))]
    public partial Preset? SelectedPreset { get; set; }

    /// <summary>Explicit output path; null means "derive from the first video".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDisplay))]
    public partial string? OutputPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(CancelExportCommand), nameof(PauseExportCommand))]
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

    public string OutputExtensionHint => SelectedPreset is { Extension.Length: > 0 } p ? $".{p.Extension}" : "same as source";

    public ExportViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        _planTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, OnPlanTimer);
        ReloadPresets();
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

    private void ReloadPresets()
    {
        var selectedId = SelectedPreset?.Id;
        Presets.Clear();
        foreach (var preset in _app.Presets.Presets)
            Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == selectedId) ?? DefaultPreset();
    }

    /// <summary>"for storage" is the default for new projects.</summary>
    private Preset? DefaultPreset() => Presets.FirstOrDefault(p => p.Id == Preset.ForStorage.Id) ?? Presets.FirstOrDefault();

    partial void OnSelectedPresetChanged(Preset? value)
    {
        if (value is not null && PresetAnalysis.Of(value).Arguments is { } args)
            _owner.SnapToKeyframes = args.VideoCopy;
        OnPropertyChanged(nameof(OutputDisplay));
        _owner.RefreshDirty();
        InvalidatePlanPreview();
    }

    partial void OnOutputPathChanged(string? value)
    {
        _owner.RefreshDirty();
        InvalidatePlanPreview();
    }

    partial void OnShowCommandChanged(bool value)
    {
        if (value)
            InvalidatePlanPreview();
    }

    internal ExportSettings CurrentExportSettings() =>
        new() { PresetId = SelectedPreset?.Id, PresetSnapshot = SelectedPreset, OutputPath = OutputPath };

    /// <summary>
    /// The project's preset. One this machine doesn't have comes from the project file and its arguments go
    /// straight to ffmpeg, so it's only added when the user agrees, and never in place of an existing preset.
    /// </summary>
    internal async Task<Preset?> ResolvePresetAsync(ExportSettings settings)
    {
        if (_app.Presets.Find(settings.PresetId) is { } known)
            return known;
        if (settings.PresetSnapshot is not { } snapshot || snapshot.Id != settings.PresetId || View is null)
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
        SelectedPreset = preset ?? DefaultPreset();
        OutputPath = settings.OutputPath is { } path && Path.IsPathFullyQualified(path) ? path : null;
    }

    private string? DefaultOutputPath()
    {
        if (State.Sources.FirstOrDefault() is not { } first)
            return null;
        var dir = Path.GetDirectoryName(first.Path)!;
        var baseName = _owner.FilePath is not null ? _owner.ProjectName : Path.GetFileNameWithoutExtension(first.Path) + "-edit";
        var ext = SelectedPreset is { Extension.Length: > 0 } p ? "." + p.Extension : Path.GetExtension(first.Path);
        var candidate = Path.Combine(dir, baseName + ext);
        for (var n = 2; File.Exists(candidate) || State.Sources.Any(s => s.Path == candidate); n++)
            candidate = Path.Combine(dir, $"{baseName}-{n}{ext}");
        return candidate;
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
        _exportCts = new CancellationTokenSource();
        // Created before planning, so a pause while finding keyframes holds the export too.
        var runner = _runner = new ExportRunner(tools);
        var workDir = Path.Combine(AppPaths.TempDirectory, "export-" + Guid.NewGuid().ToString("N")[..8]);
        _keepAwakeReason = $"Exporting {Path.GetFileName(output)}";
        _keepAwake = SleepInhibitor.Acquire(_keepAwakeReason);
        try
        {
            var plan = await PlanAsync(output, workDir, ShowPreparing);
            var progress = new Progress<ExportProgress>(OnExportProgress);
            var token = _exportCts.Token;
            AppLog.Write($"Export {plan.Mode} → {output}\n{plan.CommandText(tools.Ffmpeg)}");
            var result = await Task.Run(() => runner.RunAsync(plan, progress, token));
            AppLog.Write($"Export {result.Outcome}: {result.Message} ({result.Elapsed}){(result.Log is null ? "" : "\n" + result.Log)}");
            _lastOutputPath = output;
            switch (result.Outcome)
            {
                case ExportOutcome.Succeeded:
                    ShowExportResult($"✓ {result.Message} in {FormatElapsed(result.Elapsed)}", failed: false);
                    CanReveal = true;
                    await NotifyAsync("Export finished", $"{Path.GetFileName(output)} is ready ({FormatElapsed(result.Elapsed)}).");
                    break;
                case ExportOutcome.Cancelled:
                    ShowExportResult("Export cancelled.", failed: false);
                    break;
                default:
                    ShowExportResult($"Export failed: {result.Message}", failed: true);
                    ExportLog = result.Log;
                    await NotifyAsync("Export failed", $"{Path.GetFileName(output)}: {result.Message}");
                    break;
            }
        }
        catch (ExportPlanException ex)
        {
            ShowExportResult(ex.Message, failed: true);
        }
        catch (Exception ex)
        {
            ShowExportResult($"Export failed: {ex.Message}", failed: true);
            ExportLog = ex.ToString();
        }
        finally
        {
            IsExporting = false;
            IsExportPaused = false;
            _runner = null;
            _keepAwake?.Dispose();
            _keepAwake = null;
            _exportCts?.Dispose();
            _exportCts = null;
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

    private void ShowProgressText()
    {
        var parts = new List<string>();
        if (IsExportPaused)
            parts.Add("Paused");
        if (_progress is { } p)
        {
            parts.Add($"{p.Fraction * 100:0}%");
            parts.Add(p.StepTitle);
            if (!IsExportPaused && p.Remaining is { } left && p.Fraction > 0.01)
                parts.Add($"{FormatElapsed(left)} left");
            if (!IsExportPaused && p.Speed is { } speed)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{speed:0.0#}×"));
        }
        else if (_preparing is not null)
            parts.Add(_preparing);
        ExportProgressText = string.Join(" · ", parts);
    }

    private void ShowExportResult(string message, bool failed)
    {
        ExportStatus = message;
        ExportFailed = failed;
        ExportProgressText = null;
    }

    private async Task NotifyAsync(string title, string body)
    {
        if (_app.Settings.Current.Notifications)
            await SystemNotifier.NotifyAsync(title, body);
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s" : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))}s";

    [RelayCommand(CanExecute = nameof(CanPauseExport))]
    private void PauseExport()
    {
        _runner?.Pause();
        IsExportPaused = true;
        // A paused export doesn't need the computer awake.
        _keepAwake?.Dispose();
        _keepAwake = null;
        ShowProgressText();
    }

    [RelayCommand(CanExecute = nameof(IsExportPaused))]
    private void ResumeExport()
    {
        if (_keepAwakeReason is not null)
            _keepAwake ??= SleepInhibitor.Acquire(_keepAwakeReason);
        IsExportPaused = false;
        _runner?.Resume();
        ShowProgressText();
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport() => _exportCts?.Cancel();

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
        _exportCts?.Cancel();
    }
}
