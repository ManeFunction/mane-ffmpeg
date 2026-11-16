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
