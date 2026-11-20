using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// The editor's preview: plays the timeline (an EDL from <see cref="TimelineEdl"/>) or a single source file,
/// and keeps mpv's protocol to itself: which tracks to play (the EDL's titled tracks, or a source's chosen
/// audio stream), keeping the position across reloads, audio-only compact mode, pacing rapid seeks so each one
/// finishes (and shows its frame) before the next (<see cref="SeekPacer"/>),
/// ignoring position reports that still echo the old position while a seek is under way or come from the
/// file being replaced, forgetting a file that failed so the next load tries again, live audio-delay
/// feedback, live level metering (<see cref="LevelMeter"/>), and the timeline's audio mix (<see cref="TimelineMix"/>: clip volumes and A2) as a lavfi-complex graph. Events arrive on the <see cref="SynchronizationContext"/> it was created on.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly object _gate = new();
    private readonly Timer _seekTimer;
    private readonly SeekPacer _seeks;
    private bool _loading;
    private string? _loaded;
    private long? _entry;
    private bool _timeline;
    private int? _sourceAudioStream;
    private bool _compact;
    private readonly MixGraph _mix;
    private bool _paused = true;
    // Play was pressed while the mix was being realigned: playback starts once that seek has landed (resumeAt), so the
    // first moment isn't heard twice. The timer starts it anyway if mpv never reports the seek finished.
    private TimeSpan? _resumeAt;
    private readonly Timer _resumeTimer;
    // Where the mix's last seek back should land, while CheckSeekBack watches it, how often it was resent, and where
    // the last restart landed.
    private TimeSpan? _seekBack;
    private int _seekBackRetries;
    private TimeSpan? _landed;
    private DateTime _seekBackUntil;
    private readonly Timer _seekBackTimer;

    private PreviewPlayer(MpvPlayer mpv)
    {
        Mpv = mpv;
        _seekTimer = new Timer(_ => _seeks!.Check());
        _seeks = new SeekPacer(
            time => mpv.CommandAsync("seek", TimeFormat.Seconds(time), "absolute+exact"),
            () => mpv.GetString("seeking") == "yes",
            armed => _seekTimer.Change(armed ? SeekPacer.CheckInterval : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        // Straight from mpv's event thread: the next seek shouldn't wait for a busy UI thread.
        mpv.PlaybackRestarted += _seeks.Restarted;
        // The position is read right away: once playback runs on, it no longer tells where the seek landed.
        mpv.PlaybackRestarted += () =>
        {
            var landed = CurrentPosition();
            Post(() =>
            {
                _landed = landed;
                CheckSeekBack();
                Resume(force: false);
            });
        };
        _seekBackTimer = new Timer(_ => Post(CheckSeekBack));
        _resumeTimer = new Timer(_ => Post(() => Resume(force: true)));
        _mix = new MixGraph(graph => mpv.SetString("lavfi-complex", graph), at =>
        {
            if (_loaded is null)
                return;
            (_seekBack, _seekBackRetries) = (at, 0);
            SendSeekBack(at);
        }, CurrentPosition);
        mpv.PropertyChanged += (name, value) => Post(() => OnProperty(name, value));
        mpv.FileLoaded += entry => Post(() => OnFileLoaded(entry));
        mpv.FileFailed += (entry, error) => Post(() => OnFileFailed(entry, error));
        // mpv's warnings and errors (unplayable segments, failed commands) explain a blank or silent preview.
        mpv.LogMessage += (level, text) => AppLog.Write($"mpv {level}: {text}");
        Levels = LevelMeter.Attach(mpv);
        MixesA2 = SupportsTrackMix(FfmpegVersion);
        if (!MixesA2)
            AppLog.Write($"libmpv's ffmpeg {FfmpegVersion} is older than 4.4: the preview plays A1 without A2.");
    }

    /// <param name="headless">No video or audio output (tests).</param>
    public static PreviewPlayer Create(bool hardwareDecoding, bool headless = false) =>
        new(MpvPlayer.Create(hardwareDecoding, headless));

    /// <summary>
    /// The mpv instance, for the video view's renderer. The view must drop its renderer before
    /// <see cref="Dispose"/> (the owner unbinds the player first).
    /// </summary>
    public MpvPlayer Mpv { get; }

    /// <summary>
    /// Whether the preview can mix A2 in: libmpv's own ffmpeg must have amix's normalize option (4.4+). Without it the
    /// preview plays A1 alone (the export is unaffected: it uses the system ffmpeg).
    /// </summary>
    public bool MixesA2 { get; }

    /// <summary>The ffmpeg version libmpv was built with, as mpv reports it ("7.1.1", "n4.3.2", a git build…).</summary>
    public string? FfmpegVersion => Mpv.GetString("ffmpeg-version");

    /// <summary>Whether an ffmpeg version has what the A2 mix needs; unrecognized versions (git builds) are assumed new.</summary>
    internal static bool SupportsTrackMix(string? version)
    {
        var match = System.Text.RegularExpressions.Regex.Match(version ?? "", @"^n?(\d+)\.(\d+)");
        if (!match.Success)
            return true;
        var (major, minor) = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        return major > 4 || (major == 4 && minor >= 4);
    }

    /// <summary>The live peak levels of what plays, measured while it plays.</summary>
    public LevelMeter Levels { get; }

    /// <summary>The playback position, except reports that echo an old position during a seek or a load.</summary>
    public event Action<TimeSpan>? PositionChanged;

    public event Action<TimeSpan>? DurationChanged;

    public event Action<bool>? PausedChanged;

    public event Action? EndReached;

    /// <summary>What was loaded can't be played (mpv's error); it counts as unloaded, so loading it again retries.</summary>
    public event Action<string>? LoadFailed;

    /// <summary>Whether <paramref name="url"/> is the timeline EDL that is loaded.</summary>
    public bool IsLoaded(string url) => IsTimelineLoaded && _loaded == url;

    /// <summary>True when the timeline is what's loaded (rather than a source or nothing).</summary>
    public bool IsTimelineLoaded => _timeline && _loaded is not null;

    /// <summary>Audio only: no video is decoded (compact mode).</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            ApplyTracks();
        }
    }

    public double Volume
    {
        set => Mpv.SetDouble("volume", value);
    }

    public bool Paused
    {
        set
        {
            _paused = value;
            _resumeAt = null;
            if (!value && _mix.OnPlay() is { } at)
            {
                // The rebuilt graph restarts playback: stay paused until the seek back has landed.
                _resumeAt = at;
                _resumeTimer.Change(ResumeTimeout, Timeout.InfiniteTimeSpan);
                return;
            }
            Mpv.SetFlag("pause", value);
        }
    }

    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How often a seek back that landed elsewhere is sent again.</summary>
    private const int SeekBackRetries = 3;

    private static readonly TimeSpan SeekBackCheckInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long after a seek back mpv may still move the position for the earlier seek it merged it with.</summary>
    private static readonly TimeSpan SeekBackWatch = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Sends the mix's seek back again when playback restarted somewhere else. Setting lavfi-complex starts mpv's own
    /// refresh seek at once; when the seek back arrives while that one is still under way, mpv can merge the two and land
    /// on the next keyframe instead (seen under load: 0.5 s instead of 0). Runs on every restart and, as mpv doesn't
    /// always report a restart once it stops seeking, on a timer for a while after the seek back (a check can come before
    /// the merged seek moves the position). Paused, the position is where it landed; playing, the last restart tells.
    /// </summary>
    private void CheckSeekBack()
    {
        if (_seekBack is not { } target)
            return;
        if (!_seeks.Idle || Mpv.GetString("seeking") != "no"
            || (Mpv.GetString("pause") == "yes" ? CurrentPosition() : _landed) is not { } at)
        {
            ArmSeekBackCheck();
            return;
        }
        if (Math.Abs((at - target).TotalSeconds) <= 0.1 || _seekBackRetries >= SeekBackRetries)
        {
            if (DateTime.UtcNow < _seekBackUntil && _seekBackRetries < SeekBackRetries)
                ArmSeekBackCheck();
            else
                _seekBack = null;
            return;
        }
        _seekBackRetries++;
        AppLog.Write($"Preview: the seek back to {TimeFormat.Seconds(target)} landed at {TimeFormat.Seconds(at)}; seeking again.");
        SendSeekBack(target);
    }

    private void SendSeekBack(TimeSpan target)
    {
        _seekBackUntil = DateTime.UtcNow + SeekBackWatch;
        _seeks.Seek(target);
        ArmSeekBackCheck();
    }

    private void ArmSeekBackCheck() => _seekBackTimer.Change(SeekBackCheckInterval, Timeout.InfiniteTimeSpan);

    /// <summary>Starts playback put off by <see cref="Paused"/> once the seek back has landed (or, forced, regardless).</summary>
    private void Resume(bool force)
    {
        if (_resumeAt is not { } at)
            return;
        if (!force && (!_seeks.Idle || Mpv.GetString("seeking") == "yes"
                       || Mpv.GetDouble("time-pos") is not { } seconds || Math.Abs(seconds - at.TotalSeconds) > 0.1))
            return;
        _resumeAt = null;
        _resumeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Mpv.SetFlag("pause", false);
    }

    /// <summary>
    /// Plays the timeline EDL starting at <paramref name="position"/>, mixed as <paramref name="mix"/> says (null plays A1
    /// as it is). When that EDL is already loaded only a changed mix is applied, in place.
    /// </summary>
    public void LoadTimeline(PreviewTimeline timeline, TimeSpan position) => LoadTimeline(timeline.Url, position, timeline.Mix);

    /// <summary>
    /// Applies a changed mix to the timeline that is loaded, in place and without reloading (a volume being dragged).
    /// Nothing happens while no timeline is loaded.
    /// </summary>
    public void ApplyMix(TimelineMix? mix)
    {
        if (!IsTimelineLoaded)
            return;
        _mix.Mix = mix;
        ApplyTracks();
    }

    /// <inheritdoc cref="LoadTimeline(PreviewTimeline, TimeSpan)"/>
    public void LoadTimeline(string url, TimeSpan position, TimelineMix? mix = null)
    {
        _mix.Mix = mix;
        if (IsTimelineLoaded && _loaded == url)
        {
            ApplyTracks();
            return;
        }
        _timeline = true;
        _sourceAudioStream = null;
        Load(url, position);
    }

    /// <summary>Plays one file from its start with the given audio stream (an absolute stream index).</summary>
    public void LoadSource(string path, int? audioStreamIndex)
    {
        _timeline = false;
        _sourceAudioStream = audioStreamIndex;
        Load(path, TimeSpan.Zero);
    }

    public void Unload()
    {
        _seeks.Reset();
        _seekBack = null;
        if (_loaded is null)
            return;
        _loaded = null;
        _entry = null;
        Mpv.Command("stop");
    }

    private void Load(string target, TimeSpan start)
    {
        // A seek under way is dropped with the file it was in: the next one goes at once.
        _seeks.Reset();
        _resumeAt = null;
        _seekBack = null;
        Levels.Reset();
        lock (_gate)
            _loading = true;
        _loaded = target;
        // mpv carries track choices over to the next file, where "no" for both would leave it nothing to play;
        // every file starts from mpv's own choice, and the tracks are picked once it has loaded.
        Mpv.SetString("vid", "auto");
        Mpv.SetString("aid", "auto");
        // ApplyTracks builds the new file's graph once it has loaded.
        _mix.OnLoad(start);
        Mpv.SetString("start", TimeFormat.Seconds(start));
        _entry = Mpv.LoadFile(target);
    }

    /// <summary>Seeks exactly, paced so each seek finishes and shows its frame before the next (<see cref="SeekPacer"/>).</summary>
    public void Seek(TimeSpan time)
    {
        if (_loaded is null)
            return;
        Levels.Reset();
        _seekBack = null;
        _mix.OnSeek(time, _paused);
        _seeks.Seek(time);
    }

    public void StepFrame(bool back)
    {
        _seekBack = null;
        _mix.OnFrameStep();
        _seeks.ForgetLastSeek();
        Mpv.CommandAsync(back ? "frame-back-step" : "frame-step");
    }

    /// <summary>Shifts the audio by <paramref name="delta"/> without reloading (while an offset is dragged).</summary>
    public void PreviewAudioDelay(TimeSpan delta) => Mpv.SetDouble("audio-delay", delta.TotalSeconds);

    private void OnProperty(string name, object? value)
    {
        switch (name)
        {
            case "time-pos" when value is double seconds:
                bool echo;
                lock (_gate)
                    echo = _loading;
                echo = echo || _seeks.MayEcho;
                if (!echo)
                    PositionChanged?.Invoke(TimeFormat.FromSeconds(seconds));
                break;
            case "duration" when value is double seconds:
                DurationChanged?.Invoke(TimeFormat.FromSeconds(seconds));
                break;
            case "pause" when value is bool paused:
                _paused = paused;
                Levels.Running = !paused;
                PausedChanged?.Invoke(paused);
                break;
            case "eof-reached" when value is true:
                EndReached?.Invoke();
                break;
        }
    }

    private void OnFileLoaded(long entry)
    {
        // A file replaced before it finished loading: the current one's events are still to come.
        if (entry != _entry)
            return;
        // Events come in mpv's order, so every report from the replaced file arrived before this one.
        lock (_gate)
            _loading = false;
        Mpv.SetDouble("audio-delay", 0);
        Mpv.SetString("start", "none");
        ApplyTracks();
    }

    private void OnFileFailed(long entry, string error)
    {
        if (entry != _entry)
            return;
        AppLog.Write($"Preview couldn't play the {(_timeline ? "timeline" : "source")}: {error}");
        _seeks.Reset();
        lock (_gate)
            _loading = false;
        _loaded = null;
        _entry = null;
        Paused = true;
        LoadFailed?.Invoke(error);
    }

    /// <summary>The timeline's titled tracks, or a source's video and chosen audio stream; no video when compact.</summary>
    private void ApplyTracks()
    {
        // Until the file has loaded there are no tracks to pick from, and picking none would carry over to it;
        // OnFileLoaded picks them.
        bool loading;
        lock (_gate)
            loading = _loading;
        if (loading || _loaded is null)
            return;
        var (videoTitle, audioTitle) = _timeline ? (TimelineEdl.VideoTitle, TimelineEdl.AudioTitle) : ((string?)null, (string?)null);
        var count = Mpv.GetInt("track-list/count") ?? 0;
        string? vid = null, aid = null;
        for (var i = 0; i < count; i++)
        {
            var type = Mpv.GetString($"track-list/{i}/type");
            var title = Mpv.GetString($"track-list/{i}/title");
            var id = Mpv.GetString($"track-list/{i}/id");
            if (type == "video" && vid is null && (videoTitle is null || title == videoTitle))
                vid = id;
            if (type == "audio" && (audioTitle is null || title == audioTitle)
                && (aid is null || (_sourceAudioStream is { } wanted && Mpv.GetInt($"track-list/{i}/ff-index") == wanted)))
                aid = id;
        }
        if (_compact)
            Mpv.SetString("vid", "no");
        else if (vid is not null)
            Mpv.SetString("vid", vid);
        if (!_mix.OnTracks(_timeline && _mix.Mix is not null && aid is not null ? (aid, MixesA2 ? TrackId(count) : null) : null))
            Mpv.SetString("aid", aid ?? "no");
    }

    private TimeSpan? CurrentPosition() => Mpv.GetDouble("time-pos") is { } seconds ? TimeFormat.FromSeconds(seconds) : null;

    /// <summary>The id of the EDL's A2 track, or null when it has none.</summary>
    private string? TrackId(long count)
    {
        for (var i = 0; i < count; i++)
            if (Mpv.GetString($"track-list/{i}/type") == "audio" && Mpv.GetString($"track-list/{i}/title") == TimelineEdl.TrackTitle)
                return Mpv.GetString($"track-list/{i}/id");
        return null;
    }

    private void Post(Action action)
    {
        if (_context is null)
            action();
        else
            _context.Post(_ => action(), null);
    }

    public void Dispose()
    {
        // Seeks also go from mpv's event thread and the timer's; neither may touch the timer once it's gone.
        _seeks.Dispose();
        _seekTimer.Dispose();
        _resumeTimer.Dispose();
        _seekBackTimer.Dispose();
        Levels.Dispose();
        Mpv.Dispose();
    }
}
