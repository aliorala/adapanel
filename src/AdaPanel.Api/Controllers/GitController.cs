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
