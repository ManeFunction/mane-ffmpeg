using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Ffmpeg;

public sealed record ExportProgress(double Fraction, TimeSpan Done, TimeSpan Total, double? Speed, TimeSpan? Remaining, string StepTitle);

public enum ExportOutcome
{
    Succeeded,
    Cancelled,
    Failed,
}

public sealed record ExportResult(ExportOutcome Outcome, string Message, TimeSpan Elapsed, string? Log = null);

/// <summary>
/// Runs one <see cref="ExportPlan"/> with ffmpeg, reporting progress and cleaning up after itself.
/// The export can be paused: ffmpeg is frozen with SIGSTOP and continues with SIGCONT.
/// </summary>
public sealed partial class ExportRunner(FfmpegTools tools)
{
    private const int LogLines = 40;

    /// <summary>
    /// Runs ffmpeg ($0) as a child of sh instead of this process, and prints its pid on the progress pipe.
    /// A stopped child of this process would hang .NET on macOS: waitid reports it as exited, so the runtime's
    /// SIGCHLD handler loops trying to reap it and holds the lock every Process.Start needs.
    /// </summary>
    private const string Wrapper = "\"$0\" \"$@\" & echo \"" + PidKey + "$!\"; wait $!";

    private const string PidKey = "mane_ffmpeg_pid=";

    private readonly Lock _lock = new();

    /// <summary>Time spent running, pauses left out.</summary>
    private readonly Stopwatch _clock = new();

    /// <summary>Completes on resume; null while not paused.</summary>
    private TaskCompletionSource? _resumed;

    /// <summary>The running ffmpeg, which a pause has to stop.</summary>
    private int? _processId;

    /// <summary>While ffmpeg is stopped: kills it if this process dies, since nothing else would ever continue it.</summary>
    private Process? _guard;

