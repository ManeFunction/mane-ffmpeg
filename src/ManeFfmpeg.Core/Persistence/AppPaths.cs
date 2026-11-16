using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Persistence;

/// <summary>Per-user folders, following each platform's conventions (<see cref="IPlatform.ConfigDirectory"/>).</summary>
public static class AppPaths
{
    public const string AppName = "Mane FFmpeg";
    private const string TempName = "mane-ffmpeg";

    /// <summary>Overrides both folders (tests use a temp directory).</summary>
    public static string? RootOverride { get; set; }

    public static string ConfigDirectory =>
        RootOverride is { } root ? Path.Combine(root, "config") : Platform.Current.ConfigDirectory;

    public static string CacheDirectory =>
        RootOverride is { } root ? Path.Combine(root, "cache") : Platform.Current.CacheDirectory;

    public static string TempDirectory => Path.Combine(Path.GetTempPath(), TempName);
}
