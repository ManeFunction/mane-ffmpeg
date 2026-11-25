using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms;
using ManeFfmpeg.UI.Services;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>
/// The small window a Quick Action opens: converts the files one after another with the preset, next to themselves,
/// with one progress bar for all of them. It closes itself when everything worked; failures stay on screen.
/// </summary>
public sealed partial class QuickConvertViewModel(ConversionRequest request) : ViewModelBase
{
    private readonly AppServices _app = AppServices.Instance;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _outputs = [];
    private ExportRunner? _runner;

    /// <summary>Closes the window.</summary>
    public Action? RequestClose { get; set; }

    [ObservableProperty]
    public partial string Heading { get; private set; } = "Converting…";

    [ObservableProperty]
    public partial string? FileText { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool HasErrors { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(RevealCommand))]
    public partial bool IsRunning { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    public partial bool IsPaused { get; private set; }

    public string ButtonText => IsRunning ? "Cancel" : "Close";

    public string PauseText => IsPaused ? "Resume" : "Pause";

    public async Task RunAsync()
    {
        var files = request.Files.Select(Path.GetFullPath).Where(File.Exists).ToList();
        AppLog.Write($"Quick Action: preset {request.PresetId}, {files.Count} file(s)");
        var failures = new List<string>();
        try
        {
            if (_app.Presets.Find(request.PresetId) is not { } preset)
            {
                Fail("This preset no longer exists. Remove the Quick Action in Mane FFmpeg's Export Presets window.");
                return;
            }
            Heading = $"Converting with “{preset.Name}”";
            if (files.Count == 0)
            {
                Fail("None of the selected files could be found.");
                return;
            }
            await _app.LocateToolsAsync();
            if (_app.Tools is not { } tools)
            {
                Fail(_app.ToolsError ?? "ffmpeg wasn't found.");
                return;
            }
            var keyframes = _app.Keyframes ?? new KeyframeService(tools);
            using var awake = Platform.Current.KeepAwake($"Converting with {preset.Name}");
            for (var i = 0; i < files.Count && !_cts.IsCancellationRequested; i++)
            {
                if (await ConvertAsync(files[i], i, files.Count, preset, tools, keyframes) is { } error)
                    failures.Add($"{Path.GetFileName(files[i])}: {error}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Quick Action failed: {ex}");
            failures.Add(ex.Message);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
        }

        if (_cts.IsCancellationRequested)
        {
            RequestClose?.Invoke();
            return;
        }
        if (failures.Count == 0)
        {
            await NotifyAsync("Conversion finished",
                _outputs.Count == 1 ? $"{Path.GetFileName(_outputs[0])} is ready." : $"{_outputs.Count} files are ready.");
            RequestClose?.Invoke();
            return;
        }
        HasErrors = true;
        Status = string.Join("\n", failures);
        ProgressText = _outputs.Count > 0 ? $"{_outputs.Count} of {files.Count} converted" : null;
        await NotifyAsync("Conversion failed", failures[0]);
    }

    /// <summary>Converts one file; returns why it failed, or null.</summary>
    private async Task<string?> ConvertAsync(string file, int index, int count, Preset preset, FfmpegTools tools, KeyframeService keyframes)
    {
        FileText = count > 1 ? $"{index + 1} of {count}: {Path.GetFileName(file)}" : Path.GetFileName(file);
        ProgressText = "Preparing…";
        Progress = 100.0 * index / count;
        var workDir = Path.Combine(AppPaths.TempDirectory, "convert-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var info = await new Ffprobe(tools).ProbeAsync(file, _cts.Token);
            if (preset.Kind == PresetKind.Video && PresetAnalysis.Of(preset).VideoCopy)
                ProgressText = "Finding keyframes…";
            var plan = await FileConversion.PlanAsync(file, info, preset, keyframes, workDir);
            FileText = (count > 1 ? $"{index + 1} of {count}: " : "") + $"{Path.GetFileName(file)} → {Path.GetFileName(plan.OutputPath)}";
            AppLog.Write($"Quick Action {plan.Mode} → {plan.OutputPath}\n{plan.CommandText(tools.Ffmpeg)}");
            var runner = _runner = new ExportRunner(tools);
            if (IsPaused)
                runner.Pause();
            var progress = new Progress<ExportProgress>(p => ShowProgress(p, index, count));
            var result = await Task.Run(() => runner.RunAsync(plan, progress, _cts.Token));
            AppLog.Write($"Quick Action {result.Outcome}: {result.Message} ({result.Elapsed}){(result.Log is null ? "" : "\n" + result.Log)}");
            switch (result.Outcome)
            {
                case ExportOutcome.Succeeded:
                    _outputs.Add(plan.OutputPath);
                    return null;
                case ExportOutcome.Cancelled:
                    return null;
                default:
                    return result.Message;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ExportPlanException or FfprobeException)
        {
            return ex.Message;
        }
        finally
        {
            _runner = null;
        }
    }

    private void ShowProgress(ExportProgress p, int index, int count)
    {
        Progress = 100.0 * (index + p.Fraction) / count;
        var parts = new List<string> { $"{p.Fraction * 100:0}%" };
        if (IsPaused)
            parts.Insert(0, "Paused");
        else
        {
            if (p.Remaining is { } left && p.Fraction > 0.01)
                parts.Add($"{FormatTime(left)} left");
            if (p.Speed is { } speed)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{speed:0.0#}×"));
        }
        ProgressText = string.Join(" · ", parts);
    }

    private void Fail(string message)
    {
        HasErrors = true;
        Status = message;
        ProgressText = null;
    }

    private async Task NotifyAsync(string title, string body)
    {
        if (_app.Settings.Current.Notifications)
            await Platform.Current.NotifyAsync(title, body);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s" : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))}s";

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Pause()
    {
        if (IsPaused)
        {
            IsPaused = false;
            _runner?.Resume();
        }
        else
        {
            IsPaused = true;
            _runner?.Pause();
        }
        ProgressText = IsPaused ? "Paused" : ProgressText;
    }

    /// <summary>Cancel while running (the partly written file is removed), Close afterwards.</summary>
    [RelayCommand]
    private void CancelOrClose()
    {
        if (IsRunning)
        {
            Cancel();
            return;
        }
        RequestClose?.Invoke();
    }

    public void Cancel()
    {
        if (!IsRunning)
            return;
        _runner?.Resume();
        _cts.Cancel();
    }

    private bool CanReveal => !IsRunning && _outputs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanReveal))]
    private void Reveal() => Platform.Current.RevealInFileManager(_outputs[^1]);
}
