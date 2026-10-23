using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Notifications;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _exportCts;
    private DispatcherTimer _planTimer = null!;
    private string? _lastOutputPath;

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
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(CancelExportCommand))]
    public partial bool IsExporting { get; private set; }

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

    public string OutputDisplay => OutputPath ?? (DefaultOutputPath() is { } p ? $"{p}  (automatic)" : "Choose where to save…");

    public string OutputExtensionHint => SelectedPreset is { Extension.Length: > 0 } p ? $".{p.Extension}" : "same as source";

    private void InitExport()
    {
        _planTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _planTimer.Stop();
            _ = UpdatePlanPreviewAsync();
        });
        ReloadPresets();
        _app.Presets.Changed += OnPresetsChanged;
    }

    private void OnPresetsChanged(object? sender, EventArgs e) => ReloadPresets();

    private void ReloadPresets()
    {
        var selectedId = SelectedPreset?.Id;
        Presets.Clear();
        foreach (var preset in _app.Presets.Presets)
            Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(p => p.Id == selectedId) ?? Presets.FirstOrDefault();
    }

    partial void OnSelectedPresetChanged(Preset? value)
    {
        if (value is not null)
        {
            try
            {
                SnapToKeyframes = PresetArguments.Parse(value.Arguments).VideoCopy;
            }
            catch (FormatException)
            {
            }
        }
        OnPropertyChanged(nameof(OutputDisplay));
        RefreshDirty();
        InvalidatePlanPreview();
    }

    partial void OnOutputPathChanged(string? value)
    {
        RefreshDirty();
        InvalidatePlanPreview();
    }

    partial void OnShowCommandChanged(bool value)
    {
        if (value)
            InvalidatePlanPreview();
    }

    private ExportSettings CurrentExportSettings() =>
        new() { PresetId = SelectedPreset?.Id, PresetSnapshot = SelectedPreset, OutputPath = OutputPath };

    private void ApplyExportSettings(ExportSettings settings)
    {
        var preset = _app.Presets.Find(settings.PresetId);
        if (preset is null && settings.PresetSnapshot is { } snapshot)
        {
            // The project came with a preset this machine doesn't have: add it back.
            _app.Presets.Save(snapshot);
            preset = _app.Presets.Find(snapshot.Id);
        }
        SelectedPreset = preset ?? Presets.FirstOrDefault();
        OutputPath = settings.OutputPath;
    }

    private string? DefaultOutputPath()
    {
        if (State.Sources.FirstOrDefault() is not { } first)
            return null;
        var dir = Path.GetDirectoryName(first.Path)!;
        var baseName = FilePath is not null ? ProjectName : Path.GetFileNameWithoutExtension(first.Path) + "-edit";
        var ext = SelectedPreset is { Extension.Length: > 0 } p ? "." + p.Extension : Path.GetExtension(first.Path);
        var candidate = Path.Combine(dir, baseName + ext);
        for (var n = 2; File.Exists(candidate) || State.Sources.Any(s => s.Path == candidate); n++)
            candidate = Path.Combine(dir, $"{baseName}-{n}{ext}");
        return candidate;
    }

    private void InvalidatePlanPreview()
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
            var plan = await PlanAsync(OutputPath ?? DefaultOutputPath() ?? "out.mp4", Path.Combine(AppPaths.TempDirectory, "preview"));
            var notes = plan.Notes.Count > 0 ? string.Join("\n", plan.Notes.Select(n => "• " + n)) + "\n\n" : "";
            CommandPreview = notes + plan.CommandText(tools.Ffmpeg);
        }
        catch (ExportPlanException ex)
        {
            CommandPreview = ex.Message;
        }
        catch (Exception ex)
        {
            CommandPreview = ex.Message;
        }
    }

    private async Task<ExportPlan> PlanAsync(string output, string workDir)
    {
        if (SelectedPreset is not { } preset)
            throw new ExportPlanException("Choose a preset.");
        var keyframes = new Dictionary<string, KeyframeIndex>();
        if (PresetArguments.Parse(preset.Arguments).VideoCopy && _app.Keyframes is { } service)
        {
            foreach (var source in State.Sources.Where(s => State.Timeline.Clips.Any(c => c.SourceId == s.Id)))
            {
                if (!_media.TryGetValue(source.Id, out var info))
                    continue;
                if (service.TryGet(source.Path) is not { } index)
                {
                    ExportProgressText = $"Finding keyframes in {source.DisplayName}…";
                    index = await service.GetAsync(source.Path, info);
                    Timeline.Invalidate();
                }
                keyframes[source.Id] = index;
            }
        }
        return ExportPlanner.Plan(new ExportJob
        {
            State = State,
            Media = new Dictionary<string, MediaInfo>(_media),
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
        if (path is not null)
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

        IsExporting = true;
        ExportFailed = false;
        CanReveal = false;
        ExportLog = null;
        ExportStatus = null;
        ExportProgress = 0;
        ExportProgressText = "Preparing…";
        _exportCts = new CancellationTokenSource();
        var workDir = Path.Combine(AppPaths.TempDirectory, "export-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var plan = await PlanAsync(output, workDir);
            var progress = new Progress<ExportProgress>(OnExportProgress);
            var runner = new ExportRunner(tools);
            var token = _exportCts.Token;
            AppLog.Write($"Export {plan.Mode} → {output}\n{plan.CommandText(tools.Ffmpeg)}");
            var result = await Task.Run(() => runner.RunAsync(plan, output, workDir, progress, token));
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
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    private void OnExportProgress(ExportProgress p)
    {
        ExportProgress = p.Fraction * 100;
        var parts = new List<string> { $"{p.Fraction * 100:0}%", p.StepTitle };
        if (p.Remaining is { } left && p.Fraction > 0.01)
            parts.Add($"{FormatElapsed(left)} left");
        if (p.Speed is { } speed)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{speed:0.0#}×"));
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

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport() => _exportCts?.Cancel();

    [RelayCommand]
    private void RevealOutput()
    {
        if (_lastOutputPath is not null && File.Exists(_lastOutputPath))
            View?.RevealInFileManager(_lastOutputPath);
    }

    private void DisposeExport()
    {
        _app.Presets.Changed -= OnPresetsChanged;
        _planTimer.Stop();
        _exportCts?.Cancel();
    }
}
