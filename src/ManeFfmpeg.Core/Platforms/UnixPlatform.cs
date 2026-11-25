using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CliWrap;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms;

/// <summary>What macOS and Linux share: POSIX signals, <c>/bin/sh</c> scripts and the usual Unix install folders.</summary>
[UnsupportedOSPlatform("windows")]
public abstract partial class UnixPlatform : IPlatform
{
    /// <summary>How long the update script waits for the app to quit before it gives up (in 0.1 s steps).</summary>
    private const int QuitWaitSteps = 600;

    /// <summary>
    /// Runs the program ($0) as a child of sh instead of this process, and prints its pid on stdout. A stopped child
    /// of this process would hang .NET on macOS: waitid reports it as exited, so the runtime's SIGCHLD handler loops
    /// trying to reap it and holds the lock every Process.Start needs.
    /// </summary>
    private const string PausableWrapper = "\"$0\" \"$@\" & echo \"" + PidKey + "$!\"; wait $!";

    private const string PidKey = "mane_ffmpeg_pid=";

    protected static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public abstract PlatformKind Kind { get; }
    public abstract string RuntimePrefix { get; }
    public abstract CommandKey CommandKey { get; }
    public abstract bool HasGlobalAppMenu { get; }
    public abstract bool OpensFilesThroughEvents { get; }
    public abstract string FileManagerName { get; }
    public abstract StringComparison PathComparison { get; }
    public string? FileDropBlockedReason => null;
    public abstract string ConfigDirectory { get; }
    public abstract string CacheDirectory { get; }
    public abstract string LogDirectory { get; }
    public abstract string FfmpegMissingHint { get; }
    public abstract string FfmpegSearchDescription { get; }
    public abstract IReadOnlyList<string> LibMpvLoaderNames { get; }
    public abstract string LibMpvInstallHint { get; }
    public abstract string LibMpvSearchDescription { get; }
    public abstract bool CanRegisterProjectFiles { get; }
    public abstract string UpdateAssetExtension { get; }
    public abstract string UpdateStagedName { get; }
    public abstract IToolInstaller? ToolInstaller { get; }

    public abstract IEnumerable<string> LibMpvCandidates(string? explicitPath);
    public abstract Task NotifyAsync(string title, string body);
    public abstract IDisposable KeepAwake(string reason);
    public abstract Task<bool> IsProjectFileHandlerAsync();
    public abstract Task RegisterProjectFilesAsync();
    public abstract Task UnregisterProjectFilesAsync();
    public abstract void RevealInFileManager(string path);
    public abstract Task UnpackUpdateAsync(string archive, string folder, CancellationToken ct);
    public abstract string UpdateExecutableIn(string staged);
    public abstract string? UpdateInstalledPath(string processPath);

    public virtual IReadOnlyDictionary<string, string>? LibMpvRelaunchEnvironment(string libMpvPath) => null;

    public virtual IReadOnlyList<int> ProcessesToWaitFor(bool relaunched) => [Environment.ProcessId];

    public virtual string? UpdateCannotReplaceReason(string installed) => null;

    public virtual IQuickActions? QuickActions => null;

    public virtual bool TryChangeWindowFrame(nint handle, string? handleDescriptor, double topDown, double grow) => false;

    // Tools

    /// <summary>Install folders checked after PATH: GUI apps on macOS start with a minimal PATH.</summary>
    protected virtual IEnumerable<string> ExtraToolDirectories =>
    [
        "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/opt/local/bin", "/snap/bin",
        Path.Combine(Home, ".local", "bin"), Path.Combine(Home, "bin"),
    ];

