using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>Names for exported files: the extension a preset gives them, and a path that doesn't replace anything.</summary>
public static class OutputNaming
{
    /// <summary>
    /// The preset's extension (without its dot), else for saving audio the codec's (.m4a when that can't be told), else
    /// the source's.
    /// </summary>
    public static string Extension(Preset? preset, PresetKind kind, ProjectState state, IReadOnlyDictionary<string, MediaInfo> media,
        string sourcePath)
    {
        if (preset?.Extension?.Trim().TrimStart('.') is { Length: > 0 } own)
            return own;
        if (kind == PresetKind.Audio)
            return (preset is null ? null : ExportPlanner.AudioExtensionFor(state, media, preset)) ?? "m4a";
        return Path.GetExtension(sourcePath).TrimStart('.');
    }

    /// <summary>
    /// "&lt;directory&gt;/&lt;baseName&gt;.&lt;extension&gt;", with "-2", "-3"… added while that is a file or folder already,
    /// or <paramref name="taken"/> says it's spoken for.
    /// </summary>
    public static string FreePath(string directory, string baseName, string extension, Func<string, bool>? taken = null)
    {
        var ext = extension.Length > 0 ? "." + extension : "";
        var candidate = Path.Combine(directory, baseName + ext);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate) || taken?.Invoke(candidate) == true; n++)
            candidate = Path.Combine(directory, $"{baseName}-{n}{ext}");
        return candidate;
    }
}
