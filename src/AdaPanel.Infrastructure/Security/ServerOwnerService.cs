using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// <see cref="ServerOwnerRegistry"/> kararını veritabanı + veri kökündeki sabitlenmiş kayıtla
/// birleştirir ve kararı SABİTLER.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden okuma yolunda YAZIYOR:</b> geri düşüş kuralı ("en eski etkin yönetici") tek başına
/// bırakılırsa şu yol açık kalırdı — ikinci bir yönetici, sahibi silip/pasife alıp sıranın
/// başına geçer. Sabitleme, sahibin kimliğini ilk okumada diske çiviler; bundan sonra sıralamayı
/// değiştirmek sahipliği değiştirmez. Sahibin silinmesi/pasife alınması ayrıca
/// <c>UserManagementService</c> tarafından da reddedilir; bu iki koruma AYNI deliğin iki ucudur ve
/// biri kalkarsa diğeri hâlâ tutar.
/// </para>
/// <para>
/// <b>Yazma BAŞARISIZ olursa karar DEĞİŞMEZ.</b> Diske yazamamak (salt okunur veri kökü, ACL,
/// yarış) terminalin ölmesi için bir sebep değildir: sonuç yine döndürülür, hata yalnızca günlüğe
/// yazılır. Sessiz <c>catch</c> DEĞİL — yutulan bir hata, sabitlemenin hiç çalışmadığını gizlerdi.
/// </para>
/// </remarks>
public sealed class ServerOwnerService(
    AdaPanelDbContext dbContext,
    JsonFileStore fileStore,
    ILogger<ServerOwnerService> logger) : IServerOwnerService
{
    public async Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default)
    {
        var kayit = await OkuAsync(cancellationToken);

        var adaylar = await dbContext.Users
            .AsNoTracking()
            .Select(user => new SunucuSahibiAdayi(user.Id, user.Role, user.IsActive, user.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        var sahip = ServerOwnerRegistry.Belirle(kayit?.UserId, adaylar);

        if (sahip is { } belirlenen && kayit?.UserId != belirlenen)
        {
            await SabitleAsync(belirlenen, cancellationToken);
        }

        return sahip;
    }

    public async Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Guid.Empty asla bir kullanıcı kimliği değildir; "kimlik çözülemedi" durumunun sahiplik
        // sınamasından geçmesini engeller.
        if (userId == Guid.Empty)
        {
            return false;
        }

        return await GetServerOwnerIdAsync(cancellationToken) == userId;
    }

    private async Task<SunucuSahibiKaydi?> OkuAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await fileStore.ReadAsync<SunucuSahibiKaydi?>(
                ServerOwnerRegistry.MarkerRelativePath, null, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // BOZUK KAYIT KAPI KAPATMAZ. Okunamayan dosya "sahip yok" değil "sabitleme yok"
            // demektir; karar geri düşüşe bırakılır (bkz. ServerOwnerRegistry).
            logger.LogWarning(
                exception,
                "Sunucu sahibi kaydi okunamadi ({Yol}); en eski etkin yoneticiye dusuluyor.",
                ServerOwnerRegistry.MarkerRelativePath);
            return null;
        }
    }

    private async Task SabitleAsync(Guid sahip, CancellationToken cancellationToken)
    {
        try
        {
            await fileStore.WriteAsync(
                ServerOwnerRegistry.MarkerRelativePath,
                new SunucuSahibiKaydi { UserId = sahip },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Sunucu sahibi kaydi yazilamadi ({Yol}); karar bu istek icin gecerli ama SABITLENMEDI.",
                ServerOwnerRegistry.MarkerRelativePath);
        }
    }
}
