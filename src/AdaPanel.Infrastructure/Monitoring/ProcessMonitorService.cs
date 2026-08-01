using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Monitoring;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Monitoring;

/// <summary>
/// htop benzeri canlı süreç izleyici — ama hosting paneline uygun hâli: her satır sürecin HANGİ SİTEYE
/// veya HANGİ PANEL BİLEŞENİNE ait olduğunu da söyler.
///
/// DİKKAT — DI ÖMRÜ: Bu servis, projedeki diğer servislerin aksine SINGLETON kaydedilmelidir.
/// Sebebi teknik bir zorunluluktur: bir sürecin CPU yüzdesi tek ölçümle HESAPLANAMAZ; yalnızca
/// <see cref="Process.TotalProcessorTime"/> değerinin İKİ ölçüm arasındaki farkı, geçen süreye ve
/// çekirdek sayısına bölünerek bulunur. Yani servisin önceki anlık görüntüyü hatırlaması gerekir;
/// Scoped kayıtta her istek yeni örnek alır ve CPU sütunu daima 0 görünürdü.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ProcessMonitorService(ILogger<ProcessMonitorService> logger) : IProcessMonitorService
{
    // WMI/CIM çağrısı PAHALIDIR (süreç başlatma + sorgu ~200-500 ms). Uç saniyede bir çağrılacağı için
    // komut satırı/servis eşlemesi kısa süreli önbelleğe alınır: app pool ↔ PID eşlemesi ancak worker
    // geri dönüştüğünde değişir, o da yeni bir PID görüldüğünde zaten anında yenilenir.
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromSeconds(15);

    // CPU farkının anlamlı olması için asgari ölçüm penceresi. Çok kısa aralıkta (ör. UI'ın arka arkaya
    // iki isteği) yüzde gürültülü/uçuk çıkar; bu durumda kısa bir gecikmeyle pencere genişletilir.
    private static readonly TimeSpan MinimumSampleWindow = TimeSpan.FromMilliseconds(250);

    // Panel bileşenlerinin Windows servis adları (components.json "serviceName" alanları) + çekirdek
    // IIS/FTP/DNS servisleri. Yalnız BUNLAR sorgulanır; tüm Win32_Service listesini çekmek yavaştır.
    private static readonly string[] TrackedServiceNames =
    [
        "AdaPanel", "AdaMySQL", "AdaMariaDB", "AdaPostgreSQL", "AdaRedis", "AdaMemcached", "AdaStalwart",
        "docker", "W3SVC", "WAS", "FTPSVC", "DNS", "MSSQLSERVER"
    ];

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<int, CpuSample> previousSamples = [];
    private DateTime previousCapturedUtc;
    private bool baselineReady;

    private ProcessMetadata metadata = ProcessMetadata.Empty;
    private DateTime metadataCapturedUtc;

    public async Task<ProcessSnapshotDto> GetSnapshotAsync(
        int limit = 30,
        string sortBy = "cpu",
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 200);
        var sortKey = string.Equals(sortBy, "memory", StringComparison.OrdinalIgnoreCase) ? "memory" : "cpu";

        // Tek seferde tek ölçüm: eşzamanlı istekler önceki anlık görüntüyü birbirinin altından çekmesin.
        await gate.WaitAsync(cancellationToken);
        try
        {
            var samples = CaptureRawSamples();

            // İlk çağrıda karşılaştırılacak önceki ölçüm yoktur → tüm CPU sütunu 0 gelirdi. Bunun yerine
            // kısa bir taban ölçümü alıp beklenir; böylece kullanıcı İLK açılışta da gerçek değer görür.
            if (!baselineReady)
            {
                StoreBaseline(samples);
                await Task.Delay(MinimumSampleWindow, cancellationToken);
                samples = CaptureRawSamples();
            }
            else if (DateTime.UtcNow - previousCapturedUtc < MinimumSampleWindow)
            {
                await Task.Delay(MinimumSampleWindow, cancellationToken);
                samples = CaptureRawSamples();
            }

            var meta = await ResolveMetadataAsync(samples, cancellationToken);
            return BuildSnapshot(samples, meta, take, sortKey);
        }
        finally
        {
            gate.Release();
        }
    }

    private ProcessSnapshotDto BuildSnapshot(
        IReadOnlyList<RawSample> samples,
        ProcessMetadata meta,
        int take,
        string sortKey)
    {
        var capturedUtc = DateTime.UtcNow;
        var elapsed = baselineReady ? capturedUtc - previousCapturedUtc : TimeSpan.Zero;
        var processorCount = Environment.ProcessorCount;
        var memory = ReadMemoryStatus();
        var totalMemory = (long)memory.TotalPhysical;
        var availableMemory = (long)memory.AvailablePhysical;
        var usedMemory = Math.Max(0, totalMemory - availableMemory);

        var rows = new List<ProcessUsageDto>(samples.Count);
        var cpuTimeDeltaMs = 0d;

        foreach (var sample in samples)
        {
            var cpuPercent = 0d;
            if (baselineReady &&
                previousSamples.TryGetValue(sample.ProcessId, out var previous) &&
                previous.StartTimeUtc == sample.StartTimeUtc) // PID geri dönüştürülmüşse eski değerle karşılaştırma
            {
                cpuPercent = CalculateCpuPercent(previous.ProcessorTime, sample.ProcessorTime, elapsed, processorCount);
                cpuTimeDeltaMs += Math.Max(0, (sample.ProcessorTime - previous.ProcessorTime).TotalMilliseconds);
            }

            var appPool = meta.AppPoolFor(sample.ProcessId);

            // php-cgi'nin kendi komut satırı hangi siteye hizmet ettiğini söylemez; FastCGI süreçlerini
            // ilgili app pool'un w3wp'si doğurur, bu yüzden site bilgisi EBEVEYN süreçten çözülür.
            appPool ??= meta.AppPoolForParentOf(sample.ProcessId);

            var label = ResolveLabel(sample.Name, appPool, meta.ServiceFor(sample.ProcessId));

            rows.Add(new ProcessUsageDto
            {
                ProcessId = sample.ProcessId,
                Name = sample.Name,
                Category = label.Category,
                Label = label.Label,
                Detail = label.Detail,
                CpuPercent = cpuPercent,
                WorkingSetBytes = sample.WorkingSetBytes,
                PrivateMemoryBytes = sample.PrivateMemoryBytes,
                MemoryPercent = totalMemory <= 0
                    ? 0
                    : Math.Round(sample.WorkingSetBytes * 100d / totalMemory, 2),
                ThreadCount = sample.ThreadCount,
                StartTimeUtc = sample.StartTimeUtc,
                UptimeSeconds = sample.StartTimeUtc is { } startUtc
                    ? Math.Max(0, (capturedUtc - startUtc).TotalSeconds)
                    : 0
            });
        }

        StoreBaseline(samples, capturedUtc);

        var ordered = (sortKey == "memory"
                ? rows.OrderByDescending(row => row.WorkingSetBytes).ThenByDescending(row => row.CpuPercent)
                : rows.OrderByDescending(row => row.CpuPercent).ThenByDescending(row => row.WorkingSetBytes))
            .ThenBy(row => row.ProcessId)
            .Take(take)
            .ToArray();

        return new ProcessSnapshotDto
        {
            CapturedAtUtc = capturedUtc,
            SampleWindowSeconds = Math.Round(elapsed.TotalSeconds, 3),
            CpuBaselineReady = elapsed > TimeSpan.Zero,
            TotalCpuPercent = CalculateTotalCpuPercent(cpuTimeDeltaMs, elapsed, processorCount),
            LogicalProcessorCount = processorCount,
            TotalMemoryBytes = totalMemory,
            UsedMemoryBytes = usedMemory,
            AvailableMemoryBytes = availableMemory,
            MemoryUsagePercent = totalMemory <= 0 ? 0 : Math.Round(usedMemory * 100d / totalMemory, 2),
            TotalProcessCount = rows.Count,
            SortBy = sortKey,
            Processes = ordered
        };
    }

    private void StoreBaseline(IReadOnlyList<RawSample> samples, DateTime? capturedUtc = null)
    {
        previousSamples.Clear();
        foreach (var sample in samples)
        {
            previousSamples[sample.ProcessId] = new CpuSample(sample.ProcessorTime, sample.StartTimeUtc);
        }

        previousCapturedUtc = capturedUtc ?? DateTime.UtcNow;
        baselineReady = true;
    }

    /// <summary>
    /// Tüm süreçleri tek turda okur. Korumalı süreçler (SYSTEM altındakiler, Idle, Secure System)
    /// alan okurken <see cref="Win32Exception"/> fırlatır; her alan tek tek korunur ki bir süreç
    /// yüzünden LİSTE BOZULMASIN — okunamayan alan 0/null kalır, satır yine görünür.
    /// </summary>
    private static List<RawSample> CaptureRawSamples()
    {
        var processes = Process.GetProcesses();
        var samples = new List<RawSample>(processes.Length);

        foreach (var process in processes)
        {
            try
            {
                var id = process.Id;
                if (id <= 0)
                {
                    continue; // PID 0 = System Idle Process; ölçülemez, listede anlamı yok.
                }

                samples.Add(new RawSample(
                    id,
                    process.ProcessName,
                    ReadValue(() => process.TotalProcessorTime, TimeSpan.Zero),
                    ReadValue(() => process.WorkingSet64, 0L),
                    ReadValue(() => process.PrivateMemorySize64, 0L),
                    ReadValue(() => process.Threads.Count, 0),
                    ReadValue<DateTime?>(() => process.StartTime.ToUniversalTime(), null)));
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Ölçüm sırasında sonlanan veya erişilemeyen süreç → atla.
            }
            finally
            {
                process.Dispose();
            }
        }

        return samples;
    }

    private static T ReadValue<T>(Func<T> reader, T fallback)
    {
        try
        {
            return reader();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// w3wp/php-cgi komut satırı ve panel servislerinin PID'leri yalnız WMI/CIM'den okunabilir.
    /// TÜM süreçler için WMI çağırmak pahalı olduğundan sorgu bu iki süreç adına daraltılır ve sonuç
    /// kısa süre önbelleklenir; yeni bir w3wp/php-cgi PID'i görülürse önbellek anında tazelenir.
    /// </summary>
    private async Task<ProcessMetadata> ResolveMetadataAsync(IReadOnlyList<RawSample> samples, CancellationToken cancellationToken)
    {
        var interesting = samples
            .Where(sample => IsCommandLineInteresting(sample.Name))
            .Select(sample => sample.ProcessId)
            .ToArray();

        var expired = DateTime.UtcNow - metadataCapturedUtc > MetadataTtl;
        var hasUnknown = interesting.Any(id => !metadata.KnownProcessIds.Contains(id));
        if (!expired && !hasUnknown)
        {
            return metadata;
        }

        try
        {
            var result = await ProcessRunner.RunPowerShellAsync(MetadataScript, cancellationToken);
            var parsed = ParseMetadata(result.StandardOutput);
            if (parsed is not null)
            {
                metadata = parsed;
                metadataCapturedUtc = DateTime.UtcNow;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Etiket zenginleştirmesi BEST-EFFORT'tur: WMI erişilemezse liste yine döner, yalnız
            // site/app pool etiketi yerine süreç adı tabanlı sınıflandırma kullanılır.
            logger.LogDebug(exception, "Süreç meta verisi (WMI) okunamadı; süreç adı tabanlı etiketleme kullanılacak.");
        }

        return metadata;
    }

    private static bool IsCommandLineInteresting(string processName)
    {
        return processName.Equals("w3wp", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("php-cgi", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string MetadataScript =
        $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'w3wp.exe' OR Name = 'php-cgi.exe'" | ForEach-Object {
            [pscustomobject]@{
                id = [int]$_.ProcessId
                parentId = [int]$_.ParentProcessId
                commandLine = [string]$_.CommandLine
            }
        })
        $services = @(Get-CimInstance -ClassName Win32_Service -Filter "{{ServiceFilter}}" | Where-Object { $_.ProcessId -gt 0 } | ForEach-Object {
            [pscustomobject]@{
                id = [int]$_.ProcessId
                name = [string]$_.Name
            }
        })
        ConvertTo-Json -InputObject ([ordered]@{ processes = $processes; services = $services }) -Compress -Depth 4
        """;

    private static string ServiceFilter =>
        string.Join(" OR ", TrackedServiceNames.Select(name => $"Name = '{name}'"));

    // ---------------------------------------------------------------------------------------------
    // Saf mantık (birim testi kapsamında): CPU yüzdesi, app pool → site adı, süreç → etiket eşlemesi.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// CPU yüzdesi = (CPU zamanı farkı) / (geçen süre × mantıksal çekirdek sayısı) × 100.
    /// Çekirdek sayısına bölme sayesinde 8 çekirdekli makinede tek çekirdeği doyuran bir süreç
    /// %100 değil %12,5 görünür — yani sütun "sunucunun ne kadarını yiyor" sorusunu yanıtlar.
    /// </summary>
    internal static double CalculateCpuPercent(TimeSpan previousCpu, TimeSpan currentCpu, TimeSpan elapsed, int processorCount)
    {
        if (processorCount <= 0 || elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var deltaMs = (currentCpu - previousCpu).TotalMilliseconds;
        if (deltaMs <= 0)
        {
            return 0;
        }

        var percent = deltaMs / (elapsed.TotalMilliseconds * processorCount) * 100d;
        return Math.Round(Math.Clamp(percent, 0, 100), 2);
    }

    internal static double CalculateTotalCpuPercent(double cpuTimeDeltaMs, TimeSpan elapsed, int processorCount)
    {
        if (processorCount <= 0 || elapsed <= TimeSpan.Zero || cpuTimeDeltaMs <= 0)
        {
            return 0;
        }

        var percent = cpuTimeDeltaMs / (elapsed.TotalMilliseconds * processorCount) * 100d;
        return Math.Round(Math.Clamp(percent, 0, 100), 2);
    }

    /// <summary>
    /// IIS worker süreçleri <c>w3wp.exe -ap "&lt;AppPoolAdı&gt;" -v "v4.0" ...</c> biçiminde çalışır.
    /// App pool adı süreci siteye bağlayan TEK ipucudur, bu yüzden komut satırından çıkarılır.
    /// </summary>
    internal static string? ExtractAppPoolName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var match = AppPoolArgumentPattern().Match(commandLine);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Panel kendi app pool'larını <c>AdaPanel.&lt;siteAdı&gt;</c> kalıbıyla kurar
    /// (WebAdministrationSiteService.BuildManagedPoolName). Alan adlarında yalnız harf/rakam/nokta/tire
    /// bulunduğundan ad dönüşümü kayıpsızdır: öneki atmak site adını verir. Panel dışı bir pool ise null.
    /// </summary>
    internal static string? ResolveSiteNameFromPool(string? appPoolName)
    {
        const string managedPoolPrefix = "AdaPanel.";
        if (string.IsNullOrWhiteSpace(appPoolName) ||
            !appPoolName.StartsWith(managedPoolPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var siteName = appPoolName[managedPoolPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(siteName) ? null : siteName;
    }

    /// <summary>
    /// Süreci hosting yöneticisinin anlayacağı bir varlığa bağlar. Öncelik sırası:
    /// 1) süreç bilinen bir panel/IIS Windows servisiyse servis kimliği,
    /// 2) w3wp ise app pool → site,
    /// 3) php-cgi ise (ebeveyn w3wp'den gelen) site,
    /// 4) tanınan çalıştırılabilir adı (servis sorgusu başarısızsa yedek yol),
    /// 5) çekirdek Windows süreçleri, aksi hâlde "other".
    /// </summary>
    internal static ProcessLabel ResolveLabel(string processName, string? appPoolName, string? serviceName)
    {
        var name = processName ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var fromService = MapServiceName(serviceName);
            if (fromService is not null)
            {
                return fromService;
            }
        }

        if (name.Equals("w3wp", StringComparison.OrdinalIgnoreCase))
        {
            var siteName = ResolveSiteNameFromPool(appPoolName);
            if (siteName is not null)
            {
                return new ProcessLabel("site", siteName, appPoolName ?? string.Empty);
            }

            return string.IsNullOrWhiteSpace(appPoolName)
                ? new ProcessLabel("web", "IIS", string.Empty)
                : new ProcessLabel("apppool", appPoolName, appPoolName);
        }

        if (name.Equals("php-cgi", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("php-cgi-fcgi", StringComparison.OrdinalIgnoreCase))
        {
            var siteName = ResolveSiteNameFromPool(appPoolName);
            return new ProcessLabel("php", siteName ?? appPoolName ?? string.Empty, appPoolName ?? string.Empty);
        }

        var fromExecutable = MapExecutableName(name);
        if (fromExecutable is not null)
        {
            return fromExecutable;
        }

        return WindowsCoreProcesses.Contains(name)
            ? new ProcessLabel("windows", name, string.Empty)
            : new ProcessLabel("other", name, string.Empty);
    }

    private static ProcessLabel? MapServiceName(string serviceName)
    {
        return serviceName.ToLowerInvariant() switch
        {
            "adapanel" => new ProcessLabel("panel", "AdaPanel", serviceName),
            "adamysql" => new ProcessLabel("database", "MySQL", serviceName),
            "adamariadb" => new ProcessLabel("database", "MariaDB", serviceName),
            "adapostgresql" => new ProcessLabel("database", "PostgreSQL", serviceName),
            "mssqlserver" => new ProcessLabel("database", "SQL Server", serviceName),
            "adaredis" => new ProcessLabel("cache", "Redis", serviceName),
            "adamemcached" => new ProcessLabel("cache", "Memcached", serviceName),
            "adastalwart" => new ProcessLabel("mail", "Stalwart", serviceName),
            "docker" => new ProcessLabel("docker", "Docker", serviceName),
            "w3svc" or "was" => new ProcessLabel("web", "IIS", serviceName),
            "ftpsvc" => new ProcessLabel("ftp", "FTP", serviceName),
            "dns" => new ProcessLabel("dns", "DNS", serviceName),
            _ => null
        };
    }

    // Servis sorgusu (WMI) başarısız olursa ya da bileşen servis olarak değil zamanlanmış görev olarak
    // çalışıyorsa (ör. Stalwart) etiket yine doğru çıksın diye çalıştırılabilir adı üzerinden yedek eşleme.
    private static ProcessLabel? MapExecutableName(string processName)
    {
        return processName.ToLowerInvariant() switch
        {
            "mysqld" => new ProcessLabel("database", "MySQL", string.Empty),
            "mariadbd" => new ProcessLabel("database", "MariaDB", string.Empty),
            "postgres" => new ProcessLabel("database", "PostgreSQL", string.Empty),
            "sqlservr" => new ProcessLabel("database", "SQL Server", string.Empty),
            "redis-server" => new ProcessLabel("cache", "Redis", string.Empty),
            "memcached" => new ProcessLabel("cache", "Memcached", string.Empty),
            "stalwart" or "stalwart-mail" => new ProcessLabel("mail", "Stalwart", string.Empty),
            "adapanel" or "adapanel.api" => new ProcessLabel("panel", "AdaPanel", string.Empty),
            "nginx" => new ProcessLabel("web", "Nginx", string.Empty),
            "dockerd" or "docker" or "com.docker.backend" => new ProcessLabel("docker", "Docker", string.Empty),
            "inetinfo" => new ProcessLabel("web", "IIS", string.Empty),
            "dns" => new ProcessLabel("dns", "DNS", string.Empty),
            _ => null
        };
    }

    private static readonly HashSet<string> WindowsCoreProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Secure System", "Memory Compression", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "svchost", "fontdrvhost", "dwm", "spoolsv", "taskhostw",
        "explorer", "MsMpEng", "NisSrv", "SearchIndexer", "conhost", "WmiPrvSE"
    };

    internal static ProcessMetadata? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var appPools = new Dictionary<int, string>();
            var parents = new Dictionary<int, int>();
            var services = new Dictionary<int, string>();
            var knownIds = new HashSet<int>();

            foreach (var item in EnumerateArray(root, "processes"))
            {
                var id = ReadInt(item, "id");
                if (id <= 0)
                {
                    continue;
                }

                knownIds.Add(id);
                var parentId = ReadInt(item, "parentId");
                if (parentId > 0)
                {
                    parents[id] = parentId;
                }

                var appPool = ExtractAppPoolName(ReadString(item, "commandLine"));
                if (appPool is not null)
                {
                    appPools[id] = appPool;
                }
            }

            foreach (var item in EnumerateArray(root, "services"))
            {
                var id = ReadInt(item, "id");
                var name = ReadString(item, "name");
                if (id > 0 && !string.IsNullOrWhiteSpace(name))
                {
                    services[id] = name;
                }
            }

            return new ProcessMetadata(appPools, parents, services, knownIds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // PowerShell 5.1'de tek elemanlı diziler ConvertTo-Json tarafından nesneye indirgenebilir;
    // bu yüzden hem dizi hem tekil nesne kabul edilir (savunmacı ayrıştırma).
    private static IEnumerable<JsonElement> EnumerateArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            yield return value;
        }
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static MemoryStatus ReadMemoryStatus()
    {
        var native = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        return GlobalMemoryStatusEx(ref native)
            ? new MemoryStatus(native.TotalPhys, native.AvailPhys)
            : new MemoryStatus(0, 0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private readonly record struct MemoryStatus(ulong TotalPhysical, ulong AvailablePhysical);

    private readonly record struct CpuSample(TimeSpan ProcessorTime, DateTime? StartTimeUtc);

    private sealed record RawSample(
        int ProcessId,
        string Name,
        TimeSpan ProcessorTime,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        int ThreadCount,
        DateTime? StartTimeUtc);

    // -ap "AdaPanel.site.com" (tırnaklı) veya -ap AdaPanel.site.com (tırnaksız) biçimlerini yakalar.
    [GeneratedRegex("-ap\\s+(?:\"([^\"]+)\"|([^\\s\"]+))", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AppPoolArgumentPattern();
}

/// <summary>Süreç etiketi: sabit kategori anahtarı + okunabilir ad + ek bağlam.</summary>
internal sealed record ProcessLabel(string Category, string Label, string Detail);

/// <summary>WMI'dan çözülen süreç meta verisi (app pool komut satırları + panel servis PID'leri).</summary>
internal sealed class ProcessMetadata(
    IReadOnlyDictionary<int, string> appPools,
    IReadOnlyDictionary<int, int> parents,
    IReadOnlyDictionary<int, string> services,
    IReadOnlySet<int> knownProcessIds)
{
    public static ProcessMetadata Empty { get; } = new(
        new Dictionary<int, string>(),
        new Dictionary<int, int>(),
        new Dictionary<int, string>(),
        new HashSet<int>());

    public IReadOnlySet<int> KnownProcessIds { get; } = knownProcessIds;

    public string? AppPoolFor(int processId)
    {
        return appPools.TryGetValue(processId, out var value) ? value : null;
    }

    public string? AppPoolForParentOf(int processId)
    {
        return parents.TryGetValue(processId, out var parentId) && appPools.TryGetValue(parentId, out var value)
            ? value
            : null;
    }

    public string? ServiceFor(int processId)
    {
        return services.TryGetValue(processId, out var value) ? value : null;
    }
}
