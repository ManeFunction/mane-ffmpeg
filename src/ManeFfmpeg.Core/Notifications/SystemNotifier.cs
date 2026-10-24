using CliWrap;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Notifications;

/// <summary>Desktop notifications without native bindings: osascript on macOS, notify-send/gdbus on Linux.</summary>
public static class SystemNotifier
{
    public static async Task NotifyAsync(string title, string body)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                await MacAsync(title, body);
            else if (OperatingSystem.IsLinux())
                await LinuxAsync(title, body);
        }
        catch (Exception)
        {
            // Notifications are best-effort; the export panel always shows the result.
        }
    }

    // Title and body go in as argv, so no AppleScript escaping is needed.
    private static Task MacAsync(string title, string body) =>
        Cli.Wrap("/usr/bin/osascript")
            .WithArguments(["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
                "-e", "end run", title, body])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

    private static async Task LinuxAsync(string title, string body)
    {
        var notifySend = Executables.Find("notify-send");
        if (notifySend is not null)
        {
            var result = await Cli.Wrap(notifySend)
                .WithArguments(["--app-name", AppPaths.AppName, "--icon", "video-x-generic", title, body])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync();
            if (result.ExitCode == 0)
                return;
        }

        var gdbus = Executables.Find("gdbus");
        if (gdbus is null)
            return;
        await Cli.Wrap(gdbus)
            .WithArguments(["call", "--session", "--dest", "org.freedesktop.Notifications",
                "--object-path", "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.Notify",
                AppPaths.AppName, "0", "video-x-generic", title, body, "[]", "{}", "8000"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();
    }
}
