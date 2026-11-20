using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.Core.Media;

/// <summary>A stretch of timeline time where the mix reaches full scale, and its loudest peak in dBFS (above 0 when over).</summary>
public sealed record ClipRange(TimeSpan Start, TimeSpan End, double PeakDb);

/// <summary>What a clipping scan found: the clipped stretches in timeline order and the loudest peak overall.</summary>
public sealed record ClippingReport(IReadOnlyList<ClipRange> Ranges, double PeakDb, TimeSpan Scanned)
{
    public bool Clips => Ranges.Count > 0;
}

/// <summary>
/// Finds clipping in interleaved float samples: a frame clips when any channel reaches the level meters' clip level
/// (<see cref="LevelReading.ClipDb"/>). Clipped frames closer than <see cref="MergeGap"/> make one range, so a
/// distorted passage shows as one mark rather than hundreds.
/// </summary>
public sealed class ClipDetector(int sampleRate, int channels)
{
    public static readonly TimeSpan MergeGap = TimeSpan.FromMilliseconds(250);

    /// <summary>The sample magnitude of <see cref="LevelReading.ClipDb"/>.</summary>
    public static readonly float Threshold = (float)Math.Pow(10, LevelReading.ClipDb / 20);

    private readonly long _gap = (long)(MergeGap.TotalSeconds * sampleRate);
    private readonly List<ClipRange> _ranges = [];
    private long _frames;
    private float _peak;
    private long _rangeStart = -1, _rangeEnd;
    private float _rangePeak;

    public int SampleRate => sampleRate;

    public TimeSpan Position => Time(_frames);

    /// <summary>Takes whole frames of interleaved samples (a multiple of the channel count).</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / channels;
        for (var f = 0; f < frames; f++)
        {
            var max = 0f;
            var frame = samples.Slice(f * channels, channels);
            foreach (var s in frame)
            {
                var m = Math.Abs(s);
                if (m > max)
                    max = m;
            }
            if (max > _peak)
                _peak = max;
            if (max < Threshold)
                continue;
            var at = _frames + f;
            if (_rangeStart >= 0 && at - _rangeEnd > _gap)
                Close();
            if (_rangeStart < 0)
            {
                _rangeStart = at;
                _rangePeak = 0;
            }
            _rangeEnd = at + 1;
            if (max > _rangePeak)
                _rangePeak = max;
        }
        _frames += frames;
    }

    public ClippingReport Finish()
    {
        if (_rangeStart >= 0)
            Close();
        return new ClippingReport([.. _ranges], Db(_peak), Time(_frames));
    }

    private void Close()
    {
        _ranges.Add(new ClipRange(Time(_rangeStart), Time(_rangeEnd), Db(_rangePeak)));
        _rangeStart = -1;
    }

    private TimeSpan Time(long frames) => TimeSpan.FromSeconds((double)frames / sampleRate);

    private static double Db(float magnitude) => magnitude > 0 ? 20 * Math.Log10(magnitude) : double.NegativeInfinity;
}

/// <summary>Runs an <see cref="AudioScanPlan"/> through ffmpeg and reports where its audio clips.</summary>
public sealed class ClippingScanner(FfmpegTools tools)
{
    /// <param name="progress">The fraction of the timeline scanned so far, reported a few times a second.</param>
    /// <exception cref="InvalidOperationException">ffmpeg failed; the message holds its error output.</exception>
    public async Task<ClippingReport> ScanAsync(AudioScanPlan plan, IProgress<double>? progress, CancellationToken ct)
    {
        var detector = new ClipDetector(plan.SampleRate, plan.Channels);
        var errors = new StringBuilder();
        var frameBytes = plan.Channels * sizeof(float);
        var total = Math.Max(plan.Duration.TotalSeconds, 1e-3);
        var result = await Cli.Wrap(tools.Ffmpeg)
            .WithArguments(plan.Arguments)
            .WithStandardOutputPipe(PipeTarget.Create(async (output, token) =>
            {
                var buffer = new byte[frameBytes * 16384];
                var carry = 0;
                var reported = TimeSpan.Zero;
                int read;
                while ((read = await output.ReadAsync(buffer.AsMemory(carry), token)) > 0)
                {
                    var available = carry + read;
                    var whole = available / frameBytes * frameBytes;
                    detector.Add(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole)));
                    carry = available - whole;
                    Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
                    if (progress is not null && detector.Position - reported >= TimeSpan.FromSeconds(5))
                    {
                        reported = detector.Position;
                        progress.Report(Math.Min(1, reported.TotalSeconds / total));
                    }
                }
            }))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(errors))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(errors.Length > 0 ? errors.ToString().Trim() : $"ffmpeg exited with code {result.ExitCode}.");
        return detector.Finish();
    }
}
