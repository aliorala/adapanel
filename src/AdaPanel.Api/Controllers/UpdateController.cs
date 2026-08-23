using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/update")]
// DENETİM KAYDI: bu denetleyicinin üç durum değiştiren ucunun hiçbiri iz bırakmıyordu — oysa
// panelin KENDİ İKİLİSİNİ değiştiren yol burasıdır ve indirilen paket SYSTEM olarak çalıştırılır.
// Bir olaydan sonra "panele bu sürümü kim, ne zaman, nereden kurdu" sorusu cevapsız kalıyordu;
// üstelik güncelleme kaynağını (GitHub deposu) değiştirmek, gelecekteki her kurulumun kaynağını
// değiştirmektir ve tek satırlık, izsiz bir işlemdi.
//
// KİRACI BAĞI YOK ve olmamalı: panelin kendisi hiçbir aboneliğe ait değildir. Konu alanı bilerek
// boş bırakılır — sunucu geneli bir kaydı bir kiracıya bağlamak, o kiracıya panelin yönetim
// işlemlerini göstermek olurdu.
public sealed class UpdateController(
    ISelfUpdateService selfUpdateService,
    IAuditService auditService) : ControllerBase
{
    /// <param name="force">
    /// Kullanıcı "kontrol et" düğmesine bastığında true: önbellek atlanır, GitHub'a gidilir.
    /// Varsayılan false, çünkü bu uç aynı zamanda panel açılışında ve rozet yenilemelerinde
    /// çağrılıyor; her seferinde ağa çıkmak GitHub'ın kimliksiz saatlik sınırını (60 istek)
    /// doldurur ve o noktadan sonra yeni sürüm YAYINLANSA BİLE görünmez olur.
    /// </param>
    [HttpGet("check")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> CheckAsync(
        CancellationToken cancellationToken,
        [FromQuery] bool force = false)
    {
        var status = await selfUpdateService.CheckForUpdateAsync(force, cancellationToken);
        return Ok(ApiResponse<UpdateStatusDto>.Ok(status));
    }

    /// <param name="request">
    /// GÖVDEDEKİ <c>downloadUrl</c> ve <c>packageSha256</c> ARTIK KULLANILMIYOR; kaynak yalnızca
    /// panelin GitHub'dan taze çektiği manifesttir (bkz. <c>SelfUpdateService.ApplyUpdateAsync</c>).
    /// <para>
    /// Neden: indirilen paket <b>SYSTEM</b> olarak çalıştırılıyor. Adresi ve özeti aynı isteğin
    /// göndermesi, özet doğrulamasını anlamsız kılıyordu — saldırgan ikisini de kendisi veriyor.
    /// Panel yöneticisi bir Windows yöneticisi değildir (kabuk uçları bilerek LOCAL SERVICE olarak
    /// koşuyor), dolayısıyla bu gerçek bir sınır aşımıydı.
    /// </para>
    /// <para>
    /// Alanlar sözleşmede BIRAKILDI: arayüz zaten manifestten okuduğu iki değeri aynen geri
    /// gönderiyor, kaldırmak eski arayüzleri gereksiz yere kırardı.
    /// </para>
    /// </param>
    [HttpPost("apply")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> ApplyAsync([FromBody] ApplyUpdateRequest request, CancellationToken cancellationToken)
    {
        var status = await selfUpdateService.ApplyUpdateAsync(request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "update.package.downloaded",
            "panel",
            $"Update package prepared: current={status.CurrentVersion}, latest={status.LatestVersion}, "
            + $"package ready={status.IsPackageReady}, sha256={status.PackageSha256}.");

        return Ok(ApiResponse<UpdateStatusDto>.Ok(status, "update.downloaded"));
    }

    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> InstallAsync(CancellationToken cancellationToken)
    {
        var status = await selfUpdateService.InstallPreparedUpdateAsync(cancellationToken);

        // KAYIT KURULUMDAN ÖNCE DEĞİL, ZAMANLAMADAN SONRA ama panel DURMADAN önce yazılır: kurulum
        // paneli yeniden başlatır, yani bu satır yazılamazsa işlemin hiçbir izi kalmaz.
        await auditService.AuditAsync(
            HttpContext,
            "update.install.scheduled",
            "panel",
            $"Panel update installation scheduled: {status.CurrentVersion} → {status.LatestVersion}.");

        return Ok(ApiResponse<UpdateStatusDto>.Ok(status, "update.install_scheduled"));
    }

    // Güncellemelerin çekileceği GitHub deposunu (owner/repo) ayarlar — panel en son Release'i buradan otomatik görür.
    //
    // ARTIK KEYFİ DEĞİL: değer `UpdatePackageTrust.IzinliDepolar` listesinden geçmek zorunda, aksi
    // hâlde `update.github_repo_invalid` döner. Sebep: buradan geçen depo, panelin indirip SYSTEM
    // olarak çalıştıracağı exe'nin kaynağıdır — denetimsiz bırakıldığında bu uç, panel yöneticisini
    // doğrudan SYSTEM'e yükseltiyordu. Listeyi genişletmek bilinçli bir kod değişikliği ister.
    [HttpPost("github")]
    public async Task<ActionResult<ApiResponse<UpdateStatusDto>>> SetGithubAsync([FromBody] SetUpdateGithubRequest request, CancellationToken cancellationToken)
    {
        var status = await selfUpdateService.SetGithubRepoAsync(request.Repo, cancellationToken);

        // Bu değer, panelin indirip SYSTEM olarak çalıştıracağı exe'nin KAYNAĞIDIR.
        await auditService.AuditAsync(
            HttpContext,
            "update.source.changed",
            "panel",
            $"Panel update source repository set to '{request.Repo}'.");

        return Ok(ApiResponse<UpdateStatusDto>.Ok(status, "update.github_saved"));
    }
}
