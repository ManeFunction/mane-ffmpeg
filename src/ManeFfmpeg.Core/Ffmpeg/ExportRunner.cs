using System.Diagnostics;
using System.Globalization;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Ffmpeg;

public sealed record ExportProgress(double Fraction, TimeSpan Done, TimeSpan Total, double? Speed, TimeSpan? Remaining, string StepTitle);

public enum ExportOutcome
{
    Succeeded,
    Cancelled,
    Failed,
}

public sealed record ExportResult(ExportOutcome Outcome, string Message, TimeSpan Elapsed, string? Log = null);

/// <summary>Runs an <see cref="ExportPlan"/> with ffmpeg, reporting progress and cleaning up after itself.</summary>
public sealed class ExportRunner(FfmpegTools tools)
{
    private const int LogLines = 40;

    public async Task<ExportResult> RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var outputPath = plan.OutputPath;
        var workDirectory = plan.WorkDirectory;
        var clock = Stopwatch.StartNew();
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
                outputTouched |= step.WritesOutput;
                var parser = new ProgressParser();
                var stepStart = doneBefore;
                parser.Updated += p =>
                {
                    var position = p.Position > step.Duration ? step.Duration : p.Position;
                    var done = stepStart + position;
                    var fraction = total > TimeSpan.Zero ? Math.Clamp(done / total, 0, 1) : 0;
                    TimeSpan? remaining = p.Speed is > 0 ? TimeSpan.FromSeconds((total - done).TotalSeconds / p.Speed.Value) : null;
                    progress?.Report(new ExportProgress(fraction, done, total, p.Speed, remaining, step.Title));
                };
                progress?.Report(new ExportProgress(total > TimeSpan.Zero ? doneBefore / total : 0, doneBefore, total, null, null, step.Title));

                var arguments = step.Shift is { } shift
                    ? shift.ArgumentsFor(TimeFormat.Seconds(await ConcatShiftAsync(shift.ListPath, ct)))
                    : step.Arguments;

                var result = await Cli.Wrap(tools.Ffmpeg)
                    .WithArguments(arguments)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(parser.Feed))
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

                if (result.ExitCode != 0)
                {
                    DeleteOutput(outputPath, outputTouched);
                    string text;
                    lock (log)
                        text = string.Join('\n', log);
                    var first = log.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? $"ffmpeg exited with code {result.ExitCode}";
                    return new ExportResult(ExportOutcome.Failed, first, clock.Elapsed, text);
                }
                doneBefore += step.Duration;
            }

            progress?.Report(new ExportProgress(1, total, total, null, TimeSpan.Zero, "Done"));
            return new ExportResult(ExportOutcome.Succeeded, $"Saved {Path.GetFileName(outputPath)}", clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            DeleteOutput(outputPath, outputTouched);
            return new ExportResult(ExportOutcome.Cancelled, "Export cancelled", clock.Elapsed);
        }
        catch (Exception ex)
        {
            DeleteOutput(outputPath, outputTouched);
            return new ExportResult(ExportOutcome.Failed, ex.Message, clock.Elapsed, ex.ToString());
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
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
