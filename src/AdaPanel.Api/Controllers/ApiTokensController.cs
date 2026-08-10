using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Ai;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Kişisel erişim anahtarları — yapay zeka istemcisini (Claude vb.) panele bağlamak için.
/// <para>
/// HER kullanıcı kendi anahtarını oluşturur ve yalnızca kendi anahtarlarını görür/iptal eder.
/// Anahtarın yetkisi sahibinin rolünden türer: müşteri anahtarı müşterinin gördüğü kadarını görür.
/// (Plesk'in MCP eklentisi yalnız yöneticiye izin veriyor; burada bilerek daha ileri gidiliyor.)
/// </para>
/// </summary>
/// <remarks>
/// <b>YALNIZ ÇEREZ ŞEMASI.</b> <c>/api/v1/**</c> uçları artık kişisel anahtarı da kabul ediyor; bu
/// controller kabul ETMEMELİ. Sebep yetki yükseltmesi: salt-okur (yıkıcı yetkisi kapalı) bir anahtar
/// buradan kendine <c>AllowDestructive</c>/<c>AllowTerminal</c> açık YENİ bir anahtar basabilseydi,
/// anahtarın kısıtı kâğıt üzerinde kalırdı — sızan bir anahtar tek çağrıyla tam yetkiye dönüşürdü.
/// Anahtar sunucudan sunucuya iş içindir; kimlik bilgisi üretmek panelde oturum açmayı gerektirir.
/// (Aynı gerekçe <c>AuthController</c> için de geçerli.)
/// </remarks>
[ApiController]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
[Route("api/v1/api-tokens")]
public sealed class ApiTokensController(
    IApiTokenService apiTokenService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ApiTokenDto>>>> GetAsync(CancellationToken cancellationToken)
    {
        var tokens = await apiTokenService.GetForUserAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<ApiTokenDto>>.Ok(tokens));
    }

    /// <summary>
    /// Yeni anahtar üretir. Düz metin değer YALNIZCA bu yanıtta döner; sonra bir daha gösterilemez.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<CreatedApiTokenDto>>> CreateAsync(
        [FromBody] CreateApiTokenRequest request,
        CancellationToken cancellationToken)
    {
        // Sunucuda komut çalıştırma yetkisi YALNIZ yöneticiye verilebilir. Bu, paneli değil
        // sunucuyu vermek demektir; müşteri/bayi anahtarında istense bile sessizce kapatılır.
        if (request.AllowTerminal && !User.IsInRole("Administrator"))
        {
            request.AllowTerminal = false;
        }

        var created = await apiTokenService.CreateAsync(User.GetRequiredUserId(), request, cancellationToken);

        // Denetim kaydı: anahtar DEĞERİ asla yazılmaz, yalnız kimliği ve yetkileri.
        await auditService.WriteAsync(
            "apitoken.created",
            "api-token",
            $"Token '{created.Token.Name}' ({created.Token.Suffix}) created. destructive={created.Token.AllowDestructive}, terminal={created.Token.AllowTerminal}",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<CreatedApiTokenDto>.Ok(created, "apitoken.created"));
    }

    [HttpDelete("{tokenId:guid}")]
    public async Task<ActionResult<ApiResponse<object?>>> RevokeAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        await apiTokenService.RevokeAsync(User.GetRequiredUserId(), tokenId, cancellationToken);
        await auditService.WriteAsync(
            "apitoken.revoked",
            "api-token",
            $"Token {tokenId} revoked.",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<object?>.Ok(null, "apitoken.revoked"));
    }
}
