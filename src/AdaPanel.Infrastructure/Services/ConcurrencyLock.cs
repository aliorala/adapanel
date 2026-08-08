using System.Collections.Concurrent;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Services;

/// <inheritdoc />
public sealed class ConcurrencyLock : IConcurrencyLock
{
    // Kuyruk BAŞINA bir semafor. Önceden TEK bir static semafor vardı ve süreçteki her iş onu
    // paylaşıyordu; gece yedeği (saatler sürebilir) giriş akışını bloke ediyordu. Gerekçe
    // IConcurrencyLock'ta yazılı.
    //
    // Sözlük static: kilit süreç genelinde tektir, DI ömrüne bağlı değildir. Semaforlar hiç
    // kaldırılmaz — anahtarlar ConcurrencyKeys'te tanımlı sabit bir kümedir, sınırsız büyüme yok.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(ConcurrencyKeys.Iis, operation, cancellationToken);

    public Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(ConcurrencyKeys.Iis, operation, cancellationToken);

    public async Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var gate = Gate(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var gate = Gate(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim Gate(string? key)
        => Gates.GetOrAdd(
            string.IsNullOrWhiteSpace(key) ? ConcurrencyKeys.Iis : key,
            _ => new SemaphoreSlim(1, 1));
}
