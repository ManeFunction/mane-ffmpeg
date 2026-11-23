using System.Runtime.CompilerServices;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Tests;

internal static class TestSetup
{
    /// <summary>Keeps the app log and the waveform cache written by tests out of the user's folders.</summary>
    [ModuleInitializer]
    internal static void RedirectAppFolders() =>
        AppPaths.RootOverride = Directory.CreateTempSubdirectory("mane-ffmpeg-core-tests").FullName;

    /// <summary>
    /// Test classes run in parallel and some block a thread while a process runs. The pool adds threads only about
    /// once a second past its minimum (the core count: 4 on CI runners), so a few blocked threads stalled other tests'
    /// Task.Run and Task.Delay for seconds and their timed waits failed (most often on Windows, where starting a
    /// process is slow).
    /// </summary>
    [ModuleInitializer]
    internal static void WidenThreadPool()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), io);
    }
}
