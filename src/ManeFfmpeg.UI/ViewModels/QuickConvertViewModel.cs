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
    private ExportSession? _session;
    private bool _cancelled;
    private IReadOnlyList<string> _outputs = [];
    private ConversionStatus? _status;

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
        ConversionReport report;
        try
        {
            report = await ConvertAllAsync(files);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Quick Action failed: {ex}");
            report = new ConversionReport([], [ex.Message]);
        }
        finally
        {
            _session?.Dispose();
            _session = null;
        }
        _outputs = report.Outputs;
        IsRunning = false;
        IsPaused = false;

        if (_cancelled)
        {
            RequestClose?.Invoke();
            return;
        }
        if (report.Failures.Count == 0)
        {
            await _app.NotifyAsync("Conversion finished",
                _outputs.Count == 1 ? $"{Path.GetFileName(_outputs[0])} is ready." : $"{_outputs.Count} files are ready.");
            RequestClose?.Invoke();
            return;
        }
        HasErrors = true;
        Status = string.Join("\n", report.Failures);
        ProgressText = _outputs.Count > 0 ? $"{_outputs.Count} of {files.Count} converted" : null;
        await _app.NotifyAsync("Conversion failed", report.Failures[0]);
    }

    /// <summary>
    /// Converts the files one after another; the report has why each one failed, or why none could start (no preset,
    /// no files, no ffmpeg).
    /// </summary>
    private async Task<ConversionReport> ConvertAllAsync(List<string> files)
    {
        if (_app.Presets.Find(request.PresetId) is not { } preset)
            return new ConversionReport([], ["This preset no longer exists. Remove the Quick Action in Mane FFmpeg's Export Presets window."]);
        Heading = $"Converting with “{preset.Name}”";
        if (files.Count == 0)
            return new ConversionReport([], ["None of the selected files could be found."]);
        await _app.LocateToolsAsync();
        if (_app.Tools is not { } tools)
            return new ConversionReport([], [_app.ToolsError ?? "ffmpeg wasn't found."]);
        if (_cancelled)
            return new ConversionReport([], []);
        var session = _session = new ExportSession($"Converting with {preset.Name}", "Quick Action");
        // Paused while ffmpeg was being looked for.
        if (IsPaused)
            session.Pause();
        return await FileConversion.ConvertAsync(files, preset, tools, _app.Keyframes ?? new KeyframeService(tools), session,
            new Progress<ConversionStatus>(Show));
    }

    private void Show(ConversionStatus s)
    {
        _status = s;
        FileText = (s.Count > 1 ? $"{s.Index + 1} of {s.Count}: " : "") + Path.GetFileName(s.File)
            + (s.Output is { } output ? $" → {Path.GetFileName(output)}" : "");
        Progress = 100.0 * s.Fraction;
        ShowProgressText();
    }

    private void ShowProgressText()
    {
        if (IsRunning)
            ProgressText = ExportText.Progress(_status?.Progress, IsPaused, _status?.Preparing, showStep: false);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Pause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
            _session?.Pause();
        else
            _session?.Resume();
        ShowProgressText();
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
        _cancelled = true;
        _session?.Cancel();
    }

    private bool CanReveal => !IsRunning && _outputs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanReveal))]
    private void Reveal() => Platform.Current.RevealInFileManager(_outputs[^1]);
}
