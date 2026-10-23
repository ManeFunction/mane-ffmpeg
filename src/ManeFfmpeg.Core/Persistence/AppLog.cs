namespace ManeFfmpeg.Core.Persistence;

/// <summary>
/// A small append-only log for diagnosing problems on users' machines
/// (macOS: ~/Library/Logs/Mane FFmpeg/app.log, Linux: ~/.cache/mane-ffmpeg/app.log).
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => OperatingSystem.IsMacOS() && AppPaths.RootOverride is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", AppPaths.AppName, "app.log")
        : Path.Combine(AppPaths.CacheDirectory, "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }
}
