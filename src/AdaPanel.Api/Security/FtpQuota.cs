using System.Security.Claims;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Api.Security;

/// <summary>
/// M3 (plan <c>MaxFtpUsers</c>): sitenin sahibi aboneliğin FTP hesabı kotası doldu mu?
/// </summary>
/// <remarks>
/// FTP hesabı açan HER yol buradan geçer (FtpController ve site oluştururken açılan varsayılan hesap):
/// kota bir uca gömülürse diğeri korumasız doğar (bu depoda ölçülen kalıp). Kardeşleri:
/// <c>MailController.EnsureMailboxQuotaAsync</c>, <c>SitesController.EnsureSiteQuotaAsync</c>.
/// Yönetici sınırsızdır; <c>MaxFtpUsers = 0</c> limitsizdir. Sayım abonelikteki BÜTÜN sitelerin
/// hesaplarını kapsar (bir sitede dolan kota diğer sitedeki açılışı da durdurur); başka aboneliğin
/// hesapları sayılmaz.
/// </remarks>
internal static class FtpQuota
{
    public static async Task EnsureAsync(
        ICustomerScopeService customerScopes,
        IFtpService ftpService,
        ClaimsPrincipal principal,
        string siteName,
        CancellationToken cancellationToken)
    {
        if (principal.IsInRole("Administrator"))
        {
            return;
        }

        var ownerId = await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        if (ownerId is not { } subscriptionId)
        {
            return;
        }

        var scope = await customerScopes.GetScopeAsync(subscriptionId, cancellationToken);
        if (scope.MaxFtpUsers <= 0)
        {
            return;
        }

        var sites = new HashSet<string>(scope.Sites, StringComparer.OrdinalIgnoreCase);
        var count = (await ftpService.GetFtpUsersAsync(cancellationToken))
            .Count(item => sites.Contains(item.SiteName));
        if (count >= scope.MaxFtpUsers)
        {
            throw new AppException("ftp.quota_exceeded", 403, 403);
        }
    }
}
