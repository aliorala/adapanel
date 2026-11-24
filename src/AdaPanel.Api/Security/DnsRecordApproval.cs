using System.Security.Claims;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Api.Security;

/// <summary>
/// DNS yazma yolunun KAPSAM ONAYI — REST (<c>DnsController</c>) ve MCP araçları AYNI fonksiyonu kullanır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekli:</b> bölge adı paylaşımlı olabilir (aynı alan adını birkaç site kullanır); yazmaya
/// ya da yayına çıkan kayıt BAŞKA sitelerin katkılarını da etkiler. Çağıranın yalnız kendi sitesine
/// erişebilmesi yetmez: etkilenen TÜM siteler kapsamında olmalıdır, değilse 403. Servis bu listeyi bölge
/// kilitleri altında verir ve yayın sınırında yeniden çağırır; kapsam her seferinde yeniden okunur.
/// </para>
/// <para>
/// Önceden yalnız <c>Reseller</c> için yazılmıştı; Customer rolü DNS'e açılınca aynı kural TÜM kapsamlı
/// roller (<see cref="CustomerAccess.IsScoped"/>) için geçerli olmak zorunda — aksi hâlde müşteri kendi
/// sitesi üzerinden paylaşımlı bölgedeki başkasının kaydına yazabilirdi. Yönetici muaf.
/// </para>
/// </remarks>
public static class DnsRecordApproval
{
    public static Func<IReadOnlyList<string>, CancellationToken, Task> Create(
        ICustomerScopeService scopes, ClaimsPrincipal user, string siteName)
    {
        // İstek bittikten sonra da (yayın sınırında) çalışabilsin diye kimlik kopyalanır.
        var principal = new ClaimsPrincipal(user.Identities.Select(identity => new ClaimsIdentity(identity)));
        return async (affectedSites, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (principal.IsInRole("Administrator")) return;
            if (!CustomerAccess.IsScoped(principal)) throw new AppException("auth.site_forbidden", 403, 403);

            var names = affectedSites.Prepend(siteName).Select(name => name?.Trim() ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var allowed = await CustomerAccess.GetEffectiveSitesAsync(scopes, principal, token);
            token.ThrowIfCancellationRequested();
            // Kapsam dışı eşlerin adı/sahibi açıklanmaz.
            if (allowed is null || names.Any(name => name.Length == 0 || !allowed.Contains(name, StringComparer.OrdinalIgnoreCase)))
                throw new AppException("auth.site_forbidden", 403, 403);
        };
    }
}
