using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Cluster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Çoklu sunucu yönetimi (Plesk Multi Server karşılığı).
/// </summary>
/// <remarks>
/// <para>
/// <b>Model kopyalandı, icat edilmedi.</b> Plesk'te bir "yönetim düğümü" (merkez) ve ona kayıtlı
/// "hizmet düğümleri" vardır; merkez tek giriş noktasıdır, siteler düğümlerde yaşar, bir abonelik
/// TEK düğüme kurulur ve düğümler arasına bölünmez, yeni abonelik için düğüm yük dengelemeyle
/// seçilir. Buradaki tasarım birebir bu modeldir.
/// </para>
/// <para>
/// <b>Bu denetleyici İKİ rolü birden taşır</b> — Plesk'teki gibi, her panel hem merkez hem düğüm
/// olabilir:
/// </para>
/// <list type="bullet">
/// <item><b>Düğüm tarafı:</b> <c>GET node-info</c>. Merkez bu ucu, düğümde üretilmiş bir kişisel
/// API anahtarıyla çağırır (<c>PanelTokenAuthenticationHandler</c>). Salt-okur bir anahtar yeter.</item>
/// <item><b>Merkez tarafı:</b> diğer tüm uçlar. Yönetici tarayıcıdan çerezle çağırır.</item>
/// </list>
/// <para>
/// <b>YALNIZ YÖNETİCİ.</b> Sınıf düzeyinde <c>Roles = "Administrator"</c>: bayi ve müşteri bu
/// ekranı hiç görmez. Gerekçe kapsam değil MAHİYET — düğüm listesi altyapının haritasıdır (kaç
/// sunucu, hangi adreste, hangi sürümde, ne kadar dolu) ve bir kiracının hiçbir kaynağı bu bilgiyi
/// gerektirmez. Aynı sebeple <c>SystemController</c> de sunucu geneli ölçümleri kiracıdan gizler.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/cluster")]
public sealed class ClusterController(
    IClusterService clusterService,
    IAuditService auditService) : ControllerBase
{
    // ---------------------------------------------------------------------------------------
    // DÜĞÜM TARAFI — bu paneli bir hizmet düğümü olarak kullanan merkez buraya sorar.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Bu sunucunun merkeze verdiği tek paket: sürüm, donanım, servis sağlığı, site sayısı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Biçim icat edilmedi:</b> yanıtın içindeki <c>Overview</c> ve <c>Services</c> alanları
    /// panelin kendi ekranlarının kullandığı DTO'ların ta kendisidir (<c>SystemController</c> ve
    /// <c>ServerHealthController</c> aynı tipleri döner). Merkez, uzak sunucunun sağlığını yerel
    /// sunucununkiyle aynı kodla yorumlar.
    /// </para>
    /// <para>
    /// <b>Gizli giriş yolu neden burada dönüyor:</b> "düğüme git" bağlantısı ancak düğümün kendi
    /// yolunu bilirse kurulabilir ve merkez o yolu TAHMİN EDEMEZ. Uç yönetici kimliği ister; anonim
    /// <c>GET /api/v1/health</c> aynı alanı kurulum bittikten sonra bilerek boş bırakır.
    /// </para>
    /// <para>
    /// <b>Neden GET:</b> okuma olduğu için salt-okur bir API anahtarıyla çalışır
    /// (<c>PanelTokenCapabilities</c> yalnız durum değiştiren fiillerde <c>AllowDestructive</c>
    /// ister). Merkez, düğümü yalnız İZLEMEK için tam yetkili anahtar taşımak zorunda kalmaz.
    /// </para>
    /// </remarks>
    [HttpGet("node-info")]
    public async Task<ActionResult<ApiResponse<ClusterNodeInfoDto>>> GetNodeInfoAsync(
        [FromServices] ISystemMonitoringService systemMonitoring,
        [FromServices] IHealthService healthService,
        [FromServices] IIisSiteService iisSiteService,
        [FromServices] IPanelSecurityService securityService,
        [FromServices] IPanelPreferencesService panelPreferences,
        CancellationToken cancellationToken)
    {
        var overview = await systemMonitoring.GetOverviewAsync(cancellationToken);
        var services = await healthService.GetHealthAsync(cancellationToken);
        var sites = await iisSiteService.GetSitesAsync(cancellationToken);
        var securePath = await securityService.GetSecureEntryPathAsync(cancellationToken);
        var preferences = await panelPreferences.GetAsync(cancellationToken);

        return Ok(ApiResponse<ClusterNodeInfoDto>.Ok(new ClusterNodeInfoDto
        {
            PanelVersion = overview.PanelVersion,
            MachineName = overview.MachineName,
            SecureEntryPath = securePath,
            SiteCount = sites.Count,
            DefaultSiteRoot = preferences.DefaultSiteRoot,
            Overview = overview,
            Services = services
        }));
    }

    // ---------------------------------------------------------------------------------------
    // MERKEZ TARAFI
    // ---------------------------------------------------------------------------------------

    [HttpGet("nodes")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ClusterNodeDto>>>> GetNodesAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<IReadOnlyList<ClusterNodeDto>>.Ok(await clusterService.GetNodesAsync(cancellationToken)));
    }

    /// <summary>Tüm düğümlerin toplu görünümü. Ulaşılamayan düğüm satırı DÜŞMEZ, "ulaşılamıyor" der.</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<ApiResponse<ClusterOverviewDto>>> GetOverviewAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<ClusterOverviewDto>.Ok(await clusterService.GetOverviewAsync(cancellationToken)));
    }

    /// <summary>Yeni düğüm kaydı.</summary>
    /// <remarks>
    /// TLS doğrulaması KAPATILDIYSA denetim kaydına ayrıca yazılır: bu, bir gün "araya girme"
    /// olayı incelendiğinde ilk sorulacak sorunun cevabıdır.
    /// </remarks>
    [HttpPost("nodes")]
    public async Task<ActionResult<ApiResponse<ClusterNodeDto>>> RegisterAsync(
        [FromBody] RegisterClusterNodeRequest request,
        CancellationToken cancellationToken)
    {
        var node = await clusterService.RegisterAsync(request, cancellationToken);

        // Denetim kaydına BELİRTEÇ yazılmaz; yalnız kim/neyi/hangi adresle kaydetti.
        await WriteAuditAsync(
            "cluster.node.registered",
            $"Node '{node.Label}' registered at {node.Address}. tlsVerify={node.VerifyTlsCertificate}, enabled={node.Enabled}",
            cancellationToken);

        return Ok(ApiResponse<ClusterNodeDto>.Ok(node, "cluster.node_registered"));
    }

    [HttpPut("nodes/{nodeId:guid}")]
    public async Task<ActionResult<ApiResponse<ClusterNodeDto>>> UpdateAsync(
        Guid nodeId,
        [FromBody] UpdateClusterNodeRequest request,
        CancellationToken cancellationToken)
    {
        var node = await clusterService.UpdateAsync(nodeId, request, cancellationToken);

        await WriteAuditAsync(
            "cluster.node.updated",
            $"Node '{node.Label}' updated at {node.Address}. tlsVerify={node.VerifyTlsCertificate}, enabled={node.Enabled}",
            cancellationToken);

        return Ok(ApiResponse<ClusterNodeDto>.Ok(node, "cluster.node_updated"));
    }

    [HttpDelete("nodes/{nodeId:guid}")]
    public async Task<ActionResult<ApiResponse<object?>>> RemoveAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await clusterService.RemoveAsync(nodeId, cancellationToken);
        await WriteAuditAsync("cluster.node.removed", $"Node {nodeId} removed from the cluster registry.", cancellationToken);

        return Ok(ApiResponse<object?>.Ok(null, "cluster.node_removed"));
    }

    /// <summary>Tek düğümü şimdi yokla ("Bağlantıyı sına").</summary>
    [HttpPost("nodes/{nodeId:guid}/test")]
    public async Task<ActionResult<ApiResponse<ClusterNodeStatusDto>>> ProbeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var status = await clusterService.ProbeAsync(nodeId, cancellationToken);
        await WriteAuditAsync(
            "cluster.node.probed",
            $"Node {nodeId} probed. reachable={status.Reachable}, status={status.StatusKey}",
            cancellationToken);

        return Ok(ApiResponse<ClusterNodeStatusDto>.Ok(status, "cluster.node_probed"));
    }

    /// <summary>
    /// "Düğüme git" bağlantısı.
    /// </summary>
    /// <remarks>
    /// Bağlantı üretmek, yöneticiye başka bir sunucunun yönetim adresini vermektir; ayrıca gizli
    /// giriş yolunu da içerir. Bu yüzden denetim kaydına düşer. Oturum DEVREDİLMEZ — gerekçe
    /// <see cref="ClusterNodeLinkDto"/> içinde yazılı.
    /// </remarks>
    [HttpGet("nodes/{nodeId:guid}/panel-link")]
    public async Task<ActionResult<ApiResponse<ClusterNodeLinkDto>>> GetPanelLinkAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var link = await clusterService.GetPanelLinkAsync(nodeId, cancellationToken);
        await WriteAuditAsync("cluster.node.opened", $"Panel link resolved for node {nodeId}.", cancellationToken);

        return Ok(ApiResponse<ClusterNodeLinkDto>.Ok(link));
    }

    /// <summary>Yeni site hangi düğüme giderdi (form açılırken gösterilir).</summary>
    [HttpGet("placement")]
    public async Task<ActionResult<ApiResponse<ClusterPlacementDto>>> GetPlacementAsync(CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<ClusterPlacementDto>.Ok(await clusterService.GetPlacementAsync(cancellationToken)));
    }

    /// <summary>Siteyi seçilen (ya da otomatik seçilen) düğümde oluşturur.</summary>
    [HttpPost("sites")]
    public async Task<ActionResult<ApiResponse<ClusterSiteCreationDto>>> CreateSiteAsync(
        [FromBody] CreateClusterSiteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await clusterService.CreateSiteAsync(request, cancellationToken);

        await WriteAuditAsync(
            "cluster.site.created",
            $"Site '{result.SiteName}' created on node '{result.NodeLabel}' ({result.NodeId}). automatic={result.PlacementWasAutomatic}",
            cancellationToken);

        return Ok(ApiResponse<ClusterSiteCreationDto>.Ok(result, "cluster.site_created"));
    }

    private Task WriteAuditAsync(string action, string details, CancellationToken cancellationToken) =>
        auditService.WriteAsync(
            action,
            "cluster",
            details,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
}
