using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// The editor's preview: plays the timeline (an EDL from <see cref="TimelineEdl"/>) or a single source file,
/// and keeps mpv's protocol to itself: which tracks to play (the EDL's titled tracks, or a source's chosen
/// audio stream), keeping the position across reloads, audio-only compact mode, coalescing rapid seeks,
/// ignoring position reports that still echo the old position while a seek is under way, and live
/// audio-delay feedback. Events arrive on the <see cref="SynchronizationContext"/> it was created on.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    private static readonly TimeSpan SeekInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan SeekEcho = TimeSpan.FromMilliseconds(250);

    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly object _gate = new();
    private readonly Timer _seekTimer;
    private TimeSpan? _pendingSeek;
    private bool _seeking;
    private DateTime _lastSeekAt;
    private string? _loaded;
    private bool _timeline;
    private int? _sourceAudioStream;
    private bool _compact;

    private PreviewPlayer(MpvPlayer mpv)
    {
        Mpv = mpv;
        _seekTimer = new Timer(_ => FlushSeek());
        mpv.PropertyChanged += (name, value) => Post(() => OnProperty(name, value));
        mpv.FileLoaded += () => Post(OnFileLoaded);
        // mpv's warnings and errors (unplayable segments, failed commands) explain a blank or silent preview.
        mpv.LogMessage += (level, text) => AppLog.Write($"mpv {level}: {text}");
    }

    /// <param name="headless">No video or audio output (tests).</param>
    public static PreviewPlayer Create(bool hardwareDecoding, bool headless = false) =>
        new(MpvPlayer.Create(hardwareDecoding, headless));

    /// <summary>
    /// The mpv instance, for the video view's renderer. The view must drop its renderer before
    /// <see cref="Dispose"/> (the owner unbinds the player first).
    /// </summary>
    public MpvPlayer Mpv { get; }

    /// <summary>The playback position, except reports that echo an old position during a seek.</summary>
    public event Action<TimeSpan>? PositionChanged;

    public event Action<TimeSpan>? DurationChanged;

    public event Action<bool>? PausedChanged;

    public event Action? EndReached;

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
        set => Mpv.SetFlag("pause", value);
    }

    /// <summary>Plays the timeline EDL starting at <paramref name="position"/>; nothing happens when it's already loaded.</summary>
    public void LoadTimeline(string url, TimeSpan position)
    {
        if (IsTimelineLoaded && _loaded == url)
            return;
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
        lock (_gate)
            _pendingSeek = null;
        if (_loaded is null)
            return;
        _loaded = null;
        Mpv.Command("stop");
    }

    private void Load(string target, TimeSpan start)
    {
        lock (_gate)
            _pendingSeek = null;
        _loaded = target;
        Mpv.SetString("start", TimeFormat.Seconds(start));
        Mpv.Command("loadfile", target, "replace");
    }

    /// <summary>Seeks exactly; seeks arriving faster than mpv can follow collapse into the latest one.</summary>
    public void Seek(TimeSpan time)
    {
        lock (_gate)
        {
            if (_loaded is null)
                return;
            _pendingSeek = time;
            if (_seeking)
                return;
            _seeking = true;
        }
        FlushSeek();
    }

    private void FlushSeek()
    {
        TimeSpan time;
        lock (_gate)
        {
            if (_pendingSeek is not { } pending || _loaded is null)
            {
                _pendingSeek = null;
                _seeking = false;
                return;
            }
            time = pending;
            _pendingSeek = null;
            _lastSeekAt = DateTime.UtcNow;
            _seekTimer.Change(SeekInterval, Timeout.InfiniteTimeSpan);
        }
        Mpv.CommandAsync("seek", TimeFormat.Seconds(time), "absolute+exact");
    }

    public void StepFrame(bool back)
    {
        lock (_gate)
            _lastSeekAt = DateTime.MinValue;
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
                    echo = _pendingSeek is not null || DateTime.UtcNow - _lastSeekAt <= SeekEcho;
                if (!echo)
                    PositionChanged?.Invoke(TimeFormat.FromSeconds(seconds));
                break;
            case "duration" when value is double seconds:
                DurationChanged?.Invoke(TimeFormat.FromSeconds(seconds));
                break;
            case "pause" when value is bool paused:
                PausedChanged?.Invoke(paused);
                break;
            case "eof-reached" when value is true:
                EndReached?.Invoke();
                break;
        }
    }

    private void OnFileLoaded()
    {
        Mpv.SetDouble("audio-delay", 0);
        Mpv.SetString("start", "none");
        ApplyTracks();
    }

    /// <summary>The timeline's titled tracks, or a source's video and chosen audio stream; no video when compact.</summary>
    private void ApplyTracks()
    {
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
        Mpv.SetString("aid", aid ?? "no");
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
        _seekTimer.Dispose();
        Mpv.Dispose();
    }
}
