using System.Text.Json;
using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Plesk/cPanel/ham sunucu → AdaPanel site göçü (tek akış): site oluştur + dosya çek (FTP/SFTP) + DB aktar.
// Admin-only. Her adım bağımsız → kısmi başarı olabilir; sonuç adım-adım döner.
//
// DENETİM KAYDI: göç, MÜŞTERİ VERİSİNİ bir sunucudan diğerine taşır ve bu sunucuda yeni bir site +
// yeni bir veritabanı yaratır. İki soruyu kaydın cevaplaması gerekir: (1) "bu sitedeki veri nereden
// geldi" — veri sahipliği/uyum sorusu; (2) "kısmi göç ne zaman ve kim tarafından yapıldı" — göç yarım
// kalırsa site canlıya yanlış içerikle çıkar. Kaynak PAROLASI asla yazılmaz, yalnız kaynak ana bilgisayar
// ve kullanıcı adı.
//
// Keşif (plesk/cpanel discover) ve doğrulama (validate) uçları POST'tur ama hiçbir şey DEĞİŞTİRMEZ —
// uzak sunucudan okuma yaparlar. Bunlar denetim kaydı taramasında MUAF listesindedir.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/migration")]
public sealed class MigrationController(
    ISiteMigrationService migrationService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpPost("site")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<MigrateSiteResultDto>>> MigrateSiteAsync(
        [FromBody] MigrateSiteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.MigrateSiteAsync(
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            onProgress: null,
            cancellationToken);

        await GocuKaydetAsync(request, result.OverallSuccess, cancellationToken);

        return Ok(ApiResponse<MigrateSiteResultDto>.Ok(
            result,
            result.OverallSuccess ? "migration.site.success" : "migration.site.partial"));
    }

    // Aynı göç, ama CANLI: her aşama (plan/başla/sonuç/bitti) üretildiği an SSE (text/event-stream) ile akar →
    // istemci "hangi adımda ne yapılıyor"u anında gösterir. Tek istek; göç bitince akış kapanır. İstemci sekmeyi
    // kapatırsa istek iptal olur (mevcut /site ile aynı davranış). Her olay tek satır: "data: {json}\n\n".
    [HttpPost("site/stream")]
    [RequestSizeLimit(8192)]
    public async Task MigrateSiteStreamAsync(
        [FromBody] MigrateSiteRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // ara proxy varsa tamponlamayı kapat
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        async Task WriteEventAsync(MigrationProgressEvent progressEvent, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(progressEvent, serializerOptions);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        try
        {
            var result = await migrationService.MigrateSiteAsync(
                request,
                User.GetRequiredUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                WriteEventAsync,
                cancellationToken);

            await GocuKaydetAsync(request, result.OverallSuccess, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // İstemci bağlantıyı kapattı (sekme kapandı/yenilendi). Göç sunucuda YARIM kalmış olabilir
            // ve yarım göç de bir DEĞİŞİKLİKTİR: siteyi/veritabanını çoktan oluşturmuş olabilir. İptali
            // izsiz bırakmak, izi silmenin en kolay yolunu (sekmeyi kapat) açık bırakırdı.
            await GocuKaydetAsync(request, basarili: false, CancellationToken.None, "cancelled by client");
        }
        catch (Exception exception)
        {
            await GocuKaydetAsync(request, basarili: false, CancellationToken.None, "failed");

            // Beklenmeyen hata → akışa bir "done(fail)" çerçevesi yaz ki istemci sonsuz "çalışıyor"da takılmasın.
            try
            {
                await WriteEventAsync(
                    new MigrationProgressEvent
                    {
                        Phase = "done",
                        OverallSuccess = false,
                        Message = exception is AppException appException ? appException.MessageKey : "migration.site.partial"
                    },
                    HttpContext.RequestAborted);
            }
            catch
            {
                // bağlantı zaten kapanmış olabilir → yut
            }
        }
    }

    // Plesk REST API keşfi: domain'leri otomatik listele (sihirbazı doldurmak için). Best-effort.
    [HttpPost("plesk/discover")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<PleskDiscoverResultDto>>> DiscoverPleskAsync(
        [FromBody] PleskDiscoverRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.DiscoverPleskAsync(request, cancellationToken);
        return Ok(ApiResponse<PleskDiscoverResultDto>.Ok(result));
    }

    // cPanel/WHM keşfi: hesap/domain'leri otomatik listele (WHM listaccts). Best-effort.
    [HttpPost("cpanel/discover")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<PleskDiscoverResultDto>>> DiscoverCpanelAsync(
        [FromBody] CpanelDiscoverRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.DiscoverCpanelAsync(request, cancellationToken);
        return Ok(ApiResponse<PleskDiscoverResultDto>.Ok(result));
    }

    // Göç ÖNCESİ ön-kontrol: site adı + dosya/DB kaynağı erişilebilirliği. Göç başlatmadan "bağlanır mı" der.
    [HttpPost("validate")]
    [RequestSizeLimit(8192)]
    public async Task<ActionResult<ApiResponse<MigrationValidationResultDto>>> ValidateAsync(
        [FromBody] MigrateSiteRequest request,
        CancellationToken cancellationToken)
    {
        var result = await migrationService.ValidateMigrationAsync(request, cancellationToken);
        return Ok(ApiResponse<MigrationValidationResultDto>.Ok(result));
    }

    /// <summary>
    /// Göçün denetim kaydı: NEREDEN, NEYİN taşındığı ve sonucu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden kaynak adresi yazılıyor:</b> göç sonrası sitede duran veri başka bir sunucudan
    /// gelmiştir. "Bu içerik nereden geldi / hangi hesaptan çekildi" sorusu hem teşhis hem uyum
    /// (veri sahipliği) sorusudur ve cevabı yalnızca bu kayıtta olabilir — kaynak sunucu bizim
    /// değildir, oraya sonradan bakılamaz.
    /// </para>
    /// <para>
    /// <b>Neden parola yazılmıyor:</b> istek uzak sunucunun FTP/SFTP ve veritabanı parolalarını
    /// taşır. Bunlar kayda düşerse denetim kaydı, başka bir sunucunun kimlik bilgilerinin ikinci
    /// deposu olur. Yalnız ana bilgisayar + kullanıcı adı yazılır.
    /// </para>
    /// <para>
    /// <b>Neden abonelik sahibi çözülüyor:</b> hedef site mevcut bir kiracıya aitse, o kiracı kendi
    /// sitesine yapılan yönetici dokunuşunu kendi denetim görünümünde görebilmelidir. Site bu göçte
    /// yeni oluşturulduysa henüz sahibi yoktur → null (sunucu geneli kayıt).
    /// </para>
    /// </remarks>
    private async Task GocuKaydetAsync(
        MigrateSiteRequest request,
        bool basarili,
        CancellationToken cancellationToken,
        string? notu = null)
    {
        Guid? siteSahibi = null;
        try
        {
            siteSahibi = await customerScopes.GetSiteOwnerUserIdAsync(request.SiteName, cancellationToken);
        }
        catch
        {
            // Sahip çözülemezse kaydı hiç yazmamak çok daha kötü olurdu: eylem gerçekleşti.
        }

        var kaynaklar = new List<string>();
        if (request.CreateSite)
        {
            kaynaklar.Add("site created");
        }

        if (request.PullFiles)
        {
            kaynaklar.Add($"files from {request.FileProtocol}://{request.FileUserName}@{request.FileHost}:{request.FilePort}{request.RemotePath}");
        }

        if (request.TransferDatabase)
        {
            kaynaklar.Add(
                $"database '{request.SourceDatabase}' from {request.DatabaseEngine} {request.DbUserName}@{request.DbHost}:{request.DbPort} "
                + $"→ '{(string.IsNullOrWhiteSpace(request.TargetDatabase) ? request.SourceDatabase : request.TargetDatabase)}'");
        }

        var sonuc = notu ?? (basarili ? "success" : "partial");

        await auditService.AuditAsync(
            HttpContext,
            "migration.site",
            $"site:{request.SiteName}",
            $"Site migration ({sonuc}): {(kaynaklar.Count == 0 ? "no step requested" : string.Join("; ", kaynaklar))}.",
            siteSahibi);
    }
}
