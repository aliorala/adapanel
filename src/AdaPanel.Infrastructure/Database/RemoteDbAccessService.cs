using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Database;

// panel "Uzak Erişim": MariaDB tüm arayüzlerde dinler ama 3306 Windows Güvenlik Duvarı ile varsayılan
// KAPALIDIR. Bu servis, panelden açılınca SADECE istenen IP'ye 3306 inbound kuralı açar; kapanınca siler.
// Yeniden başlatma / my.ini düzenleme YOK -> canlı DB'ye sıfır kesinti riski.
internal sealed class RemoteDbAccessService(
    IPanelPreferencesService preferencesService,
    JsonFileStore fileStore) : IRemoteDbAccessService
{
    private const string StatePath = "config/remote-db.json";
    private const int DbPort = 3306;
    private const string RuleName = "AdaPanel RemoteDB 3306";

    public async Task<RemoteDbAccessDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var state = await fileStore.ReadAsync(StatePath, new RemoteDbState(), cancellationToken);
        return await BuildDtoAsync(state, cancellationToken);
    }

    public async Task<RemoteDbAccessDto> SetAsync(UpdateRemoteDbAccessRequest request, CancellationToken cancellationToken = default)
    {
        var allowedIp = (request.AllowedIp ?? string.Empty).Trim();
        if (request.Enabled && allowedIp.Length > 0 && !IPAddress.TryParse(allowedIp, out _))
        {
            throw new AppException("database.remote_invalid_ip", 400, 400);
        }

        if (request.Enabled)
        {
            // -RemoteAddress 'Any' (her yer) ya da tek IP. Idempotent: önce sil sonra ekle.
            var remote = allowedIp.Length == 0 ? "Any" : allowedIp;
            var script =
                $"Remove-NetFirewallRule -DisplayName '{RuleName}' -ErrorAction SilentlyContinue | Out-Null; " +
                $"New-NetFirewallRule -DisplayName '{RuleName}' -Direction Inbound -Action Allow -Protocol TCP " +
                $"-LocalPort {DbPort} -RemoteAddress {remote} -Profile Any -ErrorAction Stop | Out-Null";
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            if (!result.Success)
            {
                throw new AppException("database.remote_firewall_failed", 500, 500, result.StandardError);
            }
        }
        else
        {
            // KAPATMA da açma kadar sıkı denetlenmeli. Burada eskiden sonuç okunmuyordu ve
            // -ErrorAction SilentlyContinue hatayı ayrıca bastırıyordu: kural silinemediğinde
            // (grup ilkesiyle yönetilen firewall, farklı profilde oluşmuş kural, yetki yok) panel
            // "kapalı" yazarken veritabanı portu internete açık kalıyordu. Kullanıcının kapalı
            // sandığı bir port, açık olduğunu bildiği porttan daha tehlikelidir.
            //
            // Kural YOKSA hata sayılmaz: kapatma isteği idempotent olmalı.
            var script =
                $"$ErrorActionPreference = 'Stop'; " +
                $"$rule = Get-NetFirewallRule -DisplayName '{RuleName}' -ErrorAction SilentlyContinue; " +
                $"if ($rule) {{ Remove-NetFirewallRule -DisplayName '{RuleName}' | Out-Null }}; " +
                $"if (Get-NetFirewallRule -DisplayName '{RuleName}' -ErrorAction SilentlyContinue) {{ throw 'kural silinemedi' }}";
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            if (!result.Success)
            {
                throw new AppException("database.remote_firewall_failed", 500, 500, result.StandardError);
            }
        }

        var newState = new RemoteDbState { Enabled = request.Enabled, AllowedIp = allowedIp };
        await fileStore.WriteAsync(StatePath, newState, cancellationToken);
        return await BuildDtoAsync(newState, cancellationToken);
    }

    private async Task<RemoteDbAccessDto> BuildDtoAsync(RemoteDbState state, CancellationToken cancellationToken)
    {
        var preferences = await preferencesService.GetAsync(cancellationToken);
        return new RemoteDbAccessDto
        {
            Enabled = state.Enabled,
            AllowedIp = state.AllowedIp,
            ServerHost = preferences.ServerAddress,
            Port = DbPort
        };
    }

    private sealed class RemoteDbState
    {
        public bool Enabled { get; set; }

        public string AllowedIp { get; set; } = string.Empty;
    }
}
