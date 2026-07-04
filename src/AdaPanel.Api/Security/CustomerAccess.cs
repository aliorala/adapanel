using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Api.Security;

/// <summary>
/// Kaynak-kapsam denetimleri. Administrator/Operator muaftır. KAPSAMLI roller:
/// <list type="bullet">
/// <item>Customer → yalnızca kendisine atanan siteler.</item>
/// <item>Reseller (Bayi) → sahip olduğu TÜM müşterilerin sitelerinin birleşimi.</item>
/// </list>
/// Her iki rol de yalnızca etkin kapsamındaki sitelerin/mail domainlerinin kaynaklarına erişebilir.
/// </summary>
public static class CustomerAccess
{
    public static bool IsCustomer(ClaimsPrincipal user) => user.IsInRole("Customer");

    public static bool IsReseller(ClaimsPrincipal user) => user.IsInRole("Reseller");

    /// <summary>Kapsam denetimine tabi rol mü (Customer veya Reseller).</summary>
    public static bool IsScoped(ClaimsPrincipal user) => IsCustomer(user) || IsReseller(user);

    /// <summary>
    /// Etkin abonelik sahibi: alt-kullanıcı ise parent müşteri, değilse kullanıcının kendisi.
    /// Aboneliğe bağlı kaynaklar (cron görevleri, self-servis veritabanları) bu kimliğe yazılır →
    /// parent ve tüm alt-kullanıcılar aynı kaynak kümesini ve kotayı paylaşır (Plesk davranışı).
    /// </summary>
    public static async Task<Guid> GetSubscriptionUserIdAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var userId = user.GetRequiredUserId();
        if (IsCustomer(user) && await scopes.GetParentSubscriptionAsync(userId, cancellationToken) is { } parent)
        {
            return parent;
        }

        return userId;
    }

    /// <summary>Kullanıcının erişebileceği site adları; kapsamsız rol (admin/operator) için null.</summary>
    public static async Task<IReadOnlyCollection<string>?> GetEffectiveSitesAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (IsReseller(user))
        {
            return await scopes.GetResellerSitesAsync(user.GetRequiredUserId(), cancellationToken);
        }

        if (IsCustomer(user))
        {
            return await scopes.GetAssignedSitesAsync(user.GetRequiredUserId(), cancellationToken);
        }

        return null;
    }

    /// <summary>Kullanıcının erişebileceği mail domainleri; kapsamsız rol için null.</summary>
    public static async Task<IReadOnlyCollection<string>?> GetEffectiveMailDomainsAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (IsReseller(user))
        {
            return await scopes.GetResellerMailDomainsAsync(user.GetRequiredUserId(), cancellationToken);
        }

        if (IsCustomer(user))
        {
            return await scopes.GetAllowedMailDomainsAsync(user.GetRequiredUserId(), cancellationToken);
        }

        return null;
    }

    public static async Task EnsureSiteAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string? siteName,
        CancellationToken cancellationToken)
    {
        var allowed = await GetEffectiveSitesAsync(scopes, user, cancellationToken);
        if (allowed is null)
        {
            return;
        }

        if (!allowed.Contains(siteName?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("auth.site_forbidden", 403, 403);
        }
    }

    public static async Task EnsureMailDomainAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string? domain,
        CancellationToken cancellationToken)
    {
        var allowed = await GetEffectiveMailDomainsAsync(scopes, user, cancellationToken);
        if (allowed is null)
        {
            return;
        }

        if (!allowed.Contains(domain?.Trim().ToLowerInvariant() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("auth.domain_forbidden", 403, 403);
        }
    }

    /// <summary>
    /// Dosya yöneticisi sanal yolu denetimi ("sites/&lt;site&gt;/...").
    /// <paramref name="allowRoot"/> true ise kök ve "sites" listelemesine izin verilir
    /// (kök listesi controller'da etkin sitelerle sentezlenir).
    /// </summary>
    public static async Task EnsureFilesPathAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string? virtualPath,
        bool allowRoot,
        CancellationToken cancellationToken)
    {
        if (!IsScoped(user))
        {
            return;
        }

        var normalized = (virtualPath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || string.Equals(normalized, "sites", StringComparison.OrdinalIgnoreCase))
        {
            if (allowRoot)
            {
                return;
            }

            throw new AppException("auth.site_forbidden", 403, 403);
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("auth.site_forbidden", 403, 403);
        }

        await EnsureSiteAsync(scopes, user, segments[1], cancellationToken);
    }

    public static async Task<IReadOnlyCollection<string>?> GetSiteScopeAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        return await GetEffectiveSitesAsync(scopes, user, cancellationToken);
    }
}
