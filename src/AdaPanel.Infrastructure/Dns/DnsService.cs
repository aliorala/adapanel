using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Dns;

public sealed partial class DnsService(
    IIisSiteService iisSiteService,
    IPanelPreferencesService panelPreferencesService,
    JsonFileStore fileStore) : IDnsService
{
    private const string StatePath = "config/dns-zones.json";
    private const string TemplatePath = "config/dns-template.json";
    private const string WatchdogPath = "config/dns-watchdog.json";
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WatchdogCooldown = TimeSpan.FromHours(1);
    private const int CurrentDefaultsVersion = 1;
    private static readonly string[] SupportedTypes = ["A", "AAAA", "CNAME", "MX", "TXT", "NS"];

    public async Task<SiteDnsZoneDto> GetZoneAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task EnsureZonePublishedAsync(string siteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            var state = await GetOrCreateStateAsync(site, cancellationToken);
            await TrySyncWindowsDnsAsync(state, cancellationToken);
        }
        catch
        {
            // Best-effort: Windows DNS yoksa/yazilamazsa site olusturmayi bozmamak icin sessiz gec.
        }
    }

    public async Task<SiteDnsZoneDto> InstallWindowsDnsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            // Install-WindowsFeature yalnız Windows Server'da vardir (istemci OS'ta yok).
            var install = await ProcessRunner.RunPowerShellAsync(
                "if (-not (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue)) { exit 21 }; " +
                "Install-WindowsFeature -Name DNS -IncludeManagementTools | Out-Null",
                cancellationToken);

            if (install.ExitCode == 21)
            {
                throw new AppException(
                    "dns.windows_install_unsupported",
                    400,
                    400,
                    "Install-WindowsFeature is not available. The Windows DNS Server role requires Windows Server.");
            }

            if (!install.Success)
            {
                throw new AppException("dns.windows_install_failed", 500, 500, BuildCommandDetails(install));
            }
        }

        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> UpdateZoneAsync(string siteName, UpdateDnsZoneRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        state.ZoneName = NormalizeZoneName(string.IsNullOrWhiteSpace(request.ZoneName) ? state.ZoneName : request.ZoneName);
        state.NameServers = NormalizeNameServers(request.NameServers, state.ZoneName);
        state.ZoneTransferIps = NormalizeZoneTransferIps(request.ZoneTransferIps);
        state.Records.RemoveAll(record => record.IsManaged);
        state.Records.AddRange(await BuildDefaultRecordsAsync(site, state.ZoneName, serverAddress, cancellationToken));
        state.DefaultsVersion = CurrentDefaultsVersion;
        state.Records = SortRecords(state.Records).ToList();

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> ResetDefaultsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        state.Records.RemoveAll(record => record.IsManaged);
        state.Records.AddRange(await BuildDefaultRecordsAsync(site, state.ZoneName, serverAddress, cancellationToken));
        state.DefaultsVersion = CurrentDefaultsVersion;
        state.Records = SortRecords(state.Records).ToList();

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var record = NormalizeRecord(request, state.ZoneName);
        record.IsManaged = false;

        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            var index = state.Records.FindIndex(item => string.Equals(item.Id, request.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new AppException("dns.record_not_found", 404, 404);
            }

            record.Id = state.Records[index].Id;
            state.Records[index] = record;
        }
        else
        {
            var duplicate = state.Records.FirstOrDefault(item =>
                string.Equals(item.Type, record.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Host, record.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Value, record.Value, StringComparison.OrdinalIgnoreCase));

            if (duplicate is not null)
            {
                record.Id = duplicate.Id;
                state.Records[state.Records.IndexOf(duplicate)] = record;
            }
            else
            {
                state.Records.Add(record);
            }
        }

        state.Records = SortRecords(state.Records).ToList();
        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);
        var removed = state.Records.RemoveAll(item => string.Equals(item.Id, recordId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            throw new AppException("dns.record_not_found", 404, 404);
        }

        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return await ToDtoAsync(state, cancellationToken);
    }

    public async Task DeleteZoneAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken)).ToList();
        var zone = states.FirstOrDefault(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        if (zone is null)
        {
            return;
        }

        states.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        await fileStore.WriteAsync(StatePath, states.OrderBy(item => item.SiteName, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);

        // Windows DNS'te bölge varsa best-effort kaldır (yoksa sessiz geç).
        if (!string.IsNullOrWhiteSpace(zone.ZoneName) && await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            var script =
                "$ErrorActionPreference = 'SilentlyContinue'\n" +
                $"$zoneName = '{EscapePowerShellLiteral(zone.ZoneName)}'\n" +
                "if (Get-DnsServerZone -Name $zoneName -ErrorAction SilentlyContinue) { Remove-DnsServerZone -Name $zoneName -Force -ErrorAction SilentlyContinue }";
            try
            {
                await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            }
            catch
            {
                // best-effort: panel DNS kaydı zaten kaldırıldı.
            }
        }
    }

    private async Task<DnsZoneState> GetOrCreateStateAsync(SiteDto site, CancellationToken cancellationToken)
    {
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken);
        var state = states.FirstOrDefault(item => string.Equals(item.SiteName, site.Name, StringComparison.OrdinalIgnoreCase));
        if (state is not null)
        {
            state.ZoneName = NormalizeZoneName(state.ZoneName);
            state.NameServers = NormalizeNameServers(state.NameServers, state.ZoneName);
            state.ZoneTransferIps = NormalizeZoneTransferIps(state.ZoneTransferIps?.ToArray());
            state.Records = SortRecords(state.Records.Select(record => NormalizeRecord(record, state.ZoneName))).ToList();
            if (state.DefaultsVersion < CurrentDefaultsVersion)
            {
                MergeMissingDefaults(state.Records, await BuildDefaultRecordsAsync(site, state.ZoneName, serverAddress, cancellationToken));
                state.DefaultsVersion = CurrentDefaultsVersion;
                state.Records = SortRecords(state.Records).ToList();
                await UpsertStateAsync(state, cancellationToken);
                await TrySyncWindowsDnsAsync(state, cancellationToken);
            }

            return state;
        }

        var zoneName = ResolveDefaultZoneName(site);
        var defaultRecords = await BuildDefaultRecordsAsync(site, zoneName, serverAddress, cancellationToken);
        state = new DnsZoneState
        {
            SiteName = site.Name,
            ZoneName = zoneName,
            NameServers = NormalizeNameServers([], zoneName),
            Records = defaultRecords,
            DefaultsVersion = CurrentDefaultsVersion
        };
        await UpsertStateAsync(state, cancellationToken);
        await TrySyncWindowsDnsAsync(state, cancellationToken);
        return state;
    }

    private async Task UpsertStateAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<DnsZoneState>(), cancellationToken)).ToList();
        states.RemoveAll(item => string.Equals(item.SiteName, state.SiteName, StringComparison.OrdinalIgnoreCase));
        states.Add(state);
        await fileStore.WriteAsync(StatePath, states.OrderBy(item => item.SiteName, StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
    }

    private async Task<SiteDnsZoneDto> ToDtoAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        var isDnsAvailable = await IsWindowsDnsAvailableAsync(cancellationToken);
        var isZonePresent = isDnsAvailable && await IsWindowsZonePresentAsync(state.ZoneName, cancellationToken);
        var (isSigned, dsRecords) = isZonePresent
            ? await ReadDnssecAsync(state.ZoneName, cancellationToken)
            : (false, Array.Empty<string>());
        return new SiteDnsZoneDto
        {
            SiteName = state.SiteName,
            ZoneName = state.ZoneName,
            NameServers = state.NameServers,
            ZoneTransferIps = (state.ZoneTransferIps ?? []).ToArray(),
            Records = SortRecords(state.Records).Select(ToDto).ToArray(),
            IsWindowsDnsAvailable = isDnsAvailable,
            IsWindowsZonePresent = isZonePresent,
            Source = isDnsAvailable ? "windows-dns" : "panel-managed",
            StatusMessage = isDnsAvailable
                ? isZonePresent ? "dns.windows_zone_present" : "dns.windows_zone_missing"
                : "dns.windows_dns_unavailable",
            IsDnssecSigned = isSigned,
            DnssecDsRecords = dsRecords
        };
    }

    private static SiteDnsRecordDto ToDto(DnsRecordState state)
    {
        return new SiteDnsRecordDto
        {
            Id = state.Id,
            Type = state.Type,
            Host = state.Host,
            Value = state.Value,
            Ttl = state.Ttl,
            Priority = state.Priority,
            IsManaged = state.IsManaged
        };
    }

    // --- DNS sunucusu erişilebilirlik sağlığı + onarım ---
    // KANITLANMIŞ OLAY (2026-07-23 reboot, 6 gün fark edilmedi): DNS servisi "Running", yapılandırmada
    // genel IP dinleme listesinde, AMA ağ arayüzü hazır olmadan başladığı için fiilen yalnız
    // 127.0.0.1/::1/link-local'e bind olmuş. Dışarıdan tek bir sorgu bile yanıtlanmadı → siteler
    // açılmadı, Let's Encrypt DNS-01 zaman aşımına uğradı. Bu yüzden SERVİS DURUMU DEĞİL,
    // 53 portunun FİİLİ DİNLEYİCİLERİ ölçülür (Get-NetUDPEndpoint / Get-NetTCPConnection, DNS PID'i ile).
    private const string ServerHealthScript =
        """
        $ErrorActionPreference = 'Continue'
        $out = [ordered]@{
            installed = $false
            serviceStatus = 'NotInstalled'
            startMode = 'Unknown'
            delayedAutoStart = $false
            listening = @()
            expected = @()
            zoneCount = 0
            notes = @()
        }

        $out.installed = [bool](Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue)

        $service = Get-Service -Name 'DNS' -ErrorAction SilentlyContinue
        if ($service) { $out.serviceStatus = $service.Status.ToString() }

        $processId = 0
        try {
            $wmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='DNS'" -ErrorAction Stop
            if ($wmi) {
                $out.startMode = [string]$wmi.StartMode
                $processId = [int]$wmi.ProcessId
            }
        }
        catch { $out.notes += "service-query: $($_.Exception.Message)" }

        try {
            $delayed = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\DNS' -Name 'DelayedAutostart' -ErrorAction Stop).DelayedAutostart
            $out.delayedAutoStart = ($delayed -eq 1)
        }
        catch { }

        if ($processId -gt 0) {
            $udp = @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $processId } | Select-Object -ExpandProperty LocalAddress)
            $tcp = @(Get-NetTCPConnection -LocalPort 53 -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $processId } | Select-Object -ExpandProperty LocalAddress)
            $out.listening = @(($udp + $tcp) | Sort-Object -Unique)
        }
        else {
            $out.notes += 'service-not-running'
        }

        $out.expected = @(
            Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
                Select-Object -ExpandProperty IPAddress -Unique
        )

        if ($out.installed) {
            try { $out.zoneCount = @(Get-DnsServerZone -ErrorAction Stop | Where-Object { -not $_.IsAutoCreated -and $_.ZoneType -eq 'Primary' }).Count }
            catch { $out.notes += "zone-query: $($_.Exception.Message)" }
        }

        $out | ConvertTo-Json -Depth 4 -Compress
        """;

    private sealed class DnsWatchdogRecord
    {
        public DateTime? LastCheckUtc { get; set; }

        public DateTime? LastRepairUtc { get; set; }

        public string LastStatus { get; set; } = string.Empty;
    }

    private sealed class ServerHealthProbe
    {
        public bool Installed { get; init; }

        public string ServiceStatus { get; init; } = "Unknown";

        public string StartMode { get; init; } = "Unknown";

        public bool DelayedAutoStart { get; init; }

        public string[] Listening { get; init; } = [];

        public string[] Expected { get; init; } = [];

        public int ZoneCount { get; init; }

        public string[] Notes { get; init; } = [];
    }

    /// <summary>
    /// Saf karar mantığı (test edilebilir): 53'te dinlenen adresler, beklenen genel adreslerden
    /// hangilerini karşılıyor. <c>0.0.0.0</c> / <c>::</c> tüm arayüzleri kapsar.
    /// </summary>
    internal static (bool PubliclyBound, string[] Missing) EvaluateBinding(
        IEnumerable<string> listeningAddresses,
        IEnumerable<string> expectedAddresses)
    {
        var listening = listeningAddresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .ToArray();

        var expected = expectedAddresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var wildcard = listening.Any(address => address is "0.0.0.0" or "::" or "*");
        if (wildcard)
        {
            return (true, []);
        }

        var missing = expected
            .Where(address => !listening.Contains(address, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        // Beklenen adres hiç bulunamadıysa (ör. NAT arkasında tek arayüz okunamadı) loopback
        // DIŞINDA bir adreste dinliyor olmak yeterli sayılır; yanlış alarm üretmeyiz.
        var bound = expected.Length > 0
            ? missing.Length < expected.Length
            : listening.Any(address => !IsLoopbackOrLinkLocal(address));

        return (bound, missing);
    }

    private static bool IsLoopbackOrLinkLocal(string address)
    {
        return address.StartsWith("127.", StringComparison.Ordinal) ||
               address.StartsWith("169.254.", StringComparison.Ordinal) ||
               address is "::1" ||
               address.StartsWith("fe80", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DnsServerHealthDto> GetServerHealthAsync(CancellationToken cancellationToken = default)
    {
        var run = await ProcessRunner.RunPowerShellAsync(ServerHealthScript, cancellationToken);
        var watchdog = await fileStore.ReadAsync(WatchdogPath, new DnsWatchdogRecord(), cancellationToken);
        var probe = ParseProbe(run.StandardOutput);
        if (probe is null)
        {
            return new DnsServerHealthDto
            {
                LastAutoRepairUtc = watchdog.LastRepairUtc,
                LastAutoRepairStatus = watchdog.LastStatus,
                Details = string.IsNullOrWhiteSpace(run.StandardError)
                    ? "DNS sunucu sağlığı ölçülemedi."
                    : run.StandardError.Trim()
            };
        }

        var expected = probe.Expected.ToList();

        // Panelin kayıtlı sunucu adresi bir IP ise beklenenlere ekle: NAT arkasında yerel arayüz
        // adresi farklı olabilir, ama panel hangi adresi yayınladığını bilir.
        var serverAddress = await ResolvePreferredServerAddressAsync(cancellationToken);
        if (System.Net.IPAddress.TryParse(serverAddress, out _) &&
            !expected.Contains(serverAddress, StringComparer.OrdinalIgnoreCase))
        {
            expected.Add(serverAddress);
        }

        var (publiclyBound, missing) = EvaluateBinding(probe.Listening, expected);

        var details = new StringBuilder()
            .Append("service=").Append(probe.ServiceStatus)
            .Append("; startMode=").Append(probe.StartMode)
            .Append(probe.DelayedAutoStart ? " (delayed-auto)" : string.Empty)
            .Append("; listening=").Append(probe.Listening.Length == 0 ? "(none)" : string.Join(",", probe.Listening))
            .Append("; expected=").Append(expected.Count == 0 ? "(none)" : string.Join(",", expected))
            .Append("; zones=").Append(probe.ZoneCount);

        if (probe.Notes.Length > 0)
        {
            details.Append("; notes=").Append(string.Join(" | ", probe.Notes.Take(6)));
        }

        return new DnsServerHealthDto
        {
            Installed = probe.Installed,
            ServiceStatus = probe.ServiceStatus,
            StartMode = probe.StartMode,
            DelayedAutoStart = probe.DelayedAutoStart,
            ListeningAddresses = probe.Listening,
            ExpectedAddresses = expected.ToArray(),
            MissingAddresses = missing,
            PubliclyBound = publiclyBound,
            ZoneCount = probe.ZoneCount,
            LastAutoRepairUtc = watchdog.LastRepairUtc,
            LastAutoRepairStatus = watchdog.LastStatus,
            Details = details.ToString()
        };
    }

    public async Task<DnsServerHealthDto?> RunBindWatchdogAsync(CancellationToken cancellationToken = default)
    {
        // Otomasyon worker'ı dakikada bir tetiklenir; ölçüm aralığını BURADA kısıtlıyoruz (servis
        // Scoped olduğu için bellekte sayaç tutulamaz, durum dosyada). Zaman damgası ölçümden ÖNCE
        // yazılır: ölçüm patlarsa dakika başı yeniden denenip sıcak döngü oluşmaz.
        var watchdog = await fileStore.ReadAsync(WatchdogPath, new DnsWatchdogRecord(), cancellationToken);
        if (watchdog.LastCheckUtc is { } lastCheck && DateTime.UtcNow - lastCheck < WatchdogInterval)
        {
            return null;
        }

        watchdog.LastCheckUtc = DateTime.UtcNow;
        await fileStore.WriteAsync(WatchdogPath, watchdog, cancellationToken);

        var health = await GetServerHealthAsync(cancellationToken);
        if (!health.NeedsRepair)
        {
            return health;
        }

        if (watchdog.LastRepairUtc is { } last && DateTime.UtcNow - last < WatchdogCooldown)
        {
            // Soğuma süresi: art arda yeniden başlatma döngüsü kurmayız. Sorun devam ediyorsa
            // Sunucu Sağlığı ekranında kırmızı olarak görünmeye devam eder.
            return health;
        }

        try
        {
            var repaired = await RepairServerAsync(true, cancellationToken);
            watchdog.LastRepairUtc = DateTime.UtcNow;
            watchdog.LastStatus = repaired.PubliclyBound
                ? "ok: DNS servisi yeniden başlatıldı, genel adreste dinliyor."
                : $"failed: yeniden başlatıldı ama hâlâ dinlemiyor ({repaired.Details})";
            await fileStore.WriteAsync(WatchdogPath, watchdog, cancellationToken);
        }
        catch (Exception exception)
        {
            watchdog.LastRepairUtc = DateTime.UtcNow;
            watchdog.LastStatus = $"failed: {exception.Message}";
            await fileStore.WriteAsync(WatchdogPath, watchdog, cancellationToken);
            throw;
        }

        // Nöbetçi kaydı yazıldıktan sonra yeniden ölç: dönen DTO hem güncel bind durumunu hem de
        // "son otomatik onarım" bilgisini içerir (Sunucu Sağlığı ekranı bunu gösterir).
        return await GetServerHealthAsync(cancellationToken);
    }

    public async Task<DnsServerHealthDto> RepairServerAsync(bool applyDelayedAutoStart, CancellationToken cancellationToken = default)
    {
        var before = await GetServerHealthAsync(cancellationToken);
        if (!before.Installed)
        {
            throw new AppException("dns.server_not_installed", 409, 409, before.Details);
        }

        // sc.exe kullanılıyor: Windows PowerShell 5.1'in Set-Service'i AutomaticDelayedStart'ı
        // DESTEKLEMEZ. Bu, canlı sunucuda kanıtlanmış komutun aynısıdır.
        var script = new StringBuilder();
        if (applyDelayedAutoStart)
        {
            script.AppendLine("& sc.exe config DNS start= delayed-auto | Out-Null");
        }

        script.AppendLine("Restart-Service -Name 'DNS' -Force -ErrorAction Stop");
        // Servis ayağa kalkıp soketleri açana kadar kısa bekleme; hemen ölçersek dinleyiciler boş görünür.
        script.AppendLine("Start-Sleep -Seconds 3");

        var result = await ProcessRunner.RunPowerShellAsync(script.ToString(), cancellationToken);
        if (!result.Success)
        {
            throw new AppException("dns.server_restart_failed", 500, 500, BuildCommandDetails(result));
        }

        return await GetServerHealthAsync(cancellationToken);
    }

    private static ServerHealthProbe? ParseProbe(string standardOutput)
    {
        try
        {
            var text = (standardOutput ?? string.Empty).Trim();
            var start = text.IndexOf('{');
            if (start < 0)
            {
                return null;
            }

            return System.Text.Json.JsonSerializer.Deserialize<ServerHealthProbe>(
                text[start..],
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    public async Task<DnsTemplateDto> GetTemplateAsync(CancellationToken cancellationToken = default)
    {
        var saved = await fileStore.ReadAsync(TemplatePath, new DnsTemplateDto(), cancellationToken);
        return saved.Records.Count > 0 ? saved : BuiltInTemplate();
    }

    public async Task<DnsTemplateDto> UpdateTemplateAsync(UpdateDnsTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA"
        };

        var clean = new DnsTemplateDto();
        foreach (var record in request.Records ?? [])
        {
            var type = (record.Type ?? string.Empty).Trim().ToUpperInvariant();
            var host = (record.Host ?? string.Empty).Trim();
            var value = (record.Value ?? string.Empty).Trim();
            if (!allowed.Contains(type) || host.Length == 0 || value.Length == 0)
            {
                continue;
            }

            clean.Records.Add(new DnsTemplateRecordDto
            {
                Type = type,
                Host = host,
                Value = value,
                Ttl = record.Ttl > 0 ? record.Ttl : 3600
            });
        }

        await fileStore.WriteAsync(TemplatePath, clean, cancellationToken);
        return clean.Records.Count > 0 ? clean : BuiltInTemplate();
    }

    public async Task<SiteDnsZoneDto> SetDnssecAsync(string siteName, bool enabled, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var state = await GetOrCreateStateAsync(site, cancellationToken);

        // DNSSEC, Windows DNS Server zone-imzalama gerektirir (Plesk-on-Windows ile aynı). Yoksa net hata.
        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            throw new AppException("dns.dnssec_requires_windows", 409, 409);
        }

        // İmzalamak için bölgenin Windows DNS'te bulunması gerekir → önce yayınla (kayıtlarıyla).
        await TrySyncWindowsDnsAsync(state, cancellationToken);

        var zone = EscapePowerShellLiteral(state.ZoneName);
        var script = enabled
            ? $"$ErrorActionPreference='Stop'; Invoke-DnsServerZoneSign -ZoneName '{zone}' -SignWithDefault -Force | Out-Null"
            : $"$ErrorActionPreference='Stop'; Invoke-DnsServerZoneUnsign -ZoneName '{zone}' -Force | Out-Null";
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException(
                enabled ? "dns.dnssec_sign_failed" : "dns.dnssec_unsign_failed",
                500,
                500,
                BuildCommandDetails(result));
        }

        return await ToDtoAsync(state, cancellationToken);
    }

    // Bölgenin DNSSEC durumunu + registrar'a verilecek DS kayıtlarını Windows DNS'ten okur. IsSigned + DS
    // (Export-DnsServerDnsSecPublicKey, SHA-256). DS alınamazsa boş döner (imza durumu yine doğru; UI ipucu verir). Best-effort.
    private async Task<(bool IsSigned, string[] DsRecords)> ReadDnssecAsync(string zoneName, CancellationToken cancellationToken)
    {
        var zone = EscapePowerShellLiteral(zoneName);
        var script = string.Join("\n", new[]
        {
            "$ErrorActionPreference='SilentlyContinue'",
            $"$z = Get-DnsServerZone -Name '{zone}'",
            "if (-not $z) { 'SIGNED=False'; return }",
            "'SIGNED=' + [bool]$z.IsSigned",
            "if ($z.IsSigned) {",
            $"  $keys = Export-DnsServerDnsSecPublicKey -ZoneName '{zone}' -Path $env:TEMP -PassThru -DigestType Sha256 -ErrorAction SilentlyContinue",
            "  foreach ($k in $keys) {",
            "    $rd = $k.RecordData",
            "    if ($rd -and ($rd.PSObject.Properties.Name -contains 'KeyTag') -and ($rd.PSObject.Properties.Name -contains 'Digest')) {",
            "      'DS=' + $k.HostName + ' IN DS ' + $rd.KeyTag + ' ' + [int]$rd.CryptographicAlgorithm + ' ' + [int]$rd.DigestType + ' ' + $rd.Digest",
            "    }",
            "  }",
            "}"
        });

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            return (false, Array.Empty<string>());
        }

        var lines = result.StandardOutput
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var isSigned = lines.Any(line => line.Equals("SIGNED=True", StringComparison.OrdinalIgnoreCase));
        var dsRecords = lines
            .Where(line => line.StartsWith("DS=", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())
            .Where(line => line.Length > 0)
            .Distinct()
            .ToArray();

        return (isSigned, dsRecords);
    }

    // Kayıtlı şablon yoksa UI'de gösterilecek/uygulanacak yerleşik varsayılan (mevcut CreateDefaultRecords ile aynı set, yer tutuculu).
    private static DnsTemplateDto BuiltInTemplate() => new()
    {
        Records =
        [
            new DnsTemplateRecordDto { Type = "NS", Host = "@", Value = "{ns1}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "NS", Host = "@", Value = "{ns2}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "A", Host = "@", Value = "{ip}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "A", Host = "ns1", Value = "{ip}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "A", Host = "ns2", Value = "{ip}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "CNAME", Host = "www", Value = "{domain}", Ttl = 3600 },
            new DnsTemplateRecordDto { Type = "CNAME", Host = "ftp", Value = "{domain}", Ttl = 3600 }
        ]
    };

    // Yeni bölgenin yönetilen (managed) varsayılan kayıtları: kayıtlı şablon varsa onu yer tutucularla genişletir,
    // yoksa yerleşik CreateDefaultRecords'a düşer. Şablon tüm kayıtları atlanırsa da yerleşiğe düşer (boş zone olmaz).
    private async Task<List<DnsRecordState>> BuildDefaultRecordsAsync(SiteDto site, string zoneName, string serverAddress, CancellationToken cancellationToken)
    {
        var template = await fileStore.ReadAsync(TemplatePath, new DnsTemplateDto(), cancellationToken);
        if (template.Records.Count == 0)
        {
            return CreateDefaultRecords(site, zoneName, serverAddress).ToList();
        }

        var ip = ResolveDefaultIpv4Address(site, serverAddress);
        var ns1 = $"ns1.{zoneName}";
        var ns2 = $"ns2.{zoneName}";
        var records = new List<DnsRecordState>();
        foreach (var tpl in template.Records)
        {
            var host = ExpandPlaceholders(tpl.Host, zoneName, ip, ns1, ns2);
            var value = ExpandPlaceholders(tpl.Value, zoneName, ip, ns1, ns2);
            // {ip} çözülemediyse (sunucu IPv4 yok) A/AAAA gibi kayıtlar boş value'lu olur → atla (geçersiz kayıt yazma).
            if (host.Length == 0 || value.Length == 0)
            {
                continue;
            }

            records.Add(new DnsRecordState
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = tpl.Type,
                Host = host,
                Value = value,
                Ttl = tpl.Ttl > 0 ? tpl.Ttl : 3600,
                IsManaged = true
            });
        }

        return records.Count > 0 ? records : CreateDefaultRecords(site, zoneName, serverAddress).ToList();
    }

    private static string ExpandPlaceholders(string input, string zone, string ip, string ns1, string ns2)
        => (input ?? string.Empty)
            .Replace("{domain}", zone, StringComparison.OrdinalIgnoreCase)
            .Replace("{ip}", ip, StringComparison.OrdinalIgnoreCase)
            .Replace("{ns1}", ns1, StringComparison.OrdinalIgnoreCase)
            .Replace("{ns2}", ns2, StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static IEnumerable<DnsRecordState> CreateDefaultRecords(SiteDto site, string zoneName, string preferredServerAddress)
    {
        var serverIp = ResolveDefaultIpv4Address(site, preferredServerAddress);
        yield return CreateManagedRecord("NS", "@", $"ns1.{zoneName}");
        yield return CreateManagedRecord("NS", "@", $"ns2.{zoneName}");
        yield return CreateManagedRecord("CNAME", "www", zoneName);
        yield return CreateManagedRecord("CNAME", "ftp", zoneName);

        if (!string.IsNullOrWhiteSpace(serverIp))
        {
            yield return CreateManagedRecord("A", "@", serverIp);
            yield return CreateManagedRecord("A", "ns1", serverIp);
            yield return CreateManagedRecord("A", "ns2", serverIp);
        }
    }

    private static DnsRecordState CreateManagedRecord(string type, string host, string value)
    {
        return new DnsRecordState
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Host = host,
            Value = value,
            Ttl = 3600,
            IsManaged = true
        };
    }

    private static void MergeMissingDefaults(List<DnsRecordState> records, IEnumerable<DnsRecordState> defaults)
    {
        foreach (var defaultRecord in defaults)
        {
            var exists = records.Any(record =>
                string.Equals(record.Type, defaultRecord.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.Host, defaultRecord.Host, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                records.Add(defaultRecord);
            }
        }
    }

    private static string ResolveDefaultIpv4Address(SiteDto site, string preferredServerAddress)
    {
        var explicitIp = site.Bindings
            .Select(binding => binding.IpAddress)
            .FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value != "*" &&
                IPAddress.TryParse(value, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address));

        if (!string.IsNullOrWhiteSpace(explicitIp))
        {
            return explicitIp;
        }

        if (IPAddress.TryParse(preferredServerAddress, out var preferredAddress) &&
            preferredAddress.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(preferredAddress))
        {
            return preferredAddress.ToString();
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => address.ToString())
            .FirstOrDefault() ?? string.Empty;
    }

    private async Task<string> ResolvePreferredServerAddressAsync(CancellationToken cancellationToken)
    {
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        return preferences.ServerAddress;
    }

    private async Task TrySyncWindowsDnsAsync(DnsZoneState state, CancellationToken cancellationToken)
    {
        if (!await IsWindowsDnsAvailableAsync(cancellationToken))
        {
            return;
        }

        var script = BuildSyncScript(state);
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("dns.sync_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    private static string BuildSyncScript(DnsZoneState state)
    {
        var groups = state.Records
            .GroupBy(record => $"{record.Type}|{record.Host}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Type = group.First().Type.ToUpperInvariant(), Host = group.First().Host })
            .ToArray();

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"$zoneName = '{EscapePowerShellLiteral(state.ZoneName)}'",
            "if (-not (Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue)) { exit 21 }",
            "if (-not (Get-DnsServerZone -Name $zoneName -ErrorAction SilentlyContinue)) {",
            "  Add-DnsServerPrimaryZone -Name $zoneName -ZoneFile \"$zoneName.dns\" | Out-Null",
            "}"
        };

        // panel "zone transfer ACL": izin verilen IP varsa yalnız onlara AXFR + NOTIFY; yoksa transfer KAPALI (güvenli + panel varsayılanı).
        if (state.ZoneTransferIps is { Count: > 0 })
        {
            var ipList = string.Join(",", state.ZoneTransferIps.Select(ip => $"'{EscapePowerShellLiteral(ip)}'"));
            lines.Add($"Set-DnsServerPrimaryZone -Name $zoneName -SecureSecondaries TransferToSecureServers -SecondaryServers {ipList} -Notify NotifyServers -NotifyServers {ipList} | Out-Null");
        }
        else
        {
            lines.Add("Set-DnsServerPrimaryZone -Name $zoneName -SecureSecondaries NoTransfer | Out-Null");
        }

        foreach (var group in groups)
        {
            lines.Add($"Get-DnsServerResourceRecord -ZoneName $zoneName -Name '{EscapePowerShellLiteral(group.Host)}' -RRType '{group.Type}' -ErrorAction SilentlyContinue | Remove-DnsServerResourceRecord -ZoneName $zoneName -Force -ErrorAction SilentlyContinue");
        }

        foreach (var record in SortRecords(state.Records))
        {
            var host = EscapePowerShellLiteral(record.Host);
            var value = EscapePowerShellLiteral(record.Value);
            var ttl = Math.Clamp(record.Ttl, 60, 86400);
            lines.Add(record.Type.ToUpperInvariant() switch
            {
                "A" => $"Add-DnsServerResourceRecordA -ZoneName $zoneName -Name '{host}' -IPv4Address '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "AAAA" => $"Add-DnsServerResourceRecordAAAA -ZoneName $zoneName -Name '{host}' -IPv6Address '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "CNAME" => $"Add-DnsServerResourceRecordCName -ZoneName $zoneName -Name '{host}' -HostNameAlias '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "MX" => $"Add-DnsServerResourceRecordMX -ZoneName $zoneName -Name '{host}' -MailExchange '{value}' -Preference {record.Priority ?? 10} -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "TXT" => $"Add-DnsServerResourceRecord -ZoneName $zoneName -Txt -Name '{host}' -DescriptiveText '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                "NS" => $"Add-DnsServerResourceRecord -ZoneName $zoneName -NS -Name '{host}' -NameServer '{value}' -TimeToLive ([TimeSpan]::FromSeconds({ttl})) | Out-Null",
                _ => string.Empty
            });
        }

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    // İzin verilen AXFR hedef IP'lerini doğrular (IPv4/IPv6), kırpar, tekrarları atar. Geçersiz IP → 400.
    private static List<string> NormalizeZoneTransferIps(string[]? ips)
    {
        var result = new List<string>();
        if (ips is null)
        {
            return result;
        }

        foreach (var raw in ips)
        {
            var ip = raw?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(ip))
            {
                continue;
            }

            if (!System.Net.IPAddress.TryParse(ip, out _))
            {
                throw new AppException("dns.invalid_transfer_ip", 400, 400);
            }

            if (!result.Contains(ip, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(ip);
            }
        }

        return result;
    }

    private static DnsRecordState NormalizeRecord(UpsertDnsRecordRequest request, string zoneName)
    {
        return NormalizeRecord(new DnsRecordState
        {
            Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id.Trim(),
            Type = request.Type,
            Host = request.Host,
            Value = request.Value,
            Ttl = request.Ttl,
            Priority = request.Priority
        }, zoneName);
    }

    private static DnsRecordState NormalizeRecord(DnsRecordState record, string zoneName)
    {
        var type = record.Type.Trim().ToUpperInvariant();
        if (!SupportedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("dns.invalid_record_type", 400, 400);
        }

        var host = NormalizeHost(record.Host, zoneName);
        var value = record.Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppException("dns.invalid_record_value", 400, 400);
        }

        ValidateRecordValue(type, value, record.Priority);
        return new DnsRecordState
        {
            Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
            Type = type,
            Host = host,
            Value = NormalizeRecordValue(type, value),
            Ttl = Math.Clamp(record.Ttl <= 0 ? 3600 : record.Ttl, 60, 86400),
            Priority = type == "MX" ? Math.Clamp(record.Priority ?? 10, 0, 65535) : null,
            IsManaged = record.IsManaged
        };
    }

    private static void ValidateRecordValue(string type, string value, int? priority)
    {
        switch (type)
        {
            case "A" when !IsAddressFamily(value, System.Net.Sockets.AddressFamily.InterNetwork):
                throw new AppException("dns.invalid_record_value", 400, 400);
            case "AAAA" when !IsAddressFamily(value, System.Net.Sockets.AddressFamily.InterNetworkV6):
                throw new AppException("dns.invalid_record_value", 400, 400);
            case "CNAME":
            case "NS":
                _ = NormalizeDomainValue(value);
                break;
            case "MX":
                _ = NormalizeDomainValue(value);
                if (priority is < 0 or > 65535)
                {
                    throw new AppException("dns.invalid_priority", 400, 400);
                }
                break;
            case "TXT" when value.Length > 512:
                throw new AppException("dns.invalid_record_value", 400, 400);
        }
    }

    private static bool IsAddressFamily(string value, System.Net.Sockets.AddressFamily family)
    {
        return IPAddress.TryParse(value, out var address) && address.AddressFamily == family;
    }

    private static string NormalizeRecordValue(string type, string value)
    {
        return type is "CNAME" or "MX" or "NS"
            ? NormalizeDomainValue(value)
            : value.Trim();
    }

    private static string ResolveDefaultZoneName(SiteDto site)
    {
        var hostName = site.Bindings
            .Select(binding => binding.HostName)
            .FirstOrDefault(host => !string.IsNullOrWhiteSpace(host) && host.Contains('.'));

        if (!string.IsNullOrWhiteSpace(hostName))
        {
            var normalizedHost = NormalizeDomainValue(hostName);
            return normalizedHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? normalizedHost[4..]
                : normalizedHost;
        }

        return NormalizeZoneName(site.Name.Contains('.') ? site.Name : $"{site.Name}.local");
    }

    private static string NormalizeZoneName(string value)
    {
        var normalized = NormalizeDomainValue(value);
        if (!normalized.Contains('.'))
        {
            throw new AppException("dns.invalid_zone", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeDomainValue(string value)
    {
        var normalized = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (!DomainPattern().IsMatch(normalized))
        {
            throw new AppException("dns.invalid_zone", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeHost(string value, string zoneName)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "@" : value.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.Equals(normalized, zoneName, StringComparison.OrdinalIgnoreCase))
        {
            return "@";
        }

        if (normalized.EndsWith($".{zoneName}", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^(zoneName.Length + 1)];
        }

        if (normalized != "@" && !HostPattern().IsMatch(normalized))
        {
            throw new AppException("dns.invalid_host", 400, 400);
        }

        return normalized;
    }

    private static string[] NormalizeNameServers(string[] nameServers, string zoneName)
    {
        var normalized = nameServers
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Select(NormalizeDomainValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length > 0
            ? normalized
            : [$"ns1.{zoneName}", $"ns2.{zoneName}"];
    }

    private static IEnumerable<DnsRecordState> SortRecords(IEnumerable<DnsRecordState> records)
    {
        return records
            .OrderBy(record => Array.IndexOf(SupportedTypes, record.Type.ToUpperInvariant()))
            .ThenBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsWindowsDnsAvailableAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            "if (Get-Command Get-DnsServerZone -ErrorAction SilentlyContinue) { 'true' } else { 'false' }",
            cancellationToken);

        return result.Success && result.StandardOutput.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsWindowsZonePresentAsync(string zoneName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            $"if (Get-DnsServerZone -Name '{EscapePowerShellLiteral(zoneName)}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}",
            cancellationToken);

        return result.Success && result.StandardOutput.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''");
    }

    private static string BuildCommandDetails(CommandResult result)
    {
        var details = $"""
            ExitCode={result.ExitCode}
            StandardOutput:
            {result.StandardOutput}
            StandardError:
            {result.StandardError}
            """;

        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private sealed class DnsZoneState
    {
        public string SiteName { get; set; } = string.Empty;

        public string ZoneName { get; set; } = string.Empty;

        public string[] NameServers { get; set; } = [];

        public List<DnsRecordState> Records { get; set; } = [];

        public List<string> ZoneTransferIps { get; set; } = [];

        public int DefaultsVersion { get; set; }
    }

    private sealed class DnsRecordState
    {
        public string Id { get; set; } = string.Empty;

        public string Type { get; set; } = "A";

        public string Host { get; set; } = "@";

        public string Value { get; set; } = string.Empty;

        public int Ttl { get; set; } = 3600;

        public int? Priority { get; set; }

        public bool IsManaged { get; set; }
    }

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.Compiled)]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^(?:@|\*|[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*)$", RegexOptions.Compiled)]
    private static partial Regex HostPattern();
}
