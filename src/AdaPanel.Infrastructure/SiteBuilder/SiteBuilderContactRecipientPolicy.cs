using AdaPanel.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// G21: iletişim formunun alıcısı kiracının seçtiği serbest bir adrestir; panel SMTP'si üzerinden
/// rastgele adrese ileti yollatmak için kullanılamamalı. İzinli alıcı: sitenin kendi alan adındaki adres
/// ya da sitenin sahibinin (sahip çözülemezse etkin yöneticilerin) panel hesabı e-postası.
/// Gönderim anı (<see cref="SiteBuilderContactService"/>) ve kayıt/yayın anı (<see cref="SiteBuilderService"/>)
/// AYNI kuralı kullanır; böylece kiracı kuralı kaydederken öğrenir.
/// </summary>
public sealed class SiteBuilderContactRecipientPolicy(
    AdaPanel.Data.Persistence.AdaPanelDbContext? db = null,
    ICustomerScopeService? customerScopes = null,
    ILogger<SiteBuilderContactRecipientPolicy>? logger = null)
{
    public async Task<bool> IsAllowedAsync(string siteName, string recipient, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipient)) return false;
        if (SiteBuilderContactService.IsRecipientInSiteDomain(siteName, recipient)) return true;
        if (db is null) return false;

        try
        {
            Guid? ownerId = customerScopes is null
                ? null
                : await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
            var wanted = recipient.Trim().ToLowerInvariant();
            var users = db.Users.AsNoTracking().Where(user => user.IsActive);
            var emails = ownerId is { } id
                ? await users.Where(user => user.Id == id).Select(user => user.Email).ToListAsync(cancellationToken)
                : await users.Where(user => user.Role == AdaPanel.Core.Enums.UserRole.Administrator).Select(user => user.Email).ToListAsync(cancellationToken);
            return emails.Any(email => string.Equals((email ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Alici dogrulamasi okunamadi, reddedildi: {Site}", siteName);
            return false;
        }
    }
}
