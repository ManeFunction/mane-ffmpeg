using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Platforms;
using ManeFfmpeg.Core.Platforms.Linux;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Pins what tests compare to one platform's conventions, so the snapshots hold on every system.</summary>
internal static class TestPlatforms
{
#pragma warning disable CA1416 // Only its sh quoting is used, which is plain text.
    /// <summary>sh quoting, as on macOS and Linux.</summary>
    public static IPlatform Sh { get; } = new LinuxPlatform();
#pragma warning restore CA1416

    /// <summary>The commands as sh, with "/" for the separators Path.Combine put in on Windows.</summary>
    public static string ShCommands(this ExportPlan plan) => plan.CommandText("ffmpeg", Sh).Slashes();

    /// <summary>"/" for "\" on Windows, where Path.Combine joins the tests' Unix-style paths with backslashes.</summary>
    public static string Slashes(this string text) => OperatingSystem.IsWindows() ? text.Replace('\\', '/') : text;
}
