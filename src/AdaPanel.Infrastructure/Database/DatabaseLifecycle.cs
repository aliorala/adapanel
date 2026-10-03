using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// In-process database mutation leases. Acquire subscription, engine/resource, account, then JSON state.
/// Leases are not reentrant: callbacks inside a resource lease must use the service's private core.
/// </summary>
public static class DatabaseLifecycle
{
    private static readonly GateMap<EngineGate> Engines = new(() => new EngineGate());
    private static readonly GateMap<SemaphoreSlim> Resources = new(() => new SemaphoreSlim(1, 1));
    private static readonly GateMap<SemaphoreSlim> Subscriptions = new(() => new SemaphoreSlim(1, 1));
    private static readonly GateMap<SemaphoreSlim> Accounts = new(() => new SemaphoreSlim(1, 1));

    public static Task<IDisposable> EnterSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        EnterExclusiveAsync(Subscriptions, subscriptionId.ToString("N"), cancellationToken);

    public static async Task<IDisposable> EnterResourceAsync(string engine, string name, CancellationToken cancellationToken = default)
    {
        var engineLease = await EnterEngineAsync(engine, exclusive: false, cancellationToken);
        try
        {
            var resourceLease = await EnterExclusiveAsync(Resources, DatabaseResourceIdentity.Key(engine, name), cancellationToken);
            return new EngineLease(DatabaseResourceIdentity.NormalizeEngine(engine), () =>
            {
                try { resourceLease.Dispose(); }
                finally { engineLease.Dispose(); }
            });
        }
        catch
        {
            engineLease.Dispose();
            throw;
        }
    }

    public static Task<IDisposable> EnterEngineAsync(string engine, CancellationToken cancellationToken = default) =>
        EnterEngineAsync(engine, exclusive: true, cancellationToken);

    internal static Task<IDisposable> EnterSharedEngineAsync(string engine, CancellationToken cancellationToken = default) =>
        EnterEngineAsync(engine, exclusive: false, cancellationToken);

    // The caller already owns an engine or DB lease. Taking another engine reader here
    // would deadlock behind a queued writer waiting for that very same outer lease.
    internal static Task<IDisposable> EnterAccountAsync(
        IDisposable parent, string engine, string userName, CancellationToken cancellationToken = default)
    {
        if (parent is not EngineLease lease || !lease.Active || lease.EngineKey != DatabaseResourceIdentity.NormalizeEngine(engine))
            throw new InvalidOperationException("An active lease for the same database engine is required.");
        return EnterExclusiveAsync(Accounts, DatabaseAccountIdentity.FamilyKey(engine, userName), cancellationToken);
    }

    private static async Task<IDisposable> EnterEngineAsync(string engine, bool exclusive, CancellationToken cancellationToken)
    {
        var key = DatabaseResourceIdentity.NormalizeEngine(engine);
        var entry = Engines.Rent(key);
        try
        {
            var gateLease = await entry.Value.EnterAsync(exclusive, cancellationToken);
            return new EngineLease(key, () =>
            {
                try { gateLease.Dispose(); }
                finally { Engines.Return(key, entry); }
            });
        }
        catch
        {
            Engines.Return(key, entry);
            throw;
        }
    }

    private static async Task<IDisposable> EnterExclusiveAsync(GateMap<SemaphoreSlim> gates, string key, CancellationToken cancellationToken)
    {
        var entry = gates.Rent(key);
        try
        {
            await entry.Value.WaitAsync(cancellationToken);
            return new Lease(() =>
            {
                try { entry.Value.Release(); }
                finally { gates.Return(key, entry); }
            });
        }
        catch
        {
            gates.Return(key, entry);
            throw;
        }
    }

    internal static bool HasEngineGate(string engine) => Engines.Contains(DatabaseResourceIdentity.NormalizeEngine(engine));
    internal static bool HasResourceGate(string engine, string name) => Resources.Contains(DatabaseResourceIdentity.Key(engine, name));
    internal static bool HasSubscriptionGate(Guid subscription) => Subscriptions.Contains(subscription.ToString("N"));
    internal static bool HasAccountGate(string engine, string userName) => Accounts.Contains(DatabaseAccountIdentity.FamilyKey(engine, userName));
    internal static int AccountLeaseReferences(string engine, string userName) => Accounts.References(DatabaseAccountIdentity.FamilyKey(engine, userName));

    private sealed class EngineLease(string engineKey, Action release) : IDisposable
    {
        private Action? releaseOnce = release;
        internal string EngineKey { get; } = engineKey;
        internal bool Active => Volatile.Read(ref releaseOnce) is not null;
        public void Dispose() => Interlocked.Exchange(ref releaseOnce, null)?.Invoke();
    }

    private sealed class GateMap<T>(Func<T> factory) where T : IDisposable
    {
        private readonly object sync = new();
        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

        internal sealed class Entry(T value)
        {
            internal T Value { get; } = value;
            internal int References { get; set; }
        }

        internal Entry Rent(string key)
        {
            lock (sync)
            {
                if (!entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry(factory());
                    entries.Add(key, entry);
                }
                entry.References++;
                return entry;
            }
        }

        internal void Return(string key, Entry entry)
        {
            lock (sync)
            {
                if (--entry.References != 0) return;
                entries.Remove(key);
            }
            // No holder or waiter can still use this instance after its last reference.
            entry.Value.Dispose();
        }

        internal bool Contains(string key)
        {
            lock (sync) return entries.ContainsKey(key);
        }

        internal int References(string key)
        {
            lock (sync) return entries.TryGetValue(key, out var entry) ? entry.References : 0;
        }
    }

    private sealed class EngineGate : IDisposable
    {
        // The turnstile keeps a queued whole-engine mutation from starving behind new readers.
        private readonly SemaphoreSlim turnstile = new(1, 1);
        private readonly SemaphoreSlim roomEmpty = new(1, 1);
        private readonly SemaphoreSlim readersMutex = new(1, 1);
        private int readers;

        internal async Task<IDisposable> EnterAsync(bool exclusive, CancellationToken cancellationToken)
        {
            await turnstile.WaitAsync(cancellationToken);
            if (exclusive)
            {
                try { await roomEmpty.WaitAsync(cancellationToken); }
                catch { turnstile.Release(); throw; }
                return new Lease(() =>
                {
                    roomEmpty.Release();
                    turnstile.Release();
                });
            }
            turnstile.Release();

            await readersMutex.WaitAsync(cancellationToken);
            try
            {
                if (readers == 0) await roomEmpty.WaitAsync(cancellationToken);
                readers++;
            }
            finally { readersMutex.Release(); }

            return new Lease(() =>
            {
                readersMutex.Wait();
                try
                {
                    if (--readers == 0) roomEmpty.Release();
                }
                finally { readersMutex.Release(); }
            });
        }

        public void Dispose()
        {
            turnstile.Dispose();
            roomEmpty.Dispose();
            readersMutex.Dispose();
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? releaseOnce = release;
        public void Dispose() => Interlocked.Exchange(ref releaseOnce, null)?.Invoke();
    }
}
