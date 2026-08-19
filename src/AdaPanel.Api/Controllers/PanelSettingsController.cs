using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: panel tercihleri kozmetik değildir. Varsayılan SİTE KÖKÜ ve varsayılan YEDEK KÖKÜ
// buradan gelir; bunları değiştirmek, o andan sonra oluşturulan her sitenin ve alınan her yedeğin
// sunucuda NEREYE yazılacağını değiştirir. "Yedeklerim neden bu diskte / eski yedekler nerede"
// sorusunun cevabı yalnız bu kayıtta bulunur — ayar dosyası son hâli tutar, geçmişi tutmaz. Panelin
// dış alan adı da buradan okunur (panel erişimi kurulumunun girdisi).
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/panel-settings")]
public sealed class PanelSettingsController(
    IPanelPreferencesService preferencesService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PanelPreferencesDto>>> GetAsync(CancellationToken cancellationToken)
        => Ok(ApiResponse<PanelPreferencesDto>.Ok(await preferencesService.GetAsync(cancellationToken)));

    [HttpPut]
    public async Task<ActionResult<ApiResponse<PanelPreferencesDto>>> UpdateAsync(
        [FromBody] UpdatePanelPreferencesRequest request,
        CancellationToken cancellationToken)
    {
        // ÖNCEKİ kökler değişiklikten ÖNCE okunur: eski yedekler eski kökte kalır ve o yolu bilen
        // başka hiçbir kayıt yoktur.
        var oncesi = await preferencesService.GetAsync(cancellationToken);

        var sonrasi = await preferencesService.UpdateAsync(request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "panel.settings.updated",
            "panel",
            $"Panel preferences updated. Site root '{oncesi.DefaultSiteRoot}' → '{sonrasi.DefaultSiteRoot}'; "
            + $"backup root '{oncesi.DefaultBackupRoot}' → '{sonrasi.DefaultBackupRoot}'; "
            + $"server address '{oncesi.ServerAddress}' → '{sonrasi.ServerAddress}'; "
            + $"default language '{oncesi.DefaultLanguage}' → '{sonrasi.DefaultLanguage}'.");

        return Ok(ApiResponse<PanelPreferencesDto>.Ok(sonrasi, "settings.saved"));
    }
}
