using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Cache;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Cache;

public sealed class CacheService(AdaPanelDbContext dbContext) : ICacheService
{
    // Redis GERÇEK bir Windows servisidir (redis-server --service-install).
    private static readonly CacheDescriptor RedisDescriptor = new("redis", "Redis", "AdaRedis", 6379, "redis-server.exe", RunsAsScheduledTask: false);

    // MEMCACHED SERVİS DEĞİL, ZAMANLANMIŞ GÖREVDİR.
    //
    // Depoda aynı bileşen için İKİ ÇELİŞEN SÖZLEŞME vardı: kurulum (ComponentBootstrapper)
    // memcached'i ONSTART zamanlanmış görev olarak kuruyor — gerekçesi orada yazılı: mingw ile
    // derlenmiş memcached.exe Windows Servis Denetim Protokolü'nü UYGULAMAZ, 'sc' ile başlatılırsa
    // SCM "1053 hizmet yanıt vermedi" döner. Bu servis ise durumu Get-Service ile okuyordu; görev
    // adında bir SERVİS hiç var olmadığı için panel HER ZAMAN "Durduruldu" gösteriyordu.
    // "AdaMemcached" adı burada VARSAYILMAZ; kurulu bileşen kaydından okunur (aşağıda).
    private static readonly CacheDescriptor MemcachedDescriptor = new("memcached", "Memcached", "AdaMemcached", 11211, "memcached.exe", RunsAsScheduledTask: true);

