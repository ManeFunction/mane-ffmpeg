using System.Text;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms.Linux;

/// <summary>
/// The .mfvp association: installs the shared-mime-info type and the default application for the current user, like the
/// packaged install.sh.
/// </summary>
public sealed partial class LinuxPlatform
{
    private const string MimeType = "application/x-mane-ffmpeg-project";
    private const string DesktopId = "mane-ffmpeg.desktop";

    private static string DataHome => Xdg("XDG_DATA_HOME", Path.Combine(".local", "share"));

    private static string MimePackage => Path.Combine(DataHome, "mime", "packages", "mane-ffmpeg-mime.xml");

    private static string DesktopFile => Path.Combine(DataHome, "applications", DesktopId);

    private static string MimeAppsList => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "mimeapps.list");

    public override bool CanRegisterProjectFiles => Platform.RunningExecutable is not null;

    public override async Task<bool> IsProjectFileHandlerAsync()
    {
        if (!CanRegisterProjectFiles || !File.Exists(MimePackage) || !File.Exists(DesktopFile))
            return false;
        // Without xdg-mime the installed desktop file is the only handler, so it's the default.
        if (Executables.Find("xdg-mime") is not { } xdgMime)
            return true;
        var result = await Cli.Wrap(xdgMime).WithArguments(["query", "default", MimeType])
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
        return result.StandardOutput.Trim() == DesktopId;
    }

    public override async Task RegisterProjectFilesAsync()
    {
        AtomicFile.WriteAllText(MimePackage, MimeXml);
        // install.sh puts one in place for the installed copy; keep it.
        if (!File.Exists(DesktopFile))
            AtomicFile.WriteAllText(DesktopFile, DesktopEntry(Platform.RunningExecutable!));
        await RunAsync("update-mime-database", Path.Combine(DataHome, "mime"));
        await RunAsync("update-desktop-database", Path.GetDirectoryName(DesktopFile)!);
        await RunAsync("xdg-mime", "default", DesktopId, MimeType);
    }

    public override async Task UnregisterProjectFilesAsync()
    {
        // Without the type, .mfvp files are plain JSON again; the desktop file stays as the menu entry.
        File.Delete(MimePackage);
        await RunAsync("update-mime-database", Path.Combine(DataHome, "mime"));
        if (File.Exists(MimeAppsList))
            AtomicFile.WriteAllText(MimeAppsList, WithoutAssociations(File.ReadAllText(MimeAppsList), MimeType));
    }

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
