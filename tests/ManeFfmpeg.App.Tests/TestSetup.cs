using System.Runtime.CompilerServices;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.App.Tests;

internal static class TestSetup
{
    /// <summary>Points the app's settings and presets at a temporary folder before any test touches them.</summary>
    [ModuleInitializer]
    internal static void RedirectAppFolders() =>
        AppPaths.RootOverride = Directory.CreateTempSubdirectory("mane-ffmpeg-app-tests").FullName;
}
