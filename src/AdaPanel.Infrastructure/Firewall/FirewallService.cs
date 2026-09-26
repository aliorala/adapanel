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
    /// <summary>Okunabilirlik damgasi: bu satir cikmadiysa liste bir OLCUM DEGILDIR.</summary>
    private const string OkunduDamgasi = "ADAPANEL-DUVAR=OKUNDU";

    /// <summary>
    /// Panelin kendi kurallarini guvenlik duvarindan OKUR. "Kural yok" ile "okuyamadim" AYRIDIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>OLCULDU (bu makinede, 04.09.2026):</b> yukseltilmemis bir surecte
    /// <c>Get-NetFirewallRule</c> "Erisim engellendi" ile duser; ama varsayilan
    /// <c>-ErrorAction SilentlyContinue</c> ile hicbir sey yazmaz, <c>powershell.exe</c> <b>0</b>
    /// ile cikar ve sonuc BOS LISTE olur. Yani "kural okunamadi" ile "hic kural yok" birbirinden
    /// ayirt edilemiyordu. Bu, bu turda avladigimiz sinifin ta kendisi.
    /// </para>
    /// <para>
    /// Bu yuzden betik once OKUNABILIRLIK KAPISINDAN gecer (bir kural okumayi <c>-ErrorAction
    /// Stop</c> ile dener) ve ancak gectikten sonra damgayi basar. Damga yoksa cagiran, listeyi
    /// bir olcum saymaz.
    /// </para>
    /// </remarks>
    private async Task<(bool Measured, FirewallRuleDto[] Rules)> ReadManagedRulesAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(BuildReadRulesScript(), cancellationToken);
        if (!result.Success || !result.StandardOutput.Contains(OkunduDamgasi, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Guvenlik duvari kurallari OKUNAMADI (cikis kodu {ExitCode}). Hata: {Error}",
                result.ExitCode,
                result.StandardError);
            return (false, []);
        }

        return (true, DeserializeArray<FirewallRuleDto>(StripOkunduDamgasi(result.StandardOutput)));
    }

    /// <summary>
    /// Kural okuma betigi. Damga ile C# sabiti AYNI yerden gelir; ikisi ayrisirsa her okuma
    /// sessizce "olculemedi" olurdu.
    /// </summary>
    internal static string BuildReadRulesScript() =>
        """
        $okunabilir = $true
        try { $null = @(Get-NetFirewallRule -ErrorAction Stop | Select-Object -First 1) } catch { $okunabilir = $false }
        if ($okunabilir) {
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
          [Console]::Out.WriteLine('__DAMGA__')
          $rules | ConvertTo-Json -Depth 4
        }
        """.Replace("__DAMGA__", OkunduDamgasi, StringComparison.Ordinal);

    /// <summary>Damga satirini JSON'dan ayirir (JSON ayristiricisi onu yutmaz).</summary>
    internal static string StripOkunduDamgasi(string output) =>
        string.Join(
            "\n",
            (output ?? string.Empty)
                .Split('\n')
                .Where(line => !line.Contains(OkunduDamgasi, StringComparison.Ordinal)));

    public async Task<IReadOnlyCollection<FirewallRuleDto>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        // OKUNAMADI ILE "HIC KURAL YOK" AYRI: ikincisi panelde bos bir liste demektir ve yonetici
        // kurdugu kurallarin silindigini sanir. Okunamiyorsa liste UYDURULMAZ, hata soylenir.
        var (measured, rules) = await ReadManagedRulesAsync(cancellationToken);
        return measured ? rules : throw new AppException("firewall.read_failed", 500, 500);
    }

    public async Task<IReadOnlyCollection<PortStatusDto>> GetPortStatusAsync(int panelPort, CancellationToken cancellationToken = default)
    {
        var catalog = BuildCatalog(panelPort);

        // 1) Dinleme: tek TCP listener tablosu taraması (servisleri rahatsız etmez, her portu ayrı ayrı yoklamaktan iyidir).
        IPEndPoint[] listeners;
        var listeningMeasured = true;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (Exception exception)
        {
            // Preserve the unavailable measurement separately from a measured empty table.
            logger.LogWarning(
                exception,
                "TCP dinleyici tablosu okunamadi; port durumlari 'olculemedi' olarak gosterilecek.");
            listeners = [];
            listeningMeasured = false;
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
                ListeningMeasured = listeningMeasured,
                ListenScope = scope,
                FirewallMeasured = ranges is not null,
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
        try
        {
            return await WindowsFirewallInspection.ReadAllowCoverageAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Native firewall inspection failed; using PowerShell fallback.");
        }

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

            var (coversAll, ranges) = ParseAllowCoverageOutput(result.StandardOutput);

            // Hic kural okunamadiysa da olcum SAYILMAZ: bir Windows sunucusunda etkin gelen
            // izin kuralinin sifir olmasi gercekci degildir, okuma basarisiz olmustur.
            if (ranges is null)
            {
                logger.LogWarning(
                    "Guvenlik duvarinda hic etkin gelen izin kurali cozulemedi; bu gercekci olmadigi icin olcum yapilmamis sayiliyor.");
                return (false, null);
            }

            return (coversAll, ranges);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Guvenlik duvari kapsami cozulemedi; port durumlari 'olcemedik' olarak dondurulecek.");
            return (false, null);
        }
    }

    // Okuma damgası JSON değildir. Damga ayrı doğrulanır; yalnız takip eden veri ayrıştırılır.
    internal static (bool CoversAllTcp, List<(int Lo, int Hi)>? Ranges) ParseAllowCoverageOutput(string output)
    {
        const string prefix = "ADAPANEL-KURAL=";
        var text = output.TrimStart();
        var newline = text.IndexOf('\n');
        if (newline < 0 || !text.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(text[prefix.Length..newline].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            return (false, null);
        }

        var rows = DeserializeArray<FirewallPortFilterRow>(text[(newline + 1)..]);
        return rows.Length == 0 ? (false, null) : ParseAllowCoverage(rows.Select(row => (row.Protocol, row.LocalPort)));
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

    /// <summary>
    /// Panelin kendi ENGELLEME kurallarini kaldirir ve GERCEKTEN kaldirilanlari geri okur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ONCEDEN BULUNAN kural sayisi KALDIRILAN diye yaziliyordu.</b> Betik
    /// <c>Remove-NetFirewallRule -ErrorAction SilentlyContinue</c> ile siliyor, sonra
    /// <c>$kurallar.Count</c> — yani SILMEDEN ONCE bulunan sayiyi — basiyordu. Kaldirma
    /// basarisiz olsa (GPO ile dayatilmis kural, yetki hatasi) sayi yine ayni cikardi. Okuma
    /// bastan basarisiz olduysa sayi 0 cikar ve bu da "temizlenecek kural yoktu" diye okunurdu.
    /// </para>
    /// <para>
    /// <b>Bu, panelin ERISIM KURTARMA kolu.</b> Yonetici panele ulasamadigi icin sunucu
    /// konsolundan bayrak dosyasi birakir; bu yol calisir ve gunluge "N kural kaldirildi"
    /// yazar. O cumle yanlissa yonetici guvenlik duvarini eleyip baska yerde arar -- oysa
    /// erisimi kapatan kural hala yerindedir. Bir kurtarma kolunun yalan soylemesi, en pahali
    /// yalandir.
    /// </para>
    /// </remarks>
    public async Task<FirewallBlockCleanupReport> ClearManagedBlockRulesAsync(CancellationToken cancellationToken = default)
    {
        var oncesi = await ReadManagedBlockRuleNamesAsync(cancellationToken);
        if (!oncesi.Measured)
        {
            return FirewallBlockCleanupReport.Olculemedi;
        }

        if (oncesi.Names.Count == 0)
        {
            return FirewallBlockCleanupReport.Bos;
        }

        var script = """
            $kurallar = @(Get-NetFirewallRule -DisplayName "AdaPanel Block*" -ErrorAction SilentlyContinue)
            if ($kurallar.Count -gt 0) { $kurallar | Remove-NetFirewallRule -ErrorAction SilentlyContinue }
            [Console]::Out.WriteLine('ADAPANEL-KALDIRMA-DENENDI=' + $kurallar.Count)
            """;

        await ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        // GERI OKU. Kaldirma cagrisinin cikis kodu degil, kuralin SUNUCUDAKI HALI olculur.
        var sonrasi = await ReadManagedBlockRuleNamesAsync(cancellationToken);
        if (!sonrasi.Measured)
        {
            return FirewallBlockCleanupReport.Olculemedi;
        }

        var kalan = oncesi.Names
            .Where(name => sonrasi.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var kaldirilan = oncesi.Names
            .Where(name => !sonrasi.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FirewallBlockCleanupReport(true, kaldirilan, kalan);
    }

    /// <summary>Panelin ENGELLEME kurallarinin adlari; okunamadiysa isaretli doner.</summary>
    private async Task<(bool Measured, IReadOnlyCollection<string> Names)> ReadManagedBlockRuleNamesAsync(
        CancellationToken cancellationToken)
    {
        var (measured, rules) = await ReadManagedRulesAsync(cancellationToken);
        if (!measured)
        {
            return (false, []);
        }

        // Ad kalibi kaldirma betigininkiyle AYNI olmali ("AdaPanel Block*"), yoksa geri okuma
        // baska bir kumeyi olcer ve rapor sessizce yanlis cikar.
        return (
            true,
            rules
                .Select(rule => rule.DisplayName ?? string.Empty)
                .Where(name => name.StartsWith("AdaPanel Block", StringComparison.OrdinalIgnoreCase))
                .ToArray());
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

    // ARTIK STATIK DEGIL: yazdiktan sonra kurali GERI OKUR (ReadManagedRulesAsync).
    private async Task<FirewallRuleDto> UpsertRuleAsync(
        UpdateFirewallRuleRequest request,
        string action,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var ruleName = string.IsNullOrWhiteSpace(request.RuleName)
            ? $"AdaPanel {action} {request.Protocol.ToUpperInvariant()} {request.Port}"
            : request.RuleName.Trim();

        // KATI KOSUCU. OLCULDU (bu makinede, 04.09.2026): cok ifadeli bir script'te
        // `New-NetFirewallRule` sonlandirici OLMAYAN hata verdiginde script akmaya devam eder ve
        // powershell.exe 0 ile cikar -- olcum: gecersiz -Action ile calistirildiginda cmdlet
        // hata yazdi, sonraki satir kostu, cikis kodu 0 oldu. Yani `result.Success` "kural
        // kuruldu" DEMIYORDU; panel kural HIC OLUSMADIGI halde "eklendi" diyordu.
        var result = await ProcessRunner.RunPowerShellStrictAsync(
            BuildUpsertScript(ruleName, action, request.Protocol.ToUpperInvariant(), request.Port),
            cancellationToken);
        if (!result.Success)
        {
            throw new AppException("firewall.write_failed", 500, 500, result.StandardError);
        }

        // GERI OKU -- CIKIS KODUNU DEGIL, KURALIN KENDISINI.
        //
        // Kati kosucu "arac sikayet etmedi" der, "kural yururlukte" demez. Onceden bu metot
        // ISTENEN degerleri geri veriyor ve `Enabled = true` UYDURUYORDU; denetim kaydi da
        // ("enabled {rule.Enabled}") o uydurmadan besleniyordu. Bir yonetici RDP'yi engellemek
        // icin dugmeye basip "engellendi" yazisini gorup sunucuyu acik birakabilirdi.
        var (measured, rules) = await ReadManagedRulesAsync(cancellationToken);
        var applied = rules.FirstOrDefault(rule =>
            string.Equals(rule.DisplayName, ruleName, StringComparison.OrdinalIgnoreCase));

        if (measured && applied is null)
        {
            // Yazma "basarili" dondu ama kural ORTADA YOK. Sessizce basari saymak, tam olarak
            // kapatmaya calistigimiz yalandir.
            throw new AppException("firewall.rule_not_applied", 500, 500, ruleName);
        }

        return applied ?? new FirewallRuleDto
        {
            DisplayName = ruleName,
            Direction = "Inbound",
            Action = action,
            Protocol = request.Protocol.ToUpperInvariant(),
            LocalPort = request.Port.ToString(),

            // OLCULEMEDI. `Enabled` uydurulmaz: kural okunamadigi icin etkin oldugunu
            // BILMIYORUZ ve "true" demek bilmedigimizi soylemek olurdu. Uc degerli durustlugun
            // ucuncu degeri Verified alanindadir.
            Enabled = false,
            Verified = false
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
