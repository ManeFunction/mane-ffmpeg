using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Updates;

/// <summary>A downloaded and unpacked release, waiting to replace the installed copy.</summary>
/// <param name="Staged">The unpacked package (<see cref="IPlatform.UpdateStagedName"/>): the new "Mane FFmpeg.app" (macOS),
/// the "mane-ffmpeg" folder with app/ and install.sh (Linux), or the "Mane FFmpeg" folder (Windows).</param>
public sealed record PreparedUpdate(Version Version, string Staged);

/// <summary>
/// Downloads a release in the form the release workflow packs it for this platform and swaps it in for the running copy.
/// The swap runs in a small script (<see cref="IPlatform.WriteUpdateScript"/>) that waits for the app to quit first, so
/// nothing is replaced while it runs, and puts the old copy back if the new one can't be moved in.
/// </summary>
public static class UpdateInstaller
{
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
            return arch is not null ? $"{Platform.Current.RuntimePrefix}-{arch}" : null;
        }
    }

    /// <summary>The release asset for this machine, named like the release workflow names it.</summary>
    public static string? AssetName(Version version) =>
        RuntimeId is { } rid ? AssetName(Platform.Current, version, rid) : null;

    /// <summary>The release asset <paramref name="platform"/> downloads for <paramref name="runtimeId"/>.</summary>
    public static string AssetName(IPlatform platform, Version version, string runtimeId) =>
        $"mane-ffmpeg-{version.ToString(3)}-{runtimeId}{platform.UpdateAssetExtension}";

    /// <summary>
    /// What an update replaces: the .app bundle this process runs from (macOS), the app/ folder of the Linux bundle, or
    /// the Windows package folder. Null for anything else, such as <c>dotnet run</c>.
    /// </summary>
    public static string? InstalledPath(string? processPath = null)
    {
        return Platform.AppExecutable(processPath ?? Environment.ProcessPath) is { } executable
            ? Platform.Current.UpdateInstalledPath(executable)
            : null;
    }

    /// <summary>Why the copy at <paramref name="installed"/> can't replace itself, or null when it can.</summary>
    public static string? CannotInstallReason(string? installed)
    {
        if (installed is null)
            return "This copy doesn't run from a release package, so it can't update itself.";
        if (Platform.Current.UpdateCannotReplaceReason(installed) is { } reason)
            return reason;
        var parent = Path.GetDirectoryName(installed)!;
        return IsWritable(parent) ? null : $"{parent} isn't writable, so the update can't be put in place.";
    }

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
        var platform = Platform.Current;
        CleanUp();
        var work = Directory.CreateDirectory(WorkDirectory).FullName;
        var archive = Path.Combine(work, Path.GetFileName(asset.Name));
        await DownloadAsync(http, asset, archive, progress, ct);

        var unpacked = Directory.CreateDirectory(Path.Combine(work, "new")).FullName;
        await platform.UnpackUpdateAsync(archive, unpacked, ct);
        File.Delete(archive);

        var staged = Path.Combine(unpacked, platform.UpdateStagedName);
        if (!File.Exists(platform.UpdateExecutableIn(staged)))
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
        var plan = new UpdatePlan(installed, update.Staged, update.Version, waitFor, relaunch, reopen, LogPath, CleanUp: WorkDirectory);
        var script = Platform.Current.WriteUpdateScript(WorkDirectory, plan);
        AppLog.Write($"Update: installing {update.Version.ToString(3)} after quitting (relaunch: {relaunch}); log in {LogPath}");
        Platform.Current.StartUpdateScript(script);
    }

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
}
