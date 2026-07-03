using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// Customer kullanıcı → site atamaları ve kaynak limitleri. DB migration'a gerek
/// kalmaması için JSON dosyasında tutulur (config/customer-scopes.json).
/// </summary>
public sealed class CustomerScopeService(
    JsonFileStore fileStore,
    IIisSiteService iisSiteService) : ICustomerScopeService
{
    private const string StatePath = "config/customer-scopes.json";

    public async Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state.TryGetValue(userId.ToString("N"), out var entry) ? entry.Sites : [];
    }

    public async Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state.TryGetValue(userId.ToString("N"), out var entry)
            ? new CustomerScope(entry.Sites, entry.MaxSites, entry.MaxDiskMb, entry.MaxDatabases, entry.MaxMailboxes, entry.MaxScheduledTasks)
            : new CustomerScope([], 0, 0, 0, 0, 0);
    }

    public async Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default)
    {
        var normalized = siteNames
            .Select(site => site?.Trim() ?? string.Empty)
            .Where(site => site.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Atomik oku-değiştir-yaz (eşzamanlı scope değişikliklerinde kayıp güncelleme olmasın).
        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            var key = userId.ToString("N");
            state.TryGetValue(key, out var current);
            var ownerId = current?.OwnerId; // bayi sahipliğini koru
            var maxDatabases = current?.MaxDatabases ?? 0; // DB kotasını koru (ayrı setter'la set edilir)
            var maxMailboxes = current?.MaxMailboxes ?? 0; // posta kutusu kotasını koru
            var maxScheduledTasks = current?.MaxScheduledTasks ?? 0; // planlı görev kotasını koru

            if (normalized.Count == 0 && maxSites <= 0 && maxDiskMb <= 0 && maxDatabases <= 0 && maxMailboxes <= 0 && maxScheduledTasks <= 0 && string.IsNullOrEmpty(ownerId))
            {
                state.Remove(key);
            }
            else
            {
                state[key] = new ScopeEntry
                {
                    Sites = normalized,
                    MaxSites = Math.Max(0, maxSites),
                    MaxDiskMb = Math.Max(0, maxDiskMb),
                    MaxDatabases = maxDatabases,
                    MaxMailboxes = maxMailboxes,
                    MaxScheduledTasks = maxScheduledTasks,
                    OwnerId = ownerId
                };
            }

            return state;
        }, cancellationToken);
    }

    public async Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            var key = userId.ToString("N");
            if (!state.TryGetValue(key, out var entry))
            {
                entry = new ScopeEntry();
                state[key] = entry;
            }

            entry.MaxDatabases = Math.Max(0, maxDatabases);
            entry.MaxMailboxes = Math.Max(0, maxMailboxes);
            entry.MaxScheduledTasks = Math.Max(0, maxScheduledTasks);

            if (entry.OwnerId is null && entry.Sites.Count == 0 && entry.MaxSites <= 0 && entry.MaxDiskMb <= 0 && entry.MaxDatabases <= 0 && entry.MaxMailboxes <= 0 && entry.MaxScheduledTasks <= 0)
            {
                state.Remove(key);
            }

            return state;
        }, cancellationToken);
    }

    public async Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            state.Remove(userId.ToString("N"));
            return state;
        }, cancellationToken);
    }

    public async Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var name = (siteName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return;
        }

        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            foreach (var entry in state.Values)
            {
                entry.Sites.RemoveAll(site => string.Equals(site, name, StringComparison.OrdinalIgnoreCase));
            }

            return state;
        }, cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var assigned = await GetAssignedSitesAsync(userId, cancellationToken);
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var siteName in assigned)
        {
            domains.Add(siteName.ToLowerInvariant());
            try
            {
                var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
                foreach (var binding in site.Bindings)
                {
                    var host = (binding.HostName ?? string.Empty).Trim().ToLowerInvariant();
                    if (host.Length == 0)
                    {
                        continue;
                    }

                    domains.Add(host);
                    if (host.StartsWith("www.", StringComparison.Ordinal))
                    {
                        domains.Add(host[4..]);
                    }
                }
            }
            catch
            {
                // Site IIS'te bulunamadıysa yalnızca adıyla devam edilir.
            }
        }

        return domains;
    }

    public async Task<long> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var assigned = await GetAssignedSitesAsync(userId, cancellationToken);
        long totalBytes = 0;

        foreach (var siteName in assigned)
        {
            try
            {
                var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
                var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
                totalBytes += DirectorySize(root);
            }
            catch
            {
                // Erişilemeyen site atlanır.
            }
        }

        return totalBytes / (1024 * 1024);
    }

    private static long DirectorySize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // kilitli/erişilemeyen dosya atlanır
                }
            }
        }
        catch
        {
            // erişim reddi vb.
        }

        return total;
    }

    private async Task<Dictionary<string, ScopeEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync<Dictionary<string, ScopeEntry>>(StatePath, [], cancellationToken);
        return Wrap(state);
    }

    // UpdateAsync mutasyonunda kullanılan yardımcılar: boş başlangıç ve ordinal-insensitive sarma
    // (LoadAsync ile aynı davranış; anahtarlar zaten normalize GUID ama tutarlılık için korunur).
    private static Dictionary<string, ScopeEntry> NewState() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, ScopeEntry> Wrap(Dictionary<string, ScopeEntry> state) =>
        new(state, StringComparer.OrdinalIgnoreCase);

    private sealed class ScopeEntry
    {
        public List<string> Sites { get; set; } = [];
        public int MaxSites { get; set; }
        public int MaxDiskMb { get; set; }
        public int MaxDatabases { get; set; }
        public int MaxMailboxes { get; set; }
        public int MaxScheduledTasks { get; set; }

        /// <summary>Bu müşteriyi yöneten bayinin kimliği ("N" formatı); admin oluşturduysa null.</summary>
        public string? OwnerId { get; set; }
    }

    public async Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            var key = userId.ToString("N");
            if (!state.TryGetValue(key, out var entry))
            {
                entry = new ScopeEntry();
                state[key] = entry;
            }

            entry.OwnerId = ownerId?.ToString("N");

            // Sahiplik kaldırıldı ve başka veri yoksa kaydı temizle (yetim kalmasın).
            if (entry.OwnerId is null && entry.Sites.Count == 0 && entry.MaxSites <= 0 && entry.MaxDiskMb <= 0 && entry.MaxDatabases <= 0 && entry.MaxMailboxes <= 0 && entry.MaxScheduledTasks <= 0)
            {
                state.Remove(key);
            }

            return state;
        }, cancellationToken);
    }

    public async Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state.TryGetValue(userId.ToString("N"), out var entry) && Guid.TryParse(entry.OwnerId, out var owner)
            ? owner
            : null;
    }

    public async Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        var owner = ownerId.ToString("N");
        return state
            .Where(pair => string.Equals(pair.Value.OwnerId, owner, StringComparison.OrdinalIgnoreCase))
            .Select(pair => Guid.TryParse(pair.Key, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default)
    {
        var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Bayinin KENDİ doğrudan oluşturduğu/atanmış siteleri. AssignSiteToActor bayinin kendi kapsamına ekler;
        // bunlar dahil edilmezse bayi kendi açtığı siteyi göremez/yönetemez (EnsureSiteAsync 403).
        foreach (var site in await GetAssignedSitesAsync(resellerId, cancellationToken))
        {
            sites.Add(site);
        }

        // + sahip olduğu TÜM müşterilerin siteleri.
        foreach (var customerId in await GetOwnedUserIdsAsync(resellerId, cancellationToken))
        {
            foreach (var site in await GetAssignedSitesAsync(customerId, cancellationToken))
            {
                sites.Add(site);
            }
        }

        return sites;
    }

    public async Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Bayinin kendi sitelerinin mail domainleri.
        foreach (var domain in await GetAllowedMailDomainsAsync(resellerId, cancellationToken))
        {
            domains.Add(domain);
        }

        // + sahip olduğu müşterilerin mail domainleri.
        foreach (var customerId in await GetOwnedUserIdsAsync(resellerId, cancellationToken))
        {
            foreach (var domain in await GetAllowedMailDomainsAsync(customerId, cancellationToken))
            {
                domains.Add(domain);
            }
        }

        return domains;
    }

    public async Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var name = (siteName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var state = await LoadAsync(cancellationToken);
        foreach (var (key, entry) in state)
        {
            if (entry.Sites.Any(site => string.Equals(site, name, StringComparison.OrdinalIgnoreCase))
                && Guid.TryParse(key, out var userId))
            {
                return userId;
            }
        }

        return null;
    }
}
