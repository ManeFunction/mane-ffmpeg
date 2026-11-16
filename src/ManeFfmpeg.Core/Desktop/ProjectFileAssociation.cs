using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Desktop;

/// <summary>
/// Makes this app the one that opens .mfvp projects on double-click: Launch Services on macOS, shared-mime-info on Linux,
/// the registry on Windows (see each <see cref="IPlatform"/>).
/// </summary>
public static class ProjectFileAssociation
{
    /// <summary>Whether this copy can register itself: the packaged app, not <c>dotnet ManeFfmpeg.dll</c>.</summary>
    public static bool IsSupported => Platform.Current.CanRegisterProjectFiles;

    public static async Task<bool> IsRegisteredAsync()
    {
        try
        {
            return IsSupported && await Platform.Current.IsProjectFileHandlerAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Couldn't check the .{ProjectFile.Extension} association: {ex.Message}");
            return false;
        }
    }

    public static Task RegisterAsync()
    {
        if (!IsSupported)
            throw new InvalidOperationException("Only the packaged app can be registered.");
        AppLog.Write($"Registering .{ProjectFile.Extension} files");
        return Platform.Current.RegisterProjectFilesAsync();
    }

    /// <summary>Hands .mfvp files back to whatever opens JSON.</summary>
    public static Task UnregisterAsync()
    {
        if (!IsSupported)
            throw new InvalidOperationException("Only the packaged app can be registered.");
        AppLog.Write($"Unregistering .{ProjectFile.Extension} files");
        return Platform.Current.UnregisterProjectFilesAsync();
    }
}
