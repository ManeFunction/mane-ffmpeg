using ManeFfmpeg.Core.Ffmpeg;

namespace ManeFfmpeg.Core.Platforms.Mac;

/// <summary>Quick Actions: Automator workflows in ~/Library/Services that start this app to convert the selected videos.</summary>
public sealed partial class MacPlatform
{
    private IQuickActions? _quickActions;

    public override IQuickActions? QuickActions =>
        _quickActions ??= new MacQuickActions(Path.Combine(Home, "Library", "Services"), QuickActionCommand, RefreshServices, new FinderServices());

    /// <summary>
    /// The shell line a Quick Action runs. The packaged app is started as a new instance with <c>open</c>, so Launch
    /// Services gives it the environment from its Info.plist, and by bundle id when it was moved since. A development
    /// build is started directly, in the background, so Automator doesn't wait for it.
    /// </summary>
    private static string QuickActionCommand(string presetId)
    {
        var tail = $"{FileConversion.CommandLineFlag} {MacQuickActions.Quote(presetId)} \"$@\"";
        var processPath = Environment.ProcessPath ?? "";
        if (BundleAround(processPath) is { } bundle)
        {
            var byPath = $"/usr/bin/open -n -a {MacQuickActions.Quote(bundle)} --args {tail}";
            return BundleId.Value is { } id ? $"{byPath} || /usr/bin/open -n -b {MacQuickActions.Quote(id)} --args {tail}" : byPath;
        }
        var command = new List<string> { processPath };
        // "dotnet ManeFfmpeg.dll"
        if (Platform.AppExecutable(processPath) is null)
            command.Add(Environment.GetCommandLineArgs()[0]);
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot
            ? $"DOTNET_ROOT={MacQuickActions.Quote(dotnetRoot)} "
            : "";
        return $"{root}{string.Join(" ", command.Select(MacQuickActions.Quote))} {tail} >/dev/null 2>&1 &";
    }

    /// <summary>Tells the system to read ~/Library/Services again, so the menu changes at once.</summary>
    private static void RefreshServices()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/System/Library/CoreServices/pbs", "-update")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })?.Dispose();
        }
        catch (Exception)
        {
            // The system picks the change up on its own a bit later.
        }
    }
}
