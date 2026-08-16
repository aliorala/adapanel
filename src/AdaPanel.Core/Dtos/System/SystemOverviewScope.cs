namespace AdaPanel.Core.Dtos.System;

/// <summary>
/// Sunucu geneli durum bilgisini ROLE göre daraltır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan asimetri:</b> MCP aracı <c>system_overview</c> bilinçli olarak yönetici istiyordu,
/// ama REST ikizi <c>GET /api/v1/system/overview</c> yalnız <c>[Authorize]</c> taşıyordu — yani
/// HER role açıktı. Bir müşteri, sunucunun makine adını, işletim sistemi sürümünü, çekirdek
/// sayısını, toplam/boş belleğini, TÜM disklerinin doluluğunu ve ağ arabirimlerini okuyabiliyordu.
/// Bunların hiçbiri kiracının kendi kaynağı değildir; hepsi birlikte bir saldırı yüzeyi haritası
/// ve komşu kiracının yükünü çıkarmaya yarayan bir kanaldır (Plesk/ISPConfig de bu bilgiyi
/// müşteriye göstermez).
/// </para>
/// <para>
/// <b>Neden 403 DEĞİL:</b> müşteri panosu bu ucu her girişte ve 15 saniyede bir çağırıyor ve
/// çağrı <c>await</c> zincirinin içinde — 403 dönmek panonun tamamını yükleyemez hâle getirirdi.
/// Yetkiyi kapatmak yerine YANIT GÖVDESİ daraltılıyor: şema aynı kalır, hassas alanlar boşalır.
/// </para>
/// <para>
/// <b>Neden <see cref="SystemOverviewDto.IsRestricted"/> bayrağı:</b> daraltılmış yanıtta CPU %0
/// görünür ve bu bir ÖLÇÜM DEĞİLDİR. Bayrak olmadan arayüz "sunucu %0 yüklü" diye yalan söylerdi.
/// Bayrak, "bu değerler senin için ölçülmedi" demenin tek dürüst yolu.
/// </para>
/// </remarks>
public static class SystemOverviewScope
{
    /// <summary>
    /// Yönetici için tam, diğer roller için daraltılmış görünüm döner.
    /// </summary>
    /// <param name="overview">Ölçülmüş tam durum.</param>
    /// <param name="isAdministrator">
    /// Yalnız yönetici tam görünüm alır. Bayi de daraltılır: bayi bir kiracıdır, sunucunun
    /// donanımı ona ait değildir — ve MCP tarafındaki kilit de (RequireAdministrator) budur.
    /// Aynı yeteneğin iki kapısı varsa ikisi de aynı kilidi taşımalıdır.
    /// </param>
    public static SystemOverviewDto ForRole(SystemOverviewDto overview, bool isAdministrator)
    {
        ArgumentNullException.ThrowIfNull(overview);

        if (isAdministrator)
        {
            return overview;
        }

        return new SystemOverviewDto
        {
            // KALANLAR: kiracının kendi ekranında işine yarayan ve sunucu hakkında bilgi
            // vermeyen iki alan. Panel sürümü zaten giriş ekranında görünür; sunucu saati
            // kiracının kendi zamanlanmış işlerini yorumlamak için gerekir.
            PanelVersion = overview.PanelVersion,
            CurrentServerTimeUtc = overview.CurrentServerTimeUtc,

            // ÇIKARILANLAR (varsayılan değerlerde bırakılır): makine adı ve işletim sistemi
            // sürümü doğrudan parmak izidir; çekirdek/bellek/disk/ağ ise sunucunun kapasitesi ve
            // anlık yüküdür. Alanlar SİLİNMEZ, boş bırakılır: şema değişirse arayüz kırılır.
            IsRestricted = true
        };
    }
}