    public Task<CacheStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetStatusInternalAsync(cancellationToken);
    }

    public async Task<CacheRuntimeDto> StartRedisAsync(CancellationToken cancellationToken = default)
    {
        return await StartAsync(RedisDescriptor, cancellationToken);
    }

    public async Task<CacheRuntimeDto> StopRedisAsync(CancellationToken cancellationToken = default)
    {
        return await StopAsync(RedisDescriptor, cancellationToken);
    }

    public async Task<CacheRuntimeDto> StartMemcachedAsync(CancellationToken cancellationToken = default)
    {
        return await StartAsync(MemcachedDescriptor, cancellationToken);
    }

    public async Task<CacheRuntimeDto> StopMemcachedAsync(CancellationToken cancellationToken = default)
    {
        return await StopAsync(MemcachedDescriptor, cancellationToken);
    }

    private async Task<CacheStatusDto> GetStatusInternalAsync(CancellationToken cancellationToken)
    {
        var redis = await BuildRuntimeAsync(RedisDescriptor, cancellationToken);
        var memcached = await BuildRuntimeAsync(MemcachedDescriptor, cancellationToken);
        return new CacheStatusDto
        {
            Redis = redis,
            Memcached = memcached
        };
    }

    private async Task<CacheRuntimeDto> StartAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        var runtime = await BuildRuntimeAsync(descriptor, cancellationToken);
        if (!runtime.IsInstalled)
        {
            throw new AppException("cache.component_not_installed", 404, 404);
        }

        return descriptor.RunsAsScheduledTask
            ? await StartScheduledTaskAsync(descriptor, runtime, cancellationToken)
            : await StartWindowsServiceAsync(descriptor, runtime, cancellationToken);
    }

    private async Task<CacheRuntimeDto> StartWindowsServiceAsync(CacheDescriptor descriptor, CacheRuntimeDto runtime, CancellationToken cancellationToken)
    {
        if (!runtime.IsServiceRegistered)
        {
            await InstallServiceAsync(descriptor, runtime, cancellationToken);
        }

        var result = await ProcessRunner.RunPowerShellAsync($"Start-Service -Name \"{runtime.ServiceName}\" -ErrorAction Stop", cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cache.start_failed", 500, 500, Detay(result));
        }

        return await BuildRuntimeAsync(descriptor, cancellationToken);
    }

    /// <summary>
    /// Zamanlanmış görevle çalışan bileşeni (memcached) başlatır.
    /// <para>
    /// ESKİ HÂLİ HİÇ ÇALIŞAMAZDI: görev kayıtlı olmadığı için her seferinde
    /// <c>memcached.exe -d install -n AdaMemcached</c> gönderiliyordu. memcached'de <c>-n</c>
    /// "minimum chunk boyutu"dur; <c>atoi("AdaMemcached")</c> = 0 → memcached parametreyi reddedip
    /// hata ile çıkıyordu. Yani Başlat düğmesi HİÇBİR KOŞULDA çalışmıyordu.
    /// </para>
    /// <para>
    /// Görev kaydı KURULUMUN işidir (ComponentBootstrapper, run-memcached.cmd sarmalayıcısıyla).
    /// Burada dördüncü bir kurulum kopyası yazmak yerine görev yoksa DÜRÜST hata veriyoruz.
    /// </para>
    /// </summary>
    private async Task<CacheRuntimeDto> StartScheduledTaskAsync(CacheDescriptor descriptor, CacheRuntimeDto runtime, CancellationToken cancellationToken)
    {
        if (!runtime.IsServiceRegistered)
        {
            throw new AppException(
                "cache.service_install_failed",
                500,
                500,
                $"'{runtime.ServiceName}' zamanlanmis gorevi kayitli degil. {descriptor.DisplayName} bileseni Yazilim ekranindan yeniden kurulmali (gorevi kurulum olusturur).");
        }

        var result = await ProcessRunner.RunAsync(
            "schtasks.exe",
            $"/Run /TN \"{runtime.ServiceName}\"",
            cancellationToken: cancellationToken);

        // SONUCU ÖLÇ: "schtasks /Run" görevi TETİKLEDİĞİNİ söyler, sürecin ayakta kaldığını değil.
        // Tek dürüst kanıt, önbelleğin kendi portundan yanıt vermesidir.
        if (await WaitForLivenessAsync(descriptor, expectedAlive: true, cancellationToken))
        {
            return await BuildRuntimeAsync(descriptor, cancellationToken);
        }

        throw new AppException(
            "cache.start_failed",
            500,
            500,
            $"Gorev calistirildi ama {descriptor.DisplayName} 127.0.0.1:{descriptor.DefaultPort} adresinden yanit vermiyor. {Detay(result)}".Trim());
    }

    private async Task<CacheRuntimeDto> StopAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        var runtime = await BuildRuntimeAsync(descriptor, cancellationToken);
        return descriptor.RunsAsScheduledTask
            ? await StopScheduledTaskAsync(descriptor, runtime, cancellationToken)
            : await StopWindowsServiceAsync(descriptor, runtime, cancellationToken);
    }

    private async Task<CacheRuntimeDto> StopWindowsServiceAsync(CacheDescriptor descriptor, CacheRuntimeDto runtime, CancellationToken cancellationToken)
    {
        if (!runtime.IsServiceRegistered)
        {
            return runtime;
        }

        // -ErrorAction Stop: SilentlyContinue gerçek hatayı (yetki, bağımlı servis) yutuyor,
        // geriye kalan tek ölçüt olan çıkış kodu ise 0 kalıyordu.
        var result = await ProcessRunner.RunPowerShellStrictAsync($"Stop-Service -Name \"{runtime.ServiceName}\" -ErrorAction Stop", cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cache.stop_failed", 500, 500, Detay(result));
        }

        return await BuildRuntimeAsync(descriptor, cancellationToken);
    }

    /// <summary>
    /// Zamanlanmış görevle çalışan bileşeni durdurur.
    /// <para>
    /// ESKİ HÂLİ YALAN SÖYLÜYORDU: aynı adda bir SERVİS hiç var olmadığı için <c>IsServiceRegistered</c>
    /// hep false'tu ve metot HİÇBİR ŞEY YAPMADAN dönüyordu; panel de yeşil "Durduruldu" gösteriyordu.
    /// Oysa memcached zamanlanmış görevle ayakta çalışmaya devam ediyordu.
    /// </para>
    /// <para>
    /// Üç kademe: görevi bitir → hâlâ yanıt veriyorsa kurulum dizininden çalışan süreçleri öldür
    /// (schtasks /End görevin ALT süreçlerini her zaman sonlandırmaz) → yine yanıt veriyorsa
    /// kullanıcıya "durduramadım" de.
    /// </para>
    /// </summary>
    private async Task<CacheRuntimeDto> StopScheduledTaskAsync(CacheDescriptor descriptor, CacheRuntimeDto runtime, CancellationToken cancellationToken)
    {
        var detaylar = new List<string>();

        if (runtime.IsServiceRegistered)
        {
            var son = await ProcessRunner.RunAsync(
                "schtasks.exe",
                $"/End /TN \"{runtime.ServiceName}\"",
                cancellationToken: cancellationToken);
            var detay = Detay(son);
            if (!string.IsNullOrWhiteSpace(detay))
            {
                detaylar.Add(detay);
            }
        }

        if (!await WaitForLivenessAsync(descriptor, expectedAlive: false, cancellationToken))
        {
            // Görev bittiği hâlde süreç yaşıyor: sarmalayıcı .cmd'nin altındaki memcached.exe.
            // Yol karşılaştırması ORTAK primitiften geliyor (nginx'te bu karşılaştırma elle
            // yazılmıştı ve ayırıcı yönü yüzünden hiç eşleşmiyordu).
            if (!string.IsNullOrWhiteSpace(runtime.InstallDir))
            {
                var oldurulen = ProcessRunner.KillProcessesUnder(
                    runtime.InstallDir,
                    Path.GetFileNameWithoutExtension(descriptor.ExecutableName));
                detaylar.Add($"{oldurulen} surec oldurulmeye calisildi.");
            }

            if (!await WaitForLivenessAsync(descriptor, expectedAlive: false, cancellationToken))
            {
                throw new AppException(
                    "cache.stop_failed",
                    500,
                    500,
                    $"{descriptor.DisplayName} durdurulamadi: 127.0.0.1:{descriptor.DefaultPort} hala yanit veriyor. {string.Join(" ", detaylar)}".Trim());
            }
        }

        return await BuildRuntimeAsync(descriptor, cancellationToken);
    }

    /// <summary>
    /// Bileşen kendi portundan yanıt verene (ya da susana) kadar bekler; beklenen duruma ulaşıldıysa
    /// true döner. Süreç başlatma/durdurma ANINDA gerçekleşmez — tek ölçüm yanıltıcıdır.
    /// </summary>
    private static async Task<bool> WaitForLivenessAsync(CacheDescriptor descriptor, bool expectedAlive, CancellationToken cancellationToken)
    {
        for (var deneme = 0; deneme < 20; deneme++)
        {
            if (await IsAliveAsync(descriptor, cancellationToken) == expectedAlive)
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return await IsAliveAsync(descriptor, cancellationToken) == expectedAlive;
    }

    /// <summary>
    /// Bileşen GERÇEKTEN hizmet veriyor mu? Servis/görev kaydı değil, PORTUN kendisi sorulur.
    /// memcached'de <c>version\r\n</c> komutu <c>VERSION x.y.z</c> döner (kurulum doğrulaması da
    /// aynı sondayı kullanıyor); Redis'te RESP <c>INFO</c> yanıtı yeterlidir.
    /// </summary>
    private static async Task<bool> IsAliveAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.ComponentId == "memcached")
        {
            var response = await ProbeTextAsync(descriptor.DefaultPort, "version\r\n", cancellationToken);
            return response.StartsWith("VERSION", StringComparison.OrdinalIgnoreCase);
        }

        var info = await ProbeTextAsync(descriptor.DefaultPort, "PING\r\n", cancellationToken);
        return info.Length > 0;
    }

    /// <summary>Localhost'ta tek komut gönderip ilk yanıtı okur; erişilemezse boş dizge döner.</summary>
    private static async Task<string> ProbeTextAsync(int port, string command, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(2000);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cts.Token);

            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command), cts.Token);

            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, cts.Token);
            return read <= 0 ? string.Empty : Encoding.ASCII.GetString(buffer, 0, read).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Detay(CommandResult result)
        => string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();

    private async Task<CacheRuntimeDto> BuildRuntimeAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        var installedComponent = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == descriptor.ComponentId && item.Status == ComponentInstallStatus.Installed, cancellationToken);

        var installDir = installedComponent?.InstallDir ?? string.Empty;
        var executablePath = string.IsNullOrWhiteSpace(installDir)
            ? string.Empty
            : Directory.Exists(installDir)
                ? Directory.GetFiles(installDir, descriptor.ExecutableName, SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty
                : string.Empty;

        // GÖREV/SERVİS ADI VARSAYILMAZ, OKUNUR: kurulum bileşen manifestindeki serviceName'i
        // InstalledComponent kaydına yazar; koddaki sabit yalnızca kayıt boşsa yedek olarak kullanılır.
        var unitName = string.IsNullOrWhiteSpace(installedComponent?.ServiceName)
            ? descriptor.ServiceName
            : installedComponent!.ServiceName.Trim();

        // KAYIT ile ÇALIŞIYOR farklı sorulardır ve farklı yerlerden okunur:
        //  - Windows servisi (Redis)  -> Get-Service
        //  - Zamanlanmış görev (memcached) -> Get-ScheduledTask  (Get-Service burada HER ZAMAN
        //    null döndürüyordu; panelin "hep Durduruldu" göstermesinin kök nedeni buydu)
        // Her ölçüm bir powershell.exe başlatır; durum ekranı bunu sık sorar. Servis durumu TEK
        // KEZ okunur, hem "kayıtlı mı" hem "çalışıyor mu" aynı yanıttan türetilir.
        var serviceStatus = descriptor.RunsAsScheduledTask
            ? null
            : await ReadServiceStatusAsync(unitName, cancellationToken);

        var registered = descriptor.RunsAsScheduledTask
            ? await ScheduledTaskExistsAsync(unitName, cancellationToken)
            : serviceStatus is not null;

        // Zamanlanmış görevde tek dürüst "çalışıyor" kanıtı portun yanıt vermesidir: görev
        // "Running" görünüp sarmalayıcı .cmd'nin altındaki süreç çoktan ölmüş olabilir.
        var alive = descriptor.RunsAsScheduledTask && installedComponent is not null
            ? await IsAliveAsync(descriptor, cancellationToken)
            : string.Equals(serviceStatus, "Running", StringComparison.OrdinalIgnoreCase);

        // Redis çalışıyorsa canlı metrikleri (RESP INFO) oku — salt-okunur, best-effort.
        CacheMetricsDto? metrics = null;
        if (descriptor.ComponentId == "redis" && alive)
        {
            metrics = await ReadRedisInfoAsync(descriptor.DefaultPort, cancellationToken);
        }

        var status = installedComponent is null
            ? "NotInstalled"
            : descriptor.RunsAsScheduledTask
                ? alive ? "Running" : "Stopped"
                : serviceStatus ?? "Stopped";

        return new CacheRuntimeDto
        {
            ComponentId = descriptor.ComponentId,
            DisplayName = descriptor.DisplayName,
            ServiceName = unitName,
            DefaultPort = descriptor.DefaultPort,
            InstallDir = installDir,
            ExecutablePath = executablePath,
            IsInstalled = installedComponent is not null,
            IsServiceRegistered = registered,
            Status = status,
            Metrics = metrics
        };
    }

    /// <summary>
    /// Zamanlanmış görev kayıtlı mı? (memcached bir Windows SERVİSİ değildir; kurulum onu ONSTART
    /// görev olarak kaydeder.)
    /// </summary>
    private static async Task<bool> ScheduledTaskExistsAsync(string taskName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            $"if (Get-ScheduledTask -TaskName \"{taskName}\" -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}",
            cancellationToken);

        return result.Success && bool.TryParse(result.StandardOutput.Trim(), out var registered) && registered;
    }

    // Redis'e RESP ile bağlanıp INFO komutunu çalıştırır, metrikleri ayrıştırır. Şifre korumalıysa (NOAUTH)
    // veya erişilemezse null döner (panel kendi AdaRedis'inde parola yok). Sadece localhost, 2.5 sn timeout.
    private static async Task<CacheMetricsDto?> ReadRedisInfoAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(2500);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cts.Token);

            await using var stream = client.GetStream();
            var command = "INFO\r\n"u8.ToArray();
            await stream.WriteAsync(command, cts.Token);

            var buffer = new byte[16384];
            var builder = new StringBuilder();
            while (builder.Length < 131072)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read <= 0)
                {
                    break;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var current = builder.ToString();
                if (current.Contains("uptime_in_seconds", StringComparison.Ordinal) &&
                    current.Contains("used_memory_human", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var text = builder.ToString();
            return text.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase) ? null : ParseRedisInfo(text);
        }
        catch
        {
            return null;
        }
    }

    private static CacheMetricsDto ParseRedisInfo(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                map[line[..separator]] = line[(separator + 1)..];
            }
        }

        long keys = 0;
        foreach (var pair in map)
        {
            if (pair.Key.StartsWith("db", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(pair.Value, "keys=(\\d+)");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var dbKeys))
                {
                    keys += dbKeys;
                }
            }
        }

        return new CacheMetricsDto
        {
            Version = map.GetValueOrDefault("redis_version", string.Empty),
            UsedMemory = map.GetValueOrDefault("used_memory_human", string.Empty),
            ConnectedClients = int.TryParse(map.GetValueOrDefault("connected_clients"), out var clients) ? clients : 0,
            Hits = long.TryParse(map.GetValueOrDefault("keyspace_hits"), out var hits) ? hits : 0,
            Misses = long.TryParse(map.GetValueOrDefault("keyspace_misses"), out var misses) ? misses : 0,
            UptimeSeconds = long.TryParse(map.GetValueOrDefault("uptime_in_seconds"), out var uptime) ? uptime : 0,
            Keys = keys
        };
    }

    private async Task InstallServiceAsync(CacheDescriptor descriptor, CacheRuntimeDto runtime, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !File.Exists(runtime.ExecutablePath))
        {
            throw new AppException("cache.executable_not_found", 404, 404);
        }

        // memcached DALI KASTEN YOK. Eskiden burada
        // `memcached.exe -d install -n AdaMemcached` vardı ve HİÇ ÇALIŞAMAZDI: memcached'de `-n`
        // minimum chunk boyutudur, `atoi("AdaMemcached")` = 0 → araç parametreyi reddediyordu.
        // Zaten mingw memcached'i Windows servisi OLAMAZ (SCM protokolünü uygulamıyor); kurulumu
        // zamanlanmış görev olarak yapan tek yer ComponentBootstrapper'dır. Aynı kuralın burada
        // ikinci bir kopyasını yazmıyoruz.
        var command = descriptor.ComponentId == "redis"
            ? BuildRedisInstallCommand(runtime.ExecutablePath, runtime.InstallDir, runtime.ServiceName, descriptor.DefaultPort)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new AppException(
                "cache.service_install_failed",
                500,
                500,
                $"{descriptor.DisplayName} servis kaydi panelden yapilamaz; bileseni Yazilim ekranindan yeniden kurun.");
        }

        var result = await ProcessRunner.RunPowerShellStrictAsync(command, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cache.service_install_failed", 500, 500, Detay(result));
        }
    }

    private static string BuildRedisInstallCommand(string executablePath, string installDir, string serviceName, int port)
    {
        var configPath = Path.Combine(installDir, "adapanel-redis.conf");
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, $"""
                bind 127.0.0.1
                protected-mode yes
                port {port}
                dir {installDir}
                """);
        }

        return $"& '{executablePath}' --service-install '{configPath}' --service-name {serviceName}";
    }

    private static async Task<string?> ReadServiceStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            $@"$service = Get-Service -Name ""{serviceName}"" -ErrorAction SilentlyContinue
if ($service) {{ [pscustomobject]@{{ Status = $service.Status.ToString() }} | ConvertTo-Json -Depth 2 }}",
            cancellationToken);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.TryGetProperty("Status", out var statusElement)
            ? statusElement.GetString()
            : null;
    }

    private sealed record CacheDescriptor(
        string ComponentId,
        string DisplayName,
        string ServiceName,
        int DefaultPort,
        string ExecutableName,
        bool RunsAsScheduledTask);
}
