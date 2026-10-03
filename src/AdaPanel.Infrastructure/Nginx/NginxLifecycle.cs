namespace AdaPanel.Infrastructure.Nginx;

/// <summary>One managed Nginx installation and startup task, across HTTP and installer scopes.</summary>
internal static class NginxLifecycle
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        return new Lease();
    }

    internal static async Task<IDisposable?> EnterForComponentAsync(string componentId, CancellationToken cancellationToken)
        => componentId.Equals("nginx", StringComparison.OrdinalIgnoreCase)
            ? await EnterAsync(cancellationToken) : null;

    private sealed class Lease : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) Gate.Release();
        }
    }
}