    public IEnumerable<string> ToolSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(ExtraToolDirectories))
            if (seen.Add(dir))
                yield return dir;
    }

    public string ExecutableFileName(string tool) => tool;

    public string QuoteCommandArgument(string argument) =>
        argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || "-_./:=+,@%".Contains(c))
            ? argument
            : Quote(argument);

    public string CommandLinePrefix => "";

    /// <summary>Quotes <paramref name="value"/> for sh, always.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>Makes a script runnable by its owner (and, with <paramref name="everyone"/>, by everyone).</summary>
    protected static void MakeExecutable(string path, bool everyone = false) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | (everyone ? UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute : 0));

    // Running and pausing

    public CommandTask<CommandResult> StartPausable(string executable, IReadOnlyList<string> arguments, Action<int> started,
        Action<string> output, Action<string> error, CancellationToken ct) =>
        Cli.Wrap("/bin/sh")
            .WithArguments(["-c", PausableWrapper, executable, .. arguments])
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                if (line.StartsWith(PidKey, StringComparison.Ordinal))
                    started(int.Parse(line.AsSpan(PidKey.Length), CultureInfo.InvariantCulture));
                else
                    output(line);
            }))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(error))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

    /// <summary>kill(2) signal numbers: macOS uses the BSD ones.</summary>
    protected abstract int StopSignal { get; }

    protected abstract int ContinueSignal { get; }

    public IFrozenProcess Freeze(int pid)
    {
        Signal(pid, StopSignal);
        return new StoppedProcess(this, pid, StartGuard(pid));
    }

    /// <summary>
    /// A stopped process outlives a crash or a force quit and never wakes up (a launched app's process group is already
    /// orphaned, so the kernel doesn't continue it either). The guard reads a pipe from this process: a line means
    /// "resumed", the end of the file means this process is gone, and then it kills the stopped one.
    /// </summary>
    private static Process? StartGuard(int pid)
    {
        try
        {
            return Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", "read -r line || kill -KILL \"$0\"", pid.ToString(CultureInfo.InvariantCulture) },
                UseShellExecute = false,
                RedirectStandardInput = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Write($"Can't watch the paused process: {ex.Message}");
            return null;
        }
    }

    private sealed class StoppedProcess(UnixPlatform platform, int pid, Process? guard) : IFrozenProcess
    {
        private Process? _guard = guard;

        public void Resume()
        {
            Signal(pid, platform.ContinueSignal);
            Release();
        }

        public void Release()
        {
            if (_guard is null)
                return;
            try
            {
                _guard.StandardInput.WriteLine("resumed");
                _guard.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            _guard.Dispose();
            _guard = null;
        }
    }

    private static void Signal(int pid, int signal)
    {
        if (Kill(pid, signal) != 0)
            AppLog.Write($"Can't send signal {signal} to pid {pid}: errno {Marshal.GetLastPInvokeError()}");
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "getppid")]
    protected static partial int GetParentProcessId();

    // Updates

    /// <summary>Whether updating the copy at <paramref name="installed"/> runs the new package's own installer.</summary>
    protected virtual bool RunsInstaller(string installed) => false;

    /// <summary>Shell: copies the staged package's app from <c>"$staged"</c> to <c>"$incoming"</c>.</summary>
    protected abstract string CopyInScript { get; }

    /// <summary>Shell: run on <c>"$installed"</c> once the new copy is in place.</summary>
    protected virtual string AfterInstallScript => ":";

    /// <summary>Shell: starts the installed app again, opening <paramref name="reopen"/> (already quoted).</summary>
    protected abstract string RelaunchScript(string reopen);

    public string WriteUpdateScript(string directory, UpdatePlan plan) => WriteUpdateScript(directory, plan, RunsInstaller(plan.Installed));

    /// <param name="runInstaller">Install with the new package's install.sh instead of copying it in.</param>
    internal string WriteUpdateScript(string directory, UpdatePlan plan, bool runInstaller)
    {
        Directory.CreateDirectory(directory);
        var installed = Path.GetFullPath(plan.Installed).TrimEnd('/');
        var name = Path.GetFileName(installed);
        var sibling = Path.GetDirectoryName(installed)!;
        // Next to the installed copy, so moving it in place is a rename on the same disk.
        var incoming = Path.Combine(sibling, $".{name}.new");
        var backup = Path.Combine(sibling, $".{name}.old");
        var reopen = string.Join(' ', plan.Reopen.Select(Quote));

        var install = runInstaller
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
                if ! {{CopyInScript}}; then
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
        var relaunch = plan.Relaunch ? RelaunchScript(reopen) : "";

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
              {{AfterInstallScript}}
            }

            if install_update; then
              echo "Installed."
            else
              echo "The update failed; the old version stays."
            fi
            {{(plan.CleanUp is { } cleanUp ? $"rm -rf {Quote(cleanUp)}" : "")}}
            {{relaunch}}

            """.ReplaceLineEndings("\n"));
        MakeExecutable(script);
        return script;
    }

    public void StartUpdateScript(string script)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add(script);
        using var _ = Process.Start(start) ?? throw new IOException("Couldn't start the update script.");
    }
}
