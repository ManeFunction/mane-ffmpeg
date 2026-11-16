using CliWrap;
using CliWrap.Buffered;

namespace ManeFfmpeg.Core.Platforms.Mac;

/// <summary>Updates: the release is a zipped "Mane FFmpeg.app", and an update replaces the bundle the app runs from.</summary>
public sealed partial class MacPlatform
{
    public override string UpdateAssetExtension => ".zip";
    public override string UpdateStagedName => "Mane FFmpeg.app";

    // ditto keeps the bundle's symlinks, permissions and signature.
    public override async Task UnpackUpdateAsync(string archive, string folder, CancellationToken ct)
    {
        var result = await Cli.Wrap("/usr/bin/ditto").WithArguments(["-x", "-k", archive, folder])
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
            throw new IOException($"Couldn't unpack {Path.GetFileName(archive)}: {result.StandardError.Trim()}");
    }

    public override string UpdateExecutableIn(string staged) => Path.Combine(staged, "Contents", "MacOS", "ManeFfmpeg");

    /// <summary>The .app bundle holding Contents/MacOS/the executable.</summary>
    public override string? UpdateInstalledPath(string processPath) => BundleAround(processPath);

    internal static string? BundleAround(string? processPath)
    {
        var bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(processPath)));
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(bundle, "Contents", "Info.plist")) ? bundle : null;
    }

    public override string? UpdateCannotReplaceReason(string installed) =>
        installed.Contains("/AppTranslocation/", StringComparison.Ordinal)
            ? "macOS runs this copy from a read-only location. Move Mane FFmpeg to Applications and open it from there to update it here."
            : null;

    protected override string CopyInScript => "/usr/bin/ditto \"$staged\" \"$incoming\"";
    protected override string AfterInstallScript => "xattr -dr com.apple.quarantine \"$installed\" 2>/dev/null || true";
    protected override string RelaunchScript(string reopen) => reopen.Length > 0 ? $"open -a \"$installed\" {reopen}" : "open \"$installed\"";
}
