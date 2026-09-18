using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Dns;

/// <summary>One capability probe per scoped DNS service, with explicit invalidation after installing DNS.</summary>
internal sealed class DnsAvailabilityProbe(Func<CancellationToken, Task<bool>>? probe = null)
{
    private readonly Func<CancellationToken, Task<bool>> probe = probe ?? ReadAsync;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool? available;

    public async Task<bool> GetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (available is { } value) return value;
            var result = await probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            available = result;
            return result;
        }
        finally { gate.Release(); }
    }

    public void Invalidate() => available = null;

    private static async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            "if (Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue) { 'true' } else { 'false' }",
            cancellationToken);
        if (!result.Success || !bool.TryParse(result.StandardOutput.Trim(), out var installed))
            throw new IOException("Windows DNS capability could not be measured.");
        return installed;
    }
}
