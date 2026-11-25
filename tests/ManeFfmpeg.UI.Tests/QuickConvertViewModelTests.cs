using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.UI.ViewModels;

namespace ManeFfmpeg.UI.Tests;

/// <summary>The Quick Action's progress window.</summary>
public class QuickConvertViewModelTests
{
    public QuickConvertViewModelTests() =>
        AppServices.Instance.Settings.Update(s => s with { Notifications = false });

    [Theory]
    [InlineData("no-such-preset", "This preset no longer exists.")]
    [InlineData("builtin-share", "None of the selected files could be found.")]
    public async Task A_conversion_that_cant_start_says_why_and_stays_open(string presetId, string message)
    {
        var vm = new QuickConvertViewModel(new ConversionRequest(presetId, ["/no/such/file.mov"]));
        var closed = false;
        vm.RequestClose = () => closed = true;
        await vm.RunAsync();
        Assert.False(closed);
        Assert.False(vm.IsRunning);
        Assert.True(vm.HasErrors);
        Assert.StartsWith(message, vm.Status);
        Assert.Null(vm.ProgressText);
    }
}
