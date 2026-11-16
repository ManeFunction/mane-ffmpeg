using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core;

/// <summary>Finds command-line tools (ffmpeg, notify-send…) the way a shell would, plus the usual install folders.</summary>
public static class Executables
{
    /// <summary>Directories to search: PATH, then the platform's usual install folders (<see cref="IPlatform.ToolSearchDirectories"/>).</summary>
    public static IEnumerable<string> SearchDirectories() => Platform.Current.ToolSearchDirectories();

    /// <summary><paramref name="preferredPath"/> when given (and it exists), else the first match in <see cref="SearchDirectories"/>.</summary>
    public static string? Find(string name, string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
            return File.Exists(preferredPath) ? preferredPath : null;
        var file = Platform.Current.ExecutableFileName(name);
        foreach (var dir in SearchDirectories())
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
