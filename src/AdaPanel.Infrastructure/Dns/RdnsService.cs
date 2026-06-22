using System.Net;
using System.Net.Sockets;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Dns;

/// <summary>
/// Ters DNS (PTR) dogrulayicisi. PTR kaydi IP sahibinde (VPS saglayici) tutuldugundan panel
/// onu yazamaz; bu servis hostname -> IP -> PTR zincirini cozup eslesmeyi ve forward-confirmed
/// rDNS'i (FCrDNS) raporlar, kullaniciyi saglayici panelinde ne ayarlayacagina yonlendirir.
/// </summary>
public sealed class RdnsService(IPanelPreferencesService preferencesService) : IRdnsService
{
    public async Task<RdnsCheckDto> CheckAsync(string? hostName, CancellationToken cancellationToken = default)
    {
        var host = (hostName ?? string.Empty).Trim().TrimEnd('.');
        if (host.Length == 0)
        {
            var preferences = await preferencesService.GetAsync(cancellationToken);
            host = (preferences.ServerAddress ?? string.Empty).Trim().TrimEnd('.');
        }

        var dto = new RdnsCheckDto { ExpectedHostName = host };
        if (host.Length == 0)
        {
            dto.StatusMessage = "mail.rdns.no_host";
            return dto;
        }

        // 1) hostname -> IP (host zaten IP ise dogrudan kullanilir)
        IPAddress? ip;
        if (!IPAddress.TryParse(host, out ip))
        {
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);
                ip = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? (addresses.Length > 0 ? addresses[0] : null);
            }
            catch
            {
                ip = null;
            }
        }

        if (ip is null)
        {
            dto.StatusMessage = "mail.rdns.resolve_failed";
            return dto;
        }

        dto.IpAddress = ip.ToString();

        // 2) IP -> PTR (ters lookup)
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(ip.ToString(), cancellationToken);
            dto.PtrHostName = (entry.HostName ?? string.Empty).TrimEnd('.');
            dto.HasPtr = dto.PtrHostName.Length > 0 && !string.Equals(dto.PtrHostName, dto.IpAddress, StringComparison.Ordinal);
        }
        catch
        {
            dto.HasPtr = false;
        }

        // 3) eslesme + 4) forward-confirmed (PTR hostname'in A kaydi ayni IP mi)
        if (dto.HasPtr)
        {
            dto.Matches = string.Equals(dto.PtrHostName, host, StringComparison.OrdinalIgnoreCase);
            try
            {
                var forward = await System.Net.Dns.GetHostAddressesAsync(dto.PtrHostName, cancellationToken);
                dto.ForwardConfirmed = Array.Exists(forward, a => a.Equals(ip));
            }
            catch
            {
                dto.ForwardConfirmed = false;
            }
        }

        dto.StatusMessage = !dto.HasPtr
            ? "mail.rdns.missing"
            : dto.Matches && dto.ForwardConfirmed
                ? "mail.rdns.ok"
                : dto.Matches
                    ? "mail.rdns.match_no_fcrdns"
                    : "mail.rdns.mismatch";

        return dto;
    }
}
