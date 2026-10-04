namespace AdaPanel.Infrastructure.Ftp;

// Windows local account names are machine-wide and case insensitive. Hold this lease through
// native work and JSON publication; a per-file JSON lock alone cannot serialize account effects.
internal static class FtpAccountLifecycle
{
    private sealed class Entry { internal readonly SemaphoreSlim Gate = new(1, 1); internal int References; }
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    internal static int References(string name) { lock (Entries) return Entries.GetValueOrDefault(name.Trim())?.References ?? 0; }
    internal static async Task<IDisposable> EnterAsync(string name, CancellationToken token)
    {
        var key = name.Trim();
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new Entry());
            entry.References++;
        }
        try { await entry.Gate.WaitAsync(token).ConfigureAwait(false); }
        catch { Return(key, entry); throw; }
        var lease = new Lease(key, entry);
        try { token.ThrowIfCancellationRequested(); return lease; }
        catch { lease.Dispose(); throw; }
    }
    // Infrastructure repair may already hold the infrastructure gate, while Create holds
    // its account gate before entering infrastructure. Never wait in the reverse order.
    internal static IDisposable? TryEnter(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = name.Trim();
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new Entry());
            entry.References++;
        }
        bool entered;
        try { entered = entry.Gate.Wait(0, token); }
        catch { Return(key, entry); throw; }
        if (!entered) { Return(key, entry); return null; }
        var lease = new Lease(key, entry);
        try { token.ThrowIfCancellationRequested(); return lease; }
        catch { lease.Dispose(); throw; }
    }
    private static void Return(string key, Entry entry)
    {
        lock (Entries)
        {
            if (--entry.References != 0) return;
            Entries.Remove(key); entry.Gate.Dispose();
        }
    }
    private sealed class Lease(string key, Entry entry) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            entry.Gate.Release(); Return(key, entry);
        }
    }
}
