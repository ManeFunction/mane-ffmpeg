using CliWrap;

namespace ManeFfmpeg.Core.Updates;

/// <summary>
/// How the release workflow packs the app for one platform, and how that package is installed: the asset's name, how it
/// unpacks, where the app sits in it and in an installed copy, and the shell steps that put a new copy in place.
/// <see cref="MacApp"/> and <see cref="LinuxTarball"/> are the two kinds; <see cref="Current"/> is this machine's.
/// </summary>
public abstract class ReleasePackage
{
    public static ReleasePackage MacApp { get; } = new MacAppPackage();

    public static ReleasePackage LinuxTarball { get; } = new LinuxTarballPackage();

    /// <summary>This machine's kind of package, or null where there are no releases.</summary>
    public static ReleasePackage? Current => OperatingSystem.IsMacOS() ? MacApp : OperatingSystem.IsLinux() ? LinuxTarball : null;

    /// <summary>The platform part of the release workflow's runtime identifiers ("osx" in "osx-arm64").</summary>
    public abstract string Platform { get; }

    protected abstract string ArchiveExtension { get; }

    /// <summary>The release asset, named like the release workflow names it.</summary>
    public string AssetName(Version version, string runtimeId) => $"mane-ffmpeg-{version.ToString(3)}-{runtimeId}{ArchiveExtension}";

    /// <summary>The command that unpacks <paramref name="archive"/> into <paramref name="folder"/>.</summary>
    public abstract Command Unpack(string archive, string folder);

    /// <summary>The package's top entry once unpacked: what an update stages.</summary>
    public abstract string StagedName { get; }

    /// <summary>Where the app's executable is in a staged package.</summary>
    public abstract string ExecutableIn(string staged);

    /// <summary>What an update replaces when the app runs from <paramref name="processPath"/>; null when it isn't a package.</summary>
    public abstract string? InstalledPath(string processPath);

    /// <summary>Why the platform won't let the copy at <paramref name="installed"/> be replaced, or null.</summary>
    public virtual string? CannotReplaceReason(string installed) => null;

    /// <summary>Whether updating the copy at <paramref name="installed"/> runs the new package's own installer.</summary>
    public virtual bool RunsInstaller(string installed) => false;

    /// <summary>Shell: copies the staged package's app from <c>"$staged"</c> to <c>"$incoming"</c>.</summary>
    public abstract string CopyInScript { get; }

    /// <summary>Shell: run on <c>"$installed"</c> once the new copy is in place.</summary>
    public virtual string AfterInstallScript => ":";

    /// <summary>Shell: starts the installed app again, opening <paramref name="reopen"/> (already quoted).</summary>
    public abstract string RelaunchScript(string reopen);

    private sealed class MacAppPackage : ReleasePackage
    {
        public override string Platform => "osx";
        protected override string ArchiveExtension => ".zip";
        public override string StagedName => "Mane FFmpeg.app";

        // ditto keeps the bundle's symlinks, permissions and signature.
        public override Command Unpack(string archive, string folder) =>
            Cli.Wrap("/usr/bin/ditto").WithArguments(["-x", "-k", archive, folder]);

        public override string ExecutableIn(string staged) => Path.Combine(staged, "Contents", "MacOS", "ManeFfmpeg");

        /// <summary>The .app bundle holding Contents/MacOS/the executable.</summary>
        public override string? InstalledPath(string processPath)
        {
            var bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(processPath)));
            return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(bundle, "Contents", "Info.plist")) ? bundle : null;
        }

        public override string? CannotReplaceReason(string installed) =>
            installed.Contains("/AppTranslocation/", StringComparison.Ordinal)
                ? "macOS runs this copy from a read-only location. Move Mane FFmpeg to Applications and open it from there to update it here."
                : null;

        public override string CopyInScript => "/usr/bin/ditto \"$staged\" \"$incoming\"";
        public override string AfterInstallScript => "xattr -dr com.apple.quarantine \"$installed\" 2>/dev/null || true";
        public override string RelaunchScript(string reopen) => reopen.Length > 0 ? $"open -a \"$installed\" {reopen}" : "open \"$installed\"";
    }

    private sealed class LinuxTarballPackage : ReleasePackage
    {
        private const string Folder = "mane-ffmpeg";
        private const string AppFolder = "app";

        public override string Platform => "linux";
        protected override string ArchiveExtension => ".tar.gz";
        public override string StagedName => Folder;

        public override Command Unpack(string archive, string folder) =>
            Cli.Wrap(Executables.Find("tar") ?? "tar").WithArguments(["-xzf", archive, "-C", folder]);

        public override string ExecutableIn(string staged) => Path.Combine(staged, AppFolder, "ManeFfmpeg");

        /// <summary>The bundle's app/ folder.</summary>
        public override string? InstalledPath(string processPath)
        {
            var folder = Path.GetDirectoryName(processPath);
            return Path.GetFileName(folder) == AppFolder ? folder : null;
        }

        /// <summary>The bundle's install.sh installs here; updating this copy runs the new install.sh (it also refreshes the menu entry).</summary>
        public override bool RunsInstaller(string installed) =>
            Path.GetFullPath(installed) == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "opt", Folder, AppFolder);

        public override string CopyInScript => "cp -R \"$staged/app\" \"$incoming\"";
        public override string RelaunchScript(string reopen) => $"nohup \"$installed/ManeFfmpeg\" {reopen} >/dev/null 2>&1 &";
    }
}
