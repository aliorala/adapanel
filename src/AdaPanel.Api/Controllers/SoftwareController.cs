using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/software")]
public sealed class SoftwareController(
    IComponentBootstrapper componentBootstrapper,
    IOperationProgressService operationProgress,
    IRunningJobService runningJobs,
    IPhpService phpService) : ControllerBase
{
    /// <summary>
    /// Bu sunucuda ŞU AN süren bileşen kurulumu + panel yeniden başladığı için yarıda kalmış olanı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>operations/progress</c> ZATEN VARKEN NEDEN AYRI BİR UÇ:</b> o uç
    /// <c>IOperationProgressService</c>'i okur ve o servis yalnız BELLEKTEDİR — panel yeniden
    /// başladığında bütün adımlar kaybolur ve ekran, PostgreSQL'in 365 MB'lık indirmesinin
    /// yarıda kaldığını değil, hiç başlamadığını gösterir. Ayrıca o servis TEK YUVALIDIR ve ilk
    /// kurulum sihirbazıyla paylaşılır. Buradaki kayıt diske yazılır, sahibi ölçülür ve yalnız
    /// kurulum işine aittir.
    /// </para>
    /// <para>
    /// <b>İKİSİ BİRBİRİNİN YERİNİ ALMAZ:</b> <c>operations/progress</c> "hangi ADIMDA" sorusunu
    /// cevaplar (gerçek, ölçülmüş adım adları — uydurma yüzde değil); bu uç "iş sürüyor mu ve ne
    /// zamandır" sorusunu cevaplar ve sayfa yenilense de cevaplayabilir.
    /// </para>
    /// </remarks>
    [HttpGet("running")]
    public async Task<ActionResult<ApiResponse<RunningJobsStatusDto>>> GetRunningJobsAsync(CancellationToken cancellationToken)
    {
        var status = await runningJobs.GetStatusAsync(
            [RunningJobKinds.Software],
            allowedSubjectUserIds: null,
            cancellationToken);

        return Ok(ApiResponse<RunningJobsStatusDto>.Ok(status));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<SoftwareCatalogDto>>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        return Ok(ApiResponse<SoftwareCatalogDto>.Ok(catalog));
    }

    [HttpGet("operations/progress")]
    public ActionResult<ApiResponse<OperationProgressDto>> GetOperationProgress()
        => Ok(ApiResponse<OperationProgressDto>.Ok(operationProgress.Snapshot()));

    [HttpPost("updates/check")]
    public async Task<ActionResult<ApiResponse<SoftwareCatalogDto>>> CheckForUpdatesAsync()
    {
        // Upstream cozumlemesi network'e gider; istek iptaline baglamiyoruz ki yarida kalmasin.
        var catalog = await componentBootstrapper.CheckForUpdatesAsync(CancellationToken.None);
        return Ok(ApiResponse<SoftwareCatalogDto>.Ok(catalog, "software.updates_checked"));
    }

    // NOT: Kurulum/kaldirma islemleri istek iptal token'ina BAGLANMAZ: indirme uzun surer
    // (orn. PostgreSQL ~365MB) ve kullanici sekmeyi kapatirsa/baglanti koparsa kurulumun
    // yarida kesilmesi tutarsiz durum birakir. Islem sunucuda tamamlanir.
    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<ComponentInstallResultDto>>> InstallAsync(
        [FromBody] ComponentInstallRequest request)
    {
        var actorUserId = User.GetRequiredUserId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        // Kurulum sunucu geneli bir iştir → sahipsiz kayıt, yalnız yöneticide görünür.
        // Etiket bileşen kimliğidir; arayüz ondan görünen adı çözebiliyor.
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Software, request.ComponentId, subjectUserId: null, CancellationToken.None);

        var result = request.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase)
            ? await phpService.InstallVersionAsync(
                request.ComponentId,
                actorUserId,
                ipAddress,
                CancellationToken.None)
            : await componentBootstrapper.InstallAsync(
                request.ComponentId,
                actorUserId,
                ipAddress,
                request.Force,
                CancellationToken.None);

        return Ok(ApiResponse<ComponentInstallResultDto>.Ok(result, result.Message));
    }

    [HttpPost("install-defaults")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ComponentInstallResultDto>>>> InstallDefaultsAsync()
    {
        // İKİ KAYIT, İKİ AYRI SORU. `operationProgress` "hangi adımda" der ve BELLEKTEDİR
        // (panel yeniden başlarsa yok olur); `runningJobs` "iş sürüyor mu, ne zamandır" der ve
        // DİSKTEDİR — sayfa yenilense de, panel yeniden başlasa da cevabı vardır.
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Software, "defaults", subjectUserId: null, CancellationToken.None);

        // Canlı ilerleme: her bileşen InstallComponentInternalAsync içinde adım olarak raporlanır.
        operationProgress.Begin("software.installing_defaults");
        try
        {
            var results = await componentBootstrapper.InstallDefaultsAsync(
                User.GetRequiredUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                CancellationToken.None);

            foreach (var phpResult in results.Where(result =>
                         result.ComponentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(result.Status, "Installed", StringComparison.OrdinalIgnoreCase)))
            {
                await phpService.RegisterFastCgiAsync(phpResult.ComponentId, CancellationToken.None);
            }

            operationProgress.Complete();
            return Ok(ApiResponse<IReadOnlyCollection<ComponentInstallResultDto>>.Ok(results, "software.defaults.completed"));
        }
        catch (Exception exception)
        {
            operationProgress.Fail(exception.Message);
            throw;
        }
    }

    [HttpDelete("{componentId}")]
    public async Task<ActionResult<ApiResponse<object>>> RemoveAsync(string componentId)
    {
        var temizlenemeyenler = await componentBootstrapper.RemoveAsync(
            componentId,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            CancellationToken.None);

        // KISMİ TEMİZLİK "tamamlandı" DEĞİLDİR. Servis durdurulamadıysa ya da dosyalar kilitliyse
        // kullanıcı bunu bilmeli: sunucuda kalan artığı ancak o temizleyebilir.
        return Ok(temizlenemeyenler.Count == 0
            ? ApiResponse<object>.Ok(new { componentId }, "software.remove.completed")
            : ApiResponse<object>.Ok(
                new { componentId, warnings = temizlenemeyenler },
                "software.remove.partial"));
    }
}
