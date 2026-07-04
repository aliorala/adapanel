namespace AdaPanel.Core.Interfaces;

/// <summary>Bir müşteri hesabının kaynak kapsamı: atanmış siteler + limitler.</summary>
public sealed record CustomerScope(IReadOnlyList<string> Sites, int MaxSites, int MaxDiskMb, int MaxDatabases, int MaxMailboxes, int MaxScheduledTasks);

/// <summary>
/// Customer rolündeki kullanıcıların erişebileceği siteler ve kaynak limitlerinin
/// kalıcı kaydı (config/customer-scopes.json). Yetki ve kota denetimi bunun üzerinden yapılır.
/// </summary>
public interface ICustomerScopeService
{
    Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default);

    /// <summary>Müşterinin ek kaynak kotaları (panel MaxDatabases / MaxMailboxes / MaxScheduledTasks). 0 = limitsiz.</summary>
    Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default);

    Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Bir site silindiğinde tüm müşteri atamalarından o siteyi kaldırır (yetim atama kalmaz).</summary>
    Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Atanan sitelerin adları + bağlama (binding) host'larından türetilen mail domain kümesi.</summary>
    Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Müşterinin atanan sitelerinin toplam disk kullanımı (MB).</summary>
    Task<long> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default);

    // --- Bayi (Reseller) sahipliği ---

    /// <summary>Bir müşteriyi bir bayiye (sahip) bağlar. ownerId null ise sahiplik kaldırılır.</summary>
    Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default);

    /// <summary>Bir müşterinin sahibi (bayi) kim; yoksa null.</summary>
    Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Verilen bayinin sahip olduğu müşteri kullanıcı kimlikleri.</summary>
    Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>Bayinin etkin site kümesi: sahip olduğu tüm müşterilerin atanan sitelerinin birleşimi.</summary>
    Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default);

    /// <summary>Bayinin etkin mail domain kümesi: müşterilerinin izinli mail domainlerinin birleşimi.</summary>
    Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default);

    /// <summary>Verilen siteyi atanmış olarak içeren ilk müşteri (kota sahibi) kimliği; yoksa null.</summary>
    Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default);

    // --- Abonelik altı ek kullanıcı (Plesk "additional user") ---

    /// <summary>
    /// Bir kullanıcıyı başka bir müşterinin (parent) aboneliğine "ek kullanıcı" olarak bağlar; o andan itibaren
    /// parent'ın sitelerini/kotalarını MIRAS alır (kendi kaynağı olmaz). parentUserId null ise bağ kaldırılır.
    /// </summary>
    Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default);

    /// <summary>Kullanıcı bir alt-kullanıcıysa bağlı olduğu parent müşteri kimliği; değilse null.</summary>
    Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default);
}
