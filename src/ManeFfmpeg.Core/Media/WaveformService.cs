using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CliWrap;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Media;

/// <summary>Peak levels (0–255) of an audio stream, one per <see cref="BucketsPerSecond"/>-th of a second.</summary>
public sealed class Waveform(byte[] peaks)
{
    public const int BucketsPerSecond = 100;

    public byte[] Peaks { get; } = peaks;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Peaks.Length / BucketsPerSecond);

    /// <summary>Highest peak in [from, to), or 0 outside the stream.</summary>
    public byte Max(TimeSpan from, TimeSpan to)
    {
        var a = (int)Math.Floor(from.TotalSeconds * BucketsPerSecond);
        var b = (int)Math.Ceiling(to.TotalSeconds * BucketsPerSecond);
        a = Math.Max(a, 0);
        b = Math.Min(Math.Max(b, a + 1), Peaks.Length);
        byte max = 0;
        for (var i = a; i < b; i++)
            if (Peaks[i] > max)
                max = Peaks[i];
        return max;
    }
}

/// <summary>Builds waveforms with ffmpeg (mono, 8 kHz) and caches them in memory and on disk.</summary>
public sealed class WaveformService(FfmpegTools tools)
{
    private const int SampleRate = 8000;
    private const int SamplesPerBucket = SampleRate / Waveform.BucketsPerSecond;
    private static readonly byte[] Magic = "MFWF1"u8.ToArray();

    private readonly ConcurrentDictionary<string, Lazy<Task<Waveform?>>> _cache = new();

    public Waveform? TryGet(string path, MediaInfo media, int audioStreamIndex) =>
        _cache.TryGetValue(Key(path, media, audioStreamIndex), out var lazy) && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    /// <summary>
    /// The waveform of stream <paramref name="audioStreamIndex"/> (absolute index) of <paramref name="path"/> as probed
    /// in <paramref name="media"/>, or null if it has none.
    /// </summary>
    public Task<Waveform?> GetAsync(string path, MediaInfo media, int audioStreamIndex)
    {
        var lazy = _cache.GetOrAdd(Key(path, media, audioStreamIndex), _ => new Lazy<Task<Waveform?>>(() => LoadOrBuildAsync(path, audioStreamIndex)));
        return lazy.Value;
    }

    // A file replaced in place gets a new stamp when probed again, so its waveform is read again.
    private static string Key(string path, MediaInfo media, int stream) => $"{path}\n{media.Stamp}\n{stream}";

    private async Task<Waveform?> LoadOrBuildAsync(string path, int stream)
    {
        var cacheFile = CacheFile(path, stream);
        if (cacheFile is not null && TryReadCache(cacheFile) is { } cached)
            return cached;

        var peaks = new List<byte>();
        var result = await Cli.Wrap(tools.Ffmpeg)
            .WithArguments([.. FfmpegTools.Quiet, "-i", path, "-map", $"0:{stream}",
                "-ac", "1", "-ar", SampleRate.ToString(), "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1"])
            .WithStandardOutputPipe(PipeTarget.Create(async (output, ct) =>
            {
                var buffer = new byte[SamplesPerBucket * 2 * 256];
                var carry = 0;
                int read;
                while ((read = await output.ReadAsync(buffer.AsMemory(carry), ct)) > 0)
                {
                    var available = carry + read;
                    var whole = available / (SamplesPerBucket * 2) * SamplesPerBucket * 2;
                    for (var offset = 0; offset < whole; offset += SamplesPerBucket * 2)
                        peaks.Add(Peak(buffer.AsSpan(offset, SamplesPerBucket * 2)));
                    carry = available - whole;
                    Buffer.BlockCopy(buffer, whole, buffer, 0, carry);
                }
                if (carry >= 2)
                    peaks.Add(Peak(buffer.AsSpan(0, carry & ~1)));
            }))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();
        if (result.ExitCode != 0 && peaks.Count == 0)
            return null;

        var waveform = new Waveform([.. peaks]);
        if (cacheFile is not null)
            TryWriteCache(cacheFile, waveform);
        return waveform;
    }

    private static byte Peak(ReadOnlySpan<byte> pcm)
    {
        var max = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8));
            if (sample > max)
                max = sample;
        }
        // Square-root scale so quiet speech is still visible next to loud peaks.
        return (byte)Math.Clamp(Math.Sqrt(max / 32768.0) * 255, 0, 255);
    }

    private static string? CacheFile(string path, int stream)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{stream}"));
            var dir = Path.Combine(AppPaths.CacheDirectory, "waveforms");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, Convert.ToHexString(hash) + ".bin");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Waveform? TryReadCache(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            var bytes = File.ReadAllBytes(file);
            return bytes.AsSpan().StartsWith(Magic) ? new Waveform(bytes[Magic.Length..]) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryWriteCache(string file, Waveform waveform)
    {
        try
        {
            using var stream = File.Create(file);
            stream.Write(Magic);
            stream.Write(waveform.Peaks);
        }
        catch (Exception)
        {
            // A missing cache only costs time.
        }
    }
}
