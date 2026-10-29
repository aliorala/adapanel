using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Support;
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
    IAuditService auditService,
    IDnsService dnsService) : ControllerBase
{
    private static readonly SemaphoreSlim AddressSaveLock = new(1, 1);
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PanelPreferencesDto>>> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await preferencesService.GetAsync(cancellationToken);
        settings.ServerAddressDnsSync = await dnsService.GetServerAddressDnsSyncAsync(cancellationToken);
        return Ok(ApiResponse<PanelPreferencesDto>.Ok(settings));
    }

    [HttpPut]
    public async Task<ActionResult<ApiResponse<PanelPreferencesDto>>> UpdateAsync(
        [FromBody] UpdatePanelPreferencesRequest request,
        CancellationToken cancellationToken)
    {
        await AddressSaveLock.WaitAsync(cancellationToken);
        try
        {
            // ÖNCEKİ kökler değişiklikten ÖNCE okunur: eski yedekler eski kökte kalır ve o yolu bilen
            // başka hiçbir kayıt yoktur.
            var oncesi = await preferencesService.GetAsync(cancellationToken);
            var pending = await dnsService.GetServerAddressDnsSyncAsync(cancellationToken);
            var requestedAddress = PanelAddress.TryParseAddress(request.ServerAddress ?? string.Empty, out var parsed) &&
                (parsed.Port is null || parsed.Port == request.PreferredHttpsPort) ? parsed.Host : request.ServerAddress?.Trim() ?? string.Empty;
            if (pending is { Pending: true } && pending.OldAddress == oncesi.ServerAddress && pending.NewAddress != oncesi.ServerAddress)
            {
                await dnsService.CancelPreparedServerAddressDnsSyncAsync(pending.OldAddress, pending.NewAddress, cancellationToken);
                pending = await dnsService.GetServerAddressDnsSyncAsync(cancellationToken);
            }
            if (pending is { Pending: true } && !string.Equals(requestedAddress, oncesi.ServerAddress, StringComparison.Ordinal) &&
                !string.Equals(requestedAddress, pending.NewAddress, StringComparison.Ordinal))
                throw new AppException("dns.sync_failed", 409, 409, "Retry the pending DNS address migration before changing the server address again.");

            if (IsIpv4(oncesi.ServerAddress) && IsIpv4(requestedAddress) && oncesi.ServerAddress != requestedAddress)
                await dnsService.PrepareServerAddressDnsSyncAsync(oncesi.ServerAddress, requestedAddress, cancellationToken);

            PanelPreferencesDto sonrasi;
            try { sonrasi = await preferencesService.UpdateAsync(request, cancellationToken); }
            catch
            {
                // A rejected preferences save must not strand a prepared migration. If the
                // preferences file already committed, keep the journal for the next retry.
                if (pending is not { Pending: true })
                {
                    var persisted = await preferencesService.GetAsync(CancellationToken.None);
                    if (persisted.ServerAddress == oncesi.ServerAddress)
                        await dnsService.CancelPreparedServerAddressDnsSyncAsync(oncesi.ServerAddress, requestedAddress, CancellationToken.None);
                }
                throw;
            }
            sonrasi.ServerAddressDnsSync = pending;
            if (pending is { Pending: true } && pending.NewAddress == sonrasi.ServerAddress)
                sonrasi.ServerAddressDnsSync = await dnsService.SyncServerAddressAsync(pending.OldAddress, pending.NewAddress, cancellationToken);
            else if (IsIpv4(oncesi.ServerAddress) && IsIpv4(sonrasi.ServerAddress) && oncesi.ServerAddress != sonrasi.ServerAddress)
                sonrasi.ServerAddressDnsSync = await dnsService.SyncServerAddressAsync(oncesi.ServerAddress, sonrasi.ServerAddress, cancellationToken);
            else if (request.ReconcileServerAddress && IsIpv4(sonrasi.ServerAddress) && oncesi.ServerAddress == sonrasi.ServerAddress)
                sonrasi.ServerAddressDnsSync = await dnsService.ReconcileServerAddressAsync(sonrasi.ServerAddress, cancellationToken);

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
        finally { AddressSaveLock.Release(); }
    }

    [HttpPost("dns-address-sync")]
    public async Task<ActionResult<ApiResponse<PanelPreferencesDto>>> RetryDnsAddressSyncAsync(
        [FromBody] RetryServerAddressDnsSyncRequest request, CancellationToken cancellationToken)
    {
        await AddressSaveLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await preferencesService.GetAsync(cancellationToken);
            settings.ServerAddressDnsSync = await dnsService.SyncServerAddressAsync(request.OldAddress, settings.ServerAddress, cancellationToken);
            await auditService.AuditAsync(HttpContext, "panel.settings.updated", "dns-address-sync",
                $"DNS server address synchronization '{settings.ServerAddressDnsSync.OldAddress}' → '{settings.ServerAddress}'; pending={settings.ServerAddressDnsSync.Pending}.");
            return Ok(ApiResponse<PanelPreferencesDto>.Ok(settings, "settings.saved"));
        }
        finally { AddressSaveLock.Release(); }
    }

    private static bool IsIpv4(string value)
    {
        if (!System.Net.IPAddress.TryParse(value, out var address) || address.ToString() != value ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is not (0 or 10 or 127 or >= 224) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}
