using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Tests;

/// <summary>A preset's Quick Action follows the preset's name and lifetime.</summary>
public class PresetLibraryTests
{
    private sealed class FakeQuickActions : IQuickActions
    {
        public Dictionary<string, string> Installed { get; } = [];
        public List<string> Calls { get; } = [];
        public bool Broken { get; set; }

        public bool IsInstalled(string presetId) => Installed.ContainsKey(presetId);

        public void Install(Preset preset)
        {
            if (Broken)
                throw new UnauthorizedAccessException("read-only");
            Calls.Add("+" + preset.Name);
            Installed[preset.Id] = preset.Name;
        }

        public void Remove(string presetId)
        {
            if (Broken)
                throw new IOException("busy");
            Calls.Add("-" + presetId);
            Installed.Remove(presetId);
        }
    }

    private static PresetStore TempStore() => new(Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json"));

    [Fact]
    public void A_quick_action_is_renamed_with_its_preset_and_removed_with_it()
    {
        var actions = new FakeQuickActions();
        var library = new PresetLibrary(TempStore(), actions);
        var mine = new Preset { Id = "mine", Name = "Mine", Arguments = "-c copy" };
        library.Save(mine);
        Assert.Null(library.SetQuickAction(mine, on: true));
        Assert.True(library.HasQuickAction("mine"));

        // A change that keeps the name leaves the workflow alone: it runs the preset as it is.
        library.Save(mine with { Arguments = "-c:v libx264" });
        library.Save(mine with { Name = "Renamed" });
        Assert.Equal("Renamed", actions.Installed["mine"]);
        // A preset without a Quick Action doesn't get one by being saved.
        library.Save(Preset.Harsh with { Name = "harsher" });
        Assert.False(library.HasQuickAction(Preset.Harsh.Id));

        library.Delete("mine");
        Assert.Null(library.Store.Find("mine"));
        Assert.Empty(actions.Installed);
        Assert.Equal(["+Mine", "+Renamed", "-mine"], actions.Calls);
    }

    [Fact]
    public void Restoring_the_built_ins_only_renames_the_quick_actions_whose_name_changes()
    {
        var actions = new FakeQuickActions();
        var library = new PresetLibrary(TempStore(), actions);
        library.SetQuickAction(Preset.Share, on: true);
        library.SetQuickAction(Preset.ForStorage, on: true);
        library.Save(Preset.Share with { Name = "my share" });
        actions.Calls.Clear();
        library.RestoreBuiltIns();
        Assert.Equal(["+" + Preset.Share.Name], actions.Calls);
        Assert.Equal(Preset.Share.Name, actions.Installed[Preset.Share.Id]);
    }

    [Fact]
    public void A_quick_action_that_cant_be_changed_doesnt_stop_the_preset()
    {
        var actions = new FakeQuickActions();
        var library = new PresetLibrary(TempStore(), actions);
        library.SetQuickAction(Preset.Share, on: true);
        actions.Broken = true;
        Assert.Contains("read-only", library.SetQuickAction(Preset.ForStorage, on: true));
        library.Delete(Preset.Share.Id);
        Assert.Null(library.Store.Find(Preset.Share.Id));
        Assert.Null(new PresetLibrary(TempStore(), null).SetQuickAction(Preset.Share, on: true));
    }
}
