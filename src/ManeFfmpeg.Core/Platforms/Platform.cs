using ManeFfmpeg.Core.Platforms.Linux;
using ManeFfmpeg.Core.Platforms.Mac;
using ManeFfmpeg.Core.Platforms.Windows;

namespace ManeFfmpeg.Core.Platforms;

/// <summary>
/// The <see cref="IPlatform"/> this process runs on. The one place that checks the operating system; tests that need
/// another platform's rules create its implementation directly.
/// </summary>
public static class Platform
{
    private static readonly Lazy<IPlatform> Detected = new(Detect);

    public static IPlatform Current => Detected.Value;

    /// <summary>
    /// The app's own executable: <see cref="Environment.ProcessPath"/>, or null when it's <c>dotnet</c> running
    /// <c>ManeFfmpeg.dll</c> (<c>dotnet run</c>), which nothing should register or replace.
    /// </summary>
    public static string? RunningExecutable => AppExecutable(Environment.ProcessPath);

    /// <summary><paramref name="processPath"/>, or null when it's missing or the <c>dotnet</c> host.</summary>
    internal static string? AppExecutable(string? processPath) =>
        processPath is not null && Path.GetFileNameWithoutExtension(processPath) != "dotnet" ? processPath : null;

    private static IPlatform Detect()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsPlatform();
        if (OperatingSystem.IsMacOS())
            return new MacPlatform();
        // Linux, and other Unix systems, which are close enough for a best effort.
        return new LinuxPlatform();
    }
}
