using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Desktop;

/// <summary>
/// Makes this app the one that opens .mfvp projects on double-click. On macOS the app bundle declares the type
/// (scripts/package-macos.sh), and this sets the Launch Services default handler for it. On Linux it installs the
/// shared-mime-info type and the default application for the current user, like the packaged install.sh.
/// </summary>
public static partial class ProjectFileAssociation
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const uint CFStringEncodingUtf8 = 0x08000100;
    private const uint RolesAll = 0xFFFFFFFF;
    private const string ContentType = "com.manefunction.mfvp";
    /// <summary>Opens .mfvp files after unregistering when nothing else opens JSON.</summary>
    private const string FallbackHandler = "com.apple.TextEdit";

    private const string MimeType = "application/x-mane-ffmpeg-project";
    private const string DesktopId = "mane-ffmpeg.desktop";

    private static readonly Lazy<string?> BundleId = new(FindBundleId);

    /// <summary>Whether this copy can register itself: the packaged app, not <c>dotnet ManeFfmpeg.dll</c>.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsMacOS() ? BundleId.Value is not null
        : OperatingSystem.IsLinux() && LinuxExecutable() is not null;

    public static async Task<bool> IsRegisteredAsync()
    {
        try
        {
            if (!IsSupported)
                return false;
            if (OperatingSystem.IsMacOS())
                return string.Equals(MacDefaultHandler(ContentType), BundleId.Value, StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(MimePackage) || !File.Exists(DesktopFile))
                return false;
            // Without xdg-mime the installed desktop file is the only handler, so it's the default.
            if (Executables.Find("xdg-mime") is not { } xdgMime)
                return true;
            var result = await Cli.Wrap(xdgMime).WithArguments(["query", "default", MimeType])
                .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
            return result.StandardOutput.Trim() == DesktopId;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Couldn't check the .{ProjectFile.Extension} association: {ex.Message}");
            return false;
        }
    }

    public static async Task RegisterAsync()
    {
        if (!IsSupported)
            throw new InvalidOperationException("Only the packaged app can be registered.");
        AppLog.Write($"Registering .{ProjectFile.Extension} files");
        if (OperatingSystem.IsMacOS())
        {
            // Copied apps aren't known to Launch Services until Finder sees them, and their type declaration with them.
            MacRegisterBundle(MacBundlePath()!);
            MacSetDefaultHandler(ContentType, BundleId.Value!);
            return;
        }
        AtomicFile.WriteAllText(MimePackage, MimeXml);
        // install.sh puts one in place for the installed copy; keep it.
        if (!File.Exists(DesktopFile))
            AtomicFile.WriteAllText(DesktopFile, DesktopEntry(LinuxExecutable()!));
        await RunAsync("update-mime-database", Path.Combine(DataHome, "mime"));
        await RunAsync("update-desktop-database", Path.GetDirectoryName(DesktopFile)!);
        await RunAsync("xdg-mime", "default", DesktopId, MimeType);
    }

    /// <summary>Hands .mfvp files back to whatever opens JSON.</summary>
    public static async Task UnregisterAsync()
    {
        if (!IsSupported)
            throw new InvalidOperationException("Only the packaged app can be registered.");
        AppLog.Write($"Unregistering .{ProjectFile.Extension} files");
        if (OperatingSystem.IsMacOS())
        {
            // The bundle's claim stays in its Info.plist, so an explicit default is the only way to override it.
            var json = MacDefaultHandler("public.json");
            MacSetDefaultHandler(ContentType,
                json is null || string.Equals(json, BundleId.Value, StringComparison.OrdinalIgnoreCase) ? FallbackHandler : json);
            return;
        }
        // Without the type, .mfvp files are plain JSON again; the desktop file stays as the menu entry.
        File.Delete(MimePackage);
        await RunAsync("update-mime-database", Path.Combine(DataHome, "mime"));
        if (File.Exists(MimeAppsList))
            AtomicFile.WriteAllText(MimeAppsList, WithoutAssociations(File.ReadAllText(MimeAppsList), MimeType));
    }

    // macOS

    /// <summary>The app bundle around the executable: &lt;bundle&gt;.app/Contents/MacOS/ManeFfmpeg.</summary>
    private static string? MacBundlePath()
    {
        var bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Environment.ProcessPath)));
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(bundle, "Contents", "Info.plist")) ? bundle : null;
    }

    private static string? FindBundleId()
    {
        try
        {
            return OperatingSystem.IsMacOS() && MacBundlePath() is not null
                ? ToManaged(CFBundleGetIdentifier(CFBundleGetMainBundle()))
                : null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Couldn't read the bundle identifier: {ex.Message}");
            return null;
        }
    }

    private static string? MacDefaultHandler(string contentType)
    {
        var type = CFStringCreateWithCString(0, contentType, CFStringEncodingUtf8);
        try
        {
            var handler = LSCopyDefaultRoleHandlerForContentType(type, RolesAll);
            try
            {
                return ToManaged(handler);
            }
            finally
            {
                if (handler != 0)
                    CFRelease(handler);
            }
        }
        finally
        {
            CFRelease(type);
        }
    }

    private static void MacSetDefaultHandler(string contentType, string bundleId)
    {
        var type = CFStringCreateWithCString(0, contentType, CFStringEncodingUtf8);
        var handler = CFStringCreateWithCString(0, bundleId, CFStringEncodingUtf8);
        try
        {
            var status = LSSetDefaultRoleHandlerForContentType(type, RolesAll, handler);
            if (status != 0)
                throw new InvalidOperationException($"Launch Services refused to set {bundleId} as the default (error {status}).");
        }
        finally
        {
            CFRelease(handler);
            CFRelease(type);
        }
    }

    private static void MacRegisterBundle(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var url = CFURLCreateFromFileSystemRepresentation(0, bytes, bytes.Length, true);
        try
        {
            var status = LSRegisterURL(url, true);
            if (status != 0)
                throw new InvalidOperationException($"Launch Services refused to register {path} (error {status}).");
        }
        finally
        {
            CFRelease(url);
        }
    }

    private static string? ToManaged(nint cfString)
    {
        if (cfString == 0)
            return null;
        var buffer = new byte[1024];
        return CFStringGetCString(cfString, buffer, buffer.Length, CFStringEncodingUtf8)
            ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0))
            : null;
    }

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint text, [Out] byte[] buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte[] path, nint length,
        [MarshalAs(UnmanagedType.U1)] bool isDirectory);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFBundleGetMainBundle();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFBundleGetIdentifier(nint bundle);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    // Deprecated since macOS 12 in favor of NSWorkspace's block-based API, but still working.
    [LibraryImport(CoreServices)]
    private static partial nint LSCopyDefaultRoleHandlerForContentType(nint contentType, uint roles);

    [LibraryImport(CoreServices)]
    private static partial int LSSetDefaultRoleHandlerForContentType(nint contentType, uint roles, nint bundleId);

    [LibraryImport(CoreServices)]
    private static partial int LSRegisterURL(nint url, [MarshalAs(UnmanagedType.U1)] bool update);

    // Linux

    private static string DataHome => AppPaths.Xdg("XDG_DATA_HOME", Path.Combine(".local", "share"));

    private static string MimePackage => Path.Combine(DataHome, "mime", "packages", "mane-ffmpeg-mime.xml");

    private static string DesktopFile => Path.Combine(DataHome, "applications", DesktopId);

    private static string MimeAppsList => Path.Combine(AppPaths.Xdg("XDG_CONFIG_HOME", ".config"), "mimeapps.list");

    private static string? LinuxExecutable() =>
        Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path) != "dotnet" ? path : null;

    // Same as the one scripts/package-linux.sh installs.
    private const string MimeXml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
          <mime-type type="{MimeType}">
            <comment>Mane FFmpeg project</comment>
            <sub-class-of type="application/json"/>
            <glob pattern="*.mfvp"/>
          </mime-type>
        </mime-info>

        """;

    internal static string DesktopEntry(string executable) => $"""
        [Desktop Entry]
        Type=Application
        Name={AppPaths.AppName}
        Comment=Cut, reorder and export videos with your system ffmpeg
        Exec={ExecArgument(executable)} %F
        Icon=mane-ffmpeg
        Terminal=false
        Categories=AudioVideo;Video;AudioVideoEditing;
        MimeType={MimeType};

        """;

    /// <summary>
    /// Quotes a path for a desktop entry's Exec key: " ` $ \ are escaped inside the quotes, then backslashes once
    /// more for the string value, and % is doubled.
    /// </summary>
    internal static string ExecArgument(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\')
                quoted.Append('\\');
            quoted.Append(c);
        }
        return quoted.Append('"').ToString().Replace("\\", @"\\").Replace("%", "%%");
    }

    /// <summary>A mimeapps.list without the lines that set handlers for <paramref name="mimeType"/>.</summary>
    internal static string WithoutAssociations(string mimeApps, string mimeType) =>
        string.Join('\n', mimeApps.Split('\n').Where(line => !line.TrimStart().StartsWith(mimeType + "=", StringComparison.Ordinal)));

    private static async Task RunAsync(string tool, params string[] arguments)
    {
        if (Executables.Find(tool) is not { } path)
        {
            AppLog.Write($"{tool} not found; skipped");
            return;
        }
        var result = await Cli.Wrap(path).WithArguments(arguments)
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
        if (result.ExitCode != 0)
            AppLog.Write($"{tool} exited with {result.ExitCode}: {result.StandardError.Trim()}");
    }
}
