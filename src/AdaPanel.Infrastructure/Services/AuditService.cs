using System.Runtime.CompilerServices;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Services;

public sealed class AuditService(
    AdaPanelDbContext dbContext,
    ICustomerScopeService customerScopes,
    JsonFileStore fileStore) : IAuditService
{
    private const string RetentionStatePath = "config/audit-retention.json";

    /// <summary>Varsayılan saklama: 365 gün. Sıfır (sınırsız) DEĞİL — bkz. <see cref="GetRetentionAsync"/>.</summary>
    private const int DefaultRetentionDays = 365;

    /// <inheritdoc />
    public Task WriteAsync(
        string action,
        string resource,
        string details,
        Guid? userId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
        // Tek yazma yolu: konu belirtilmeyen çağrı da aynı geçitten geçer, yoksa sır temizliği gibi
        // korumaların birinde uygulanıp diğerinde unutulması an meselesi olurdu.
        => WriteForSubscriptionAsync(action, resource, details, userId, null, ipAddress, cancellationToken);

    /// <inheritdoc />
    public async Task WriteForSubscriptionAsync(
        string action,
        string resource,
        string details,
        Guid? userId,
        Guid? subscriptionUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        // SIR TEMİZLİĞİ + KIRPMA burada, yazma yolunun tek geçidinde. Gerekçe: AuditSecretRedactor.
        var entry = new AuditLog
        {
            UserId = userId,
            SubscriptionUserId = subscriptionUserId,
            Action = AuditSecretRedactor.SanitizeShortField(action),
            Resource = AuditSecretRedactor.SanitizeShortField(resource),
            Details = AuditSecretRedactor.SanitizeDetails(details),
            IpAddress = ipAddress,
            CreatedAtUtc = DateTime.UtcNow
        };

        dbContext.AuditLogs.Add(entry);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Yazılamayan kaydı İZLEMEDEN ÇIKAR. Aksi hâlde aynı DbContext'i kullanan bir sonraki
            // SaveChanges (asıl işlemin kendi kaydı) bu bozuk girişi tekrar denerdi; yani denetim
            // yazımının hatası, alakasız bir işlemi de düşürürdü.
            dbContext.Entry(entry).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        var cappedTake = Math.Clamp(take, 1, 100);

        var users = await ReadUserNamesAsync(cancellationToken);

        var items = await dbContext.AuditLogs
            .AsNoTracking()
            .OrderByDescending(log => log.CreatedAtUtc)
            .Take(cappedTake)
            .ToListAsync(cancellationToken);

        return items.Select(log => ToDto(log, users)).ToArray();
    }

    public async Task<AuditLogPageDto> SearchAsync(
        AuditLogQuery query,
        AuditVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        var (page, pageSize) = AuditLogQueryEngine.NormalizePaging(query);
        var users = await ReadUserNamesAsync(cancellationToken);
        var filtered = BuildQuery(query, visibility, users);

        var total = await filtered.CountAsync(cancellationToken);

        var items = await filtered
            .OrderByDescending(log => log.CreatedAtUtc)
            .ThenByDescending(log => log.Id) // aynı milisaniyedeki kayıtlar sayfalar arasında kaymasın
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // Açılır listedeki eylem türleri KAPSAMA göre okunur: bir kiracı, başka kiracının hangi
        // eylemleri yaptığını filtre listesinden bile öğrenememeli.
        var actions = await AuditLogQueryEngine
            .ApplyVisibility(dbContext.AuditLogs.AsNoTracking(), visibility)
            .Select(log => log.Action)
            .Distinct()
            .OrderBy(action => action)
            .Take(200)
            .ToListAsync(cancellationToken);

        return new AuditLogPageDto
        {
            Items = items.Select(log => ToDto(log, users)).ToArray(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            AvailableActions = actions
        };
    }

    public async IAsyncEnumerable<AuditLogDto> StreamAsync(
        AuditLogQuery query,
        AuditVisibility visibility,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var users = await ReadUserNamesAsync(cancellationToken);

        // Sayfalama UYGULANMAZ: dışa aktarma filtreye uyan HER kaydı verir. AsAsyncEnumerable ile
        // satırlar veritabanından akarak gelir; listeye toplanmaz.
        var stream = BuildQuery(query, visibility, users)
            .OrderByDescending(log => log.CreatedAtUtc)
            .ThenByDescending(log => log.Id)
            .AsAsyncEnumerable();

        await foreach (var log in stream.WithCancellation(cancellationToken))
        {
            yield return ToDto(log, users);
        }
    }

    public async Task<AuditVisibility> ResolveVisibilityAsync(
        Guid userId,
        UserRole role,
        CancellationToken cancellationToken = default)
    {
        // Yönetici ve operatör sunucu genelini görür (CustomerAccess'teki kapsamsız roller).
        if (role is UserRole.Administrator or UserRole.Operator)
        {
            return AuditVisibility.Unlimited;
        }

        if (role == UserRole.Reseller)
        {
            // Bayi: kendisi + sahip olduğu müşteriler + o müşterilerin alt kullanıcıları.
            var ids = new List<Guid> { userId };
            foreach (var customerId in await customerScopes.GetOwnedUserIdsAsync(userId, cancellationToken))
            {
                ids.Add(customerId);
                ids.AddRange(await customerScopes.GetSubUserIdsAsync(customerId, cancellationToken));
            }

            return AuditVisibility.ForUsers(ids);
        }

        if (role == UserRole.Customer)
        {
            // Müşteri: KENDİ aboneliği. Alt kullanıcıysa abonelik sahibi parent'ın kümesi geçerlidir —
            // parent ve alt kullanıcılar aynı kaynakları paylaşır, dolayısıyla aynı izi görürler.
            var subscriptionOwner = await customerScopes.GetParentSubscriptionAsync(userId, cancellationToken) ?? userId;
            var ids = new List<Guid> { userId, subscriptionOwner };
            ids.AddRange(await customerScopes.GetSubUserIdsAsync(subscriptionOwner, cancellationToken));
            return AuditVisibility.ForUsers(ids);
        }

        // Tanınmayan rol → hiçbir şey. Yeni bir rol eklendiğinde varsayılan "her şeyi gör" olsaydı,
        // rolü eklemeyi unutan kişi farkında olmadan sunucu genelini açardı.
        return AuditVisibility.None;
    }

    public async Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadRetentionAsync(cancellationToken);

        return new AuditRetentionSettingsDto
        {
            RetentionDays = state.RetentionDays,
            LastPurgeAtUtc = state.LastPurgeAtUtc,
            LastPurgedCount = state.LastPurgedCount,
            TotalCount = await dbContext.AuditLogs.LongCountAsync(cancellationToken),
            OldestAtUtc = await dbContext.AuditLogs
                .OrderBy(log => log.CreatedAtUtc)
                .Select(log => (DateTime?)log.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
        };
    }

    public async Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        // Üst sınır 10 yıl: "sonsuz" isteyen 0 yazar; arada bir yerde sessizce anlamsız bir değer olmaz.
        var normalized = Math.Clamp(retentionDays, 0, 3650);

        await fileStore.UpdateAsync<RetentionState>(
            RetentionStatePath,
            new RetentionState(),
            state =>
            {
                state.RetentionDays = normalized;
                return state;
            },
            cancellationToken);

        return await GetRetentionAsync(cancellationToken);
    }

    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadRetentionAsync(cancellationToken);
        if (state.RetentionDays <= 0)
        {
            return 0; // sınırsız saklama: temizlik YAPILMAZ (sessizce her şeyi silmek felaket olurdu)
        }

        var cutoff = DateTime.UtcNow.AddDays(-state.RetentionDays);

        // ExecuteDelete: kayıtlar belleğe çekilmeden tek SQL ile silinir. Milyonluk bir tabloyu
        // önce nesneye çevirmek, temizliğin kendisini bir bellek sorununa dönüştürürdü.
        var deleted = await dbContext.AuditLogs
            .Where(log => log.CreatedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        await fileStore.UpdateAsync<RetentionState>(
            RetentionStatePath,
            new RetentionState(),
            current =>
            {
                current.LastPurgeAtUtc = DateTime.UtcNow;
                current.LastPurgedCount = deleted;
                return current;
            },
            cancellationToken);

        return deleted;
    }

    /// <summary>Kapsam + ölçütleri uygular (arama ve dışa aktarma AYNI yolu kullanır).</summary>
    private IQueryable<AuditLog> BuildQuery(
        AuditLogQuery query,
        AuditVisibility visibility,
        IReadOnlyDictionary<Guid, string> users)
    {
        var source = AuditLogQueryEngine.ApplyVisibility(dbContext.AuditLogs.AsNoTracking(), visibility);

        // Kayıtta kullanıcı ADI yok (kimlik var); ad ölçütü önce kimliklere çevrilir. Eşleşen
        // kullanıcı yoksa boş koleksiyon geçilir → sonuç boş olur ("filtre yokmuş gibi" davranmaz).
        IReadOnlyCollection<Guid>? nameMatches = null;
        var userName = query.UserName.Trim();
        if (userName.Length > 0)
        {
            nameMatches = users
                .Where(pair => pair.Value.Contains(userName, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();
        }

        return AuditLogQueryEngine.ApplyFilters(source, query, nameMatches);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ReadUserNamesAsync(CancellationToken cancellationToken) =>
        await dbContext.Users
            .AsNoTracking()
            .Select(user => new { user.Id, user.UserName })
            .ToDictionaryAsync(user => user.Id, user => user.UserName, cancellationToken);

    private async Task<RetentionState> LoadRetentionAsync(CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync(RetentionStatePath, new RetentionState(), cancellationToken);
        state.RetentionDays = Math.Clamp(state.RetentionDays, 0, 3650);
        return state;
    }

    private static AuditLogDto ToDto(AuditLog log, IReadOnlyDictionary<Guid, string> users) => new()
    {
        Id = log.Id,
        UserId = log.UserId,
        UserName = log.UserId.HasValue && users.TryGetValue(log.UserId.Value, out var userName)
            ? userName
            : "system",
        Action = log.Action,
        Resource = log.Resource,
        Details = log.Details,
        IpAddress = log.IpAddress ?? string.Empty,
        CreatedAtUtc = log.CreatedAtUtc
    };

    /// <summary>
    /// Saklama politikasının kalıcı hâli. DB şeması değişmesin diye JSON'da tutulur (paneldeki
    /// diğer politika ayarlarıyla — automation.json, error-reporting.json — aynı kalıp).
    /// </summary>
    private sealed class RetentionState
    {
        /// <summary>
        /// Varsayılan 365 gün. SIFIR (sınırsız) DEĞİL: panelde bugüne kadar hiç temizlik yoktu ve
        /// kayıtlar sonsuza kadar birikiyordu; varsayılanı yine sınırsız bırakmak, politikayı
        /// "yönetici bulup açarsa" çalışan bir süse çevirirdi. Bir yıl, uyuşmazlık ve KVKK/GDPR
        /// pratiğinde yaygın bir taban; isteyen 0 yazıp sınırsıza dönebilir.
        /// </summary>
        public int RetentionDays { get; set; } = DefaultRetentionDays;

        public DateTime? LastPurgeAtUtc { get; set; }

        public int LastPurgedCount { get; set; }
    }
}
