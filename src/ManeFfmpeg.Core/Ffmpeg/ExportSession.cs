using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>
/// An export as the user runs it, from planning to the last ffmpeg run: pause, resume and cancel work at any point
/// (a pause before ffmpeg starts holds it), and the computer is kept awake while the session works but not while it's
/// paused. The editor's export is one session; a Quick Action is one session for all its files.
/// </summary>
public sealed class ExportSession : IDisposable
{
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _reason;
    private readonly string _logName;
    private readonly Func<string, IDisposable> _keepAwake;
    private IDisposable? _awake;
    private ExportRunner? _runner;
    private bool _disposed;

    /// <param name="reason">What the computer is kept awake for ("Exporting clip.mp4").</param>
    /// <param name="logName">Starts the session's log lines ("Export", "Quick Action").</param>
    /// <param name="keepAwake">Keeps the computer awake until disposed; <see cref="IPlatform.KeepAwake"/> by default.</param>
    public ExportSession(string reason, string logName, Func<string, IDisposable>? keepAwake = null)
    {
        _reason = reason;
        _logName = logName;
        _keepAwake = keepAwake ?? Platform.Current.KeepAwake;
        _awake = _keepAwake(reason);
    }

    public bool IsPaused { get; private set; }

    public CancellationToken Token => _cts.Token;

    public bool IsCancelled => _cts.IsCancellationRequested;

    /// <summary>A new temporary folder name for one export's intermediate files ("export-1a2b3c4d").</summary>
    public static string NewWorkDirectory(string prefix) =>
        Path.Combine(AppPaths.TempDirectory, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);

    public void Pause()
    {
        lock (_lock)
        {
            if (IsPaused || _disposed)
                return;
            IsPaused = true;
            _runner?.Pause();
            // A paused export doesn't need the computer awake.
            _awake?.Dispose();
            _awake = null;
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!IsPaused || _disposed)
                return;
            IsPaused = false;
            _awake ??= _keepAwake(_reason);
            _runner?.Resume();
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            if (!_disposed)
                _cts.Cancel();
        }
    }

    /// <summary>Runs one plan with ffmpeg (paused at once if the session is), logging the command and the outcome.</summary>
    public async Task<ExportResult> RunAsync(FfmpegTools tools, ExportPlan plan, IProgress<ExportProgress>? progress)
    {
        var runner = new ExportRunner(tools);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsPaused)
                runner.Pause();
            _runner = runner;
        }
        AppLog.Write($"{_logName} {plan.Mode} → {plan.OutputPath}\n{plan.CommandText(tools.Ffmpeg)}");
        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() => runner.RunAsync(plan, progress, token));
            AppLog.Write($"{_logName} {result.Outcome}: {result.Message} ({result.Elapsed}){(result.Log is null ? "" : "\n" + result.Log)}");
            return result;
        }
        finally
        {
            lock (_lock)
                _runner = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _awake?.Dispose();
            _awake = null;
            _cts.Dispose();
        }
    }
}
