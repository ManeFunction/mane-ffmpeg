using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

public sealed partial class MainWindowViewModel
{
    private DispatcherTimer _reloadTimer = null!;
    private DispatcherTimer _seekTimer = null!;
    private string? _loadedUrl;
    private string? _silencePath;
    private double? _pendingSeek;
    private DateTime _lastSeekAt;
    private bool _scrubbing;
    private bool _resumeAfterScrub;
    private bool _settingPlayheadFromPlayer;

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

    partial void OnIsCompactChanged(bool value) => ApplyTrackSelection();

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

    private void InitPreview()
    {
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _reloadTimer.Stop();
            ReloadTimeline();
        });
        _seekTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => FlushSeek());

        if (!_app.MpvAvailable)
        {
            PreviewMessage = $"Preview needs libmpv. {MpvLibrary.LoadError}\n{MpvLibrary.InstallHint}";
            return;
        }
        try
        {
            var player = MpvPlayer.Create(_app.Settings.Current.HardwareDecoding);
            player.PropertyChanged += (name, value) => Dispatcher.UIThread.Post(() => OnPlayerProperty(name, value));
            player.FileLoaded += () => Dispatcher.UIThread.Post(OnFileLoaded);
            player.SetDouble("volume", Volume);
            Player = player;
            PreviewMessage = "Add videos to start: File → Add Videos, or drop files here.";
        }
        catch (Exception ex)
        {
            PreviewMessage = $"Preview couldn't start: {ex.Message}";
        }
    }

    partial void OnVolumeChanged(double value) => Player?.SetDouble("volume", value);

    private void OnPlayerProperty(string name, object? value)
    {
        switch (name)
        {
            case "time-pos" when value is double t:
                if (Mode == PreviewMode.Source)
                {
                    SourcePosition = TimeFormat.FromSeconds(t);
                }
                else if (!_scrubbing && _pendingSeek is null && (DateTime.UtcNow - _lastSeekAt).TotalMilliseconds > 250)
                {
                    _settingPlayheadFromPlayer = true;
                    Timeline.Playhead = Timeline.Clamp(TimeFormat.FromSeconds(t));
                    _settingPlayheadFromPlayer = false;
                    if (IsPlaying)
                        Timeline.FollowPlayhead();
                }
                break;
            case "pause" when value is bool paused:
                IsPlaying = !paused;
                break;
            case "duration" when value is double d && Mode == PreviewMode.Source:
                SourceLength = TimeFormat.FromSeconds(d);
                break;
            case "eof-reached" when value is true:
                IsPlaying = false;
                break;
        }
    }

    private void OnFileLoaded()
    {
        if (Player is null)
            return;
        Player.SetDouble("audio-delay", 0);
        Player.SetString("start", "none");
        ApplyTrackSelection();
    }

    /// <summary>
    /// Plays the timeline EDL's titled tracks, or a previewed source's video and chosen audio stream;
    /// no video at all in compact mode.
    /// </summary>
    private void ApplyTrackSelection()
    {
        if (Mode == PreviewMode.Timeline)
            SelectTracks(TimelineEdl.VideoTitle, TimelineEdl.AudioTitle, null);
        else
            SelectTracks(null, null, PreviewedSource?.Info?.Audio(PreviewedSource.AudioStreamIndex)?.Index);
    }

    /// <summary>Picks tracks by EDL title (timeline) or by stream index (source preview).</summary>
    private void SelectTracks(string? videoTitle, string? audioTitle, int? audioStreamIndex)
    {
        if (Player is null)
            return;
        var count = Player.GetInt("track-list/count") ?? 0;
        string? vid = null, aid = null;
        for (var i = 0; i < count; i++)
        {
            var type = Player.GetString($"track-list/{i}/type");
            var title = Player.GetString($"track-list/{i}/title");
            var id = Player.GetString($"track-list/{i}/id");
            var ffIndex = Player.GetInt($"track-list/{i}/ff-index");
            if (type == "video" && vid is null && (videoTitle is null || title == videoTitle))
                vid = id;
            if (type == "audio" && (audioTitle is null || title == audioTitle)
                && (aid is null || (audioStreamIndex is { } wanted && ffIndex == wanted)))
                aid = id;
        }
        if (IsCompact)
            Player.SetString("vid", "no");
        else if (vid is not null)
            Player.SetString("vid", vid);
        Player.SetString("aid", aid ?? "no");
    }

    internal void OnPlayheadMoved()
    {
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(PositionText));
        if (!_settingPlayheadFromPlayer && !IsPlaying)
            OnSelectionChanged();
    }

    private void ScheduleTimelineReload()
    {
        OnPropertyChanged(nameof(PositionText));
        if (Player is null || Mode != PreviewMode.Timeline)
            return;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private void ReloadTimeline()
    {
        if (Player is null || Mode != PreviewMode.Timeline)
            return;
        if (!HasClips)
        {
            if (_loadedUrl is not null)
                Player.Command("stop");
            _loadedUrl = null;
            PreviewMessage = "Add videos to start: File → Add Videos, or drop files here.";
            return;
        }
        if (_silencePath is null)
        {
            _ = PrepareSilenceAsync();
            return;
        }
        var url = TimelineEdl.Build(State, Media, _silencePath);
        if (url is null)
        {
            PreviewMessage = Sources.Any(s => s.IsProblem) ? "Some videos are missing; relink them in the media list." : "Analyzing videos…";
            return;
        }
        PreviewMessage = null;
        if (url == _loadedUrl)
            return;
        Player.SetString("start", TimeFormat.Seconds(Timeline.Playhead));
        Player.Command("loadfile", url, "replace");
        _loadedUrl = url;
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
            if (IsPlaying)
                Player?.SetFlag("pause", true);
        }
        Timeline.Playhead = time;
        RequestSeek(time);
    }

    public void EndScrub()
    {
        if (!_scrubbing)
            return;
        _scrubbing = false;
        FlushSeek();
        if (_resumeAfterScrub)
            Player?.SetFlag("pause", false);
    }

    private void RequestSeek(TimeSpan time)
    {
        _pendingSeek = time.TotalSeconds;
        if (!_seekTimer.IsEnabled)
        {
            FlushSeek();
            _seekTimer.Start();
        }
    }

    private void FlushSeek()
    {
        if (_pendingSeek is not { } seconds || Player is null || (_loadedUrl is null && Mode == PreviewMode.Timeline))
        {
            _pendingSeek = null;
            _seekTimer.Stop();
            return;
        }
        _pendingSeek = null;
        _lastSeekAt = DateTime.UtcNow;
        Player.CommandAsync("seek", seconds.ToString("0.######", CultureInfo.InvariantCulture), "absolute+exact");
    }

    internal void PreviewAudioDelay(TimeSpan delta) => Player?.SetDouble("audio-delay", delta.TotalSeconds);

    // ---- Transport --------------------------------------------------------------------------------

    [RelayCommand]
    private void PlayPause()
    {
        if (Player is null)
            return;
        if (!IsPlaying && Mode == PreviewMode.Timeline)
        {
            if (!HasClips)
                return;
            if (_loadedUrl is null)
                ReloadTimeline();
            if (Timeline.Playhead >= State.Timeline.Duration - TimeSpan.FromMilliseconds(60))
                SeekTo(TimeSpan.Zero, false);
        }
        Player.SetFlag("pause", IsPlaying);
    }

    [RelayCommand]
    private void StepFrame(string direction)
    {
        if (Player is null)
            return;
        if (IsCompact)
        {
            // No video is decoded in compact mode, so step by one frame of what is being previewed.
            var video = Mode == PreviewMode.Source
                ? PreviewedSource?.Info?.Video
                : State.Timeline.Locate(Timeline.Playhead) is { } at ? Media.GetValueOrDefault(State.Timeline.Clips[at.Index].SourceId)?.Video : null;
            var frame = TimeFormat.FrameDuration(video?.FrameRate ?? default);
            SeekPreview(Position + (direction == "-1" ? -frame : frame));
            return;
        }
        _lastSeekAt = DateTime.MinValue;
        Player.CommandAsync(direction == "-1" ? "frame-back-step" : "frame-step");
    }

    /// <summary>Seeks whatever is previewed, within its length.</summary>
    private void SeekPreview(TimeSpan time)
    {
        time = time < TimeSpan.Zero ? TimeSpan.Zero : time > Length ? Length : time;
        if (Mode == PreviewMode.Source)
            Player?.CommandAsync("seek", TimeFormat.Seconds(time), "absolute+exact");
        else
            SeekTo(time, false);
    }

    [RelayCommand]
    private void Jump(string seconds) => SeekPreview(Position + TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture)));

    [RelayCommand]
    private void GoToStart() => SeekPreview(TimeSpan.Zero);

    [RelayCommand]
    private void GoToEnd() => SeekPreview(Length);

    /// <summary>Moves the playhead to the previous/next cut.</summary>
    [RelayCommand]
    private void JumpToCut(string direction)
    {
        var bounds = Enumerable.Range(0, State.Timeline.Clips.Count + 1).Select(State.Timeline.StartOf).ToList();
        var now = Timeline.Playhead;
        var target = direction == "-1"
            ? bounds.LastOrDefault(b => b < now - TimeSpan.FromMilliseconds(5), TimeSpan.Zero)
            : bounds.FirstOrDefault(b => b > now + TimeSpan.FromMilliseconds(5), State.Timeline.Duration);
        SeekTo(target, false);
    }

    // ---- Source preview ---------------------------------------------------------------------------

    [RelayCommand]
    private void PreviewSource(SourceItemViewModel? item)
    {
        if (item is null || Player is null || item.Status != SourceStatus.Ready)
            return;
        IsCompact = false;
        Mode = PreviewMode.Source;
        PreviewedSource = item;
        MarkIn = MarkOut = null;
        SourcePosition = TimeSpan.Zero;
        SourceLength = item.Info?.Duration ?? TimeSpan.Zero;
        PreviewMessage = null;
        _loadedUrl = null;
        Player.SetString("start", "0");
        Player.Command("loadfile", item.Path, "replace");
    }

    [RelayCommand]
    private void BackToTimeline()
    {
        if (Mode == PreviewMode.Timeline)
            return;
        Mode = PreviewMode.Timeline;
        PreviewedSource = null;
        _loadedUrl = null;
        ReloadTimeline();
    }

    [RelayCommand]
    private void SetMarkIn() => MarkIn = PreviewedSource is { } s && GridOf(s.Id) is { } grid ? grid.SnapInPoint(SourcePosition) : SourcePosition;

    [RelayCommand]
    private void SetMarkOut() => MarkOut = PreviewedSource is { } s && GridOf(s.Id) is { } grid ? grid.SnapOutPoint(SourcePosition) : SourcePosition;

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
            StatusMessage = "Set In (I) and Out (O) marks further apart.";
            return;
        }
        CommitKeepingFit(State with { Timeline = TimelineEditor.Append(State.Timeline, Clip.Create(item.Id, start, end)) });
        StatusMessage = $"Added {TimeFormat.Display(end - start)} from {item.Name} to the end of the timeline.";
        MarkIn = MarkOut = null;
    }

    private void DisposePreview()
    {
        _reloadTimer.Stop();
        _seekTimer.Stop();
        var player = Player;
        Player = null;
        player?.Dispose();
    }
}
