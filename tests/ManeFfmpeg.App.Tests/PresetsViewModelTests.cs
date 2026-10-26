using ManeFfmpeg.App.ViewModels;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.App.Tests;

/// <summary>The preset editor asks before unsaved edits are lost.</summary>
public class PresetsViewModelTests
{
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private static (PresetsViewModel Vm, List<int> Asked) Edited(int answer)
    {
        var asked = new List<int>();
        var vm = new PresetsViewModel();
        vm.AskAboutEdits = () =>
        {
            asked.Add(answer);
            return Task.FromResult(answer);
        };
        vm.ListSelection = vm.Presets[0];
        vm.Arguments += " -metadata comment=edited";
        Assert.True(vm.IsModified);
        return (vm, asked);
    }

    [Fact]
    public async Task Cancel_keeps_the_edits_and_the_selection()
    {
        var (vm, asked) = Edited(answer: 2);
        var edited = vm.Selected;
        vm.ListSelection = vm.Presets[1];
        await Until(() => asked.Count == 1);
        await Task.Delay(50);
        Assert.Same(edited, vm.Selected);
        Assert.True(vm.IsModified);
        Assert.EndsWith("comment=edited", vm.Arguments);
    }

    [Fact]
    public async Task Dont_save_switches_and_drops_the_edits()
    {
        var (vm, _) = Edited(answer: 1);
        var other = vm.Presets[1];
        vm.ListSelection = other;
        await Until(() => vm.Selected == other);
        Assert.False(vm.IsModified);
        Assert.DoesNotContain(vm.Presets, p => p.Arguments.EndsWith("comment=edited"));
    }

    [Fact]
    public async Task Save_keeps_the_edits_and_switches()
    {
        var (vm, _) = Edited(answer: 0);
        var editedId = vm.Selected!.Id;
        var otherId = vm.Presets[1].Id;
        Assert.True(await vm.ResolveEditsAsync());
        Assert.Contains(vm.Presets, p => p.Id == editedId && p.Arguments.EndsWith("comment=edited"));
        Assert.False(vm.IsModified);
        vm.ListSelection = vm.Presets.First(p => p.Id == otherId);
        Assert.Equal(otherId, vm.Selected!.Id);
        // Put the preset back for the other tests sharing the store.
        vm.ListSelection = vm.Presets.First(p => p.Id == editedId);
        vm.Arguments = vm.Arguments.Replace(" -metadata comment=edited", "");
        Assert.True(await vm.ResolveEditsAsync());
    }

    [Fact]
    public async Task Saving_doesnt_ask_when_the_list_is_refilled()
    {
        var (vm, asked) = Edited(answer: 2);
        var editedId = vm.Selected!.Id;
        // Like the ListBox: clearing its items clears its selection, which reaches the view model.
        vm.Presets.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                vm.ListSelection = null;
        };
        vm.SaveCommand.Execute(null);
        await Task.Delay(100);
        Assert.Empty(asked);
        Assert.False(vm.IsModified);
        Assert.Equal(editedId, vm.Selected?.Id);
        // Put the preset back for the other tests sharing the store.
        vm.Arguments = vm.Arguments.Replace(" -metadata comment=edited", "");
        vm.SaveCommand.Execute(null);
    }

    [Fact]
    public async Task Refilling_the_list_reselects_an_unchanged_preset()
    {
        var vm = new PresetsViewModel();
        vm.ListSelection = vm.Presets.First(p => p.Id == Preset.PlaneCopy.Id);
        var announced = 0;
        vm.PropertyChanged += (_, e) => announced += e.PropertyName == nameof(vm.ListSelection) ? 1 : 0;
        await vm.RestoreBuiltInsCommand.ExecuteAsync(null);
        Assert.Equal(Preset.PlaneCopy.Id, vm.ListSelection?.Id);
        Assert.True(announced > 0);
    }

    [Fact]
    public async Task Restoring_built_ins_asks_first()
    {
        var confirm = false;
        var vm = new PresetsViewModel { ConfirmRestore = () => Task.FromResult(confirm) };
        var builtIn = Preset.PlaneCopy;
        vm.ListSelection = vm.Presets.First(p => p.Id == builtIn.Id);
        vm.Name = "renamed";
        vm.SaveCommand.Execute(null);

        await vm.RestoreBuiltInsCommand.ExecuteAsync(null);
        Assert.Equal("renamed", vm.Presets.First(p => p.Id == builtIn.Id).Name);

        confirm = true;
        await vm.RestoreBuiltInsCommand.ExecuteAsync(null);
        Assert.Equal(builtIn.Name, vm.Presets.First(p => p.Id == builtIn.Id).Name);
    }

    [Fact]
    public async Task Adding_a_preset_asks_first()
    {
        var (vm, asked) = Edited(answer: 2);
        var count = vm.Presets.Count;
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Single(asked);
        Assert.Equal(count, vm.Presets.Count);
    }
}
