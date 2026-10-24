namespace ManeFfmpeg.Core;

/// <summary>Finds command-line tools (ffmpeg, notify-send…) the way a shell would, plus the usual install folders.</summary>
public static class Executables
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

    /// <summary><paramref name="preferredPath"/> when given (and it exists), else the first match in <see cref="SearchDirectories"/>.</summary>
    public static string? Find(string name, string? preferredPath = null)
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
}
