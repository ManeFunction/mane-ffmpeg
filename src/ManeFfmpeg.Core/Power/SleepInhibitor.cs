using System.Diagnostics;
using System.Runtime.InteropServices;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Power;

/// <summary>
/// Keeps the computer from going to sleep on its own while held: an IOKit power assertion on macOS, a
/// systemd-inhibit lock on Linux. The display can still sleep. Both are released if the app dies.
/// </summary>
public sealed partial class SleepInhibitor : IDisposable
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CFStringEncodingUtf8 = 0x08000100;
    private const uint AssertionLevelOn = 255;

    private uint? _assertion;
    private Process? _inhibit;

    private SleepInhibitor()
    {
    }

    /// <summary>
    /// Best-effort: when the platform refuses, the failure goes to the log and the returned inhibitor does nothing.
    /// <paramref name="reason"/> shows in <c>pmset -g assertions</c> and <c>systemd-inhibit --list</c>.
    /// </summary>
    public static SleepInhibitor Acquire(string reason)
    {
        var inhibitor = new SleepInhibitor();
        try
        {
            if (OperatingSystem.IsMacOS())
                inhibitor._assertion = MacAssertion(reason);
            else if (OperatingSystem.IsLinux())
                inhibitor._inhibit = LinuxInhibit(reason);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Can't keep the computer awake: {ex.Message}");
        }
        return inhibitor;
    }

    public void Dispose()
    {
        if (_assertion is { } id)
            IOPMAssertionRelease(id);
        _assertion = null;
        // systemd-inhibit holds its lock until cat exits, and cat exits when its stdin closes.
        _inhibit?.StandardInput.Close();
        _inhibit?.Dispose();
        _inhibit = null;
    }

    private static uint? MacAssertion(string reason)
    {
        var type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", CFStringEncodingUtf8);
        var name = CFStringCreateWithCString(0, reason, CFStringEncodingUtf8);
        try
        {
            var result = IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out var id);
            if (result == 0)
                return id;
            AppLog.Write($"Can't keep the computer awake: IOPMAssertionCreateWithName returned 0x{result:x8}");
            return null;
        }
        finally
        {
            CFRelease(name);
            CFRelease(type);
        }
    }

    private static Process? LinuxInhibit(string reason)
    {
        var systemdInhibit = Executables.Find("systemd-inhibit");
        if (systemdInhibit is null)
        {
            AppLog.Write("Can't keep the computer awake: systemd-inhibit not found");
            return null;
        }
        // cat reads the pipe below, so the lock also goes away when this process dies.
        return Process.Start(new ProcessStartInfo(systemdInhibit)
        {
            ArgumentList = { "--what=idle:sleep", "--mode=block", $"--who={AppPaths.AppName}", $"--why={reason}", "cat" },
            UseShellExecute = false,
            RedirectStandardInput = true,
        });
    }

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint id);

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionRelease(uint id);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
