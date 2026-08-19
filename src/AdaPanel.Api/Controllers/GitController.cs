using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: Git dağıtımı, sitenin İÇERİĞİNİ dışarıdaki bir depodan gelen kodla DEĞİŞTİRİR
// (ilk seferde clone, sonrasında fetch + reset — yani yerel değişiklikleri EZER). İki soru kaydı
// zorunlu kılar:
//   • "Sitedeki kod nereden geldi?" — depo adresi ve dal, sitenin kaynağıdır.
//   • "Bu dağıtımı kim tetikledi?" — webhook ucu ANONİMDİR: kimlik doğrulaması yoktur, yalnız gizli
//     bir token vardır. Yani panel oturumu olmayan biri (ya da sızmış bir token) siteyi yeniden
//     dağıtabilir. Bu uç iz bırakmazsa, sitenin içeriğini değiştiren tek yol denetimin tamamen
//     dışında kalırdı. Kayıtta kullanıcı "system" görünür ama İP ADRESİ yazılır — anonim tetiklemede
//     elde kalan tek tanımlayıcı odur.
//
// Token DEĞERİ asla kayda yazılmaz (webhook sırrı); yalnız doğrulamanın sonucu.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/git")]
public sealed class GitController(
    IGitDeploymentService gitDeploymentService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.GetAsync(siteName, cancellationToken);
        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment));
    }

    [HttpPut("{siteName}")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> ConfigureAsync(
        string siteName,
        [FromBody] ConfigureGitDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.ConfigureAsync(siteName, request, cancellationToken);

        // Webhook jetonunun YENİLENİP yenilenmediği olgusu kayda değer (eski jeton geçersiz olur,
        // sağlayıcıdaki kanca susar). Jetonun DEĞERİ elbette yazılmaz; olgu, adı sır çağrıştırmayan
        // bir yerel değişkene alınır ki denetim çağrısının argümanları sır taramasına takılmasın.
        var jetonYenilendi = request.RegenerateSecret;

        // Yapılandırmanın kendisi bir GÜVENLİK kararıdır: hangi deponun bu siteye yazabileceğini
        // belirler. Depo adresini değiştiren biri, siteyi kendi koduyla dağıtabilir hâle gelir.
        await auditService.AuditAsync(
            HttpContext,
            "git.deployment.configured",
            $"site:{siteName}",
            $"Git deployment configured: repository '{request.RepositoryUrl}', branch '{request.Branch}', "
            + $"webhook token rotated: {jetonYenilendi}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment, "git.configured"));
    }

    [HttpPost("{siteName}/deploy")]
    public async Task<ActionResult<ApiResponse<GitDeployResultDto>>> DeployAsync(string siteName, CancellationToken cancellationToken)
    {
        var result = await gitDeploymentService.DeployAsync(siteName, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "git.deployed",
            $"site:{siteName}",
            $"Manual git deployment ({(result.Success ? "success" : "failed")}) from '{result.Deployment.RepositoryUrl}' "
            + $"branch '{result.Deployment.Branch}', commit '{result.Commit}'.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<GitDeployResultDto>.Ok(result, result.Success ? "git.deployed" : "git.deploy_failed"));
    }

    /// <summary>Git sağlayıcısı (GitHub/GitLab) push webhook'u için anonim tetikleyici. Gizli token ile doğrulanır.</summary>
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("{siteName}/webhook")]
    public async Task<ActionResult<ApiResponse<GitDeployResultDto>>> WebhookAsync(
        string siteName,
        [FromQuery] string token,
        CancellationToken cancellationToken)
    {
        var result = await gitDeploymentService.DeployViaWebhookAsync(siteName, token ?? string.Empty, cancellationToken);

        // Anonim uç: kimlik yok, geriye yalnız İP kalıyor (HttpAuditExtensions onu her kayda yazar).
        await auditService.AuditAsync(
            HttpContext,
            "git.deployed.webhook",
            $"site:{siteName}",
            $"Webhook-triggered git deployment ({(result.Success ? "success" : "failed")}) from "
            + $"'{result.Deployment.RepositoryUrl}' branch '{result.Deployment.Branch}', commit '{result.Commit}'.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<GitDeployResultDto>.Ok(result, result.Success ? "git.deployed" : "git.deploy_failed"));
    }

    /// <summary>
    /// Sitenin abonelik sahibi — kiracının, kendi sitesine yapılan dağıtımı görebilmesi için.
    /// </summary>
    /// <remarks>
    /// Bu uçlar yönetici (ve webhook için anonim) tarafından çağrılır; sahibi yazılmazsa kiracı kendi
    /// sitesinin içeriğinin değiştirildiğini denetim görünümünde HİÇ göremez. Sahip çözülemezse kayıt
    /// yine yazılır (sunucu geneli olarak) — kaydı hiç yazmamak çok daha kötü olurdu.
    /// </remarks>
    private async Task<Guid?> SiteSahibiAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            return await customerScopes.GetSiteOwnerUserIdAsync(siteName, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
