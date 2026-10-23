using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>Paths of the ffmpeg tools this app drives, plus the detected version.</summary>
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, string VersionText, Version? Version)
{
    /// <summary>Oldest ffmpeg the generated commands are written for.</summary>
    public static readonly Version Minimum = new(5, 0);

    public bool IsTooOld => Version is { } v && v < Minimum;
}

public static partial class FfmpegLocator
{
    /// <summary>
    /// Directories to search. GUI apps on macOS start with a minimal PATH, so the usual
    /// Homebrew/MacPorts/distro locations are always checked as well.
    /// </summary>
    public static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat([
                "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/opt/local/bin", "/snap/bin",
                Path.Combine(home, ".local", "bin"), Path.Combine(home, "bin"),
            ]);
        foreach (var dir in dirs)
            if (seen.Add(dir))
                yield return dir;
    }

    public static string? FindExecutable(string name, string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
            return File.Exists(preferredPath) ? preferredPath : null;
        foreach (var dir in SearchDirectories())
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Finds ffmpeg and ffprobe (ffprobe defaults to ffmpeg's directory) and reads the version.</summary>
    public static async Task<FfmpegTools?> LocateAsync(string? ffmpegOverride = null, string? ffprobeOverride = null, CancellationToken ct = default)
    {
        var ffmpeg = FindExecutable("ffmpeg", ffmpegOverride);
        if (ffmpeg is null)
            return null;
        var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe");
        var ffprobe = !string.IsNullOrWhiteSpace(ffprobeOverride)
            ? FindExecutable("ffprobe", ffprobeOverride)
            : File.Exists(sibling) ? sibling : FindExecutable("ffprobe");
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
