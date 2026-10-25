namespace ManeFfmpeg.Core.Tests;

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> for up to 5 seconds.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for {what}");
    }
}
