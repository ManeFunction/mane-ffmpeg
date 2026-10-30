using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Updates;

/// <summary>A downloaded and unpacked release, waiting to replace the installed copy.</summary>
/// <param name="Staged">The new "Mane FFmpeg.app" (macOS), or the unpacked "mane-ffmpeg" folder with app/ and install.sh (Linux).</param>
public sealed record PreparedUpdate(Version Version, string Staged);

/// <summary>
/// Downloads a release in the form the release workflow packs it (a zipped .app on macOS, a tarball on Linux) and
/// swaps it in for the running copy. The swap runs in a small shell script that waits for the app to quit first, so
/// nothing is replaced while it runs, and puts the old copy back if the new one can't be moved in.
/// </summary>
public static partial class UpdateInstaller
{
    private const string MacZipBundle = "Mane FFmpeg.app";
    private const string LinuxFolder = "mane-ffmpeg";
    private const string LinuxAppFolder = "app";

    /// <summary>How long the script waits for the app to quit before it gives up (in 0.1 s steps).</summary>
    private const int QuitWaitSteps = 600;

    public static string WorkDirectory => Path.Combine(AppPaths.CacheDirectory, "update");

    /// <summary>The script's output, next to app.log.</summary>
    public static string LogPath => Path.Combine(Path.GetDirectoryName(AppLog.FilePath)!, "update.log");

