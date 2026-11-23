using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>
/// The clipping scan: the whole timeline's audio, mixed exactly as it would be exported (clip, A1/A2 and A2 clip
/// volumes), checked for samples at full scale. The clipped stretches are marked red on the timeline; once the project
/// changes they stay as faded marks until the next scan (undoing back to the scanned state makes them current again).
/// While marks are shown the button clears them instead of scanning.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _clipScan;
    private ProjectState? _clipScanState;

    /// <summary>The last scan's clipped stretches, in timeline time; empty before any scan.</summary>
    public IReadOnlyList<ClipRange> ClipMarks
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged(nameof(HasClipMarks));
            OnPropertyChanged(nameof(ClipScanText));
            OnPropertyChanged(nameof(ClipScanEnabled));
            OnPropertyChanged(nameof(ClipScanTip));
        }
    } = [];

    /// <summary>The timeline shows marks from a scan, so the button clears them.</summary>
    public bool HasClipMarks => ClipMarks.Count > 0;

    /// <summary>Scanning needs clips; clearing stays possible even after every clip is deleted.</summary>
    public bool ClipScanEnabled => HasClips || HasClipMarks || IsScanningClipping;

    /// <summary>The marks describe the project as it is now (nothing changed since the scan).</summary>
    public bool ClipMarksCurrent => ReferenceEquals(_clipScanState, State) || Equals(_clipScanState, State);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipScanText))]
    [NotifyPropertyChangedFor(nameof(ClipScanEnabled))]
    public partial bool IsScanningClipping { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipScanText))]
    public partial double ClipScanProgress { get; set; }

    public string ClipScanText => IsScanningClipping
        ? string.Create(CultureInfo.InvariantCulture, $"Scanning {ClipScanProgress * 100:0}%… ✕")
        : HasClipMarks ? "Clear clipping" : "Check clipping";

    public string ClipScanTip => HasClipMarks
        ? "Remove the clipping marks from the timeline."
        : "Check the whole timeline's audio for clipping at the current clip and track volumes (as it would be exported). " +
          "Clipped places are marked red on the timeline; click again while it runs to cancel.";

    /// <summary>Starts a scan, cancels the one running, or clears the marks a finished one left.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ScanClipping()
    {
        if (_clipScan is { } running)
        {
            running.Cancel();
            return;
        }
        if (HasClipMarks)
        {
            ForgetClipMarks();
            Timeline.Invalidate();
            StatusMessage = "Clipping marks cleared.";
            return;
        }
        await _app.ToolsLocated;
        if (_app.Tools is not { } tools)
        {
            StatusMessage = _app.ToolsError;
            return;
        }
        var state = State;
        AudioScanPlan? plan;
        try
        {
            plan = ExportPlanner.AudioScan(state, Media);
        }
        catch (ExportPlanException ex)
        {
            StatusMessage = "Can't check clipping: " + ex.Message;
            return;
        }
        if (plan is null)
        {
            StatusMessage = "The timeline has no audio to check.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _clipScan = cts;
        ClipScanProgress = 0;
        IsScanningClipping = true;
        StatusMessage = "Checking the mix for clipping…";
        var started = DateTime.UtcNow;
        try
        {
            var report = await new ClippingScanner(tools).ScanAsync(plan, new Progress<double>(p => ClipScanProgress = p), cts.Token);
            ClipMarks = report.Ranges;
            _clipScanState = state;
            Timeline.Invalidate();
            StatusMessage = ClipScanMessage(report, DateTime.UtcNow - started);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Clipping check cancelled.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = "Clipping check failed: " + ex.Message.Split('\n')[0];
        }
        finally
        {
            _clipScan = null;
            IsScanningClipping = false;
        }
    }

    internal static string ClipScanMessage(ClippingReport report, TimeSpan took)
    {
        var peak = double.IsNegativeInfinity(report.PeakDb) ? "silent" : string.Create(CultureInfo.InvariantCulture, $"peak {report.PeakDb:+0.0;−0.0;0.0} dBFS");
        var time = string.Create(CultureInfo.InvariantCulture, $"{took.TotalSeconds:0.0} s");
        if (!report.Clips)
            return $"No clipping ({peak}, checked in {time}).";
        var places = report.Ranges.Count == 1 ? "1 place" : $"{report.Ranges.Count} places";
        return $"Clipping in {places}, marked red on the timeline ({peak}, checked in {time}).";
    }

    private void ForgetClipMarks()
    {
        _clipScan?.Cancel();
        ClipMarks = [];
        _clipScanState = null;
    }
}
