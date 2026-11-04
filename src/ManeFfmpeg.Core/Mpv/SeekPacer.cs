namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// The preview's seek pacing, apart from mpv. One exact seek goes at a time; later ones wait and collapse into the
/// latest, which goes once mpv reports the seek finished (<see cref="Restarted"/>). mpv drops an unfinished exact seek
/// for a newer one, so seeking at the pointer's pace would show nothing until the pointer stopped (an exact seek in a
/// long GOP can take hundreds of ms). A seek mpv never reports finished is checked on every
/// <see cref="CheckInterval"/>: while mpv is still seeking it keeps waiting, however long that takes; otherwise the
/// next one goes. Thread-safe: seeks come from the UI thread, restarts from mpv's event thread and checks from a timer;
/// <see cref="Dispose"/> waits for calls into mpv under way on those threads, so none reaches a destroyed mpv.
/// </summary>
/// <param name="send">Sends an exact seek to mpv.</param>
/// <param name="isSeeking">Whether mpv is still working on a seek.</param>
/// <param name="armCheck">Arms (true) or disarms (false) a single <see cref="Check"/> after <see cref="CheckInterval"/>.</param>
/// <param name="now">The clock, for telling position echoes from new positions.</param>
internal sealed class SeekPacer(Action<TimeSpan> send, Func<bool> isSeeking, Action<bool> armCheck, Func<DateTime>? now = null)
{
    /// <summary>How often to check on a seek mpv hasn't reported finished: one that failed or was lost lets the next go.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>For this long after a seek is sent, position reports may still echo the old position.</summary>
    public static readonly TimeSpan EchoWindow = TimeSpan.FromMilliseconds(250);

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private TimeSpan? _pending;
    // A seek is under way (until mpv reports it finished or stops seeking); _target is where it goes.
    private bool _seeking;
    private TimeSpan _target;
    private DateTime _lastSentAt;
    private bool _disposed;
    // Calls into mpv under way (send or isSeeking), which Dispose waits for.
    private int _inFlight;

    /// <summary>The longest <see cref="Dispose"/> waits for a call into mpv to return.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    /// <summary>Seeks to <paramref name="time"/> now, or as soon as the seek under way has finished.</summary>
    public void Seek(TimeSpan time)
    {
        lock (_gate)
        {
            if (_seeking)
            {
                // The pointer moving along the time it's already at (up and down) costs no second decode.
                _pending = time == _target ? null : time;
                return;
            }
            _pending = time;
            _seeking = true;
        }
        Flush();
    }

    /// <summary>mpv finished a seek (or a load): the latest waiting seek goes now.</summary>
    public void Restarted()
    {
        lock (_gate)
            if (!_seeking)
                return;
        Flush();
    }

    /// <summary>The armed check: a seek still under way keeps the next one waiting; one mpv isn't working on lets it go.</summary>
    public void Check()
    {
        lock (_gate)
        {
            if (_disposed || !_seeking)
                return;
            _inFlight++;
        }
        bool stillSeeking;
        try
        {
            stillSeeking = isSeeking();
        }
        finally
        {
            Exit();
        }
        if (stillSeeking)
        {
            lock (_gate)
                if (!_disposed && _seeking)
                    armCheck(true);
            return;
        }
        Flush();
    }

    /// <summary>The file changed or went away: a seek under way and the waiting one are dropped, the next goes at once.</summary>
    public void Reset()
    {
        lock (_gate)
            (_pending, _seeking) = (null, false);
    }

    /// <summary>A position report now may be an echo of the old position: a seek is waiting or was just sent.</summary>
    public bool MayEcho
    {
        get
        {
            lock (_gate)
                return _pending is not null || _now() - _lastSentAt <= EchoWindow;
        }
    }

    /// <summary>Position reports are to be trusted again at once (frame stepping reports where it landed).</summary>
    public void ForgetLastSeek()
    {
        lock (_gate)
            _lastSentAt = DateTime.MinValue;
    }

    /// <summary>
    /// Nothing more is sent or armed, and calls into mpv under way have returned (the player is about to destroy it).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            var until = DateTime.UtcNow + DisposeWait;
            while (_inFlight > 0 && until - DateTime.UtcNow is { Ticks: > 0 } left)
                Monitor.Wait(_gate, left);
        }
    }

    private void Exit()
    {
        lock (_gate)
            if (--_inFlight == 0)
                Monitor.PulseAll(_gate);
    }

    private void Flush()
    {
        TimeSpan? next;
        lock (_gate)
        {
            if (_disposed)
                return;
            next = _pending;
            _pending = null;
            _seeking = next is not null;
            if (next is { } target)
            {
                _target = target;
                _lastSentAt = _now();
                _inFlight++;
            }
            armCheck(_seeking);
        }
        if (next is not { } time)
            return;
        try
        {
            send(time);
        }
        finally
        {
            Exit();
        }
    }
}
