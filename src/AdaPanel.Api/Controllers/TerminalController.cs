using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Terminal;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

/// <summary>
/// Sunucuda PowerShell komutu çalıştırır. <b>Panelin SYSTEM kabuğudur</b> ve YALNIZ sunucu
/// sahibine açıktır.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN GERÇEK:</b> panel servisi <c>scripts\install-service.ps1</c> içinde
/// <c>New-Service</c> ile <c>-Credential</c> VERİLMEDEN kurulur, yani <c>LocalSystem</c> olarak
/// koşar. <c>TerminalService</c> de <c>powershell.exe</c>'yi doğrudan alt süreç olarak başlatır
/// (kısıtlı kimlik yok, <c>LimitedIdentityRunner</c> yok), dolayısıyla bu uçtan geçen her komut
/// <b>SYSTEM</b> yetkisiyle çalışır.
/// </para>
/// <para>
/// <b>Neden rol YETMEZ:</b> panelde "Administrator" bir roldür ve bir yönetici
/// <c>UsersController</c> üzerinden ikinci bir yönetici yaratabilir. Uç yalnız
/// <c>[Authorize(Roles = "Administrator")]</c> ile korunsaydı, panelin başka yerlerinde bir
/// yöneticiye konan HİÇBİR sınır anlamlı olmazdı: saldırgan sınırı aşmak yerine buraya girip
/// SYSTEM olurdu. Bu yüzden yetki, rol yükseltmesiyle ele geçirilemeyen ayrı bir nitelikten
/// gelir (<see cref="IServerOwnerService"/>). Rol denetimi KALDIRILMADI — sahiplik denetimine
/// ulaşmadan önceki ucuz eleme olarak duruyor.
/// </para>
/// <para>
/// <b>Taklit edilen oturum GİREMEZ.</b> Yönetici <c>auth/impersonate</c> ile başka bir kullanıcı
/// olarak oturum açabiliyor. Taklit oturumunda kimlik hedefin kimliğidir; sahip taklit edilebilse
/// buradaki denetim taklit edenin değil hedefin kimliğini görürdü. Taklidin hedefi ayrıca
/// <c>AuthController</c> tarafında yönetici OLMAYAN hesaplarla sınırlandı; ikisi AYNI deliğin iki
/// ucudur ve biri kalkarsa diğeri hâlâ tutar.
/// </para>
/// <para>
/// <b>Kişisel API anahtarı</b> bu uca ayrıca <c>AllowTerminal</c> yeteneği olmadan giremez
/// (<c>PanelTokenCapabilities</c>); anahtarın sahibi sunucu sahibi değilse yetenek açık olsa bile
/// buradaki denetim reddeder.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/terminal")]
public sealed class TerminalController(
    ITerminalService terminalService,
    IServerOwnerService serverOwnerService,
    IAuditService auditService) : ControllerBase
{
    [HttpPost("execute")]
    public async Task<ActionResult<ApiResponse<TerminalCommandResultDto>>> ExecuteAsync(
        [FromBody] ExecuteTerminalCommandRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = User.GetRequiredUserId();
        var taklitEdiliyor = User.FindFirst("impersonator_id") is not null;

        if (taklitEdiliyor || !await serverOwnerService.IsServerOwnerAsync(actorId, cancellationToken))
        {
            // RED DE KAYDA GEÇER. "Sunucu sahibi olmayan bir yönetici SYSTEM kabuğu denedi" bir
            // olayın en erken işaretidir; yalnız başarılı komutları yazmak bu sinyali yok ederdi.
            // Denenen komut da yazılır: reddedilen komut, niyetin kendisidir.
            await auditService.AuditAsync(
                HttpContext,
                "terminal.command.denied",
                "terminal",
                "PowerShell command rejected: caller is not the server owner"
                + (taklitEdiliyor ? " (impersonated session)" : string.Empty)
                + $". Command: {request.Command}");

            throw new AppException("terminal.owner_only", 403, 403);
        }

        var result = await terminalService.ExecuteAsync(request, cancellationToken);

        // KOMUTUN KENDİSİ kayda yazılır.
        //
        // Önceden yalnızca çıkış kodu ve çalışma dizini yazılıyordu; yani "bir komut çalıştı"
        // biliniyor ama HANGİ komut olduğu bilinmiyordu. Bu uç, sunucuda SYSTEM yetkisiyle keyfî
        // kod çalıştırmanın ta kendisidir: komut kaydedilmezse bir olaydan sonra "sunucuda ne
        // yapıldı" sorusu CEVAPSIZ kalır — denetim kaydının var olma sebebi tam olarak budur.
        //
        // GİZLİLİK KARARI — komut metni NEDEN yazılıyor: bu uca yalnız sunucu sahibi girebiliyor,
        // denetim kaydını okuyan uçlar da (AuditLogsController) yönetici uçlarıdır. Yani kaydedilen
        // kişi ile kaydı okuyan kişi AYNI kişidir; korunacak bir üçüncü taraf mahremiyeti yoktur.
        // Buna karşılık kayıt olmadan sunucunun sahibi bile "makinemde ne çalıştı" sorusunu
        // yanıtlayamaz. Komut, yazma yolundaki merkezî temizleyiciden geçer (anahtar=değer biçimli
        // sırlar maskelenir, 2000 karaktere kırpılır); ayrıca "denetim yazımı asıl işlemi bozmaz"
        // güvencesi için AuditAsync yardımcısı kullanılır.
        await auditService.AuditAsync(
            HttpContext,
            "terminal.command.executed",
            "terminal",
            $"PowerShell command in '{result.WorkingDirectory}' finished with exit code {result.ExitCode}"
            + (result.TimedOut ? " (timed out)" : string.Empty)
            + $". Command: {request.Command}");

        return Ok(ApiResponse<TerminalCommandResultDto>.Ok(result, "terminal.executed"));
    }
}
