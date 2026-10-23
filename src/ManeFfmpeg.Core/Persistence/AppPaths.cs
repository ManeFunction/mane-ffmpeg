namespace ManeFfmpeg.Core.Persistence;

/// <summary>Per-user folders, following each platform's conventions.</summary>
public static class AppPaths
{
    public const string AppName = "Mane FFmpeg";
    private const string LinuxName = "mane-ffmpeg";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Overrides both folders (tests use a temp directory).</summary>
    public static string? RootOverride { get; set; }

    public static string ConfigDirectory =>
        RootOverride is { } root ? Path.Combine(root, "config")
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Application Support", AppName)
        : Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), LinuxName);

    public static string CacheDirectory =>
        RootOverride is { } root ? Path.Combine(root, "cache")
        : OperatingSystem.IsMacOS() ? Path.Combine(Home, "Library", "Caches", AppName)
        : Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), LinuxName);

    public static string TempDirectory => Path.Combine(Path.GetTempPath(), LinuxName);

    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathRooted(value)
            ? value
            : Path.Combine(Home, fallback);
}
