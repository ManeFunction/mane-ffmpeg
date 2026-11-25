using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Platforms;

/// <summary>
/// Presets offered in the file manager's context menu (Finder's Quick Actions). Choosing one starts the app with
/// <see cref="Ffmpeg.FileConversion.CommandLineFlag"/>, which converts the selected videos next to themselves.
/// </summary>
public interface IQuickActions
{
    /// <summary>Whether <paramref name="presetId"/> has a Quick Action.</summary>
    bool IsInstalled(string presetId);

    /// <summary>Adds the preset's Quick Action, or renames it after the preset was renamed.</summary>
    void Install(Preset preset);

    /// <summary>Removes the preset's Quick Action; nothing happens when there is none.</summary>
    void Remove(string presetId);
}
