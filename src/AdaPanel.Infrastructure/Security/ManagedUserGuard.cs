using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Security;

/// <summary>Kapsam denetiminin sonucu: hedef kullanıcı + isteği yapan kullanıcı.</summary>
public sealed record ManagedUserAccess(PanelUser Target, PanelUser? Actor)
{
    /// <summary>Aktör bayi mi? (Bayiye özel kısıtlar — ör. yıkıcı silmenin kapalı olması — buna bakar.)</summary>
    public bool ActorIsReseller => Actor?.Role == UserRole.Reseller;
}

/// <summary>
/// "Bu aktör, BU kullanıcı üzerinde işlem yapabilir mi?" sorusunun TEK yanıtı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek yerde:</b> aynı üç satır <c>UpdateUserAsync</c>, <c>UnlockUserAsync</c> ve
/// <c>DeleteUserAsync</c> içinde birebir tekrar ediyordu. Parola belirleme ve tek kullanımlık giriş
/// bağlantısı eklenince kopya sayısı beşe çıkacaktı; bu tür bir kuralda kopya sayısı, kuralın bir
/// gün bir yerde eksik uygulanma olasılığıdır. Kiracı izolasyonunda "bir yerde eksik" demek, bayinin
/// BAŞKA bir bayinin müşterisine erişmesi demektir.
/// </para>
/// <para>
/// <b>Kural:</b> yönetici herkes üzerinde; bayi YALNIZ sahibi olduğu Customer üzerinde; başka hiçbir
/// rol hiç kimse üzerinde. Son madde bilinçli olarak <i>kapalı</i> tarafta hata yapar: denetleyici
/// katmanındaki <c>[Authorize(Roles = …)]</c> zaten müşteriyi dışarıda tutuyor, ama yetkilendirmenin
/// tek kapısı öznitelik olsaydı servis MCP araçlarından ya da yeni bir denetleyiciden rol denetimi
/// olmadan çağrılabilirdi.
/// </para>
/// <para>
/// Bayi, müşterisinin ALT kullanıcılarına (abonelik altı ek login'ler) buradan erişemez: onların
/// sahiplik kaydı yoktur, sahibi <c>null</c> döner ve denetim reddeder. Bu mevcut davranışın
/// aynısıdır — alt kullanıcıları abonelik sahibi müşteri kendi ekranından yönetir.
/// </para>
/// </remarks>
public static class ManagedUserGuard
{
    public static async Task<ManagedUserAccess> EnsureManagedAsync(
        AdaPanelDbContext dbContext,
        ICustomerScopeService customerScopeService,
        Guid actorUserId,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        // Hedefin YOKLUĞU yetkiden ÖNCE bildirilir; mevcut üç çağrı yeri de böyle davranıyordu ve
        // kullanıcı listesi zaten aktörün kapsamıyla sınırlı olduğu için burada bir sızıntı yok.
        var target = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == targetUserId, cancellationToken)
            ?? throw new InvalidOperationException("users.not_found");

        var actor = await dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == actorUserId, cancellationToken);

        if (actor?.Role == UserRole.Administrator)
        {
            return new ManagedUserAccess(target, actor);
        }

        if (actor?.Role == UserRole.Reseller)
        {
            var owner = await customerScopeService.GetOwnerAsync(targetUserId, cancellationToken);
            if (target.Role != UserRole.Customer || owner != actorUserId)
            {
                throw new AppException("users.forbidden", 403, 403);
            }

            return new ManagedUserAccess(target, actor);
        }

        throw new AppException("users.forbidden", 403, 403);
    }
}
