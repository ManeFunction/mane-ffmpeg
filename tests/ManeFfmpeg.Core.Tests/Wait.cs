namespace ManeFfmpeg.Core.Tests;

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> for up to 5 seconds.</summary>
    public static Task Until(Func<bool> condition, string what) => Until(condition, what, null);

    /// <summary>
    /// Polls <paramref name="condition"/> for up to 5 seconds; on a timeout the message ends with what
    /// <paramref name="state"/> reports then (the values the condition looked at).
    /// </summary>
    public static async Task Until(Func<bool> condition, string what, Func<string>? state)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for {what}{(state is null ? "" : $" ({state()})")}");
    }
}
