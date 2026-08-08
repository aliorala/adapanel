using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// Customer kullanıcı → site atamaları ve kaynak limitleri. DB migration'a gerek
/// kalmaması için JSON dosyasında tutulur (config/customer-scopes.json).
/// </summary>
public sealed class CustomerScopeService(
    JsonFileStore fileStore,
    IServicePlanService servicePlans,
    IIisSiteService iisSiteService) : ICustomerScopeService
{
    private const string StatePath = "config/customer-scopes.json";

    public async Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return ResolveEffective(state, userId)?.Sites ?? [];
    }

    public async Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        var entry = ResolveEffective(state, userId);
        if (entry is null)
        {
            return new CustomerScope([], 0, 0, 0, 0, 0);
        }

        // LİMİTLER PLANDAN ÇÖZÜLÜR. Kayıttaki değerler yalnız plana bağlı OLMAYAN abonelikler
        // (özel limit) içindir. Bu sıralama sayesinde bir planı düzenlemek, ona bağlı bütün
        // müşterileri anında etkiler — plan bir kopya değil bağdır.
        ServicePlanDto? plan = null;
        if (!string.IsNullOrWhiteSpace(entry.PlanId))
        {
            plan = await servicePlans.GetPlanAsync(entry.PlanId!, cancellationToken);
            // Plan SİLİNMİŞSE kayıttaki son değerlerle devam edilir: limitleri birden sıfırlamak
            // (yani "sınırsız" yapmak) sessiz bir kota kaybı olurdu.
        }

        return new CustomerScope(
            entry.Sites,
            plan?.MaxSites ?? entry.MaxSites,
            plan?.MaxDiskMb ?? entry.MaxDiskMb,
            plan?.MaxDatabases ?? entry.MaxDatabases,
            plan?.MaxMailboxes ?? entry.MaxMailboxes,
            plan?.MaxScheduledTasks ?? entry.MaxScheduledTasks,
            plan?.MaxFtpUsers ?? entry.MaxFtpUsers,
            plan?.MaxTrafficGbPerMonth ?? entry.MaxTrafficGbPerMonth,
            entry.PlanId ?? string.Empty,
            plan?.Name ?? string.Empty,
            entry.ExpiresAtUtc,
            entry.SuspendedAtUtc,
            entry.SuspendReason ?? string.Empty);
    }

    // Alt-kullanıcı (ParentUserId set) parent'ın kaydını miras alır. TEK sıçrama: parent'ın kendisi
    // alt-kullanıcı OLAMAZ (oluşturmada engellenir) → döngü/sonsuz zincir yok.
    private static ScopeEntry? ResolveEffective(Dictionary<string, ScopeEntry> state, Guid userId)
    {
        if (!state.TryGetValue(userId.ToString("N"), out var entry))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(entry.ParentUserId) && state.TryGetValue(entry.ParentUserId, out var parent))
        {
            return parent;
        }

        return entry;
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
            var parentUserId = current?.ParentUserId; // alt-kullanıcı bağını koru
            var subUserSections = current?.SubUserSections; // alt-kullanıcı bölüm izinlerini koru

            if (normalized.Count == 0 && maxSites <= 0 && maxDiskMb <= 0 && maxDatabases <= 0 && maxMailboxes <= 0 && maxScheduledTasks <= 0 && string.IsNullOrEmpty(ownerId) && string.IsNullOrEmpty(parentUserId))
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
                    OwnerId = ownerId,
                    ParentUserId = parentUserId,
                    SubUserSections = subUserSections
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

            if (entry.OwnerId is null && entry.ParentUserId is null && entry.Sites.Count == 0 && entry.MaxSites <= 0 && entry.MaxDiskMb <= 0 && entry.MaxDatabases <= 0 && entry.MaxMailboxes <= 0 && entry.MaxScheduledTasks <= 0)
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

        /// <summary>Bu kullanıcı bir alt-kullanıcıysa, aboneliğini paylaştığı parent müşterinin kimliği ("N"); değilse null.</summary>
        public string? ParentUserId { get; set; }

        /// <summary>
        /// Alt-kullanıcının erişebildiği panel bölümleri (Plesk "additional user" izinleri).
        /// null = kısıtsız. Yalnız ParentUserId doluyken anlamlıdır.
        /// </summary>
        public List<string>? SubUserSections { get; set; }

        /// <summary>
        /// Bağlı servis planının kimliği; boş ise limitler bu kayıttaki değerlerden okunur.
        /// <para>
        /// Plan bir KOPYA değil BAĞ: doluyken limitler okuma anında plandan çözülür, dolayısıyla
        /// planı değiştirmek bağlı bütün abonelikleri aynı anda etkiler. Önceden plan yalnızca
        /// oluşturma formuna kopyalanıyordu ve sonradan yapılan plan değişikliği hiçbir müşteriye
        /// yansımıyordu.
        /// </para>
        /// </summary>
        public string? PlanId { get; set; }

        /// <summary>Aboneliğin bitiş tarihi; null = süresiz.</summary>
        public DateTime? ExpiresAtUtc { get; set; }

        /// <summary>Askıya alındıysa ne zaman; null = etkin.</summary>
        public DateTime? SuspendedAtUtc { get; set; }

        /// <summary>Askı sebebi (sabit anahtar: "traffic" | "expired" | "manual").</summary>
        public string? SuspendReason { get; set; }

        public int MaxFtpUsers { get; set; }

        public int MaxTrafficGbPerMonth { get; set; }
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
            if (entry.OwnerId is null && entry.ParentUserId is null && entry.Sites.Count == 0 && entry.MaxSites <= 0 && entry.MaxDiskMb <= 0 && entry.MaxDatabases <= 0 && entry.MaxMailboxes <= 0 && entry.MaxScheduledTasks <= 0)
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

    public async Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default)
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

            // Alt-kullanıcının KENDI kaynağı olmaz (parent'ınkini miras alır) → varsa kendi site/limitlerini temizle.
            entry.ParentUserId = parentUserId?.ToString("N");
            if (entry.ParentUserId is not null)
            {
                entry.Sites = [];
                entry.MaxSites = 0;
                entry.MaxDiskMb = 0;
                entry.MaxDatabases = 0;
                entry.MaxMailboxes = 0;
                entry.MaxScheduledTasks = 0;
            }
            else
            {
                // Bağ kaldırıldı → bölüm izinleri anlamını yitirir, sızıntı kalmasın.
                entry.SubUserSections = null;
            }

            if (entry.OwnerId is null && entry.ParentUserId is null && entry.Sites.Count == 0 && entry.MaxSites <= 0 && entry.MaxDiskMb <= 0 && entry.MaxDatabases <= 0 && entry.MaxMailboxes <= 0 && entry.MaxScheduledTasks <= 0)
            {
                state.Remove(key);
            }

            return state;
        }, cancellationToken);
    }

    public async Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state.TryGetValue(userId.ToString("N"), out var entry) && Guid.TryParse(entry.ParentUserId, out var parent)
            ? parent
            : null;
    }

    public async Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        var parent = parentUserId.ToString("N");
        return state
            .Where(pair => string.Equals(pair.Value.ParentUserId, parent, StringComparison.OrdinalIgnoreCase))
            .Select(pair => Guid.TryParse(pair.Key, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
    }

    public async Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default)
    {
        // Bilinmeyen anahtarlar elenir; TÜM bölümler seçiliyse null (kısıtsız) olarak saklanır
        // (yeni bölüm eklendiğinde "her şeye izinli" kullanıcı otomatik kapsar).
        List<string>? normalized = null;
        if (sections is not null)
        {
            normalized = sections
                .Select(section => (section ?? string.Empty).Trim().ToLowerInvariant())
                .Where(CustomerPanelSections.IsKnown)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalized.Count >= CustomerPanelSections.All.Count)
            {
                normalized = null;
            }
        }

        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            var key = userId.ToString("N");
            if (!state.TryGetValue(key, out var entry))
            {
                entry = new ScopeEntry();
                state[key] = entry;
            }

            entry.SubUserSections = normalized;
            return state;
        }, cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state.TryGetValue(userId.ToString("N"), out var entry) ? entry.SubUserSections : null;
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

    // --- Abonelik iş modeli ---

    /// <inheritdoc />
    public async Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default)
    {
        var kirpilmis = (planId ?? string.Empty).Trim();

        // Plan ÖNCE okunur: geçerlilik süresini buradan hesaplayacağız ve olmayan bir plana
        // bağlanmak sessizce "limitsiz"e düşmek olurdu.
        ServicePlanDto? plan = null;
        if (kirpilmis.Length > 0)
        {
            plan = await servicePlans.GetPlanAsync(kirpilmis, cancellationToken)
                   ?? throw new AppException("plans.not_found", 404, 404);
        }

        await fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            var key = userId.ToString("N");
            if (!state.TryGetValue(key, out var entry))
            {
                entry = new ScopeEntry();
                state[key] = entry;
            }

            entry.PlanId = plan is null ? null : plan.Id;

            if (plan is not null)
            {
                // Bağ kurulurken kayda da yazıyoruz: plan sonradan SİLİNİRSE limitler sıfırlanıp
                // abonelik sessizce "sınırsız" hâle gelmesin (bkz. GetScopeAsync).
                entry.MaxSites = plan.MaxSites;
                entry.MaxDiskMb = plan.MaxDiskMb;
                entry.MaxDatabases = plan.MaxDatabases;
                entry.MaxMailboxes = plan.MaxMailboxes;
                entry.MaxScheduledTasks = plan.MaxScheduledTasks;
                entry.MaxFtpUsers = plan.MaxFtpUsers;
                entry.MaxTrafficGbPerMonth = plan.MaxTrafficGbPerMonth;

                // Süre YALNIZCA daha önce belirlenmemişse hesaplanır. Aksi hâlde plan her
                // güncellendiğinde müşterinin bitiş tarihi ileri kayar ve süre fiilen işlemezdi.
                if (plan.ValidityDays > 0 && entry.ExpiresAtUtc is null)
                {
                    entry.ExpiresAtUtc = DateTime.UtcNow.Date.AddDays(plan.ValidityDays);
                }
            }

            return state;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default)
        => DuzenleAsync(userId, entry => entry.ExpiresAtUtc = expiresAtUtc, cancellationToken);

    /// <inheritdoc />
    public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default)
        => DuzenleAsync(userId, entry =>
        {
            // ZATEN ASKIDAYSA tarih GÜNCELLENMEZ: her denetim turunda tazelemek, "ne zamandır
            // askıda" bilgisini yok ederdi.
            if (entry.SuspendedAtUtc is null)
            {
                entry.SuspendedAtUtc = DateTime.UtcNow;
            }

            entry.SuspendReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim();
        }, cancellationToken);

    /// <inheritdoc />
    public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default)
        => DuzenleAsync(userId, entry =>
        {
            entry.SuspendedAtUtc = null;
            entry.SuspendReason = null;
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state
            .Where(item => item.Value.SuspendedAtUtc is not null && Guid.TryParseExact(item.Key, "N", out _))
            .Select(item => Guid.ParseExact(item.Key, "N"))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken);
        return state
            // Alt-kullanıcının kendi aboneliği yoktur; parent'ınkini miras alır.
            .Where(item => string.IsNullOrEmpty(item.Value.ParentUserId) && Guid.TryParseExact(item.Key, "N", out _))
            .Select(item => Guid.ParseExact(item.Key, "N"))
            .ToArray();
    }

    /// <summary>Tek bir abonelik kaydını atomik olarak düzenler (yol başına kilit).</summary>
    private Task DuzenleAsync(Guid userId, Action<ScopeEntry> duzenle, CancellationToken cancellationToken)
        => fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = Wrap(existing);
            var key = userId.ToString("N");
            if (!state.TryGetValue(key, out var entry))
            {
                entry = new ScopeEntry();
                state[key] = entry;
            }

            duzenle(entry);
            return state;
        }, cancellationToken);

}
