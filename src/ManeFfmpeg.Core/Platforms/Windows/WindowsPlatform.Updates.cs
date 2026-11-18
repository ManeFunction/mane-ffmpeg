using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;

namespace ManeFfmpeg.Core.Platforms.Windows;

/// <summary>
/// Updates: the release is a zip holding the "Mane FFmpeg" folder, and an update replaces the folder the app runs from,
/// wherever that is (Desktop, Downloads, …). The swap runs in Windows PowerShell after the app has quit, since Windows
/// won't replace files that are in use. It renames the whole folder when it can, and otherwise moves what's inside it.
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
        // Windows paths can't hold double quotes, so quoting each one is enough for Start-Process's single string.
        var reopen = string.Join(' ', plan.Reopen.Select(path => $"\"{path}\""));
        var relaunch = !plan.Relaunch ? ""
            : reopen.Length > 0
                ? $"Start-Process -FilePath (Join-Path $installed 'ManeFfmpeg.exe') -WorkingDirectory $installed -ArgumentList {PowerShellQuote(reopen)}"
                : "Start-Process -FilePath (Join-Path $installed 'ManeFfmpeg.exe') -WorkingDirectory $installed";

        var script = Path.Combine(directory, "apply-update.ps1");
        // Windows PowerShell reads a script without a byte order mark in the ANSI code page, which breaks non-ASCII paths.
        File.WriteAllText(script, $$"""
            # Written by Mane FFmpeg: installs {{plan.Version.ToString(3)}} once the app has quit.
            $ErrorActionPreference = 'Continue'
            $log = {{PowerShellQuote(plan.Log)}}
            $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log)
            function Log([string]$message) { Add-Content -LiteralPath $log -Value $message -Encoding UTF8 }
            $installed = {{PowerShellQuote(installed)}}
            $staged = {{PowerShellQuote(plan.Staged)}}
            $incoming = {{PowerShellQuote(incoming)}}
            $backup = {{PowerShellQuote(backup)}}
            Log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') Installing {{plan.Version.ToString(3)}} into $installed"

            foreach ($id in @({{string.Join(", ", plan.WaitFor)}})) {
              $app = Get-Process -Id $id -ErrorAction SilentlyContinue
              if ($app -and -not $app.WaitForExit({{QuitWaitMilliseconds}})) {
                Log "The app (pid $id) is still running; not updating."
                exit 1
              }
            }

            function Remove-Entry([string]$path) {
              if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
            }

            # Antivirus scanners and the indexer hold files open for a moment after the app quits.
            function Move-Entry([string]$from, [string]$to, [int]$tries = 40) {
              for ($try = 1; $try -le $tries; $try++) {
                try { Move-Item -LiteralPath $from -Destination $to -ErrorAction Stop; return $true }
                catch { $failure = $_; if ($try -lt $tries) { Start-Sleep -Milliseconds 250 } }
              }
              Log "Couldn't move $from to ${to}: $failure"
              return $false
            }

            # Takes out the new entries Swap-Contents placed, then moves the old ones back.
            function Restore-Contents([string[]]$placed) {
              foreach ($name in $placed) { Remove-Entry (Join-Path $installed $name) }
              foreach ($item in @(Get-ChildItem -LiteralPath $backup -Force)) {
                $null = Move-Entry $item.FullName (Join-Path $installed $item.Name)
              }
              if (@(Get-ChildItem -LiteralPath $backup -Force).Count -eq 0) { Remove-Entry $backup }
              else { Log "Part of the old version is still in $backup." }
            }

            # A folder some process has as its current folder (a Command Prompt opened there, say) or that a sync client
            # such as OneDrive holds can't be renamed, but what's inside it can be moved: the old entries go to the backup
            # folder and the new ones take their place.
            function Swap-Contents {
              Remove-Entry $backup
              try { $null = New-Item -ItemType Directory -Path $backup -ErrorAction Stop }
              catch {
                Log "Couldn't create ${backup}: $_"
                return $false
              }
              foreach ($item in @(Get-ChildItem -LiteralPath $installed -Force)) {
                if (-not (Move-Entry $item.FullName (Join-Path $backup $item.Name))) {
                  Restore-Contents @()
                  return $false
                }
              }
              $placed = @()
              foreach ($item in @(Get-ChildItem -LiteralPath $incoming -Force)) {
                if (-not (Move-Entry $item.FullName (Join-Path $installed $item.Name))) {
                  Restore-Contents $placed
                  return $false
                }
                $placed += $item.Name
              }
              Remove-Entry $incoming
              Remove-Entry $backup
              return $true
            }

            function Install-Update {
              Remove-Entry $incoming
              Remove-Entry $backup
              try { Copy-Item -LiteralPath $staged -Destination $incoming -Recurse -ErrorAction Stop }
              catch {
                Log "Couldn't copy the new version next to the old one: $_"
                Remove-Entry $incoming
                return $false
              }
              # Renaming swaps the whole folder at once. Only a few tries: what blocks a folder rename usually stays.
              if (Move-Entry $installed $backup 8) {
                if (Move-Entry $incoming $installed) {
                  Remove-Entry $backup
                  return $true
                }
                Log "Couldn't put the new version in place; restoring the old one."
                if (Move-Entry $backup $installed) { Remove-Entry $incoming }
                return $false
              }
              Log "Couldn't move the old folder aside; replacing what's inside it instead."
              if (Swap-Contents) { return $true }
              Remove-Entry $incoming
              return $false
            }

            if (Install-Update) { Log "Installed." } else { Log "The update failed; the old version stays." }
            {{(plan.CleanUp is { } cleanUp ? $"Remove-Entry {PowerShellQuote(cleanUp)}" : "")}}
            {{relaunch}}

            """.ReplaceLineEndings("\r\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return script;
    }

    public void StartUpdateScript(string script)
    {
        using var _ = Process.Start(UpdateScriptStart(script)) ?? throw new IOException("Couldn't start the update script.");
    }

    /// <summary>
    /// How the update script starts. Not in this process's current folder: started from Explorer, that is the app's own
    /// folder, and Windows won't rename a folder that a running process has as its current one, so the script would
    /// block the very swap it makes.
    /// </summary>
    internal static ProcessStartInfo UpdateScriptStart(string script)
    {
        var start = new ProcessStartInfo(WindowsPowerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
            start.ArgumentList.Add(argument);
        return start;
    }
}
