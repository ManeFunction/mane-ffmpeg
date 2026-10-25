using System.Runtime.CompilerServices;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Tests;

internal static class TestSetup
{
    /// <summary>Keeps the app log and the waveform cache written by tests out of the user's folders.</summary>
    [ModuleInitializer]
    internal static void RedirectAppFolders() =>
        AppPaths.RootOverride = Directory.CreateTempSubdirectory("mane-ffmpeg-core-tests").FullName;
}
