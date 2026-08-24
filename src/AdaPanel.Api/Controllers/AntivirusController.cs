using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Antivirus;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.RunningJobs;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// ClamAV antivirüs (Plesk antivirüs paritesi). Admin-only: tarama sunucu genelinde dosya okur,
// kurulum yazılım kataloğuna yazar. Kurulum SoftwareController'la aynı bootstrapper'ı kullanır.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/antivirus")]
public sealed class AntivirusController(
    IAntivirusService antivirusService,
    IComponentBootstrapper componentBootstrapper,
    IRunningJobService runningJobs,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<AntivirusStatusDto>>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await antivirusService.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<AntivirusStatusDto>.Ok(status));
    }

    /// <summary>ClamAV'ı kurar (~250 MB indirme + servis kaydı; dakikalarca sürer).</summary>
    /// <remarks>
    /// <para>
    /// <b>ÇALIŞAN İŞ KAYDI AÇILIYOR ve türü <c>software</c>:</b> bu uç, <c>SoftwareController</c>
    /// ile TAM OLARAK aynı işi yapar — aynı <c>IComponentBootstrapper</c>, aynı katalog, aynı
    /// bileşen kimliği. İkinci bir tür uydurmak ("antivirus") tek yuvalı kaydı ikiye böler ve
    /// yönetici Yazılım sayfasından PostgreSQL kurarken buradan ClamAV kurmaya başlayabilirdi:
    /// aynı bootstrapper'a aynı anda giren iki kurulum. Aynı tür seçildiği için ikincisi 409 alır.
    /// </para>
    /// <para>
    /// <b>DÜRÜSTLÜK — bu kaydın GÖRÜNDÜĞÜ yer:</b> kayıt <c>software</c> türünde olduğu için
    /// Yazılım ekranının zaten var olan şeridinde ("clamav, N dakikadır sürüyor") okunur. Antivirüs
    /// ekranının KENDİ şeridi bu turda YOK — o, arayüz tarafında ayrı bir iştir. Yani sayfa
    /// yenilenince bilgi artık KAYBOLMUYOR (sunucuda duruyor ve bir uçtan okunabiliyor), ama
    /// antivirüs ekranında henüz GÖSTERİLMİYOR.
    /// </para>
    /// <para>
    /// <b>İSTEK İPTAL TOKEN'I BAĞLANMIYOR</b> — aşağıdaki tarama/tanım uçlarındaki kuralın aynısı
    /// ve burada da ÖLÇÜLEBİLİR bir arıza kapatıyor: token bağlıyken kullanıcı 250 MB'lık indirmenin
    /// ortasında sayfayı yenilerse <c>RequestAborted</c> iptal olur, kurulum yarıda kesilir ve
    /// yarım kurulmuş bir bileşen bırakır. <c>SoftwareController.InstallAsync</c> aynı sebeple
    /// <c>CancellationToken.None</c> kullanıyor; bu uç o kuraldan sapmıştı.
    /// </para>
    /// </remarks>
    [HttpPost("install")]
    public async Task<ActionResult<ApiResponse<AntivirusStatusDto>>> InstallAsync()
    {
        await using var job = await runningJobs.BeginAsync(
            RunningJobKinds.Software, "clamav", subjectUserId: null, CancellationToken.None);

        await componentBootstrapper.InstallAsync(
            "clamav",
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            force: false,
            cancellationToken: CancellationToken.None);

        var status = await antivirusService.GetStatusAsync(CancellationToken.None);
        return Ok(ApiResponse<AntivirusStatusDto>.Ok(status, "antivirus.installed"));
    }

    // TANIM GÜNCELLEME ve TARAMA istek iptal token'ına BAĞLANMAZ (SoftwareController'daki
    // kurulum uçlarıyla aynı kural).
    //
    // KÖK NEDEN: ASP.NET Core, eylem imzasındaki CancellationToken'a HttpContext.RequestAborted'ı
    // bağlar. Kullanıcı 125 dakikalık taramanın ortasında sayfayı YENİLEDİĞİNDE bağlantı kopar,
    // token iptal olur ve ProcessRunner.RunAsync istisna fırlatır -- clamscan.exe ise sunucuda
    // koşmaya DEVAM eder. Yani tarama hem sürer hem de sonucu hiçbir yere yazılmazdı: 2 saatlik
    // iş sessizce çöpe giderdi. Token bağlanmayınca istek istemcisiz de olsa sunucuda tamamlanır,
    // sonuç `config/antivirus.json`'a yazılır ve yenilenmiş sayfa onu `status` ucundan okur.
    /// <summary>Virüs tanımlarını günceller (ilk sefer ~250 MB indirme; dakikalar sürebilir).</summary>
    [HttpPost("definitions/update")]
    public async Task<ActionResult<ApiResponse<AntivirusStatusDto>>> UpdateDefinitionsAsync()
    {
        var status = await antivirusService.UpdateDefinitionsAsync(CancellationToken.None);
        await WriteAuditAsync("antivirus.definitions_updated", "Virus definitions updated via freshclam.", CancellationToken.None);
        return Ok(ApiResponse<AntivirusStatusDto>.Ok(status, "antivirus.definitions_ready"));
    }

    [HttpPost("scan")]
    public async Task<ActionResult<ApiResponse<AntivirusScanResultDto>>> ScanAsync(
        [FromBody] AntivirusScanRequest request)
    {
        var result = await antivirusService.ScanAsync(request, CancellationToken.None);
        await WriteAuditAsync(
            "antivirus.scan_completed",
            $"Scan of '{result.Target}': {result.Status}, {result.InfectedCount} finding(s), {result.ScannedFiles} file(s).",
            CancellationToken.None);
        return Ok(ApiResponse<AntivirusScanResultDto>.Ok(result, "antivirus.scan_done"));
    }

    private Task WriteAuditAsync(string action, string details, CancellationToken cancellationToken) =>
        auditService.WriteAsync(
            action,
            "antivirus",
            details,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
}
