namespace AdaPanel.Infrastructure.Diagnostics;

internal static class ErrorReportDeliveryLifecycle
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
    }
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string path, string purpose) => Path.GetFullPath(path) + "|" + purpose;

    internal static int SendReferences(string path)
    {
        lock (Entries) return Entries.GetValueOrDefault(Key(path, "send"))?.References ?? 0;
    }
    internal static Task<IDisposable> EnterSendAsync(string path, CancellationToken token) => EnterAsync(Key(path, "send"), token);
    internal static Task<IDisposable> EnterSettingsAsync(string path, CancellationToken token) => EnterAsync(Key(path, "settings"), token);
    private static async Task<IDisposable> EnterAsync(string key, CancellationToken token)
    {
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
