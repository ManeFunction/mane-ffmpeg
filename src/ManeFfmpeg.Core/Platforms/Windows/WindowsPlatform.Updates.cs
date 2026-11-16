using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;

namespace ManeFfmpeg.Core.Platforms.Windows;

/// <summary>
/// Updates: the release is a zip holding the "Mane FFmpeg" folder, and an update replaces the folder the app runs from.
/// The swap runs in Windows PowerShell after the app has quit, since Windows won't replace files that are in use.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPlatform
{
    /// <summary>The file scripts/package-windows.sh puts next to the executable: this folder is a release package.</summary>
    public const string PackageMarker = ".mane-ffmpeg-package";

    /// <summary>How long the script waits for the app to quit before it gives up.</summary>
    private const int QuitWaitMilliseconds = 60_000;

    public string UpdateAssetExtension => ".zip";
    public string UpdateStagedName => "Mane FFmpeg";

    public Task UnpackUpdateAsync(string archive, string folder, CancellationToken ct) =>
        Task.Run(() => ZipFile.ExtractToDirectory(archive, folder, overwriteFiles: true), ct);

    public string UpdateExecutableIn(string staged) => Path.Combine(staged, "ManeFfmpeg.exe");

    public string? UpdateInstalledPath(string processPath)
    {
        var folder = Path.GetDirectoryName(processPath);
        return folder is not null && File.Exists(Path.Combine(folder, PackageMarker)) ? folder : null;
    }

    public string? UpdateCannotReplaceReason(string installed) => null;

    public string WriteUpdateScript(string directory, UpdatePlan plan)
    {
        Directory.CreateDirectory(directory);
        var installed = Path.GetFullPath(plan.Installed).TrimEnd('\\', '/');
        var name = Path.GetFileName(installed);
        var sibling = Path.GetDirectoryName(installed)!;
        // Next to the installed copy, so moving it in place is a rename on the same disk.
        var incoming = Path.Combine(sibling, $".{name}.new");
        var backup = Path.Combine(sibling, $".{name}.old");
        var q = (Func<string, string>)PowerShellQuote;
        // Windows paths can't hold double quotes, so quoting each one is enough for Start-Process's single string.
        var reopen = string.Join(' ', plan.Reopen.Select(path => $"\"{path}\""));
        var relaunch = !plan.Relaunch ? ""
            : reopen.Length > 0
                ? $"Start-Process -FilePath (Join-Path $installed 'ManeFfmpeg.exe') -ArgumentList {q(reopen)}"
                : "Start-Process -FilePath (Join-Path $installed 'ManeFfmpeg.exe')";

        var script = Path.Combine(directory, "apply-update.ps1");
        // Windows PowerShell reads a script without a byte order mark in the ANSI code page, which breaks non-ASCII paths.
        File.WriteAllText(script, $$"""
            # Written by Mane FFmpeg: installs {{plan.Version.ToString(3)}} once the app has quit.
            $ErrorActionPreference = 'Continue'
            $log = {{q(plan.Log)}}
            $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log)
            function Log([string]$message) { Add-Content -LiteralPath $log -Value $message -Encoding UTF8 }
            $installed = {{q(installed)}}
            $staged = {{q(plan.Staged)}}
            $incoming = {{q(incoming)}}
            $backup = {{q(backup)}}
            Log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') Installing {{plan.Version.ToString(3)}} into $installed"

            foreach ($id in @({{string.Join(", ", plan.WaitFor)}})) {
              $app = Get-Process -Id $id -ErrorAction SilentlyContinue
              if ($app -and -not $app.WaitForExit({{QuitWaitMilliseconds}})) {
                Log "The app (pid $id) is still running; not updating."
                exit 1
              }
            }

            function Remove-Folder([string]$path) {
              if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
            }

            # Antivirus scanners and the indexer hold files open for a moment after the app quits.
            function Move-Folder([string]$from, [string]$to) {
              for ($try = 0; $try -lt 40; $try++) {
                try { Move-Item -LiteralPath $from -Destination $to -ErrorAction Stop; return $true }
                catch { Start-Sleep -Milliseconds 250 }
              }
              return $false
            }

            function Install-Update {
              Remove-Folder $incoming
              Remove-Folder $backup
              try { Copy-Item -LiteralPath $staged -Destination $incoming -Recurse -ErrorAction Stop }
              catch {
                Log "Couldn't copy the new version next to the old one: $_"
                Remove-Folder $incoming
                return $false
              }
              if (-not (Move-Folder $installed $backup)) {
                Log "Couldn't move the old version aside."
                Remove-Folder $incoming
                return $false
              }
              if (-not (Move-Folder $incoming $installed)) {
                Log "Couldn't put the new version in place; restoring the old one."
                $null = Move-Folder $backup $installed
                return $false
              }
              Remove-Folder $backup
              return $true
            }

            if (Install-Update) { Log "Installed." } else { Log "The update failed; the old version stays." }
            {{(plan.CleanUp is { } cleanUp ? $"Remove-Folder {q(cleanUp)}" : "")}}
            {{relaunch}}

            """.ReplaceLineEndings("\r\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return script;
    }

    public void StartUpdateScript(string script)
    {
        var start = new ProcessStartInfo(WindowsPowerShell) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
            start.ArgumentList.Add(argument);
        using var _ = Process.Start(start) ?? throw new IOException("Couldn't start the update script.");
    }
}
