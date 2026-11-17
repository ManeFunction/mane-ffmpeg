using System.Diagnostics;
using System.Globalization;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms;

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
/// The export can be paused: ffmpeg is frozen where it is (<see cref="IPlatform.Freeze"/>) and continues on resume.
/// </summary>
public sealed class ExportRunner(FfmpegTools tools)
{
    private const int LogLines = 40;
    private const int DeleteAttempts = 30;

    private readonly Lock _lock = new();

    /// <summary>Time spent running, pauses left out.</summary>
    private readonly Stopwatch _clock = new();

    /// <summary>Completes on resume; null while not paused.</summary>
    private TaskCompletionSource? _resumed;

    /// <summary>The running ffmpeg, which a pause has to stop.</summary>
    private int? _processId;

    /// <summary>ffmpeg while it's frozen.</summary>
    private IFrozenProcess? _frozen;

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
                _frozen = Platform.Current.Freeze(pid);
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_resumed is null)
                return;
            _frozen?.Resume();
            _frozen = null;
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

                var command = Platform.Current.StartPausable(tools.Ffmpeg, arguments, OnStarted, parser.Feed, line =>
                {
                    lock (log)
                    {
                        log.Enqueue(line);
                        if (log.Count > LogLines)
                            log.Dequeue();
                    }
                }, ct);
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
                        _frozen?.Release();
                        _frozen = null;
                    }
                }

                if (result.ExitCode != 0)
                {
                    await DeleteOutputAsync(outputPath, outputTouched);
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
            await DeleteOutputAsync(outputPath, outputTouched);
            return new ExportResult(ExportOutcome.Cancelled, "Export cancelled", RunningTime());
        }
        catch (Exception ex)
        {
            await DeleteOutputAsync(outputPath, outputTouched);
            return new ExportResult(ExportOutcome.Failed, ex.Message, RunningTime(), ex.ToString());
        }
        finally
        {
            await TryDeleteDirectoryAsync(workDirectory);
        }
    }

    private void OnStarted(int pid)
    {
        lock (_lock)
        {
            _processId = pid;
            // Paused while ffmpeg was starting.
            if (_resumed is not null)
                _frozen = Platform.Current.Freeze(pid);
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

    private static Task DeleteOutputAsync(string path, bool touched) =>
        touched ? WhileLockedAsync(() => { if (File.Exists(path)) File.Delete(path); }) : Task.CompletedTask;

    private static Task TryDeleteDirectoryAsync(string path) =>
        WhileLockedAsync(() => { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); });

    /// <summary>
    /// Runs <paramref name="delete"/>, retrying for a couple of seconds while the files are in use. On Windows a killed
    /// ffmpeg holds its files until it has fully exited, and CliWrap only waits for the process it started, which for
    /// Chocolatey's and Scoop's ffmpeg.exe is a shim whose child is the real ffmpeg.
    /// </summary>
    private static async Task WhileLockedAsync(Action delete)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                {
                    AppLog.Write($"Can't clean up after the export: {ex.Message}");
                    return;
                }
            }
            await Task.Delay(100);
        }
    }
}
