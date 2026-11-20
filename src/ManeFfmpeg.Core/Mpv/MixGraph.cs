namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// The preview's lavfi-complex mix and when it has to be rebuilt: <see cref="PreviewPlayer"/> tells it what happens
/// (a load, a seek, a frame step, the tracks picked, play pressed) and it sets the graph and seeks back through the
/// callbacks. A mix with A2 is aligned at the point playback starts from (see <see cref="TimelineMix.Graph"/>), so
/// after a seek it is stale; while paused it stays stale until playback starts, so scrubbing doesn't rebuild the graph
/// at every step.
/// </summary>
/// <param name="setGraph">Sets mpv's lavfi-complex ("" goes back to the track in "aid").</param>
/// <param name="seekBack">
/// Seeks exactly: mpv restarts playback after a graph change with a seek of its own that lands on the next keyframe (up
/// to a GOP later), and seeking back keeps the position.
/// </param>
/// <param name="position">mpv's playback position, or null before it has one.</param>
internal sealed class MixGraph(Action<string> setGraph, Action<TimeSpan> seekBack, Func<TimeSpan?> position)
{
    private string _graph = "";
    // The graph without its alignment point, and the tracks it mixes.
    private string _key = "";
    private (string A1, string? A2)? _tracks;
    private bool _aligns;
    private bool _stale;
    private TimeSpan? _lastSeek;

    /// <summary>What to mix, or null to play A1 as it is.</summary>
    public TimelineMix? Mix { get; set; }

    /// <summary>The graph mpv has now ("" for none).</summary>
    public string Graph => _graph;

    /// <summary>A new file starts loading, at <paramref name="start"/>: the graph named the old file's tracks.</summary>
    public void OnLoad(TimeSpan start)
    {
        // No seek back: the file is being replaced, and a seek sent to it would hold up the first one for the new file.
        if (_graph.Length > 0)
        {
            _graph = "";
            setGraph("");
        }
        _key = "";
        _tracks = null;
        _aligns = false;
        // The new file starts at "start": its graph is built for that position once it has loaded.
        _stale = true;
        _lastSeek = start;
    }

    /// <summary>A seek to <paramref name="time"/>: playing, the graph is aligned there now; paused, once play is pressed.</summary>
    public void OnSeek(TimeSpan time, bool paused)
    {
        _lastSeek = time;
        if (!_aligns)
            return;
        if (paused)
            _stale = true;
        else
            Build(time);
    }

    /// <summary>A frame step: the position is wherever mpv lands.</summary>
    public void OnFrameStep()
    {
        _lastSeek = null;
        if (_aligns)
            _stale = true;
    }

    /// <summary>
    /// The tracks are picked: <paramref name="tracks"/> are the timeline's A1 and A2 track ids when the mix applies (null
    /// otherwise: a source, no mix, no audio). Returns whether the graph plays the audio; otherwise "aid" should.
    /// </summary>
    public bool OnTracks((string A1, string? A2)? tracks)
    {
        if (tracks is { } picked && Mix is { } mix)
        {
            _tracks = picked;
            // A changed mix, or one whose alignment is stale, is built at the position (a compact toggle or a reload of
            // the same mix leaves it alone).
            if (mix.Graph(picked.A1, picked.A2) != _key || _stale || _graph.Length == 0)
            {
                var at = _stale ? _lastSeek ?? position() : position();
                if (Build(at) && at is { } time)
                    seekBack(time);
            }
            return true;
        }
        _tracks = null;
        _aligns = false;
        _key = "";
        if (Set("") && position() is { } now)
            seekBack(now);
        return false;
    }

    /// <summary>
    /// Play was pressed. When the graph has to be realigned first, it is rebuilt and a seek back sent; returns where, so
    /// playback waits for that seek to land (the first moment would be heard twice otherwise). Null: play at once.
    /// </summary>
    public TimeSpan? OnPlay()
    {
        if (!_aligns || !_stale || (_lastSeek ?? position()) is not { } at || !Build(at))
            return null;
        seekBack(at);
        return at;
    }

    /// <summary>Sets the mix graph for the current tracks, aligned at <paramref name="at"/>; whether it changed.</summary>
    private bool Build(TimeSpan? at)
    {
        if (Mix is not { } mix || _tracks is not var (a1, a2))
            return false;
        _key = mix.Graph(a1, a2);
        _aligns = mix.A2 is not null && a2 is not null;
        _stale = false;
        return Set(_aligns ? mix.Graph(a1, a2, at ?? TimeSpan.Zero) : _key);
    }

    /// <summary>Only a change is sent: mpv rebuilds the audio chain (a short gap) every time it is set.</summary>
    private bool Set(string graph)
    {
        if (graph == _graph)
            return false;
        _graph = graph;
        setGraph(graph);
        return true;
    }
}