    /// <summary>The release workflow's runtime identifier for this machine, or null where there are no releases.</summary>
    public static string? RuntimeId
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            return arch is null ? null
                : OperatingSystem.IsMacOS() ? $"osx-{arch}"
                : OperatingSystem.IsLinux() ? $"linux-{arch}"
                : null;
        }
    }

    /// <summary>The release asset for this machine, named like the release workflow names it.</summary>
    public static string? AssetName(Version version) => RuntimeId is { } rid
        ? $"mane-ffmpeg-{version.ToString(3)}-{rid}{(OperatingSystem.IsMacOS() ? ".zip" : ".tar.gz")}"
        : null;

    /// <summary>
    /// What an update replaces: the .app bundle this process runs from (macOS) or the app/ folder of the Linux bundle.
    /// Null for anything else, such as <c>dotnet run</c>.
    /// </summary>
    public static string? InstalledPath(string? processPath = null)
    {
        processPath ??= Environment.ProcessPath;
        if (processPath is null || Path.GetFileNameWithoutExtension(processPath) == "dotnet")
            return null;
        var folder = Path.GetDirectoryName(processPath);
        if (OperatingSystem.IsMacOS())
        {
            var bundle = Path.GetDirectoryName(Path.GetDirectoryName(folder));
            return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(bundle, "Contents", "Info.plist")) ? bundle : null;
        }
        return OperatingSystem.IsLinux() && Path.GetFileName(folder) == LinuxAppFolder ? folder : null;
    }

    /// <summary>Why the copy at <paramref name="installed"/> can't replace itself, or null when it can.</summary>
    public static string? CannotInstallReason(string? installed)
    {
        if (installed is null)
            return "This copy doesn't run from a release package, so it can't update itself.";
        if (OperatingSystem.IsMacOS() && installed.Contains("/AppTranslocation/", StringComparison.Ordinal))
            return "macOS runs this copy from a read-only location. Move Mane FFmpeg to Applications and open it from there to update it here.";
        var parent = Path.GetDirectoryName(installed)!;
        return IsWritable(parent) ? null : $"{parent} isn't writable, so the update can't be put in place.";
    }

    /// <summary>The Linux bundle's install.sh installs here; updating this copy runs the new install.sh.</summary>
    private static string StandardLinuxInstall =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "opt", LinuxFolder, LinuxAppFolder);

    /// <summary>Removes leftovers of an update that was downloaded but never installed.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(WorkDirectory))
                Directory.Delete(WorkDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Update: couldn't remove {WorkDirectory}: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads <paramref name="asset"/>, checks it against GitHub's SHA-256 digest and unpacks it.
    /// <paramref name="progress"/> gets the downloaded fraction (0–1).
    /// </summary>
    public static async Task<PreparedUpdate> PrepareAsync(HttpClient http, ReleaseAsset asset, Version version,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        CleanUp();
        var work = Directory.CreateDirectory(WorkDirectory).FullName;
        var archive = Path.Combine(work, Path.GetFileName(asset.Name));
        await DownloadAsync(http, asset, archive, progress, ct);

        var unpacked = Directory.CreateDirectory(Path.Combine(work, "new")).FullName;
        var command = OperatingSystem.IsMacOS()
            // ditto keeps the bundle's symlinks, permissions and signature.
            ? Cli.Wrap("/usr/bin/ditto").WithArguments(["-x", "-k", archive, unpacked])
            : Cli.Wrap(Executables.Find("tar") ?? "tar").WithArguments(["-xzf", archive, "-C", unpacked]);
        var result = await command.WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
            throw new IOException($"Couldn't unpack {asset.Name}: {result.StandardError.Trim()}");
        File.Delete(archive);

        var staged = OperatingSystem.IsMacOS() ? Path.Combine(unpacked, MacZipBundle) : Path.Combine(unpacked, LinuxFolder);
        var executable = OperatingSystem.IsMacOS()
            ? Path.Combine(staged, "Contents", "MacOS", "ManeFfmpeg")
            : Path.Combine(staged, LinuxAppFolder, "ManeFfmpeg");
        if (!File.Exists(executable))
            throw new IOException($"{asset.Name} doesn't hold the app where it should.");
        AppLog.Write($"Update: {version.ToString(3)} is ready in {staged}");
        return new PreparedUpdate(version, staged);
    }

    private static async Task DownloadAsync(HttpClient http, ReleaseAsset asset, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (total > 0)
                    progress?.Report(Math.Min(1, (double)done / total));
            }
        }
        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (asset.Sha256 is { } expected && !string.Equals(sha256, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The download of {asset.Name} is damaged (its checksum doesn't match). Try again later.");
    }

    /// <summary>
    /// Starts the script that replaces <paramref name="installed"/> with <paramref name="update"/> once every process in
    /// <paramref name="waitFor"/> has exited. With <paramref name="relaunch"/> it then starts the app again, opening
    /// <paramref name="reopen"/> (project files). Returns right away: the caller quits the app next.
    /// </summary>
    public static void Apply(PreparedUpdate update, string installed, IReadOnlyList<int> waitFor, bool relaunch, IReadOnlyList<string> reopen)
    {
        var plan = new ApplyPlan(OperatingSystem.IsMacOS(), installed, update.Staged, update.Version, waitFor, relaunch, reopen,
            RunInstaller: OperatingSystem.IsLinux() && Path.GetFullPath(installed) == StandardLinuxInstall,
            Log: LogPath, CleanUp: WorkDirectory);
        var script = WriteApplyScript(WorkDirectory, plan);
        AppLog.Write($"Update: installing {update.Version.ToString(3)} after quitting (relaunch: {relaunch}); log in {LogPath}");
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add(script);
        using var _ = Process.Start(start) ?? throw new IOException("Couldn't start the update script.");
    }

    /// <param name="MacOS">Whether <see cref="Installed"/> is an app bundle (else the Linux app/ folder).</param>
    /// <param name="RunInstaller">Linux: install with the new bundle's install.sh, which also refreshes the menu entry.</param>
    /// <param name="CleanUp">A folder to delete at the end (the downloaded update).</param>
    internal sealed record ApplyPlan(bool MacOS, string Installed, string Staged, Version Version, IReadOnlyList<int> WaitFor,
        bool Relaunch, IReadOnlyList<string> Reopen, bool RunInstaller, string Log, string? CleanUp);

    internal static string WriteApplyScript(string directory, ApplyPlan plan)
    {
        Directory.CreateDirectory(directory);
        var installed = Path.GetFullPath(plan.Installed).TrimEnd('/');
        var name = Path.GetFileName(installed);
        var sibling = Path.GetDirectoryName(installed)!;
        // Next to the installed copy, so moving it in place is a rename on the same disk.
        var incoming = Path.Combine(sibling, $".{name}.new");
        var backup = Path.Combine(sibling, $".{name}.old");
        var reopen = string.Join(' ', plan.Reopen.Select(Quote));

        var copyIn = plan.MacOS
            ? "/usr/bin/ditto \"$staged\" \"$incoming\""
            : "cp -R \"$staged/app\" \"$incoming\"";
        var install = plan.RunInstaller
            ? """
                rm -rf "$backup"
                mv "$installed" "$backup" || { echo "Couldn't move the old version aside."; return 1; }
                if ! "$staged/install.sh"; then
                  echo "install.sh failed; restoring the old version."
                  rm -rf "$installed"
                  mv "$backup" "$installed"
                  return 1
                fi
                rm -rf "$backup"
              """
            : $$"""
                rm -rf "$incoming" "$backup"
                if ! {{copyIn}}; then
                  echo "Couldn't copy the new version next to the old one."
                  rm -rf "$incoming"
                  return 1
                fi
                mv "$installed" "$backup" || { echo "Couldn't move the old version aside."; rm -rf "$incoming"; return 1; }
                if ! mv "$incoming" "$installed"; then
                  echo "Couldn't put the new version in place; restoring the old one."
                  mv "$backup" "$installed"
                  return 1
                fi
                rm -rf "$backup"
              """;
        var afterInstall = plan.MacOS ? "xattr -dr com.apple.quarantine \"$installed\" 2>/dev/null || true" : ":";
        var relaunch = !plan.Relaunch ? ""
            : plan.MacOS
                ? plan.Reopen.Count > 0 ? $"open -a \"$installed\" {reopen}" : "open \"$installed\""
                : $"nohup \"$installed/ManeFfmpeg\" {reopen} >/dev/null 2>&1 &";

        var script = Path.Combine(directory, "apply-update.sh");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            # Written by Mane FFmpeg: installs {{plan.Version.ToString(3)}} once the app has quit.
            mkdir -p {{Quote(Path.GetDirectoryName(plan.Log)!)}}
            exec >>{{Quote(plan.Log)}} 2>&1
            installed={{Quote(installed)}}
            staged={{Quote(plan.Staged)}}
            incoming={{Quote(incoming)}}
            backup={{Quote(backup)}}
            echo "$(date '+%Y-%m-%d %H:%M:%S') Installing {{plan.Version.ToString(3)}} into $installed"

            for pid in {{string.Join(' ', plan.WaitFor)}}; do
              steps=0
              while kill -0 "$pid" 2>/dev/null; do
                steps=$((steps + 1))
                if [ "$steps" -gt {{QuitWaitSteps}} ]; then
                  echo "The app (pid $pid) is still running; not updating."
                  exit 1
                fi
                sleep 0.1
              done
            done

            install_update() {
            {{install}}
              {{afterInstall}}
            }

            if install_update; then
              echo "Installed."
            else
              echo "The update failed; the old version stays."
            fi
            {{(plan.CleanUp is { } cleanUp ? $"rm -rf {Quote(cleanUp)}" : "")}}
            {{relaunch}}

            """.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>The process that started this one (Program's relaunch on macOS keeps it waiting until this one quits).</summary>
    public static int ParentProcessId() => GetParentProcessId();

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int GetParentProcessId();

    private static bool IsWritable(string directory)
    {
        var probe = Path.Combine(directory, $".mane-ffmpeg-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
