using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>Paths of the ffmpeg tools this app drives, plus the detected version.</summary>
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, string VersionText, Version? Version)
{
    /// <summary>Oldest ffmpeg the generated commands are written for.</summary>
    public static readonly Version Minimum = new(5, 0);

    /// <summary>Leading ffmpeg options for every run: no stdin, no banner, errors only.</summary>
    public static IReadOnlyList<string> Quiet { get; } = ["-nostdin", "-hide_banner", "-loglevel", "error"];

    public bool IsTooOld => Version is { } v && v < Minimum;
}

public static partial class FfmpegLocator
{
    /// <summary>Finds ffmpeg and ffprobe (ffprobe defaults to ffmpeg's directory) and reads the version.</summary>
    public static async Task<FfmpegTools?> LocateAsync(string? ffmpegOverride = null, string? ffprobeOverride = null, CancellationToken ct = default)
    {
        var ffmpeg = Executables.Find("ffmpeg", ffmpegOverride);
        if (ffmpeg is null)
            return null;
        var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe");
        var ffprobe = !string.IsNullOrWhiteSpace(ffprobeOverride)
            ? Executables.Find("ffprobe", ffprobeOverride)
            : File.Exists(sibling) ? sibling : Executables.Find("ffprobe");
        if (ffprobe is null)
            return null;

        try
        {
            var result = await Cli.Wrap(ffmpeg)
                .WithArguments(["-hide_banner", "-version"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
            var firstLine = result.StandardOutput.Split('\n', 2)[0].Trim();
            return new FfmpegTools(ffmpeg, ffprobe, firstLine, ParseVersion(firstLine));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>"ffmpeg version 8.1.2 Copyright…" → 8.1.2; git builds ("N-1234-g…") → null.</summary>
    public static Version? ParseVersion(string versionLine)
    {
        var match = VersionRegex().Match(versionLine);
        if (!match.Success)
            return null;
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(major, minor, patch);
    }

    [GeneratedRegex(@"version\s+n?(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();
}
