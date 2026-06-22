namespace AdaPanel.Core.Interfaces;

public interface IConcurrencyLock
{
    Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default);

    Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default);
}
