using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Power;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Skips the test outside macOS.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS only";
    }
}

public class SleepInhibitorTests
{
    private static async Task<string> AssertionsAsync() =>
        (await Cli.Wrap("/usr/bin/pmset").WithArguments(["-g", "assertions"]).ExecuteBufferedAsync()).StandardOutput;

    [MacFact]
    public async Task Holds_a_power_assertion_until_disposed()
    {
        var reason = $"Exporting test-{Guid.NewGuid():N}.mp4";
        using (SleepInhibitor.Acquire(reason))
        {
            var held = await AssertionsAsync();
            Assert.Contains($"PreventUserIdleSystemSleep named: \"{reason}\"", held);
        }
        Assert.DoesNotContain(reason, await AssertionsAsync());
    }
}
