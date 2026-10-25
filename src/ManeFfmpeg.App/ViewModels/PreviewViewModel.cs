using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.App.ViewModels;

public enum PreviewMode
{
    Timeline,
    Source,
}

/// <summary>One project's preview: the player, transport, source preview with marks, and compact mode.</summary>
public sealed partial class PreviewViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _owner;
    private readonly AppServices _app = AppServices.Instance;

    private readonly DispatcherTimer _reloadTimer;
    private PreviewPlayer? _preview;
    private string? _silencePath;
    private bool _scrubbing;
    private bool _resumeAfterScrub;
    private bool _settingPlayheadFromPlayer;

    private ProjectState State => _owner.State;
    private TimelineViewModel Timeline => _owner.Timeline;
    private SourceCatalog Catalog => _owner.Catalog;

    /// <summary>The mpv instance the video view draws; unbound before the preview is disposed.</summary>
    [ObservableProperty]
    public partial MpvPlayer? Player { get; private set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial string? PreviewMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceMode), nameof(Position), nameof(PositionText), nameof(PreviewTitle))]
    public partial PreviewMode Mode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTitle))]
    public partial SourceItemViewModel? PreviewedSource { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Position), nameof(PositionText))]
    public partial TimeSpan SourcePosition { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial TimeSpan SourceLength { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarksText))]
    public partial TimeSpan? MarkIn { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarksText))]
    public partial TimeSpan? MarkOut { get; private set; }

    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    /// <summary>Compact mode: the video preview is hidden and mpv plays audio only.</summary>
    [ObservableProperty]
    public partial bool IsCompact { get; set; }

    partial void OnIsCompactChanged(bool value)
    {
        if (_preview is not null)
            _preview.Compact = value;
    }

    [RelayCommand]
    private void ToggleCompact() => IsCompact = !IsCompact;

    public bool IsSourceMode => Mode == PreviewMode.Source;

    public string PreviewTitle => Mode == PreviewMode.Source && PreviewedSource is { } s ? $"Source: {s.Name}" : "Timeline";

    /// <summary>Where the preview is: in the previewed source, or the playhead on the timeline.</summary>
    public TimeSpan Position => Mode == PreviewMode.Source ? SourcePosition : Timeline.Playhead;

    /// <summary>How long the previewed source or the timeline is.</summary>
    public TimeSpan Length => Mode == PreviewMode.Source ? SourceLength : State.Timeline.Duration;

    public string PositionText => $"{TimeFormat.Display(Position)} / {TimeFormat.Display(Length)}";

    public string MarksText => $"In {(MarkIn is { } i ? TimeFormat.Display(i) : "—")} · Out {(MarkOut is { } o ? TimeFormat.Display(o) : "—")}";

    public PreviewViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        owner.StateChanged += (_, _) => ScheduleTimelineReload();
        owner.Catalog.Changed += _ => ScheduleTimelineReload();
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnReloadTimer);

        if (!_app.MpvAvailable)
        {
            PreviewMessage = $"Preview needs libmpv. {MpvLibrary.LoadError}\n{MpvLibrary.InstallHint}";
            return;
        }
        try
        {
            var preview = PreviewPlayer.Create(_app.Settings.Current.HardwareDecoding);
            preview.PositionChanged += OnPreviewPosition;
            preview.DurationChanged += d =>
            {
                if (Mode == PreviewMode.Source)
                    SourceLength = d;
            };
            preview.PausedChanged += paused => IsPlaying = !paused;
            preview.EndReached += () => IsPlaying = false;
            preview.Volume = Volume;
            _preview = preview;
            Player = preview.Mpv;
            PreviewMessage = "Add videos to start: File → Add Videos, or drop files here.";
        }
        catch (Exception ex)
        {
            PreviewMessage = $"Preview couldn't start: {ex.Message}";
        }
    }

    partial void OnVolumeChanged(double value)
    {
        if (_preview is not null)
            _preview.Volume = value;
    }

    private void OnPreviewPosition(TimeSpan position)
    {
        if (Mode == PreviewMode.Source)
        {
            SourcePosition = position;
            return;
        }
        if (_scrubbing)
            return;
        _settingPlayheadFromPlayer = true;
        Timeline.Playhead = Timeline.Clamp(position);
        _settingPlayheadFromPlayer = false;
        if (IsPlaying)
            Timeline.FollowPlayhead();
    }

    internal void OnPlayheadMoved()
    {
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(PositionText));
        if (!_settingPlayheadFromPlayer && !IsPlaying)
            _owner.OnSelectionChanged();
    }

    private void OnReloadTimer(object? sender, EventArgs e)
    {
        _reloadTimer.Stop();
        ReloadTimeline();
    }

    private void ScheduleTimelineReload()
    {
        OnPropertyChanged(nameof(PositionText));
        if (_preview is null || Mode != PreviewMode.Timeline)
            return;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void ReloadTimeline()
    {
        if (_preview is null || Mode != PreviewMode.Timeline)
            return;
        // A drag in progress is heard through audio-delay and seen on the timeline; the EDL is rebuilt on release.
        // Rebuilding mid-drag would reset audio-delay on load and then add the whole drag on top of the new EDL.
        if (_owner.History.InGesture)
            return;
        if (!_owner.HasClips)
        {
            _preview.Unload();
            PreviewMessage = "Add videos to start: File → Add Videos, or drop files here.";
            return;
        }
        if (_silencePath is null)
        {
            _ = PrepareSilenceAsync();
            return;
        }
        var url = TimelineEdl.Build(State, _owner.Media, _silencePath);
        if (url is null)
        {
            PreviewMessage = _owner.Sources.Any(s => s.IsProblem) ? "Some videos are missing; relink them in the media list." : "Analyzing videos…";
            return;
        }
        PreviewMessage = null;
        _preview.LoadTimeline(url, Timeline.Playhead);
    }

    private async Task PrepareSilenceAsync()
    {
        if (_app.Tools is not { } tools)
        {
            PreviewMessage = _app.ToolsError;
            return;
        }
        try
        {
            _silencePath = await SilenceAsset.EnsureAsync(tools);
            ScheduleTimelineReload();
        }
        catch (Exception ex)
        {
            PreviewMessage = $"Preview setup failed: {ex.Message}";
        }
    }

    /// <summary>Moves the playhead (from timeline clicks, keys or the transport) and seeks the player.</summary>
    public void SeekTo(TimeSpan time, bool scrubbing)
    {
        if (Mode == PreviewMode.Source)
            BackToTimeline();
        if (scrubbing && !_scrubbing)
        {
            _scrubbing = true;
            _resumeAfterScrub = IsPlaying;
            if (IsPlaying && _preview is not null)
                _preview.Paused = true;
        }
        Timeline.Playhead = time;
        _preview?.Seek(time);
    }

    public void EndScrub()
    {
        if (!_scrubbing)
            return;
        _scrubbing = false;
        if (_resumeAfterScrub && _preview is not null)
            _preview.Paused = false;
    }

    internal void PreviewAudioDelay(TimeSpan delta) => _preview?.PreviewAudioDelay(delta);

    // ---- Transport --------------------------------------------------------------------------------

    [RelayCommand]
    private void PlayPause()
    {
        if (_preview is null)
            return;
        if (!IsPlaying && Mode == PreviewMode.Timeline)
        {
            if (!_owner.HasClips)
                return;
            if (!_preview.IsTimelineLoaded)
                ReloadTimeline();
            if (Timeline.Playhead >= State.Timeline.Duration - TimeSpan.FromMilliseconds(60))
                SeekTo(TimeSpan.Zero, false);
        }
        _preview.Paused = IsPlaying;
    }

    [RelayCommand]
    private void StepBack() => StepFrame(back: true);

    [RelayCommand]
    private void StepForward() => StepFrame(back: false);

    private void StepFrame(bool back)
    {
        if (_preview is null)
            return;
        if (IsCompact)
        {
            // No video is decoded in compact mode, so step by one frame of what is being previewed.
            var video = Mode == PreviewMode.Source
                ? PreviewedSource?.Info?.Video
                : State.Timeline.Locate(Timeline.Playhead) is { } at ? Catalog.Info(State.Timeline.Clips[at.Index].SourceId)?.Video : null;
            var frame = TimeFormat.FrameDuration(video?.FrameRate ?? default);
            SeekPreview(Position + (back ? -frame : frame));
            return;
        }
        _preview.StepFrame(back);
    }

    /// <summary>Seeks whatever is previewed, within its length.</summary>
    private void SeekPreview(TimeSpan time)
    {
        time = time < TimeSpan.Zero ? TimeSpan.Zero : time > Length ? Length : time;
        if (Mode == PreviewMode.Source)
            _preview?.Seek(time);
        else
            SeekTo(time, false);
    }

    /// <summary>1 second back (⇧←).</summary>
    [RelayCommand]
    private void JumpBack() => SeekPreview(Position - TimeSpan.FromSeconds(1));

    /// <summary>1 second forward (⇧→).</summary>
    [RelayCommand]
    private void JumpForward() => SeekPreview(Position + TimeSpan.FromSeconds(1));

    /// <summary>5 seconds back (J).</summary>
    [RelayCommand]
    private void SkipBack() => SeekPreview(Position - TimeSpan.FromSeconds(5));

    /// <summary>5 seconds forward (L).</summary>
    [RelayCommand]
    private void SkipForward() => SeekPreview(Position + TimeSpan.FromSeconds(5));

    [RelayCommand]
    private void GoToStart() => SeekPreview(TimeSpan.Zero);

    [RelayCommand]
    private void GoToEnd() => SeekPreview(Length);

    [RelayCommand]
    private void PreviousCut() => JumpToCut(back: true);

    [RelayCommand]
    private void NextCut() => JumpToCut(back: false);

    /// <summary>Moves the playhead to the previous or next cut.</summary>
    private void JumpToCut(bool back)
    {
        var bounds = Enumerable.Range(0, State.Timeline.Clips.Count + 1).Select(State.Timeline.StartOf).ToList();
        var now = Timeline.Playhead;
        var target = back
            ? bounds.LastOrDefault(b => b < now - TimeSpan.FromMilliseconds(5), TimeSpan.Zero)
            : bounds.FirstOrDefault(b => b > now + TimeSpan.FromMilliseconds(5), State.Timeline.Duration);
        SeekTo(target, false);
    }

    // ---- Source preview ---------------------------------------------------------------------------

    [RelayCommand]
    private void PreviewSource(SourceItemViewModel? item)
    {
        if (item is null || _preview is null || item.Status != SourceStatus.Ready)
            return;
        IsCompact = false;
        Mode = PreviewMode.Source;
        PreviewedSource = item;
        MarkIn = MarkOut = null;
        SourcePosition = TimeSpan.Zero;
        SourceLength = item.Info?.Duration ?? TimeSpan.Zero;
        PreviewMessage = null;
        _preview.LoadSource(item.Path, Catalog.Audio(item.Source)?.Index);
    }

    [RelayCommand]
    private void BackToTimeline()
    {
        if (Mode == PreviewMode.Timeline)
            return;
        Mode = PreviewMode.Timeline;
        PreviewedSource = null;
        _preview?.Unload();
        ReloadTimeline();
    }

    [RelayCommand]
    private void SetMarkIn() => MarkIn = PreviewedSource is { } s && _owner.GridOf(s.Id) is { } grid ? grid.SnapInPoint(SourcePosition) : SourcePosition;

    [RelayCommand]
    private void SetMarkOut() => MarkOut = PreviewedSource is { } s && _owner.GridOf(s.Id) is { } grid ? grid.SnapOutPoint(SourcePosition) : SourcePosition;

    [RelayCommand]
    private void AddMarkedRange()
    {
        if (PreviewedSource is not { Info: { } info } item)
            return;
        var start = MarkIn ?? TimeSpan.Zero;
        var end = MarkOut ?? info.Duration;
        if (end <= start)
            (start, end) = (end, start);
        if (end - start < TimelineEditor.MinClipDuration)
        {
            _owner.StatusMessage = "Set In (I) and Out (O) marks further apart.";
            return;
        }
        _owner.CommitKeepingFit(State with { Timeline = TimelineEditor.Append(State.Timeline, Clip.Create(item.Id, start, end)) });
        _owner.StatusMessage = $"Added {TimeFormat.Display(end - start)} from {item.Name} to the end of the timeline.";
        MarkIn = MarkOut = null;
    }

    public void Dispose()
    {
        _reloadTimer.Stop();
        // Unbinding the player first makes the video view drop its renderer, which must go before the player.
        Player = null;
        _preview?.Dispose();
        _preview = null;
    }
}
