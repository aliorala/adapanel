namespace AdaPanel.Infrastructure.Mail;

/// <summary>Owns a panel store's certificate files, provider update and state publication as one operation.</summary>
internal static class MailCertificateLifecycle
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
    }

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    internal static int ReferencesForPath(string statePath)
    {
        lock (Entries) return Entries.GetValueOrDefault(Path.GetFullPath(statePath))?.References ?? 0;
    }

    internal static Task<IDisposable> EnterAsync(string statePath, CancellationToken token) =>
        EnterAsync(statePath, token, null);

    // A controlled acquisition boundary for tests; production supplies no callback.
    internal static async Task<IDisposable> EnterAsync(string statePath, CancellationToken token, Action? afterAcquire)
    {
        var key = Path.GetFullPath(statePath);
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new Entry());
            entry.References++;
        }
        try { await entry.Gate.WaitAsync(token).ConfigureAwait(false); }
        catch { Return(key, entry); throw; }
        var lease = new Lease(key, entry);
        try
        {
            afterAcquire?.Invoke();
            // A release can win the semaphore's race with a canceled waiter.
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void Return(string key, Entry entry)
    {
        lock (Entries)
        {
            if (--entry.References != 0) return;
            Entries.Remove(key);
            entry.Gate.Dispose();
        }
    }

    private sealed class Lease(string key, Entry entry) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            entry.Gate.Release();
            Return(key, entry);
        }
    }
}
