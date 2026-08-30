using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Firewall;

/// <summary>
/// Windows Defender Firewall kurallarını PowerShell üzerinden yönetir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden günlükçü (ILogger) var:</b> bu serviste ölçüm başarısızlığını "sorun yok" gibi gösteren
/// iki yutan <c>catch</c> bloğu var ve ikisi de sunucunun ağ maruziyeti hakkında YANLIŞ bir tablo
/// çizebilir: TCP dinleyici tablosu okunamazsa açık portlar "dinlenmiyor", kural okuması patlarsa
/// açık portlar "kapalı" görünür. Kullanıcıya doğru olan davranış zaten seçilmiş (ÖLÇEMEDİK
/// durumunu güvenli tarafa yuvarlamak), ama sebep hiçbir yere yazılmıyordu — yani panel yanlış
/// bilgi gösterirken sunucuda bunun izi bile yoktu. Günlükçü, o iki noktada NEDENİ kaydeder.
/// </para>
/// <para>
/// Kural değişikliklerinin DENETİM kaydı burada değil <c>FirewallController</c>'da yazılır: kim/
/// nereden bilgisi HTTP bağlamındadır ve bu servis arka plan yollarından da (panel erişimi kurulumu,
/// alan adı onarımı) çağrılır.
/// </para>
/// </remarks>
public sealed class FirewallService(ILogger<FirewallService> logger) : IFirewallService
{
    public async Task<IReadOnlyCollection<FirewallRuleDto>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        var script = """
            $rules = Get-NetFirewallRule -DisplayName 'AdaPanel *' -ErrorAction SilentlyContinue | ForEach-Object {
              $port = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $_ -ErrorAction SilentlyContinue | Select-Object -First 1;
              [pscustomobject]@{
                DisplayName = $_.DisplayName
                Direction = $_.Direction.ToString()
                Action = $_.Action.ToString()
                Protocol = if ($port) { $port.Protocol } else { '' }
                LocalPort = if ($port) { [string]$port.LocalPort } else { '' }
                Enabled = [bool]$_.Enabled
              }
            }
            $rules | ConvertTo-Json -Depth 4
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("firewall.read_failed", 500, 500);
        }

        return DeserializeArray<FirewallRuleDto>(result.StandardOutput);
    }

    public async Task<IReadOnlyCollection<PortStatusDto>> GetPortStatusAsync(int panelPort, CancellationToken cancellationToken = default)
    {
        var catalog = BuildCatalog(panelPort);

        // 1) Dinleme: tek TCP listener tablosu taraması (servisleri rahatsız etmez, her portu ayrı ayrı yoklamaktan iyidir).
        IPEndPoint[] listeners;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (Exception exception)
        {
            // ÖLÇEMEDİK ≠ SORUN YOK: boş liste, panelde her portun "dinlenmiyor" görünmesine yol
            // açar. Davranış bilerek böyle (uydurma bir "açık" listesi göstermektense boş göstermek),
            // ama sebebi günlüğe düşmezse yönetici panelin yanlış bilgi verdiğini hiç öğrenemez.
            logger.LogWarning(
                exception,
                "TCP dinleyici tablosu okunamadi; port durumlari 'dinlenmiyor' olarak gosterilecek.");
            listeners = [];
        }

        // 2) Firewall kapsamı: etkin inbound Allow (TCP/Any) kurallarının kapsadığı portlar.
        var (coversAllTcp, ranges) = await ReadAllowCoverageAsync(cancellationToken);

        var result = new List<PortStatusDto>(catalog.Count);
        foreach (var (port, rangeEnd, key, category, required) in catalog)
        {
            var isRange = rangeEnd > port;
            var matches = isRange
                ? listeners.Where(endpoint => endpoint.Port >= port && endpoint.Port <= rangeEnd).ToArray()
                : listeners.Where(endpoint => endpoint.Port == port).ToArray();
            var listening = matches.Length > 0;
            var scope = listening
                ? (matches.Any(endpoint => !IPAddress.IsLoopback(endpoint.Address)) ? "all" : "loopback")
                : string.Empty;

            result.Add(new PortStatusDto
            {
                Port = port,
                RangeEnd = isRange ? rangeEnd : 0,
                Protocol = "TCP",
                ServiceKey = key,
                Category = category,
                Required = required,
                Listening = listening,
                ListenScope = scope,
                FirewallAllowed = IsPortCovered(port, coversAllTcp, ranges)
            });
        }

        return result;
    }

    // Panelin bildiği hizmet portları. Panel portu HttpContext'ten gelir (gerçek). Port'a göre tekilleştirilir
    // (panel 443'teyse https satırıyla çakışmasın; panel kazanır).
    private static List<(int Port, int RangeEnd, string Key, string Category, bool Required)> BuildCatalog(int panelPort)
    {
        // RangeEnd = 0 -> tek port. FTP pasif (50000-51000) FtpService'teki sabitlerle eşleşir.
        var entries = new List<(int Port, int RangeEnd, string Key, string Category, bool Required)>
        {
            (panelPort > 0 ? panelPort : 8890, 0, "panel", "panel", true),
            (80, 0, "http", "web", true),
            (443, 0, "https", "web", true),
            (21, 0, "ftp", "ftp", false),
            (50000, 51000, "ftpPassive", "ftp", false),
            (25, 0, "smtp", "mail", true),
            (587, 0, "submission", "mail", true),
            (465, 0, "smtps", "mail", false),
            (143, 0, "imap", "mail", true),
            (993, 0, "imaps", "mail", false),
            (995, 0, "pop3s", "mail", false),
            (8896, 0, "phpmyadmin", "tools", false),
            (8897, 0, "adminer", "tools", false),
            (8899, 0, "webmail", "mail", false)
        };

        var seen = new HashSet<int>();
        var unique = new List<(int Port, int RangeEnd, string Key, string Category, bool Required)>();
        foreach (var entry in entries)
        {
            if (seen.Add(entry.Port))
            {
                unique.Add(entry);
            }
        }

        return unique;
    }

    // Etkin inbound Allow (TCP veya Any protokol) kurallarını okuyup port kapsamını çıkarır.
    // "Any" port -> tüm TCP kapsanır; "5000-5100" -> aralık; "587" -> tek port. AdaPanel kuralları DAHİL
    // tüm kurallara bakar (IIS'in 80/443 kuralları da sayılır). Hata olursa kapsam boş döner (engelli görünür).
    private async Task<(bool CoversAllTcp, List<(int Lo, int Hi)>? Ranges)> ReadAllowCoverageAsync(CancellationToken cancellationToken)
    {
        var script = """
            # HIZ: kural basina Get-NetFirewallPortFilter cagirmak COM uzerinden N ayri sorgu demek
            # ve yuzlerce kurali olan bir sunucuda DAKIKALAR surer. Sure asiminda sonuc bos doner ve
            # panel butun portlari "kapali" sanardi. Iki TOPLU sorgu + bellekte birlestirme yapilir.
            $kurallar = @(Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True -ErrorAction SilentlyContinue)
            $sizintilar = @{}
            foreach ($pf in @(Get-NetFirewallPortFilter -ErrorAction SilentlyContinue)) {
              $sizintilar[[string]$pf.InstanceID] = $pf
            }
            $cikti = foreach ($kural in $kurallar) {
              $pf = $sizintilar[[string]$kural.InstanceID]
              if ($pf) {
                [pscustomobject]@{
                  Protocol = [string]$pf.Protocol
                  LocalPort = ($pf.LocalPort -join ',')
                }
              }
            }
            [Console]::Out.WriteLine('ADAPANEL-KURAL=' + @($kurallar).Count)
            $cikti | ConvertTo-Json -Depth 3
            """;

        try
        {
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);

            // OLCEMEDIK ile "kapali" AYRI seylerdir.
            //
            // Eski davranis: sorgu basarisiz olursa kapsam bos donuyordu ve panel butun portlari
            // KAPALI sayiyordu. Canlida tam olarak bu oldu -- 80, 443, 143, 587 disaridan
            // ERISILEBILIR oldugu halde panel hepsi icin "guvenlik duvarinda kapali" uyarisi
            // verdi. Olcememeyi sorun saymak, kullaniciyi olmayan bir sorunu kovalamaya iter ve
            // gercek uyarilara olan guveni yok eder.
            if (!result.Success || !result.StandardOutput.Contains("ADAPANEL-KURAL=", StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Guvenlik duvari izin kurallari OKUNAMADI (cikis kodu {ExitCode}); port kapsami 'olcemedik' sayiliyor. Hata: {Error}",
                    result.ExitCode,
                    result.StandardError);
                return (false, null);
            }

            var rows = DeserializeArray<FirewallPortFilterRow>(result.StandardOutput);
            var (coversAll, ranges) = ParseAllowCoverage(rows.Select(row => (row.Protocol, row.LocalPort)));

            // Hic kural okunamadiysa da olcum SAYILMAZ: bir Windows sunucusunda etkin gelen
            // izin kuralinin sifir olmasi gercekci degildir, okuma basarisiz olmustur.
            if (rows.Length == 0)
            {
                logger.LogWarning(
                    "Guvenlik duvarinda hic etkin gelen izin kurali cozulemedi; bu gercekci olmadigi icin olcum yapilmamis sayiliyor.");
                return (false, null);
            }

            return (coversAll, ranges);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Guvenlik duvari kapsami cozulemedi; port durumlari 'olcemedik' olarak dondurulecek.");
            return (false, null);
        }
    }

    // Saf parse (test edilebilir): PS'ten gelen (protokol, localPort) satırlarından TCP port kapsamını çıkarır.
    internal static (bool CoversAllTcp, List<(int Lo, int Hi)> Ranges) ParseAllowCoverage(IEnumerable<(string? Protocol, string? LocalPort)> rows)
    {
        var ranges = new List<(int Lo, int Hi)>();
        var coversAll = false;
        foreach (var row in rows)
        {
            var protocol = (row.Protocol ?? string.Empty).Trim();
            var isTcp = protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                        protocol.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
                        protocol == "6";
            if (!isTcp)
            {
                continue;
            }

            foreach (var token in (row.LocalPort ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Equals("Any", StringComparison.OrdinalIgnoreCase))
                {
                    coversAll = true;
                }
                else if (token.Contains('-'))
                {
                    var parts = token.Split('-', 2);
                    if (int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi) && lo <= hi)
                    {
                        ranges.Add((lo, hi));
                    }
                }
                else if (int.TryParse(token, out var single))
                {
                    ranges.Add((single, single));
                }
                // RPC / RPCEPMap / IPHTTPS gibi sayısal olmayan jetonlar yok sayılır.
            }
        }

        return (coversAll, ranges);
    }

    // Bir portun firewall kapsamında olup olmadığı (coversAll ise ya da bir aralığa düşüyorsa).
    /// <summary>
    /// Port guvenlik duvarinda ACIK mi?
    /// <para>
    /// <paramref name="ranges"/> null ise OLCUM YAPILAMAMISTIR ve "kapali" DENMEZ. Eski davranis
    /// bunun tersiydi: sorgu basarisiz olunca kapsam bos donuyor, panel butun portlari kapali
    /// sayiyordu. Canlida 80, 443, 143 ve 587 disaridan ERISILEBILIR oldugu halde panel hepsi
    /// icin uyari verdi. Olcememeyi sorun saymak, kullaniciyi olmayan bir sorunu kovalamaya iter
    /// ve gercek uyarilara olan guveni yok eder.
    /// </para>
    /// </summary>
    internal static bool IsPortCovered(int port, bool coversAllTcp, IReadOnlyList<(int Lo, int Hi)>? ranges)
    {
        if (coversAllTcp || ranges is null)
        {
            return true;
        }

        foreach (var range in ranges)
        {
            if (port >= range.Lo && port <= range.Hi)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FirewallPortFilterRow
    {
        public string? Protocol { get; set; }

        public string? LocalPort { get; set; }
    }

    public Task<FirewallRuleDto> AllowPortAsync(UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default)
    {
        return UpsertRuleAsync(request, "Allow", cancellationToken);
    }

    /// <summary>Uzak masaüstü — engellenirse sunucuya uzaktan girişin son yolu da kapanır.</summary>
    private const int RemoteDesktopPort = 3389;

    public Task<FirewallRuleDto> BlockPortAsync(UpdateFirewallRuleRequest request, int panelPort, CancellationToken cancellationToken = default)
    {
        EnsurePortIsNotSelfLocking(request.Port, panelPort);
        return UpsertRuleAsync(request, "Block", cancellationToken);
    }

    /// <summary>
    /// Panelin KENDİ kapısını kilitlemesini engeller.
    /// <para>
    /// Windows'ta engelleme kuralı izin kuralını EZER. Panel portu engellenirse panel dışarıdan
    /// erişilemez olur ve "İzin ver"e basmak da işe yaramaz — çünkü panele artık ulaşılamıyordur.
    /// Panelin kural silme yolu da yoktu, yani işlem geri alınamıyordu: tek çıkış sunucu konsolu.
    /// Aynı gerekçe uzak masaüstü için de geçerli; 80/443 ise engellenirse barındırılan bütün
    /// siteler birden düşer ve bu neredeyse hiçbir zaman istenen şey değildir.
    /// </para>
    /// </summary>
    private static void EnsurePortIsNotSelfLocking(int port, int panelPort)
    {
        var korunan = panelPort > 0 && port == panelPort;

        if (korunan || port == RemoteDesktopPort || port == 80 || port == 443)
        {
            throw new AppException("firewall.protected_port", 409, 409);
        }
    }

    public async Task<int> ClearManagedBlockRulesAsync(CancellationToken cancellationToken = default)
    {
        var script = """
            $kurallar = @(Get-NetFirewallRule -DisplayName "AdaPanel Block*" -ErrorAction SilentlyContinue)
            if ($kurallar.Count -gt 0) { $kurallar | Remove-NetFirewallRule -ErrorAction SilentlyContinue }
            [Console]::Out.WriteLine('ADAPANEL-KALDIRILAN=' + $kurallar.Count)
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        var eslesme = Regex.Match(result.StandardOutput ?? string.Empty, @"ADAPANEL-KALDIRILAN=(\d+)");
        return eslesme.Success ? int.Parse(eslesme.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    public async Task RemoveRuleAsync(string ruleName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            throw new AppException("firewall.invalid_rule_name", 400, 400);
        }

        // YALNIZCA panelin kendi kurallarını siler: yöneticinin ya da Windows'un kurduğu kuralları
        // panelden silmek, panelin sorumluluğu dışında bir sistemi sessizce değiştirmek olurdu.
        var temiz = ruleName.Trim();
        if (!temiz.StartsWith("AdaPanel ", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("firewall.rule_not_managed", 409, 409);
        }

        var result = await ProcessRunner.RunPowerShellAsync(BuildRemoveScript(temiz), cancellationToken);
        if (!result.Success)
        {
            throw new AppException("firewall.write_failed", 500, 500, result.StandardError);
        }
    }

    /// <summary>
    /// Kaldırma betiği. Ada BURADA beyaz liste UYGULANMAZ, bilerek: bu doğrulama panelde yokken
    /// kurulmuş kurallar (Türkçe harfli ya da eski kodun kabul ettiği garip adlar) sunucuda
    /// duruyor olabilir ve yönetici onları panelden temizleyebilmelidir. Dar kuralı buraya da
    /// uygulamak, bir güvenlik düzeltmesini "var olan çöpü silememe" hâline getirirdi.
    /// Koruma kaçıştan gelir: ad TEK TIRNAKLI dizeye giriyor, orada genişleme yoktur.
    /// </summary>
    internal static string BuildRemoveScript(string ruleName) =>
        $"Remove-NetFirewallRule -DisplayName '{TekTirnak(ruleName)}' -ErrorAction Stop | Out-Null";

    private static async Task<FirewallRuleDto> UpsertRuleAsync(
        UpdateFirewallRuleRequest request,
        string action,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var ruleName = string.IsNullOrWhiteSpace(request.RuleName)
            ? $"AdaPanel {action} {request.Protocol.ToUpperInvariant()} {request.Port}"
            : request.RuleName.Trim();

        var result = await ProcessRunner.RunPowerShellAsync(
            BuildUpsertScript(ruleName, action, request.Protocol.ToUpperInvariant(), request.Port),
            cancellationToken);
        if (!result.Success)
        {
            throw new AppException("firewall.write_failed", 500, 500);
        }

        return new FirewallRuleDto
        {
            DisplayName = ruleName,
            Direction = "Inbound",
            Action = action,
            Protocol = request.Protocol.ToUpperInvariant(),
            LocalPort = request.Port.ToString(),
            Enabled = true
        };
    }

    /// <summary>
    /// Kurma/güncelleme betiği. Ad TEK TIRNAKLI dizeye girer.
    /// <para>
    /// ÖNCEDEN ÇİFT TIRNAKLIYDI ve tek kaçış <c>"</c> karakterini <c>`"</c> yapmaktı. PowerShell'de
    /// çift tırnaklı dize <c>$(...)</c> alt-ifadesini ÇALIŞTIRIR; yani <c>RuleName</c> alanına
    /// yazılan <c>AdaPanel $(net localgroup administrators hax /add)</c> panelin kendi yetkisiyle
    /// (LocalSystem) koşuyordu. Aynı sınıf bu depoda cron görev adında ve
    /// <see cref="Support.LimitedIdentityRunner"/>'da zaten bir kez ölçülüp kapatılmıştı; güvenlik
    /// duvarı yolu o düzeltmeden PAYINI ALMAMIŞTI.
    /// </para>
    /// </summary>
    internal static string BuildUpsertScript(string ruleName, string action, string protocol, int port) =>
        $"""
        Remove-NetFirewallRule -DisplayName '{TekTirnak(ruleName)}' -ErrorAction SilentlyContinue | Out-Null
        New-NetFirewallRule -DisplayName '{TekTirnak(ruleName)}' -Direction Inbound -Action {action} -Protocol {protocol} -LocalPort {port} -Profile Any | Out-Null
        """;

    private static void ValidateRequest(UpdateFirewallRuleRequest request)
    {
        if (request.Port < 1 || request.Port > 65535)
        {
            throw new AppException("firewall.invalid_port", 400, 400);
        }

        if (!string.Equals(request.Protocol, "TCP", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Protocol, "UDP", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("firewall.invalid_protocol", 400, 400);
        }

        // İKİNCİ KATMAN. Kaçış tek başına yeterli olmalı ama tek savunma bırakılmıyor: ileride biri
        // yine çift tırnaklı bir betik yazarsa açık kendiliğinden geri gelmesin. Küme kasten dar —
        // panelin kendi ürettiği ad (`AdaPanel Allow TCP 8890`) ve makul yönetici adları geçer;
        // kabuk için anlamlı hiçbir karakter geçmez. BOŞ ad serbest: o durumda panel adı kendisi
        // üretir (bkz. UpsertRuleAsync).
        if (!string.IsNullOrWhiteSpace(request.RuleName) && !KuralAdiGuvenli(request.RuleName.Trim()))
        {
            throw new AppException("firewall.invalid_rule_name", 400, 400);
        }
    }

    /// <summary>
    /// Yeni kural adı beyaz listesi. Unicode harf/rakam SERBEST (Türkçe adlı kurallar meşrudur);
    /// yalnız harf/rakam dışında boşluk, nokta, alt çizgi ve tireye izin verilir. <c>$ ` ' " ( ) ;
    /// | &amp;</c>, satır sonu ve denetim karakterleri kümede YOKTUR.
    /// </summary>
    internal static bool KuralAdiGuvenli(string value)
    {
        if (value.Length is 0 or > 120)
        {
            return false;
        }

        foreach (var karakter in value)
        {
            if (!char.IsLetterOrDigit(karakter) && karakter is not (' ' or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static T[] DeserializeArray<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.Deserialize<T[]>() ?? [],
            JsonValueKind.Object => [document.RootElement.Deserialize<T>()!],
            _ => []
        };
    }

    /// <summary>
    /// Bu dosyadaki TEK kaçış, ve öyle kalmalı. PowerShell TEK TIRNAKLI dizesinde genişleme ve
    /// alt-ifade YOKTUR; tek tırnağı ikilemek dizeden çıkmayı da kapatır. Eskiden burada bir de
    /// <c>EscapePowerShellString</c> vardı (yalnız <c>"</c> kaçırırdı) ve çıktısı ÇİFT TIRNAKLI
    /// dizelere giriyordu — bkz. <see cref="BuildUpsertScript"/>.
    /// </summary>
    private static string TekTirnak(string value) => (value ?? string.Empty).Replace("'", "''");
}
