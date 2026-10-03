using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// Captures a site generation across a database wait. Only short identity/metadata work holds
/// the IIS gate; acquire any database resource lease before entering Target.RunAsync.
/// </summary>
public static class SiteMutationGuard
{
    public enum MutationKind { Create, Delete, IdentityChange }
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Pending = new(StringComparer.Ordinal);
    internal sealed class Entry
    {
        public int References;
        public int ActiveUses;
        public long Version;
        public bool NativeMutationInProgress;
        public bool Deleting;
        public Creation? ProvisioningOwner;
    }
    private static string Key(string name) => name.Trim().ToUpperInvariant();
    private static AppException Changed() => new("sites.target_changed", 409, 409);

    public static async Task<Target> CaptureAsync(IIisSiteService sites, IConcurrencyLock gates, string name, CancellationToken token)
    {
        var key = Key(name);
        Entry entry;
        long version;
        lock (Sync)
        {
            entry = Rent(key);
            if (entry.Deleting || entry.ProvisioningOwner is not null || entry.NativeMutationInProgress)
            { Return(key, entry); throw Changed(); }
            version = entry.Version;
        }
        try
        {
            // Keep evidence from before the first IIS wait. A same-name replacement while
            // capture itself queues must not become the target approved by this request.
            var observed = await sites.GetSiteAsync(name, token);
            return await gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
            {
                var target = new Target(key, entry, version, observed, sites, gates);
                await target.ValidateAsync(token);
                return target;
            }, token);
        }
        catch { lock (Sync) Return(key, entry); throw; }
    }

    /// <summary>Observe native creation before it starts, so a replacement before its return cannot be adopted.</summary>
    public static Task<Creation> WatchCreationAsync(IConcurrencyLock gates, string name, CancellationToken token,
        bool provisioning = false) =>
        gates.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
        {
            var key = Key(name);
            lock (Sync)
            {
                var entry = Rent(key);
                if (entry.Deleting || entry.ActiveUses > 0 || entry.ProvisioningOwner is not null)
                { Return(key, entry); throw Changed(); }
                var creation = new Creation(key, entry, entry.Version, gates);
                // Keep unfinished provisioning private from before native create until success or
                // rollback finishes, including intervals where its generation is not actively pinned.
                if (provisioning) entry.ProvisioningOwner = creation;
                return Task.FromResult(creation);
            }
        }, token);

    /// <summary>Used by native create/delete inside the same gate as CommitChanges.</summary>
    public static Task<T> RunMutationAsync<T>(IConcurrencyLock gates, string name, bool creating,
        Func<Task<T>> operation, CancellationToken token) =>
        RunMutationAsync(gates, name, creating ? MutationKind.Create : MutationKind.Delete, operation, token);

    /// <summary>Identity changes are foreign to both provisioning and a cascade's native delete.</summary>
    public static Task<T> RunMutationAsync<T>(IConcurrencyLock gates, string name, MutationKind mutation,
        Func<Task<T>> operation, CancellationToken token) => gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
    {
        var key = Key(name);
        Entry entry;
        lock (Sync)
        {
            // Keep the entry even if nobody captured this site before the native operation.
            // Otherwise a first capture during CommitChanges can miss its version increment
            // and adopt a replacement that reused the old IIS ID and paths.
            entry = Rent(key);
            if (entry.ActiveUses > 0 || (mutation != MutationKind.Delete && entry.Deleting) ||
                (mutation == MutationKind.IdentityChange && entry.ProvisioningOwner is not null))
            { Return(key, entry); throw Changed(); }
            entry.NativeMutationInProgress = true;
            // Exactly one increment preserves the invocation-specific creation watcher.
            entry.Version++;
        }
        try { return await operation(); }
        finally
        {
            lock (Sync)
            {
                entry.NativeMutationInProgress = false;
                Return(key, entry);
            }
        }
    }, token);

    /// <summary>Prevents new bindings/recreation while cascade cleans metadata, without holding IIS.</summary>
    public static Task<IDisposable> BeginDeletionAsync(IConcurrencyLock gates, string name, CancellationToken token) =>
        gates.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
        {
            var key = Key(name);
            lock (Sync)
            {
                var entry = Rent(key);
                if (entry.Deleting || entry.ActiveUses > 0 || entry.ProvisioningOwner is not null)
                { Return(key, entry); throw Changed(); }
                entry.Deleting = true;
                entry.Version++;
                return Task.FromResult<IDisposable>(new Deletion(key, entry));
            }
        }, token);

    private static Entry Rent(string key)
    {
        if (!Pending.TryGetValue(key, out var entry)) Pending.Add(key, entry = new Entry());
        entry.References++;
        return entry;
    }
    private static void Return(string key, Entry entry)
    {
        if (--entry.References == 0) Pending.Remove(key);
    }
    internal static int PendingCount { get { lock (Sync) return Pending.Count; } }

    public sealed class Creation : IDisposable
    {
        private readonly string key;
        private readonly Entry entry;
        private readonly long version;
        private readonly IConcurrencyLock gates;
        private bool disposed;
        internal SiteDto? CommittedSite { get; private set; }
        internal Creation(string key, Entry entry, long version, IConcurrencyLock gates)
        { this.key = key; this.entry = entry; this.version = version; this.gates = gates; }

        /// <summary>Called only by this native invocation, after commit and fresh measurement under IIS.</summary>
        internal void RecordCommitted(SiteDto site)
        {
            lock (Sync)
            {
                Check();
                // MapSite's display fallbacks are not evidence for deleting a committed resource.
                if (Key(site.Name) != key || site.Id <= 0 || string.IsNullOrWhiteSpace(site.PhysicalPath) ||
                    string.IsNullOrWhiteSpace(site.ApplicationRootPath) || string.IsNullOrWhiteSpace(site.ApplicationPoolName))
                    throw Changed();
                CommittedSite = site;
            }
        }

        public Task<Target> CaptureCreatedAsync(IIisSiteService sites, SiteDto expected, CancellationToken token) =>
            gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
            {
                lock (Sync) Check();
                SiteDto current;
                try { current = await sites.GetSiteAsync(expected.Name, token); }
                catch (AppException error) when (error.HttpStatusCode == 404) { throw Changed(); }
                if (Key(expected.Name) != key || !SameIdentity(expected, current)) throw Changed();
                lock (Sync)
                {
                    Check();
                    entry.References++;
                    return new Target(key, entry, entry.Version, current, sites, gates, this);
                }
            }, token);
        private void Check()
        {
            if (disposed || entry.Deleting || entry.Version != version + 1 ||
                (entry.ProvisioningOwner is not null && entry.ProvisioningOwner != this)) throw Changed();
        }
        public void Dispose()
        {
            lock (Sync)
            {
                if (disposed) return;
                disposed = true;
                if (entry.ProvisioningOwner == this) entry.ProvisioningOwner = null;
                Return(key, entry);
            }
        }
    }

    public sealed class Target : IDisposable
    {
        private readonly string key;
        private readonly Entry entry;
        private readonly long version;
        private readonly SiteDto original;
        private readonly IIisSiteService sites;
        private readonly IConcurrencyLock gates;
        private readonly Creation? creator;
        private bool disposed;
        public SiteDto Site => original;
        internal Target(string key, Entry entry, long version, SiteDto original, IIisSiteService sites, IConcurrencyLock gates,
            Creation? creator = null)
        { this.key = key; this.entry = entry; this.version = version; this.original = original; this.sites = sites; this.gates = gates; this.creator = creator; }

        public Task RunAsync(Func<SiteDto, Task> operation, CancellationToken token) =>
            gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
            {
                await operation(await ValidateAsync(token));
            }, token);

        /// <summary>Pin this generation for a long operation without holding IIS. Native mutation fails while pinned.</summary>
        public Task<IDisposable> ReserveAsync(CancellationToken token) =>
            ReserveAsync(null, token);

        public Task<IDisposable> ReserveAsync(Func<SiteDto, CancellationToken, Task>? beforeUse, CancellationToken token) =>
            gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
            {
                var current = await ValidateAsync(token);
                if (beforeUse is not null) await beforeUse(current, token);
                token.ThrowIfCancellationRequested();
                lock (Sync)
                {
                    Check();
                    entry.ActiveUses++;
                    entry.References++;
                    return (IDisposable)new Use(key, entry);
                }
            }, token);

        /// <summary>Validate an already measured snapshot while the caller owns the IIS gate.</summary>
        public void ValidateSnapshot(SiteDto current)
        {
            lock (Sync)
            {
                Check();
                if (!SameIdentity(original, current)) throw Changed();
            }
        }

        // Native commit and pinning the newly measured identity share a single IIS lease.
        // The write callback must not acquire that lease again.
        internal Task<PinnedSite> ChangeIdentityAsync(Func<SiteDto, Task<SiteDto>> write,
            Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken token) =>
            gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
            {
                var current = await ValidateAsync(token);
                await beforeMutation(current, token);
                token.ThrowIfCancellationRequested();
                lock (Sync)
                {
                    Check();
                    if (entry.ActiveUses > 0 || entry.ProvisioningOwner is not null) throw Changed();
                    entry.References++;
                    entry.NativeMutationInProgress = true;
                    entry.Version++;
                }
                try
                {
                    var committed = await write(current);
                    if (Key(committed.Name) != key || committed.Id != current.Id ||
                        string.IsNullOrWhiteSpace(committed.PhysicalPath) ||
                        string.IsNullOrWhiteSpace(committed.ApplicationRootPath) ||
                        string.IsNullOrWhiteSpace(committed.ApplicationPoolName)) throw Changed();
                    lock (Sync)
                    {
                        entry.ActiveUses++;
                        entry.References++;
                        return new PinnedSite(committed, new Use(key, entry));
                    }
                }
                finally
                {
                    lock (Sync)
                    {
                        entry.NativeMutationInProgress = false;
                        Return(key, entry);
                    }
                }
            }, token);

        /// <summary>After releasing a use, reserve rollback only if its original generation still exists.</summary>
        public Task<IDisposable> BeginDeletionAsync(CancellationToken token) =>
            gates.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
            {
                await ValidateAsync(token);
                lock (Sync)
                {
                    Check();
                    if (entry.ActiveUses > 0) throw Changed();
                    entry.References++;
                    entry.Deleting = true;
                    entry.Version++;
                    return (IDisposable)new Deletion(key, entry);
                }
            }, token);

        private void Check()
        {
            if (disposed || entry.Deleting || entry.NativeMutationInProgress || entry.Version != version ||
                (entry.ProvisioningOwner is not null && entry.ProvisioningOwner != creator)) throw Changed();
        }
        internal async Task<SiteDto> ValidateAsync(CancellationToken token)
        {
            lock (Sync) Check();
            SiteDto current;
            try { current = await sites.GetSiteAsync(original.Name, token); }
            catch (AppException error) when (error.HttpStatusCode == 404) { throw Changed(); }
            if (!SameIdentity(original, current)) throw Changed();
            token.ThrowIfCancellationRequested();
            lock (Sync) Check();
            return current;
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (disposed) return;
                disposed = true;
                Return(key, entry);
            }
        }
    }

    internal sealed class PinnedSite(SiteDto site, IDisposable reservation) : IDisposable
    {
        public SiteDto Site { get; } = site;
        public void Dispose() => reservation.Dispose();
    }

    private static bool SameIdentity(SiteDto original, SiteDto current) =>
        original.Id == current.Id && Same(original.Name, current.Name) &&
        Same(original.PhysicalPath, current.PhysicalPath) && Same(original.ApplicationRootPath, current.ApplicationRootPath) &&
        Same(original.ApplicationPoolName, current.ApplicationPoolName);
    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed class Use(string key, Entry entry) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (Sync)
            {
                if (disposed) return;
                disposed = true;
                entry.ActiveUses--;
                Return(key, entry);
            }
        }
    }

    private sealed class Deletion(string key, Entry entry) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            lock (Sync)
            {
                if (disposed) return;
                disposed = true;
                entry.Deleting = false;
                entry.Version++;
                Return(key, entry);
            }
        }
    }
}
