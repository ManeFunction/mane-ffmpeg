namespace ManeFfmpeg.Core.Platforms;

/// <summary>A handle that holds nothing: what a best-effort call returns when the system refused.</summary>
internal sealed class NoOp : IDisposable
{
    public static NoOp Instance { get; } = new();

    public void Dispose()
    {
    }
}
