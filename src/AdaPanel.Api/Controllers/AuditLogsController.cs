using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Denetim kaydı okuma uçları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kiracıya da açık:</b> panel denetim kaydını topluyordu ama hiçbir ekran göstermiyordu;
/// yani veri vardı, görünürlük yoktu. Müşteri uyuşmazlığında ("hesabımı ben silmedim") ve
/// KVKK/GDPR'ın "ilgili kişiye erişim" yükümlülüğünde bu kaydın SAHİBİNE de gösterilmesi gerekir.
/// Plesk'te de abonelik sahibi kendi eylem geçmişini görür.
/// </para>
/// <para>
/// <b>Kapsam nerede uygulanıyor:</b> denetleyicide DEĞİL, <see cref="IAuditService"/> içinde —
/// <see cref="AuditVisibility"/> zorunlu bir parametredir. Burada yalnızca çağıranın rolü
/// çözülür. Böylece yeni bir okuma ucu eklendiğinde kapsamı uygulamayı "unutmak" mümkün olmaz.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v1/audit-logs")]
public sealed class AuditLogsController(IAuditService auditService) : ControllerBase
{
    /// <summary>
    /// Son kayıtlar (bildirim çanı). Sunucu geneli olduğu için YALNIZ yönetici.
    /// </summary>
    /// <remarks>
    /// Sözleşmesi bilinçli olarak DEĞİŞMEDİ: <c>stores/auth.ts</c> bu ucu düz dizi bekleyerek
    /// çağırıyor. Sayfalama <c>search</c> ucuna eklendi ki var olan çağıran kırılmasın.
    /// </remarks>
    [HttpGet]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AuditLogDto>>>> GetRecentAsync(
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var items = await auditService.GetRecentAsync(take, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<AuditLogDto>>.Ok(items));
    }

    /// <summary>Filtreli + sayfalı arama. Kapsamlı roller yalnız kendi aboneliklerini görür.</summary>
    [HttpGet("search")]
    public async Task<ActionResult<ApiResponse<AuditLogPageDto>>> SearchAsync(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? userName,
        [FromQuery] string? action,
        [FromQuery] string? resource,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(fromUtc, toUtc, userName, action, resource, search, page, pageSize);
        var visibility = await ResolveVisibilityAsync(cancellationToken);
        var result = await auditService.SearchAsync(query, visibility, cancellationToken);
        return Ok(ApiResponse<AuditLogPageDto>.Ok(result));
    }

    /// <summary>
    /// Aynı filtreye uyan kayıtları CSV olarak indirir.
    /// </summary>
    /// <remarks>
    /// <see cref="ActionResult"/> değil düz <see cref="Task"/> döner: satırlar yanıt gövdesine
    /// AKARAK yazılır. Bir sonuç nesnesi döndürmek, tüm kayıtları önce belleğe toplamak demekti —
    /// dışa aktarma tam da büyük veri kümesinde gerekir ve orada çökerdi.
    /// </remarks>
    [HttpGet("export")]
    public async Task ExportAsync(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? userName,
        [FromQuery] string? action,
        [FromQuery] string? resource,
        [FromQuery] string? search,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(fromUtc, toUtc, userName, action, resource, search, page: 1, pageSize: 1);

        // Kapsam, yanıt başlıkları yazılmadan ÖNCE çözülür: burada bir hata olursa istemci düzgün
        // bir hata yanıtı alsın, yarım bir CSV dosyası değil.
        var visibility = await ResolveVisibilityAsync(cancellationToken);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"audit-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv\"";

        await AuditCsvWriter.WriteAsync(
            Response.Body,
            auditService.StreamAsync(query, visibility, cancellationToken),
            cancellationToken);
    }

    /// <summary>Saklama politikası + biriken kaydın ölçümü. Sunucu geneli → yalnız yönetici.</summary>
    [HttpGet("retention")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<AuditRetentionSettingsDto>>> GetRetentionAsync(
        CancellationToken cancellationToken)
    {
        return Ok(ApiResponse<AuditRetentionSettingsDto>.Ok(await auditService.GetRetentionAsync(cancellationToken)));
    }

    [HttpPut("retention")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<AuditRetentionSettingsDto>>> SetRetentionAsync(
        [FromBody] UpdateAuditRetentionRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await auditService.SetRetentionAsync(request.RetentionDays, cancellationToken);

        // Saklama süresini kısaltmak, geçmişi silmenin dolaylı yoludur → değişikliğin kendisi de kayda geçer.
        await auditService.AuditAsync(
            HttpContext,
            "audit.retention.updated",
            "audit-logs",
            $"Audit log retention set to {settings.RetentionDays} day(s).");

        return Ok(ApiResponse<AuditRetentionSettingsDto>.Ok(settings));
    }

    /// <summary>Saklama süresinden eski kayıtları hemen temizler.</summary>
    [HttpPost("purge")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<AuditRetentionSettingsDto>>> PurgeAsync(CancellationToken cancellationToken)
    {
        var deleted = await auditService.PurgeAsync(cancellationToken);

        // Temizlik kaydı temizlikten SONRA yazılır ki kendi kendini silmesin.
        await auditService.AuditAsync(
            HttpContext,
            "audit.retention.purged",
            "audit-logs",
            $"Audit log cleanup removed {deleted} record(s).");

        return Ok(ApiResponse<AuditRetentionSettingsDto>.Ok(await auditService.GetRetentionAsync(cancellationToken)));
    }

    private static AuditLogQuery BuildQuery(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? userName,
        string? action,
        string? resource,
        string? search,
        int page,
        int pageSize) => new()
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            UserName = userName ?? string.Empty,
            Action = action ?? string.Empty,
            Resource = resource ?? string.Empty,
            Search = search ?? string.Empty,
            Page = page,
            PageSize = pageSize
        };

    /// <summary>Çağıranın rolünü çözüp kapsamı servise sordurur.</summary>
    private Task<AuditVisibility> ResolveVisibilityAsync(CancellationToken cancellationToken) =>
        auditService.ResolveVisibilityAsync(User.GetRequiredUserId(), ResolveRole(), cancellationToken);

    /// <summary>
    /// Rol iddiasını enum'a çevirir. Sıralama önemlidir: birden çok rol taşıyan bir jetonda EN
    /// GENİŞ yetki kazanır; tersi olsaydı yönetici kendi kaydını göremezdi.
    /// Hiçbiri yoksa <see cref="UserRole.Customer"/> DEĞİL, tanınmayan bir değer döner ve servis
    /// kapsamı "hiçbir şey" olarak çözer.
    /// </summary>
    private UserRole ResolveRole()
    {
        if (User.IsInRole("Administrator"))
        {
            return UserRole.Administrator;
        }

        if (User.IsInRole("Operator"))
        {
            return UserRole.Operator;
        }

        if (User.IsInRole("Reseller"))
        {
            return UserRole.Reseller;
        }

        if (User.IsInRole("Customer"))
        {
            return UserRole.Customer;
        }

        return default; // tanımsız rol → AuditVisibility.None
    }
}

/// <summary>Saklama süresi güncelleme isteği.</summary>
public sealed class UpdateAuditRetentionRequest
{
    /// <summary>Kaç günden eski kayıtlar silinsin; 0 = sınırsız sakla.</summary>
    public int RetentionDays { get; set; }
}
