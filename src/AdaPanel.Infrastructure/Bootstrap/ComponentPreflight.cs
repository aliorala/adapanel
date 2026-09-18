using System.Globalization;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>
/// Bileşen kurulumu ÖN KONTROLÜ (Plesk kurulum ön-koşul denetimi paritesi): kurulumdan ÖNCE
/// bileşenin ihtiyaç duyduğu portları tutan yabancı süreçler ve çakışacak yabancı Windows
/// servisleri tespit edilir; kullanıcıya suçlunun ADIYLA anlaşılır bir hata gösterilir
/// ("3306 portunu MySQL80 servisi kullanıyor" gibi) — yanıltıcı ikincil hatalar yerine.
/// Saf/durumsuz kısım burada (birim testli); PS probe'u ComponentBootstrapper çalıştırır.
/// </summary>
public static class ComponentPreflight
{
    /// <summary>Bileşenin dinleyeceği portlar; kurulum öncesi yabancı bir süreç tutuyorsa çakışma.</summary>
    public static IReadOnlyList<int> GetRequiredPorts(string componentId) => componentId.Trim().ToLowerInvariant() switch
    {
        "mariadb-11" => [3306],
        "mysql-8" => [3307],
        "postgresql-16" => [5432],
        "redis" => [6379],
        "memcached" => [11211],
        // MailEnable panel-yönetimli yedektir ve kurulum akışı onu ÖNCEDEN durdurur
        // (ResolveMailServerConflictAsync) → burada yakalanan her dolu port GERÇEK yabancıdır.
        "stalwart-mail" => [25, 143, 587, 993, 4190, 8080],
        _ => []
    };

    /// <summary>
    /// Bileşenle çakışan YABANCI Windows servis ad desenleri. Servis şu an çalışmasa bile
    /// başlangıç türü Otomatik ise bir sonraki açılışta portu kapar → kurulum engellenir.
    /// </summary>
    public static IReadOnlyList<string> GetConflictingServicePatterns(string componentId) => componentId.Trim().ToLowerInvariant() switch
    {
        "mariadb-11" => ["mysql", "mariadb"],
        "mysql-8" => ["mysql", "mariadb"],
        "postgresql-16" => ["postgresql", "pgsql"],
        "redis" => ["redis"],
        "memcached" => ["memcached"],
        "stalwart-mail" => ["hmailserver", "smtpsvc"],
        _ => []
    };