    /// <summary>Freezes ffmpeg where it is and holds the next step until <see cref="Resume"/>. Works before the run starts too.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (_resumed is not null)
                return;
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _clock.Stop();
            if (_processId is { } pid)
                Freeze(pid);
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_resumed is null)
                return;
            if (_processId is { } pid)
                Signal(pid, ContinueSignal);
            ReleaseGuard();
            _clock.Start();
            _resumed.SetResult();
            _resumed = null;
        }
    }

    public async Task<ExportResult> RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var outputPath = plan.OutputPath;
        var workDirectory = plan.WorkDirectory;
        // Planning may have been paused and resumed already; only the run counts.
        lock (_lock)
        {
            if (_resumed is null)
                _clock.Restart();
            else
                _clock.Reset();
        }
        var total = plan.Steps.Aggregate(TimeSpan.Zero, (s, step) => s + step.Duration);
        var doneBefore = TimeSpan.Zero;
        var outputTouched = false;
        var log = new Queue<string>();

        try
        {
            Directory.CreateDirectory(workDirectory);
            foreach (var file in plan.Files)
                await File.WriteAllTextAsync(file.Path, file.Content, ct);

            foreach (var step in plan.Steps)
            {
                await WhilePausedAsync(ct);
                outputTouched |= step.WritesOutput;
                var parser = new ProgressParser();
                var stepStart = doneBefore;
                var stepStarted = Stopwatch.GetTimestamp();
                var runningBefore = RunningTime();
                parser.Updated += p =>
                {
                    var position = p.Position > step.Duration ? step.Duration : p.Position;
                    var done = stepStart + position;
                    var fraction = total > TimeSpan.Zero ? Math.Clamp(done / total, 0, 1) : 0;
                    var speed = UnpausedSpeed(p, Stopwatch.GetElapsedTime(stepStarted) - (RunningTime() - runningBefore));
                    TimeSpan? remaining = speed is > 0 ? TimeSpan.FromSeconds((total - done).TotalSeconds / speed.Value) : null;
                    progress?.Report(new ExportProgress(fraction, done, total, speed, remaining, step.Title));
                };
                progress?.Report(new ExportProgress(total > TimeSpan.Zero ? doneBefore / total : 0, doneBefore, total, null, null, step.Title));

                var arguments = step.Shift is { } shift
                    ? shift.ArgumentsFor(TimeFormat.Seconds(await ConcatShiftAsync(shift.ListPath, ct)))
                    : step.Arguments;

                var command = Cli.Wrap("/bin/sh")
                    .WithArguments(["-c", Wrapper, tools.Ffmpeg, .. arguments])
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                    {
                        if (line.StartsWith(PidKey, StringComparison.Ordinal))
                            OnStarted(int.Parse(line.AsSpan(PidKey.Length), CultureInfo.InvariantCulture));
                        else
                            parser.Feed(line);
                    }))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                    {
                        lock (log)
                        {
                            log.Enqueue(line);
                            if (log.Count > LogLines)
                                log.Dequeue();
                        }
                    }))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(ct);
                CommandResult result;
                try
                {
                    result = await command;
                }
                finally
                {
                    lock (_lock)
                    {
                        _processId = null;
                        ReleaseGuard();
                    }
                }

                if (result.ExitCode != 0)
                {
                    DeleteOutput(outputPath, outputTouched);
                    string text;
                    lock (log)
                        text = string.Join('\n', log);
                    var first = log.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? $"ffmpeg exited with code {result.ExitCode}";
                    return new ExportResult(ExportOutcome.Failed, first, RunningTime(), text);
                }
                doneBefore += step.Duration;
            }

            progress?.Report(new ExportProgress(1, total, total, null, TimeSpan.Zero, "Done"));
            return new ExportResult(ExportOutcome.Succeeded, $"Saved {Path.GetFileName(outputPath)}", RunningTime());
        }
        catch (OperationCanceledException)
        {
            DeleteOutput(outputPath, outputTouched);
            return new ExportResult(ExportOutcome.Cancelled, "Export cancelled", RunningTime());
        }
        catch (Exception ex)
        {
            DeleteOutput(outputPath, outputTouched);
            return new ExportResult(ExportOutcome.Failed, ex.Message, RunningTime(), ex.ToString());
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private void OnStarted(int pid)
    {
        lock (_lock)
        {
            _processId = pid;
            // Paused while ffmpeg was starting.
            if (_resumed is not null)
                Freeze(pid);
        }
    }

    private TimeSpan RunningTime()
    {
        lock (_lock)
            return _clock.Elapsed;
    }

    private Task WhilePausedAsync(CancellationToken ct)
    {
        lock (_lock)
            return _resumed?.Task.WaitAsync(ct) ?? Task.CompletedTask;
    }

    /// <summary>
    /// ffmpeg's speed is its position over the wall time since it started, so time spent paused
    /// (<paramref name="paused"/>) drags it down until the end. Take that time back out.
    /// </summary>
    private static double? UnpausedSpeed(FfmpegProgress p, TimeSpan paused)
    {
        if (p.Speed is not { } speed)
            return null;
        var ran = p.Position.TotalSeconds / speed - paused.TotalSeconds;
        return ran > 0 ? p.Position.TotalSeconds / ran : speed;
    }

    // kill(2) signal numbers: macOS uses the BSD ones.
    private static int StopSignal => OperatingSystem.IsMacOS() ? 17 : 19;
    private static int ContinueSignal => OperatingSystem.IsMacOS() ? 19 : 18;

    private void Freeze(int pid)
    {
        Signal(pid, StopSignal);
        _guard = StartGuard(pid);
    }

    /// <summary>
    /// A stopped ffmpeg outlives a crash or a force quit and never wakes up (a launched app's process group is
    /// already orphaned, so the kernel doesn't continue it either). The guard reads a pipe from this process:
    /// a line means "resumed", the end of the file means this process is gone, and then it kills ffmpeg.
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
            AppLog.Write($"Can't watch the paused ffmpeg: {ex.Message}");
            return null;
        }
    }

    private void ReleaseGuard()
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

    private static void Signal(int pid, int signal)
    {
        if (Kill(pid, signal) != 0)
            AppLog.Write($"Can't send signal {signal} to ffmpeg (pid {pid}): errno {Marshal.GetLastPInvokeError()}");
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    /// <summary>How far ffmpeg will shift a concat input: minus its start time.</summary>
    private async Task<TimeSpan> ConcatShiftAsync(string list, CancellationToken ct)
    {
        var result = await Cli.Wrap(tools.Ffprobe)
            .WithArguments(["-v", "error", "-f", "concat", "-safe", "0", "-i", list, "-show_entries", "format=start_time", "-of", "csv=p=0"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        return double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
            ? TimeFormat.FromSeconds(-start)
            : TimeSpan.Zero;
    }

    private static void DeleteOutput(string path, bool touched)
    {
        if (!touched)
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
