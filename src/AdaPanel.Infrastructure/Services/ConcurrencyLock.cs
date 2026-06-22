using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Services;

public sealed class ConcurrencyLock : IConcurrencyLock
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            Gate.Release();
        }
    }
}
