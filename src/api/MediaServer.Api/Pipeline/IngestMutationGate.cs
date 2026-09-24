namespace MediaServer.Api.Pipeline;

/// <summary>
/// The pipeline has one writer. Operator lifecycle actions and engine events share its gate so a
/// retained source cannot be released, relocated or cleaned while placement is reading it.
/// Callers acquire before loading mutable EF entities, and never nest acquisitions.
/// </summary>
public static class IngestMutationGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        public void Dispose() => Gate.Release();
    }
}
