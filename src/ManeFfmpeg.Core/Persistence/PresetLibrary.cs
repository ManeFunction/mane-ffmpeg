using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Persistence;

/// <summary>
/// Editing presets with their Quick Actions in step: a preset's Quick Action is renamed with it and goes away with it.
/// The workflow runs whatever the preset is when it's chosen, so only a new name needs it rewritten. A Quick Action
/// that can't be changed is logged, and doesn't stop the preset's change.
/// </summary>
/// <param name="quickActions">Finder's Quick Actions, or null where the platform has none.</param>
public sealed class PresetLibrary(PresetStore store, IQuickActions? quickActions)
{
    public PresetStore Store => store;

    public bool HasQuickActions => quickActions is not null;

    public bool HasQuickAction(string presetId) => quickActions?.IsInstalled(presetId) == true;

    public void Save(Preset preset)
    {
        var before = store.Find(preset.Id);
        store.Save(preset);
        if (before is not null && before.Name != preset.Name && HasQuickAction(preset.Id))
            TryQuickAction(q => q.Install(preset), $"rename the Quick Action of {preset.Name}");
    }

    public void Delete(string presetId)
    {
        var name = store.Find(presetId)?.Name ?? presetId;
        store.Delete(presetId);
        TryQuickAction(q => q.Remove(presetId), $"remove the Quick Action of {name}");
    }

    /// <summary>Puts the built-in presets back; a built-in whose name was edited gets its Quick Action renamed back.</summary>
    public void RestoreBuiltIns()
    {
        var renamed = Preset.BuiltIns.Where(b => store.Find(b.Id) is { } current && current.Name != b.Name && HasQuickAction(b.Id)).ToList();
        store.RestoreBuiltIns();
        foreach (var builtIn in renamed)
            TryQuickAction(q => q.Install(builtIn), $"rename the Quick Action of {builtIn.Name}");
    }

    /// <summary>Adds or removes the preset's Quick Action; returns why that failed, or null.</summary>
    public string? SetQuickAction(Preset preset, bool on) =>
        TryQuickAction(q =>
        {
            if (on)
                q.Install(preset);
            else
                q.Remove(preset.Id);
        }, "change the Quick Action");

    private string? TryQuickAction(Action<IQuickActions> change, string what)
    {
        if (quickActions is null)
            return null;
        try
        {
            change(quickActions);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = $"Couldn't {what}: {ex.Message}";
            AppLog.Write(message);
            return message;
        }
    }
}
