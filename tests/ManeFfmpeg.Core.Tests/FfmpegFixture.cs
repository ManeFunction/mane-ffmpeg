using System.Globalization;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Skips the test when no ffmpeg is installed.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegFixture.Tools is null)
            Skip = "ffmpeg/ffprobe not found";
    }
}

/// <summary>
/// Synthetic media for integration tests: black video with a white flash and a 1 kHz beep every
/// 2 seconds, starting at 0, so A/V sync of any export can be measured with blackdetect/silencedetect.
/// </summary>
public sealed partial class FfmpegFixture : IDisposable
{
    private static readonly Lazy<FfmpegTools?> LazyTools = new(() => FfmpegLocator.LocateAsync().GetAwaiter().GetResult());

    public static FfmpegTools? Tools => LazyTools.Value;

    public FfmpegFixture()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("mane-ffmpeg-tests").FullName;
        if (Tools is null)
            return;
        Sync30 = Generate("sync30.mp4", 12, "640x360", "30", "-c:v libx264 -preset veryfast -g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p -c:a aac -b:a 128k");
        Sync25 = Generate("sync25.mp4", 8, "1280x720", "25", "-c:v libx264 -preset veryfast -g 25 -pix_fmt yuv420p -c:a aac -b:a 128k");
        NoAudio = Generate("noaudio.mp4", 6, "640x360", "30", "-c:v libx264 -preset veryfast -g 30 -pix_fmt yuv420p", audio: false);
        TransportStream = Generate("sync.ts", 8, "640x360", "30", "-c:v libx264 -preset veryfast -g 30 -pix_fmt yuv420p -c:a aac -b:a 128k -f mpegts");
    }

    public string Directory { get; }
    public string Sync30 { get; } = "";
    public string Sync25 { get; } = "";
    public string NoAudio { get; } = "";
    public string TransportStream { get; } = "";

    private string Generate(string name, int seconds, string size, string rate, string codecArgs, bool audio = true)
    {
        var path = Path.Combine(Directory, name);
        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-i", $"color=c=black:s={size}:r={rate},drawbox=c=white:t=fill:enable='lt(mod(t\\,2)\\,0.1)'",
        };
        if (audio)
            args.AddRange(["-f", "lavfi", "-t", seconds.ToString(CultureInfo.InvariantCulture),
                "-i", "aevalsrc='if(lt(mod(t\\,2)\\,0.1)\\,0.5*sin(2*PI*1000*t)\\,0)':s=48000:c=stereo"]);
        args.AddRange(PresetArguments.Tokenize(codecArgs));
        args.Add(path);
        Cli.Wrap(Tools!.Ffmpeg).WithArguments(args).ExecuteAsync().GetAwaiter().GetResult();
        return path;
    }

    public async Task<MediaInfo> ProbeAsync(string path) => await new Ffprobe(Tools!).ProbeAsync(path);

    /// <summary>Times where a flash starts and where a beep starts in <paramref name="path"/>.</summary>
    public async Task<(List<double> Flashes, List<double> Beeps, double Duration)> MeasureAsync(string path)
    {
        var result = await Cli.Wrap(Tools!.Ffmpeg)
            .WithArguments(["-nostdin", "-hide_banner", "-i", path,
                "-vf", "blackdetect=d=0.03:pix_th=0.1", "-af", "aresample=async=1:first_pts=0,silencedetect=n=-30dB:d=0.03", "-f", "null", "-"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        var log = result.StandardError;
        // A flash is a black region ending and another starting ~0.1 s later; likewise a beep with silence.
        var flashes = Pulses(log, BlackStart(), BlackEnd());
        var beeps = Pulses(log, SilenceStart(), SilenceEnd());
        var info = await ProbeAsync(path);
        var end = info.Duration.TotalSeconds;
        return (flashes, beeps, end);
    }

    private static List<double> Pulses(string log, Regex starts, Regex ends)
    {
        var startTimes = starts.Matches(log).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        return ends.Matches(log)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(e => startTimes.Any(st => st > e && st - e < 0.2))
            .ToList();
    }

    [GeneratedRegex(@"black_start:([0-9.]+)")]
    private static partial Regex BlackStart();

    [GeneratedRegex(@"silence_start: ([0-9.]+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"black_end:([0-9.]+)")]
    private static partial Regex BlackEnd();

    [GeneratedRegex(@"silence_end: ([0-9.]+)")]
    private static partial Regex SilenceEnd();

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
