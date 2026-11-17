using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CliWrap;
using ManeFfmpeg.Core.Persistence;
using Microsoft.Win32;

namespace ManeFfmpeg.Core.Platforms.Windows;

/// <summary>Toast notifications and the .mfvp association, both registered per user under HKCU.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPlatform
{
    // Notifications: a toast through Windows PowerShell's WinRT access, so the app needs no WinRT bindings. Windows shows
    // toasts from unpackaged apps under an AppUserModelID registered in HKCU, which gives them the app's name.

    internal const string AppUserModelId = "ManeFunction.ManeFfmpeg";

    internal const string ToastScript = """
        $ErrorActionPreference = 'Stop'
        $null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
        $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $texts = $xml.GetElementsByTagName('text')
        $null = $texts.Item(0).AppendChild($xml.CreateTextNode($env:MANE_FFMPEG_TOAST_TITLE))
        $null = $texts.Item(1).AppendChild($xml.CreateTextNode($env:MANE_FFMPEG_TOAST_BODY))
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:MANE_FFMPEG_TOAST_APP).Show($toast)
        """;

    /// <summary>Windows PowerShell 5.1, which can load WinRT types (PowerShell 7 can't).</summary>
    private static string WindowsPowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public async Task NotifyAsync(string title, string body)
    {
        try
        {
            RegisterAppUserModelId();
            // Title and body go in as environment variables, so nothing needs escaping.
            await Cli.Wrap(WindowsPowerShell)
                .WithArguments(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", EncodeCommand(ToastScript)])
                .WithEnvironmentVariables(e => e
                    .Set("MANE_FFMPEG_TOAST_TITLE", title)
                    .Set("MANE_FFMPEG_TOAST_BODY", body)
                    .Set("MANE_FFMPEG_TOAST_APP", AppUserModelId))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync();
        }
        catch (Exception)
        {
            // Notifications are best-effort; the export panel always shows the result.
        }
    }

    /// <summary>What <c>-EncodedCommand</c> takes: the script as base64 of its UTF-16LE bytes.</summary>
    internal static string EncodeCommand(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static void RegisterAppUserModelId()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
        if (key.GetValue("DisplayName") as string != AppPaths.AppName)
            key.SetValue("DisplayName", AppPaths.AppName);
    }

    // .mfvp files: a ProgID under HKCU\Software\Classes, the per-user part of HKEY_CLASSES_ROOT. If the user picked
    // another app in "Open with", Windows keeps their choice (it's protected), and that's fine.

    internal const string ProgId = "ManeFfmpeg.Project";
    private const string Extension = ".mfvp";
    private const string Classes = @"Software\Classes";
    private const int ShcneAssocChanged = 0x08000000;

    public bool CanRegisterProjectFiles => Platform.RunningExecutable is not null;

    /// <summary>The open command Explorer runs for <paramref name="executable"/>.</summary>
    internal static string OpenCommand(string executable) => $"\"{executable}\" \"%1\"";

    public Task<bool> IsProjectFileHandlerAsync()
    {
        if (Platform.RunningExecutable is not { } executable)
            return Task.FromResult(false);
        using var extension = Registry.CurrentUser.OpenSubKey($@"{Classes}\{Extension}");
        using var command = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ProgId}\shell\open\command");
        return Task.FromResult(extension?.GetValue(null) as string == ProgId
                               && string.Equals(command?.GetValue(null) as string, OpenCommand(executable), StringComparison.OrdinalIgnoreCase));
    }

    public Task RegisterProjectFilesAsync()
    {
        var executable = Platform.RunningExecutable!;
        using (var progId = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ProgId}"))
        {
            progId.SetValue(null, "Mane FFmpeg project");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{executable}\",0");
            using (var command = progId.CreateSubKey(@"shell\open\command"))
                command.SetValue(null, OpenCommand(executable));
        }
        using (var extension = Registry.CurrentUser.CreateSubKey($@"{Classes}\{Extension}"))
        {
            extension.SetValue(null, ProgId);
            extension.SetValue("Content Type", "application/json");
            using var openWith = extension.CreateSubKey("OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        SHChangeNotify(ShcneAssocChanged, 0, 0, 0);
        return Task.CompletedTask;
    }

    public Task UnregisterProjectFilesAsync()
    {
        // Without a handler of its own, Windows asks which app to use.
        using (var extension = Registry.CurrentUser.OpenSubKey($@"{Classes}\{Extension}", writable: true))
        {
            if (extension?.GetValue(null) as string == ProgId)
                extension.DeleteValue("", throwOnMissingValue: false);
            using var openWith = extension?.OpenSubKey("OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ProgId}", throwOnMissingSubKey: false);
        SHChangeNotify(ShcneAssocChanged, 0, 0, 0);
        return Task.CompletedTask;
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
