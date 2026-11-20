using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>Peak levels per channel in dBFS (−∞ for silence), with whether any of them reached full scale.</summary>
public sealed record LevelReading(double[] PeaksDb)
{
    /// <summary>A peak at or above this counts as clipping: full scale (a 16-bit file's highest sample is −0.0003 dB).</summary>
    public const double ClipDb = -0.01;

    public int Channels => PeaksDb.Length;

    public bool Clipped(int channel) => PeaksDb[channel] >= ClipDb;

    /// <summary>The louder of two readings, channel by channel (the channel count of the newer one wins).</summary>
    internal static LevelReading Max(LevelReading? a, LevelReading b)
    {
        if (a is null || a.Channels != b.Channels)
            return b;
        var peaks = new double[b.Channels];
        for (var i = 0; i < peaks.Length; i++)
            peaks[i] = Math.Max(a.PeaksDb[i], b.PeaksDb[i]);
        return new LevelReading(peaks);
    }
}

/// <summary>
/// Live peak levels of what the preview plays, for the level meters: an astats filter at the end of mpv's audio chain
/// ("af", after the lavfi-complex mix and before mpv's own volume, so the master volume doesn't move the meters but clip
/// volumes and A2 do) measures every frame of about 33 ms, and a timer reads its metadata every 10 ms while playing.
/// mpv filters audio ahead of what is heard by its audio buffer, so each reading is held back by "audio-buffer" (0.2 s;
/// measured ~0.19 s with CoreAudio) before <see cref="Take"/> hands it out.
/// </summary>
public sealed partial class LevelMeter : IDisposable
{
    public const string Label = "mane-meter";

    /// <summary>
    /// The af entry. asetnsamples makes frames of a known length (decoders give 1024–4096 samples), so polling every 10 ms
    /// sees each one. The first astats counts the samples so far (it never resets), which tells frames apart: a steady
    /// sound measures the same in every frame. The second measures each frame's peaks on its own (reset=1).
    /// </summary>
    public const string Filter = "@" + Label + ":lavfi=[asetnsamples=n=1600:p=0,"
        + "astats=metadata=1:measure_perchannel=none:measure_overall=Number_of_samples,"
        + "astats=metadata=1:reset=1:measure_perchannel=Peak_level:measure_overall=none]";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly Func<string?> _read;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Queue<(long Due, LevelReading Reading)> _pending = new();
    private readonly long _latencyTicks;
    private readonly bool _polls;
    // The last frame read (or, when measuring (re)starts, the frame measured before it).
    private string? _last;
    private bool _running;

    /// <param name="read">Reads the filter's metadata (mpv's "af-metadata/mane-meter"), from a timer thread.</param>
    /// <param name="latency">How far ahead of playback mpv filters audio.</param>
    /// <param name="polls">Reads on its own timer (tests call <see cref="Poll(long)"/> themselves).</param>
    public LevelMeter(Func<string?> read, TimeSpan latency, bool polls = true)
    {
        _read = read;
        _polls = polls;
        _latencyTicks = (long)(latency.TotalSeconds * Stopwatch.Frequency);
        _timer = new Timer(_ => Poll());
    }

    /// <summary>Installs the meter filter in <paramref name="mpv"/>'s audio chain and reads it.</summary>
    public static LevelMeter Attach(MpvPlayer mpv)
    {
        mpv.SetString("af", Filter);
        var latency = TimeSpan.FromSeconds(mpv.GetDouble("audio-buffer") is { } seconds and >= 0 and < 2 ? seconds : 0.2);
        return new LevelMeter(() => mpv.GetString($"af-metadata/{Label}"), latency);
    }

    /// <summary>The channel count last measured, or null before anything played.</summary>
    public int? Channels { get; private set; }

    /// <summary>Raised (from a timer thread) when the measured channel count changes.</summary>
    public event Action<int>? ChannelsChanged;

    /// <summary>Measures while audio plays; stopping (pause) or <see cref="Reset"/> (seek, load) drops what is still held back.</summary>
    public bool Running
    {
        set
        {
            lock (_gate)
            {
                if (_running == value)
                    return;
                _running = value;
                Restart();
            }
            if (_polls)
                _timer.Change(value ? PollInterval : Timeout.InfiniteTimeSpan, value ? PollInterval : Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Forgets readings not heard yet: after a seek mpv drops the audio they came from.</summary>
    public void Reset()
    {
        lock (_gate)
            Restart();
    }

    private void Restart()
    {
        _pending.Clear();
        _last = _running ? _read() : null;
    }

    /// <summary>
    /// The loudest of the readings due by now (heard since the last call), or null when none is: nothing is lost
    /// between two calls, however rarely they come.
    /// </summary>
    public LevelReading? Take() => Take(Stopwatch.GetTimestamp());

    internal LevelReading? Take(long now)
    {
        LevelReading? result = null;
        lock (_gate)
            while (_pending.TryPeek(out var next) && next.Due <= now)
                result = LevelReading.Max(result, _pending.Dequeue().Reading);
        return result;
    }

    /// <summary>One timer tick: changed metadata is a new frame (its sample count differs).</summary>
    internal void Poll() => Poll(Stopwatch.GetTimestamp());

    internal void Poll(long now)
    {
        int? channels = null;
        lock (_gate)
        {
            if (!_running)
                return;
            var metadata = _read();
            if (metadata is null || metadata == _last)
                return;
            _last = metadata;
            if (Parse(metadata) is not { } reading)
                return;
            _pending.Enqueue((now + _latencyTicks, reading));
            // Far more than the latency's worth only piles up if nobody takes them.
            while (_pending.Count > 200)
                _pending.Dequeue();
            if (Channels != reading.Channels)
                channels = Channels = reading.Channels;
        }
        if (channels is { } count)
            ChannelsChanged?.Invoke(count);
    }

    /// <summary>The per-channel peaks in mpv's af-metadata JSON (<c>{"lavfi.astats.Overall.Number_of_samples":"4800","lavfi.astats.1.Peak_level":"-18.06",…}</c>).</summary>
    internal static LevelReading? Parse(string metadata)
    {
        var peaks = new SortedDictionary<int, double>();
        foreach (Match m in PeakPattern().Matches(metadata))
        {
            var channel = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var text = m.Groups[2].Value;
            peaks[channel] = text switch
            {
                "-inf" => double.NegativeInfinity,
                "inf" => double.PositiveInfinity,
                _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) ? db : double.NegativeInfinity,
            };
        }
        return peaks.Count == 0 ? null : new LevelReading([.. peaks.Values]);
    }

    [GeneratedRegex("\"lavfi\\.astats\\.(\\d+)\\.Peak_level\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex PeakPattern();

    public void Dispose()
    {
        lock (_gate)
            _running = false;
        _timer.Dispose();
    }
}