    /// <summary>Panelin kendi kurduğu Windows servisleri (components.json serviceName alanları + panel servisi).</summary>
    private static readonly HashSet<string> OwnServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AdaPanel", "AdaMySQL", "AdaMariaDB", "AdaPostgreSQL", "AdaRedis", "AdaMemcached", "AdaStalwart"
    };

    /// <summary>
    /// Servis panele mi ait? Yeniden kurulum/güncellemede kendi servisimiz çakışma sayılmaz.
    /// DİKKAT: eskiden yalnızca "Ada" öneki aranıyordu — bu, üçüncü parti "AdAware"/"Adaptive..."
    /// gibi servisleri de panele aitmiş gibi gösterip GERÇEK bir çakışmayı sessizce yutabilirdi
    /// (kurulum ön kontrolünün varlık sebebini ortadan kaldırır). Artık bilinen adlar kümesi +
    /// "Ada" ardından BÜYÜK harf gelen panel kalıbı ("AdaMariaDB") aranıyor.
    /// </summary>
    public static bool IsOwnService(string? serviceName, string? componentServiceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(componentServiceName) &&
            serviceName.Equals(componentServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (OwnServiceNames.Contains(serviceName))
        {
            return true;
        }

        // Gelecekte eklenecek "Ada<Bilesen>" servisleri: "Ada" + BÜYÜK harf kalıbı.
        return serviceName.Length > 3 &&
               serviceName.StartsWith("Ada", StringComparison.Ordinal) &&
               char.IsUpper(serviceName[3]);
    }

    public static bool MatchesConflictPattern(string? serviceName, IReadOnlyList<string> patterns) =>
        !string.IsNullOrWhiteSpace(serviceName) &&
        patterns.Any(pattern => serviceName.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    public sealed class PortOccupancy
    {
        public int Port { get; set; }
        public int Pid { get; set; }
        public string Process { get; set; } = string.Empty;
        public string Service { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
    }

    public sealed class ServiceInfo
    {
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string StartMode { get; set; } = string.Empty;
    }

    /// <summary>Dolu portlardan panele ait OLMAYANLARI seçer (yeniden kurulumda kendi servisimiz elenmeli).</summary>
    public static IReadOnlyList<PortOccupancy> SelectForeignPortConflicts(
        IEnumerable<PortOccupancy> occupancies,
        string? componentServiceName,
        string? ownExecutablePath = null) =>
        occupancies
            .Where(item => item.Port > 0 && !IsOwnService(item.Service, componentServiceName))
            .Where(item => !string.IsNullOrWhiteSpace(item.Service) || !SameExecutable(item.ExecutablePath, ownExecutablePath))
            .ToList();

    private static bool SameExecutable(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected) ||
            !Path.IsPathFullyQualified(actual) || !Path.IsPathFullyQualified(expected)) return false;
        try { return Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Portu tutan süreç çekirdeğin HTTP dinleyicisi (HTTP.sys) mi?
    /// <para>
    /// Bir Windows barındırma sunucusunda IIS'in dinlediği HER port, netstat/Get-NetTCPConnection
    /// çıktısında "System" (PID 4) olarak görünür — çünkü bağlantıyı IIS değil çekirdek sürücüsü
    /// tutar. Yöneticiye "System.exe (PID 4)" demek hiçbir şey söylemez: o süreç durdurulamaz,
    /// kaldırılamaz. Gerçek sahip, o portu kullanan IIS SİTESİDİR ve çözüm o sitenin bağlamasını
    /// değiştirmektir. Bu ayrım tam olarak panelin kendi ürününde geçerlidir.
    /// </para>
    /// </summary>
    public static bool IsHttpSysHolder(int pid, string? processName) =>
        pid == 4 ||
        string.Equals(processName, "System", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "Idle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>appcmd list sites</c> metin çıktısındaki bağlamaları ayrıştırır. Örnek satır:
    /// <c>SITE "test.local" (id:2,bindings:http/*:8080:test.local,state:Started)</c>
    /// <para>
    /// Metin biçimi bilerek seçildi: XML biçimi de aynı bilgiyi verir ama bu ayrıştırıcı yalnızca
    /// "hangi site hangi portta" sorusunu yanıtlar ve metin biçimi appcmd sürümleri arasında bu
    /// konuda daha kararlıdır.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string SiteName, string Binding, int Port)> ParseAppcmdSiteBindings(string? output)
    {
        var result = new List<(string SiteName, string Binding, int Port)>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var siteStart = line.IndexOf('"');
            var siteEnd = siteStart >= 0 ? line.IndexOf('"', siteStart + 1) : -1;
            var bindingsStart = line.IndexOf("bindings:", StringComparison.OrdinalIgnoreCase);
            if (siteEnd <= siteStart || bindingsStart < 0)
            {
                continue;
            }

            var siteName = line[(siteStart + 1)..siteEnd];
            var rest = line[(bindingsStart + "bindings:".Length)..];

            // Bağlama listesi ",state:" ile biter; yoksa satır sonuna (kapanış parantezine) kadar.
            var stateIndex = rest.IndexOf(",state:", StringComparison.OrdinalIgnoreCase);
            if (stateIndex >= 0)
            {
                rest = rest[..stateIndex];
            }

            rest = rest.TrimEnd(')', '\r', ' ');

            foreach (var binding in rest.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                // "http/*:8080:test.local" → protokol / adres : port : konak
                var slash = binding.IndexOf('/');
                var address = slash >= 0 ? binding[(slash + 1)..] : binding;
                var parts = address.Split(':');
                if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                {
                    continue;
                }

                result.Add((siteName, binding.Trim(), port));
            }
        }

        return result;
    }

    /// <summary>
    /// HTTP.sys'in tuttuğu bir port için insan tarafından çözülebilir açıklama üretir: portu hangi
    /// IIS sitesi/bağlaması kullanıyor. Eşleşme bulunamazsa boş döner (mesaj eski hâlinde kalır).
    /// </summary>
    public static string DescribeHttpSysPortOwner(int port, IEnumerable<(string SiteName, string Binding, int Port)> bindings)
    {
        var matches = bindings
            .Where(item => item.Port == port && !string.IsNullOrWhiteSpace(item.SiteName))
            .Select(item => string.IsNullOrWhiteSpace(item.Binding) ? item.SiteName : $"{item.SiteName} ({item.Binding})")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count == 0 ? string.Empty : $"IIS sitesi: {string.Join(", ", matches)}";
    }

    /// <summary>
    /// Yabancı çakışan servislerden ENGELLEYİCİ olanları seçer: çalışanlar ve (durmuş olsa da)
    /// başlangıç türü Otomatik olanlar. Devre dışı/elle-başlatılır durmuş servis engellemez.
    /// </summary>
    public static IReadOnlyList<ServiceInfo> SelectBlockingServices(
        IEnumerable<ServiceInfo> services,
        IReadOnlyList<string> patterns,
        string? componentServiceName) =>
        services
            .Where(item => MatchesConflictPattern(item.Name, patterns))
            .Where(item => !IsOwnService(item.Name, componentServiceName))
            .Where(item =>
                item.State.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
                item.StartMode.Equals("Auto", StringComparison.OrdinalIgnoreCase) ||
                item.StartMode.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Port + servis çakışmalarını tek seferde toplayan PowerShell probe script'i.</summary>
    public static string BuildProbeScript(IReadOnlyList<int> ports, IReadOnlyList<string> patterns)
    {
        var portList = ports.Count > 0 ? string.Join(",", ports) : "0";
        var patternRegex = patterns.Count > 0
            ? string.Join("|", patterns.Select(System.Text.RegularExpressions.Regex.Escape))
            : "^$";

        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $portRows = @()
            foreach ($p in @({{portList}})) {
                if ($p -le 0) { continue }
                foreach ($conn in @(Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)) {
                    $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
                    $svc = Get-CimInstance Win32_Service -Filter "ProcessId=$($conn.OwningProcess)" -ErrorAction SilentlyContinue | Select-Object -First 1
                    $portRows += [pscustomobject]@{ Port = [int]$p; Pid = [int]$conn.OwningProcess; Process = [string]$proc.ProcessName; Service = [string]$svc.Name; ExecutablePath = [string]$proc.Path }
                }
            }
            $svcRows = @(Get-CimInstance Win32_Service |
                Where-Object { $_.Name -match '{{patternRegex}}' } |
                ForEach-Object { [pscustomobject]@{ Name = [string]$_.Name; State = [string]$_.State; StartMode = [string]$_.StartMode } })
            [pscustomobject]@{ Ports = @($portRows); Services = $svcRows } | ConvertTo-Json -Depth 4
            """;
    }
}
