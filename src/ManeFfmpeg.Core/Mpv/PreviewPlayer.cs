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
    private TimelineMix? _mix;
    private string _graph = "";
    // The graph without its alignment point, and the tracks it mixes.
    private string _graphKey = "";
    private (string A1, string? A2)? _mixTracks;
    // The graph mixes A2 in, so after a seek its inputs must be aligned again (see TimelineMix.Graph); while paused that
    // waits for playback to start, so scrubbing doesn't rebuild the graph at every step.
    private bool _aligns;
    private bool _alignStale;
    private TimeSpan? _lastSeek;
    private bool _paused = true;
    // Play was pressed while the mix was being realigned: playback starts once that seek has landed (resumeAt), so the
    // first moment isn't heard twice. The timer starts it anyway if mpv never reports the seek finished.
    private TimeSpan? _resumeAt;
    private readonly Timer _resumeTimer;

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
        mpv.PlaybackRestarted += () => Post(OnPlaybackRestarted);
        _resumeTimer = new Timer(_ => Post(() => Resume(force: true)));
        mpv.PropertyChanged += (name, value) => Post(() => OnProperty(name, value));
        mpv.FileLoaded += entry => Post(() => OnFileLoaded(entry));
        mpv.FileFailed += (entry, error) => Post(() => OnFileFailed(entry, error));
        // mpv's warnings and errors (unplayable segments, failed commands) explain a blank or silent preview.
        mpv.LogMessage += (level, text) => AppLog.Write($"mpv {level}: {text}");
        Levels = LevelMeter.Attach(mpv);
    }

    /// <param name="headless">No video or audio output (tests).</param>
    public static PreviewPlayer Create(bool hardwareDecoding, bool headless = false) =>
        new(MpvPlayer.Create(hardwareDecoding, headless));

    /// <summary>
    /// The mpv instance, for the video view's renderer. The view must drop its renderer before
    /// <see cref="Dispose"/> (the owner unbinds the player first).
    /// </summary>
    public MpvPlayer Mpv { get; }

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
            if (!value && _aligns && _alignStale && (_lastSeek ?? CurrentPosition()) is { } at && BuildGraph(at))
            {
                // The rebuilt graph restarts playback: stay paused until the seek back has landed.
                SeekBack(at);
                _resumeAt = at;
                _resumeTimer.Change(ResumeTimeout, Timeout.InfiniteTimeSpan);
                return;
            }
            Mpv.SetFlag("pause", value);
        }
    }

    private static readonly TimeSpan ResumeTimeout = TimeSpan.FromSeconds(2);

    private void OnPlaybackRestarted() => Resume(force: false);

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
    public void LoadTimeline(string url, TimeSpan position, TimelineMix? mix = null)
    {
        _mix = mix;
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
        Levels.Reset();
        lock (_gate)
            _loading = true;
        _loaded = target;
        // mpv carries track choices over to the next file, where "no" for both would leave it nothing to play;
        // every file starts from mpv's own choice, and the tracks are picked once it has loaded.
        Mpv.SetString("vid", "auto");
        Mpv.SetString("aid", "auto");
        // The graph names the old file's tracks; ApplyTracks sets the new one once the file has loaded. No seek back: the
        // file is being replaced, and a seek sent to it would hold up the first one for the new file.
        if (_graph.Length > 0)
        {
            _graph = "";
            Mpv.SetString("lavfi-complex", "");
        }
        _graphKey = "";
        _mixTracks = null;
        _aligns = false;
        // The new file starts at "start": its graph is built for that position once it has loaded.
        _alignStale = true;
        _lastSeek = start;
        Mpv.SetString("start", TimeFormat.Seconds(start));
        _entry = Mpv.LoadFile(target);
    }

    /// <summary>Seeks exactly, paced so each seek finishes and shows its frame before the next (<see cref="SeekPacer"/>).</summary>
    public void Seek(TimeSpan time)
    {
        if (_loaded is null)
            return;
        _lastSeek = time;
        Levels.Reset();
        if (_aligns)
        {
            if (_paused)
                _alignStale = true;
            else
                BuildGraph(time);
        }
        _seeks.Seek(time);
    }

    public void StepFrame(bool back)
    {
        _lastSeek = null;
        if (_aligns)
            _alignStale = true;
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
        if (_timeline && _mix is { } mix && aid is not null)
        {
            _mixTracks = (aid, TrackId(count));
            // A changed mix, or one whose alignment is stale, is built at the position (a compact toggle or a reload of
            // the same mix leaves it alone).
            if (mix.Graph(aid, _mixTracks.Value.A2) != _graphKey || _alignStale || _graph.Length == 0)
                Realign(_alignStale ? _lastSeek ?? CurrentPosition() : CurrentPosition());
            return;
        }
        _mixTracks = null;
        _aligns = false;
        _graphKey = "";
        if (SetGraph(""))
            SeekBack(CurrentPosition());
        Mpv.SetString("aid", aid ?? "no");
    }

    private TimeSpan? CurrentPosition() => Mpv.GetDouble("time-pos") is { } seconds ? TimeFormat.FromSeconds(seconds) : null;

    /// <summary>Builds the mix aligned at <paramref name="at"/> and, if the graph changed, seeks back there.</summary>
    private void Realign(TimeSpan? at)
    {
        if (BuildGraph(at))
            SeekBack(at);
    }

    /// <summary>Sets the mix graph for the current tracks, aligned at <paramref name="at"/>; whether it changed.</summary>
    private bool BuildGraph(TimeSpan? at)
    {
        if (_mix is not { } mix || _mixTracks is not var (a1, a2))
            return false;
        _graphKey = mix.Graph(a1, a2);
        _aligns = mix.A2 is not null && a2 is not null;
        _alignStale = false;
        return SetGraph(_aligns ? mix.Graph(a1, a2, at ?? TimeSpan.Zero) : _graphKey);
    }

    /// <summary>
    /// mpv restarts playback after a graph change with a seek of its own that lands on the next keyframe (up to a GOP
    /// later); seeking back exactly keeps the position.
    /// </summary>
    private void SeekBack(TimeSpan? at)
    {
        if (_loaded is not null && at is { } time)
            _seeks.Seek(time);
    }

    /// <summary>The id of the EDL's A2 track, or null when it has none.</summary>
    private string? TrackId(long count)
    {
        for (var i = 0; i < count; i++)
            if (Mpv.GetString($"track-list/{i}/type") == "audio" && Mpv.GetString($"track-list/{i}/title") == TimelineEdl.TrackTitle)
                return Mpv.GetString($"track-list/{i}/id");
        return null;
    }

    /// <summary>
    /// Sets mpv's lavfi-complex graph, which then picks the audio tracks itself; "" goes back to the track in "aid".
    /// Only a change is sent: mpv rebuilds the audio chain (a short gap) every time it is set.
    /// </summary>
    private bool SetGraph(string graph)
    {
        if (graph == _graph)
            return false;
        _graph = graph;
        Mpv.SetString("lavfi-complex", graph);
        return true;
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
        Levels.Dispose();
        Mpv.Dispose();
    }
}
