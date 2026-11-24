using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
            + $"webhook token rotated: {jetonYenilendi}, HTTPS access token removed because the host changed: {deployment.HttpsTokenRemoved}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment, deployment.HttpsTokenRemoved ? "git.configured_token_removed" : "git.configured"));
    }

    /// <summary>
    /// Yapılandırmayı + panelin git dizinini + geçmişi + anahtar/jetonu kaldırır. YIKICI: dağıtım öncesi
    /// yedeklenen yerel değişiklikler (refs/adapanel/saved) ve geri alma geçmişi de gider; site adı
    /// AYNEN onaylanmadan çalışmaz. Site dosyalarına dokunulmaz.
    /// </summary>
    [HttpPost("{siteName}/remove")]
    public async Task<ActionResult<ApiResponse<GitRemovalResultDto>>> RemoveAsync(
        string siteName, [FromBody] RemoveGitDeploymentRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ConfirmSiteName?.Trim(), siteName, StringComparison.OrdinalIgnoreCase))
            throw new AppException("git.confirmation_required", 400, 400);
        var result = await gitDeploymentService.RemoveAsync(siteName, cancellationToken);
        await auditService.AuditAsync(
            HttpContext,
            "git.deployment.removed",
            $"site:{siteName}",
            $"Git deployment configuration removed (removed: {string.Join(',', result.Removed)}; leftovers: {result.Leftovers.Length}).",
            await SiteSahibiAsync(siteName, cancellationToken));
        return Ok(ApiResponse<GitRemovalResultDto>.Ok(result, result.Leftovers.Length == 0 ? "git.removed" : "git.removed_partial"));
    }

    // Dağıtım anahtarı: kayda yalnız OLGU ve genel anahtarın parmak izi yazılır; özel anahtar hiçbir yere çıkmaz.
    [HttpPost("{siteName}/deploy-key")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> GenerateDeployKeyAsync(string siteName, CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.GenerateDeployKeyAsync(siteName, cancellationToken);
        await auditService.AuditAsync(HttpContext, "git.deploy_key.generated", $"site:{siteName}",
            $"Git deploy key generated (fingerprint {deployment.DeployKeyFingerprint}).", await SiteSahibiAsync(siteName, cancellationToken));
        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment, "git.deploy_key_generated"));
    }

    [HttpDelete("{siteName}/deploy-key")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> RemoveDeployKeyAsync(string siteName, CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.RemoveDeployKeyAsync(siteName, cancellationToken);
        await auditService.AuditAsync(HttpContext, "git.deploy_key.removed", $"site:{siteName}",
            "Git deploy key removed.", await SiteSahibiAsync(siteName, cancellationToken));
        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment, "git.deploy_key_removed"));
    }

    // HTTPS jetonu: kayda JETON ve kullanıcı adı DEĞERİ yazılmaz, yalnız olgu.
    [HttpPut("{siteName}/https-token")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> SetHttpsTokenAsync(
        string siteName, [FromBody] SetGitHttpsTokenRequest request, CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.SetHttpsTokenAsync(siteName, request, cancellationToken);
        await auditService.AuditAsync(HttpContext, "git.https_token.set", $"site:{siteName}",
            "Git HTTPS access token stored (value not logged).", await SiteSahibiAsync(siteName, cancellationToken));
        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment, "git.https_token_set"));
    }

    [HttpDelete("{siteName}/https-token")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> RemoveHttpsTokenAsync(string siteName, CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.RemoveHttpsTokenAsync(siteName, cancellationToken);
        await auditService.AuditAsync(HttpContext, "git.https_token.removed", $"site:{siteName}",
            "Git HTTPS access token removed.", await SiteSahibiAsync(siteName, cancellationToken));
        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment, "git.https_token_removed"));
    }

    // Derleme sonrası komut: yalnız beyaz liste kimlikleri. Kayıt, hangi kimliklerin seçildiğini yazar
    // (siteye kod çalıştıran bir ayardır).
    [HttpPut("{siteName}/post-deploy")]
    public async Task<ActionResult<ApiResponse<GitDeploymentDto>>> SetPostDeployAsync(
        string siteName, [FromBody] SetGitPostDeployRequest request, CancellationToken cancellationToken)
    {
        var deployment = await gitDeploymentService.SetPostDeployAsync(siteName, request, cancellationToken);
        await auditService.AuditAsync(HttpContext, "git.post_deploy.updated", $"site:{siteName}",
            $"Git post-deploy commands set: [{string.Join(", ", deployment.PostDeployCommands)}], timeout {deployment.PostDeployTimeoutSeconds}s.",
            await SiteSahibiAsync(siteName, cancellationToken));
        return Ok(ApiResponse<GitDeploymentDto>.Ok(deployment, "git.post_deploy_updated"));
    }

    [HttpPost("{siteName}/rollback")]
    public async Task<ActionResult<ApiResponse<GitDeployResultDto>>> RollbackAsync(
        string siteName, [FromBody] RollbackGitRequest request, CancellationToken cancellationToken)
    {
        // Geri alma da siteye dosya yazar: dağıtımla aynı disk kotası kapısı.
        await DiskQuotaGate.EnsureForSiteAsync(HttpContext, siteName, 0, cancellationToken);
        var result = await gitDeploymentService.RollbackAsync(siteName, request.DeploymentId, cancellationToken);
        await auditService.AuditAsync(HttpContext, "git.rolled_back", $"site:{siteName}",
            $"Git rollback ({(result.Success ? "success" : "failed")}) to the state before deployment '{request.DeploymentId}', commit '{result.Commit}'.",
            await SiteSahibiAsync(siteName, cancellationToken));
        return Ok(ApiResponse<GitDeployResultDto>.Ok(result, result.Success ? "git.rolled_back" : "git.deploy_failed"));
    }

    [HttpPost("{siteName}/deploy")]
    public async Task<ActionResult<ApiResponse<GitDeployResultDto>>> DeployAsync(string siteName, CancellationToken cancellationToken)
    {
        // M3: dağıtım depoyu siteye yazar; sahibi aboneliğin disk tavanı doluysa BAŞLAMADAN reddedilir.
        await DiskQuotaGate.EnsureForSiteAsync(HttpContext, siteName, 0, cancellationToken);
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
    /// <remarks>
    /// <para>
    /// <b>JETON ARTIK BAŞLIKLA DA KABUL EDİLİYOR</b> (<c>X-AdaPanel-Token</c>) ve tercih edilen
    /// yol odur. Sorgu dizesi IIS/W3C erişim günlüklerine yazılır ve panelin kendi günlük
    /// görüntüleyicisi vardır — yani dağıtım anahtarı, günlüğü okuyabilen herkese görünüyordu.
    /// Sorgu yolu KAPATILMADI: kullanıcıların sağlayıcıya yapıştırdığı mevcut adresler onu
    /// taşıyor ve kapatmak, güncellemeyle birlikte o kancaların hepsini sessizce öldürürdü.
    /// Bedeli görünür kılınıyor: sorgu yolu kullanıldığında denetim kaydı bunu AÇIKÇA yazar.
    /// Seçim kuralı saf bir fonksiyonda (<see cref="GitWebhookToken.Select"/>).
    /// </para>
    /// <para>
    /// <b>Hız sınırı</b> (<see cref="PanelRateLimits.GitWebhook"/>): sağlayıcı push başına BİR
    /// istek gönderir. Sınırın işi, her tetiklemede <c>fetch</c> + <c>reset --hard</c> çalıştıran
    /// ve <c>concurrencyLock</c> yüzünden SERİ olan bu yolu tıkamayı ucuz olmaktan çıkarmak.
    /// </para>
    /// <para>
    /// <b>DOĞRULAMA MANTIĞINA DOKUNULMADI:</b> karşılaştırma <c>CryptographicOperations
    /// .FixedTimeEquals</c> ile SABİT ZAMANLI (<c>GitDeploymentService.cs:117</c>) ve dağıtım
    /// serileştirilmiş durumda. Buradaki değişiklik yalnız jetonun NEREDEN okunduğudur.
    /// </para>
    /// </remarks>
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting(PanelRateLimits.GitWebhook)]
    [HttpPost("{siteName}/webhook")]
    public async Task<ActionResult<ApiResponse<GitDeployResultDto>>> WebhookAsync(
        string siteName,
        [FromQuery(Name = GitWebhookToken.QueryName)] string? token,
        CancellationToken cancellationToken)
    {
        var (jeton, tasiyici) = GitWebhookToken.Select(
            Request.Headers[GitWebhookToken.HeaderName].ToString(),
            token);

        // M3: anonim tetikleyici de sitenin sahibi aboneliğin kotasına tabidir (yönetici muafiyeti yok:
        // kimlik yok). Jeton doğrulaması servistedir; kota yalnız disk dolu mu diye bakar.
        await DiskQuotaGate.EnsureForSiteAsync(HttpContext, siteName, 0, cancellationToken);
        var result = await gitDeploymentService.DeployViaWebhookAsync(siteName, jeton, cancellationToken);

        // Anonim uç: kimlik yok, geriye yalnız İP kalıyor (HttpAuditExtensions onu her kayda yazar).
        //
        // TAŞIYICI KAYDA YAZILIR ve bu düzeltmenin görünür olan yarısıdır: sorgu dizesiyle gelen
        // bir tetikleme, jetonun o istekle birlikte erişim günlüğüne DÜŞTÜĞÜ anlamına gelir.
        // Yönetici bunu ancak kendi denetim ekranında görürse anahtarını yenilemeyi düşünebilir;
        // yoksa sızıntı sessiz kalırdı. Jetonun DEĞERİ elbette yazılmaz, yalnız geldiği yol.
        var tasiyiciNotu = tasiyici switch
        {
            GitWebhookToken.Transport.Header => "token transport: header (not logged)",
            GitWebhookToken.Transport.Query =>
                "token transport: QUERY STRING — the secret is written to IIS/W3C access logs; "
                + $"switch the provider to the '{GitWebhookToken.HeaderName}' header and rotate the secret",
            _ => "token transport: none"
        };

        await auditService.AuditAsync(
            HttpContext,
            "git.deployed.webhook",
            $"site:{siteName}",
            $"Webhook-triggered git deployment ({(result.Success ? "success" : "failed")}) from "
            + $"'{result.Deployment.RepositoryUrl}' branch '{result.Deployment.Branch}', commit '{result.Commit}'. "
            + tasiyiciNotu + ".",
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
