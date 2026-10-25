using CliWrap;

namespace ManeFfmpeg.Core;

/// <summary>
/// Installs missing tools with Homebrew (macOS). Homebrew's own installer asks for the user's password and to
/// press Return, so it runs in Terminal and reports back through a status file; <c>brew install</c> runs here.
/// </summary>
public static class Homebrew
{
    /// <summary>The installer behind the one-liner on brew.sh.</summary>
    public const string InstallerUrl = "https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh";

    private const string ScriptName = "install-homebrew.command";
    private const string StatusName = "install-homebrew.status";

    /// <summary>How long a stopped <c>brew install</c> gets to clean up after Ctrl-C before it's killed.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    /// <summary>The command brew.sh shows, for display.</summary>
    public static string InstallerCommand => $"/bin/bash -c \"$(curl -fsSL {InstallerUrl})\"";

    public static string? Find() => Executables.Find("brew");

    /// <summary>
    /// Writes a .command script into <paramref name="directory"/> that runs Homebrew's installer and then writes its exit
    /// code to the status file read by <see cref="ReadInstallerStatus"/>. Closing the window or pressing Ctrl-C records
    /// 129 or 130: a plain EXIT trap would record 0 for a closed window.
    /// </summary>
    public static string WriteInstallerScript(string directory, string installerUrl = InstallerUrl)
    {
        Directory.CreateDirectory(directory);
        var status = Path.Combine(directory, StatusName);
        File.Delete(status);
        var script = Path.Combine(directory, ScriptName);
        File.WriteAllText(script, $$"""
            #!/bin/bash
            # Written by Mane FFmpeg: runs Homebrew's installer and tells the app how it ended.
            status_file={{Quote(status)}}
            trap 'echo $? > "$status_file"' EXIT
            trap 'exit 129' HUP
            trap 'exit 130' INT
            trap 'exit 143' TERM
            clear
            echo "Installing Homebrew for Mane FFmpeg."
            echo "The installer asks for your Mac password and may install Apple's Command Line Tools."
            echo
            installer="$(curl -fsSL {{Quote(installerUrl)}})" || { echo "Couldn't download Homebrew's installer. Check your internet connection."; exit 1; }
            /bin/bash -c "$installer"
            code=$?
            echo
            if [ $code -eq 0 ]; then
              echo "Homebrew is installed. Go back to Mane FFmpeg to continue; you can close this window."
              echo "(The \"Next steps\" above are only needed to use brew in Terminal yourself.)"
            else
              echo "Homebrew's installer stopped (exit code $code)."
            fi
            exit $code

            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return script;
    }

    /// <summary>The installer's exit code once its script has ended, or null while it runs (or before it started).</summary>
    public static int? ReadInstallerStatus(string directory)
    {
        try
        {
            return int.TryParse(File.ReadAllText(Path.Combine(directory, StatusName)).Trim(), out var code) ? code : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Runs a .command script in the user's terminal app (Terminal, unless they picked another one).</summary>
    public static async Task OpenInTerminalAsync(string script)
    {
        var result = await Cli.Wrap("/usr/bin/open")
            .WithArguments([script])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();
        if (result.ExitCode != 0)
            throw new IOException($"open exited with code {result.ExitCode}.");
    }

    /// <summary>
    /// Runs <c>brew install</c> for <paramref name="formulae"/>, passing each line it prints (stdout and stderr, in
    /// order) to <paramref name="output"/>; returns brew's exit code. Cancelling interrupts brew like Ctrl-C, then kills it.
    /// </summary>
    public static async Task<int> InstallAsync(string brew, IReadOnlyList<string> formulae, Action<string> output, CancellationToken ct)
    {
        using var forceful = new CancellationTokenSource();
        // Disposed before `forceful`, so the callback never sees a disposed source.
        await using var _ = ct.Register(static state => ((CancellationTokenSource)state!).CancelAfter(StopGrace), forceful);
        // stderr goes into the stdout pipe, so errors stay next to the step that printed them. exec keeps brew the
        // process that gets the interrupt.
        var result = await Cli.Wrap("/bin/sh")
            .WithArguments(["-c", "exec \"$0\" \"$@\" 2>&1", brew, "install", .. formulae])
            .WithEnvironmentVariables(e => e.Set("HOMEBREW_NO_ENV_HINTS", "1"))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(output))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(forceful.Token, ct);
        return result.ExitCode;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
