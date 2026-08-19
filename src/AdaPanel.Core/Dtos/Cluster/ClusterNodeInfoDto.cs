using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Dtos.System;

namespace AdaPanel.Core.Dtos.Cluster;

/// <summary>
/// Bir AdaPanel sunucusunun KENDİSİ hakkında merkeze verdiği tek paket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek uç, üç ayrı uç değil:</b> merkezin ihtiyacı olan bilgi zaten panelde vardı —
/// <c>GET /api/v1/system/overview</c> (donanım), <c>GET /api/v1/server-health</c> (servisler),
/// <c>GET /api/v1/sites</c> (site sayısı). Merkez bunları tek tek çağırsaydı her düğüm için ÜÇ
/// gidiş-dönüş olurdu; on düğümlük bir kurulumda otuz istek, ve bunlardan biri zaman aşımına
/// uğradığında satırın hangi parçasının eksik olduğu belirsiz kalırdı.
/// </para>
/// <para>
/// <b>Biçim İCAT EDİLMEDİ:</b> <see cref="Overview"/> ve <see cref="Services"/> alanları panelin
/// kendi ekranlarının kullandığı DTO'ların TA KENDİSİDİR. Merkez, düğümün sağlığını yerel sunucunun
/// sağlığıyla aynı kodla yorumlar; iki ayrı biçim olsaydı biri güncellenip diğeri unutulurdu.
/// </para>
/// </remarks>
public sealed class ClusterNodeInfoDto
{
    /// <summary>Düğümdeki panel sürümü. Merkez, sürümü geride kalan düğümü işaretler.</summary>
    public string PanelVersion { get; init; } = string.Empty;

    public string MachineName { get; init; } = string.Empty;

    /// <summary>
    /// Düğümün gizli giriş yolu — "düğüme git" bağlantısı bununla kurulur.
    /// </summary>
    /// <remarks>
    /// Yalnızca KİMLİĞİ DOĞRULANMIŞ yönetici isteğine döner (uç <c>Administrator</c> ister).
    /// Anonim <c>GET /api/v1/health</c> bu alanı kurulum bittikten sonra bilerek boş bırakır;
    /// aynı bilgiyi orada sızdırmak gizli yolu anlamsızlaştırırdı.
    /// </remarks>
    public string SecureEntryPath { get; init; } = string.Empty;

    /// <summary>Düğümdeki IIS site sayısı (yerleşim kararının girdisi).</summary>
    public int SiteCount { get; init; }

    /// <summary>
    /// Düğümün varsayılan site kökü (<c>C:\inetpub\wwwroot</c> gibi).
    /// </summary>
    /// <remarks>
    /// Merkez, uzak düğümde site oluştururken fiziksel yolu bu köke göre kurar. Merkezin KENDİ
    /// kökünü kullanmak yanlış olurdu: düğümler farklı disk düzenine sahip olabilir ve panelin
    /// site oluşturma ucu yönetici isteğinde yolu ZORLAMAZ — yani yanlış yol sessizce kabul edilir
    /// ve site var olmayan bir dizine kurulurdu.
    /// </remarks>
    public string DefaultSiteRoot { get; init; } = string.Empty;

    /// <summary>Donanım/işletim durumu — panelin kendi <c>SystemOverviewDto</c> biçimi.</summary>
    public SystemOverviewDto Overview { get; init; } = new();

    /// <summary>Kritik servislerin durumu — panelin kendi <c>ServiceHealthDto</c> biçimi.</summary>
    public IReadOnlyCollection<ServiceHealthDto> Services { get; init; } = [];
}
