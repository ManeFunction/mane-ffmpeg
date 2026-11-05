using System.Diagnostics;

namespace ManeFfmpeg.UI.Controls;

/// <summary>
/// The video view's frame counters and timings, for the debug automation's latency and drag reports. The recording
/// methods compile away outside debug builds, so the render path carries no diagnostics there. Thread-safe: the render
/// thread and the UI thread both record.
/// </summary>
internal sealed class FrameStats
{
    private int _wakes;
    private int _rendered;
    private int _presented;
    private long _renderTicks;
    private long _waitTicks;
    private long _copyTicks;

    /// <summary>Wake-ups of the render thread, frames mpv rendered, frames shown.</summary>
    public (int Wakes, int Rendered, int Presented) Counters =>
        (Volatile.Read(ref _wakes), Volatile.Read(ref _rendered), Volatile.Read(ref _presented));

    /// <summary>Total milliseconds spent in mpv's render call, waiting for the frame lock in Present, and copying.</summary>
    public (double Render, double PresentWait, double Copy) Timings =>
        (Ms(Interlocked.Read(ref _renderTicks)), Ms(Interlocked.Read(ref _waitTicks)), Ms(Interlocked.Read(ref _copyTicks)));

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Sets <paramref name="timestamp"/> to now.</summary>
    [Conditional("DEBUG")]
    public static void Start(ref long timestamp) => timestamp = Stopwatch.GetTimestamp();

    [Conditional("DEBUG")]
    public void Woke() => Interlocked.Increment(ref _wakes);

    /// <summary>mpv rendered a frame, starting at <paramref name="start"/>.</summary>
    [Conditional("DEBUG")]
    public void Rendered(long start)
    {
        Interlocked.Add(ref _renderTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _rendered);
    }

    /// <summary>Present got the frame lock it started waiting for at <paramref name="start"/>; the copy starts now.</summary>
    [Conditional("DEBUG")]
    public void Waited(long start, ref long copyStart)
    {
        copyStart = Stopwatch.GetTimestamp();
        Interlocked.Add(ref _waitTicks, copyStart - start);
    }

    /// <summary>A frame was copied (from <paramref name="copyStart"/>) and shown.</summary>
    [Conditional("DEBUG")]
    public void Presented(long copyStart)
    {
        Interlocked.Add(ref _copyTicks, Stopwatch.GetTimestamp() - copyStart);
        Interlocked.Increment(ref _presented);
    }
}
