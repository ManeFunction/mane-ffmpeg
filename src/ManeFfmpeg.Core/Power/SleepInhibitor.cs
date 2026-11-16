using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Power;

/// <summary>
/// Keeps the computer from going to sleep on its own while held (<see cref="IPlatform.KeepAwake"/>). The display can
/// still sleep. Released if the app dies.
/// </summary>
public sealed class SleepInhibitor : IDisposable
{
    private IDisposable? _handle;

    private SleepInhibitor(IDisposable handle) => _handle = handle;

    /// <summary>
    /// Best-effort: when the platform refuses, the failure goes to the log and the returned inhibitor does nothing.
    /// <paramref name="reason"/> shows in the system's list of what keeps it awake.
    /// </summary>
    public static SleepInhibitor Acquire(string reason) => new(Platform.Current.KeepAwake(reason));

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
