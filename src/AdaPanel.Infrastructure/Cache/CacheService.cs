using System.Net.Sockets;
using System.Globalization;
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
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Cache;

/// <summary>
/// Redis ve memcached bileşenlerinin durumunu ÖLÇER, başlatır ve durdurur.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden günlükçü (ILogger) var:</b> bu servisin verdiği her yanıt bir ÖLÇÜMDÜR ve ölçümün
/// kendisi başarısız olabilir. Sorun şu ki başarısız ölçüm burada sessizce KESİN BİR CEVABA
/// yuvarlanıyordu: <c>Get-Service</c> çalıştırılamazsa "servis kayıtlı değil", <c>Get-ScheduledTask</c>
/// çalıştırılamazsa "görev yok", port sondası patlarsa "durdurulmuş". Yani panelde "Durduruldu"
/// yazısı iki apayrı gerçeği aynı şekilde gösteriyordu: (a) bileşen gerçekten duruyor,
/// (b) BİZ ÖLÇEMEDİK. İkincisi için sunucuda hiçbir iz kalmıyordu; kullanıcı "Başlat"a basınca
/// gelen "bileşeni yeniden kurun" tavsiyesi de o yüzden yanlış olabiliyordu.
/// </para>
/// <para>
/// <b>Gürültü kuralı:</b> port sondasının "bağlantı reddedildi" ile dönmesi bileşen dururken
/// BEKLENEN sonuçtur ve Debug'a yazılır. Uyarı seviyesi yalnızca ölçüm aracının kendisi
/// (powershell/schtasks) beklenmedik biçimde başarısız olduğunda kullanılır — çünkü panelin
/// gösterdiği tabloyu yanlış yapan tek şey odur.
/// </para>
/// <para>
/// <b>Sır sızıntısı:</b> Redis <c>INFO</c> çıktısı ve <c>adapanel-redis.conf</c> içeriği günlüğe
/// HİÇ yazılmaz; yalnızca ayrıştırılmış sayısal metrikler ve bileşen/port adları yazılır.
/// </para>
/// <para>
/// Günlükçü İSTEĞE BAĞLI: dar kapsamlı testler bu servisi doğrudan kuruyor (bkz.
/// <c>MemcachedRuntimeTruthTests</c>); zorunlu yapmak o kurulumları kırardı. Üretimde DI daima verir.
/// Aynı gerekçe <c>BackupService</c>'te de yazılı.
/// </para>
/// </remarks>
public sealed class CacheService(AdaPanelDbContext dbContext, ILogger<CacheService>? logger = null) : ICacheService
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
    internal const int MemcachedDefaultPort = 11211;

    /*
     * SONDA PORTU TEST KANCASI — ve bunun sebebi bir kolaylık değil, GÜVENİLMEZ BİR NÖBETÇİYDİ.
     *
     * ÖLÇÜLDÜ: <c>MemcachedRuntimeTruthTests</c> gerçek 11211 portunu kullanıyordu. Bir test oraya
     * sahte bir dinleyici bağlıyor, kardeş test ise "kimse cevap vermiyorsa Durduruldu" diye
     * ölçüyor. İkisi de MAKİNENİN TAMAMINDA paylaşılan tek bir kaynağa bağlıydı: makinede gerçek
     * bir memcached varsa, ya da sahte dinleyicinin soketi henüz tam kapanmadıysa, ikinci test
     * "Running" görüp DÜŞÜYORDU. Tam koşumda kırmızı, tek başına yeşil — yani sonuç koda değil
     * ORTAMA bağlıydı.
     *
     * Bu, testin yakalamaya çalıştığı hatadan daha pahalıdır: rastgele kırmızı yanan bir nöbetçi,
     * bir süre sonra "yine o test" diye görmezden gelinir ve gerçekten bozulduğunda kimse bakmaz.
     * Güvenilmez bir koruma, olmayandan beterdir.
     *
     * Port artık dışarıdan verilebiliyor; testler her koşuda BOŞ bir port seçip kendi dinleyicisini
     * oraya kuruyor, böylece paylaşılan kaynak tümden ortadan kalkıyor. Üretimde değer HER ZAMAN
     * varsayılandır: DI bu özelliği hiç set etmez.
     */
    internal int MemcachedProbePort { get; init; } = MemcachedDefaultPort;

    // Keep OS inspection replaceable per instance; tests exercise the real runtime
    // decisions without starting PowerShell or touching a Windows service.
    internal Func<string, CancellationToken, Task<CommandResult>> InspectPowerShellAsync { get; init; }
        = ProcessRunner.RunPowerShellStrictAsync;
    internal Func<int, string, CancellationToken, Task<string?>>? InspectCachePortAsync { get; init; }
    internal Func<int, CancellationToken, Task<Stream>>? OpenRedisInfoStreamAsync { get; init; }

    private CacheDescriptor MemcachedDescriptor
        => new("memcached", "Memcached", "AdaMemcached", MemcachedProbePort, "memcached.exe", RunsAsScheduledTask: true);

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

        EnsureRuntimeMeasured(runtime);

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
            // İSTİSNA HTTP yanıtına gider ve orada BİTER. Sunucuda bunun izi yoksa "dün akşam
            // Redis başlamıyordu" şikâyeti sonradan hiç incelenemez.
            logger?.LogError(
                "{Bilesen} servisi baslatilamadi: {Servis} (cikis kodu {CikisKodu}). {Detay}",
                descriptor.DisplayName,
                runtime.ServiceName,
                result.ExitCode,
                Detay(result));
            throw new AppException("cache.start_failed", 500, 500, Detay(result));
        }

        logger?.LogInformation("{Bilesen} servisi baslatildi: {Servis}", descriptor.DisplayName, runtime.ServiceName);
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
            // "Kayıtlı değil" iki farklı gerçeğin ORTAK yanıtıdır: görev gerçekten yok, ya da
            // Get-ScheduledTask çalıştırılamadı (bkz. ScheduledTaskExistsAsync uyarısı). Kullanıcıya
            // verilen "yeniden kur" tavsiyesi ikinci durumda YANLIŞTIR; hangi durumda olduğumuz
            // yalnız günlükten anlaşılır.
            logger?.LogWarning(
                "{Bilesen} baslatilamadi: '{Gorev}' zamanlanmis gorevi kayitli GORUNMUYOR (gercekten yok ya da olcum yapilamadi).",
                descriptor.DisplayName,
                runtime.ServiceName);

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

        // DIŞ ARACIN ÇIKIŞ KODU AKIŞI DURDURMUYOR — o hâlde en azından KAYDEDİLMELİ.
        // Görev zaten çalışıyorken (schtasks 0x41301) ya da tetikleme reddedildiğinde port yine de
        // yanıt verebilir; o durumda "Başlat" başarılı görünür ama başlatan BİZ olmayız. Bu ayrım
        // yalnız günlükte durur.
        if (result.ExitCode != 0)
        {
            logger?.LogWarning(
                "schtasks /Run sifir olmayan kod dondu ama akis surduruluyor: {Gorev} (kod {CikisKodu}). {Detay}",
                runtime.ServiceName,
                result.ExitCode,
                Detay(result));
        }

        // SONUCU ÖLÇ: "schtasks /Run" görevi TETİKLEDİĞİNİ söyler, sürecin ayakta kaldığını değil.
        // Tek dürüst kanıt, önbelleğin kendi portundan yanıt vermesidir.
        if (await WaitForLivenessAsync(descriptor, expectedAlive: true, cancellationToken))
        {
            logger?.LogInformation(
                "{Bilesen} zamanlanmis gorevle baslatildi ve 127.0.0.1:{Port} yanit veriyor: {Gorev}",
                descriptor.DisplayName,
                descriptor.DefaultPort,
                runtime.ServiceName);
            return await BuildRuntimeAsync(descriptor, cancellationToken);
        }

        logger?.LogError(
            "{Bilesen} baslatilamadi: gorev calistirildi ama 127.0.0.1:{Port} yanit vermiyor. Gorev {Gorev}, kod {CikisKodu}. {Detay}",
            descriptor.DisplayName,
            descriptor.DefaultPort,
            runtime.ServiceName,
            result.ExitCode,
            Detay(result));

        throw new AppException(
            "cache.start_failed",
            500,
            500,
            $"Gorev calistirildi ama {descriptor.DisplayName} 127.0.0.1:{descriptor.DefaultPort} adresinden yanit vermiyor. {Detay(result)}".Trim());
    }

    private async Task<CacheRuntimeDto> StopAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        var runtime = await BuildRuntimeAsync(descriptor, cancellationToken);
        EnsureRuntimeMeasured(runtime);
        return descriptor.RunsAsScheduledTask
            ? await StopScheduledTaskAsync(descriptor, runtime, cancellationToken)
            : await StopWindowsServiceAsync(descriptor, runtime, cancellationToken);
    }

    private void EnsureRuntimeMeasured(CacheRuntimeDto runtime)
    {
        if (!string.Equals(runtime.Status, "Unknown", StringComparison.OrdinalIgnoreCase)) return;
        logger?.LogWarning("{Bilesen} servis/gorev durumu GORUNMUYOR: {Birim}. Olcum yapilamadigi icin islem uygulanmadi.", runtime.DisplayName, runtime.ServiceName);
        throw new AppException("cache.status_unavailable", 503, 503);
    }

    private async Task<CacheRuntimeDto> StopWindowsServiceAsync(CacheDescriptor descriptor, CacheRuntimeDto runtime, CancellationToken cancellationToken)
    {
        if (!runtime.IsServiceRegistered)
        {
            // HİÇBİR ŞEY YAPMADAN "durduruldu" dönen yol. Servis kaydı okunamadıysa (bkz.
            // ReadServiceStatusAsync uyarısı) bileşen ÇALIŞMAYA DEVAM ETTİĞİ hâlde panel yeşil
            // gösterir — memcached'de kanıtlanmış arıza deseni tam olarak buydu.
            logger?.LogWarning(
                "{Bilesen} durdurma istegi ISLEM YAPILMADAN dondu: '{Servis}' servisi kayitli gorunmuyor.",
                descriptor.DisplayName,
                runtime.ServiceName);
            return runtime;
        }

        // -ErrorAction Stop: SilentlyContinue gerçek hatayı (yetki, bağımlı servis) yutuyor,
        // geriye kalan tek ölçüt olan çıkış kodu ise 0 kalıyordu.
        var result = await ProcessRunner.RunPowerShellStrictAsync($"Stop-Service -Name \"{runtime.ServiceName}\" -ErrorAction Stop", cancellationToken);
        if (!result.Success)
        {
            logger?.LogError(
                "{Bilesen} servisi durdurulamadi: {Servis} (cikis kodu {CikisKodu}). {Detay}",
                descriptor.DisplayName,
                runtime.ServiceName,
                result.ExitCode,
                Detay(result));
            throw new AppException("cache.stop_failed", 500, 500, Detay(result));
        }

        logger?.LogInformation("{Bilesen} servisi durduruldu: {Servis}", descriptor.DisplayName, runtime.ServiceName);
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

            // schtasks /End başarısız olsa bile aşağıdaki canlılık ölçümü "sustu" derse kullanıcı
            // "durduruldu" görür. Aracın reddettiği bir komutun izi kalmalı: aynı arıza yarın
            // kalıcı hâle geldiğinde tek başlangıç noktası bu satır olur.
            if (son.ExitCode != 0)
            {
                logger?.LogWarning(
                    "schtasks /End sifir olmayan kod dondu ama akis surduruluyor: {Gorev} (kod {CikisKodu}). {Detay}",
                    runtime.ServiceName,
                    son.ExitCode,
                    detay);
            }
        }
        else
        {
            logger?.LogWarning(
                "{Bilesen} durdurulurken '{Gorev}' zamanlanmis gorevi kayitli GORUNMUYOR; yalnizca surec avina guveniliyor.",
                descriptor.DisplayName,
                runtime.ServiceName);
        }

        if (!await WaitForLivenessAsync(descriptor, expectedAlive: false, cancellationToken))
        {
            // KISMİ BAŞARI: görev bitti ama süreç yaşıyor. Kullanıcı sonunda "Durduruldu" görecek;
            // hangi kademenin işe yaradığı yalnız burada kayıtlı.
            logger?.LogWarning(
                "{Bilesen} gorev sonlandirildiktan sonra hala 127.0.0.1:{Port} adresinden yanit veriyor; surecler oldurulmeye gecildi.",
                descriptor.DisplayName,
                descriptor.DefaultPort);

            // Görev bittiği hâlde süreç yaşıyor: sarmalayıcı .cmd'nin altındaki memcached.exe.
            // Yol karşılaştırması ORTAK primitiften geliyor (nginx'te bu karşılaştırma elle
            // yazılmıştı ve ayırıcı yönü yüzünden hiç eşleşmiyordu).
            if (!string.IsNullOrWhiteSpace(runtime.InstallDir))
            {
                var oldurulen = ProcessRunner.KillProcessesUnder(
                    runtime.InstallDir,
                    Path.GetFileNameWithoutExtension(descriptor.ExecutableName));
                detaylar.Add($"{oldurulen} surec oldurulmeye calisildi.");
                logger?.LogWarning(
                    "{Bilesen}: {Sayi} surec {Dizin} altindan oldurulmeye calisildi.",
                    descriptor.DisplayName,
                    oldurulen,
                    runtime.InstallDir);
            }
            else
            {
                // Kurulum dizini bilinmiyorsa ikinci kademe HİÇ ÇALIŞMAZ; aşağıdaki ölçüm yine de
                // "sustu" derse bileşen kendiliğinden durmuş demektir, biz durdurmuş olmayız.
                logger?.LogWarning(
                    "{Bilesen}: kurulum dizini bilinmiyor, surec oldurme kademesi ATLANDI.",
                    descriptor.DisplayName);
            }

            if (!await WaitForLivenessAsync(descriptor, expectedAlive: false, cancellationToken))
            {
                logger?.LogError(
                    "{Bilesen} durdurulamadi: 127.0.0.1:{Port} hala yanit veriyor. {Detay}",
                    descriptor.DisplayName,
                    descriptor.DefaultPort,
                    string.Join(" ", detaylar));

                throw new AppException(
                    "cache.stop_failed",
                    500,
                    500,
                    $"{descriptor.DisplayName} durdurulamadi: 127.0.0.1:{descriptor.DefaultPort} hala yanit veriyor. {string.Join(" ", detaylar)}".Trim());
            }
        }

        logger?.LogInformation(
            "{Bilesen} durduruldu ve 127.0.0.1:{Port} artik yanit vermiyor.",
            descriptor.DisplayName,
            descriptor.DefaultPort);
        return await BuildRuntimeAsync(descriptor, cancellationToken);
    }

    /// <summary>
    /// Bileşen kendi portundan yanıt verene (ya da susana) kadar bekler; beklenen duruma ulaşıldıysa
    /// true döner. Süreç başlatma/durdurma ANINDA gerçekleşmez — tek ölçüm yanıltıcıdır.
    /// </summary>
    private async Task<bool> WaitForLivenessAsync(CacheDescriptor descriptor, bool expectedAlive, CancellationToken cancellationToken)
    {
        for (var deneme = 0; deneme < 20; deneme++)
        {
            if (await IsAliveAsync(descriptor, cancellationToken) is bool measuredAlive && measuredAlive == expectedAlive)
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return await IsAliveAsync(descriptor, cancellationToken) is bool finalAlive && finalAlive == expectedAlive;
    }

    /// <summary>
    /// Bileşen GERÇEKTEN hizmet veriyor mu? Servis/görev kaydı değil, PORTUN kendisi sorulur.
    /// memcached'de <c>version\r\n</c> komutu <c>VERSION x.y.z</c> döner (kurulum doğrulaması da
    /// aynı sondayı kullanıyor); Redis'te RESP <c>INFO</c> yanıtı yeterlidir.
    /// </summary>
    private async Task<bool?> IsAliveAsync(CacheDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.ComponentId == "memcached")
        {
            var response = await (InspectCachePortAsync ?? ProbeTextAsync)(descriptor.DefaultPort, "version\r\n", cancellationToken);
            if (response is null) return null;
            if (response.Length == 0) return false;
            return response.StartsWith("VERSION ", StringComparison.OrdinalIgnoreCase) ? true : null;
        }

        var info = await (InspectCachePortAsync ?? ProbeTextAsync)(descriptor.DefaultPort, "PING\r\n", cancellationToken);
        return info is null ? null : info.Length > 0;
    }

    /// <summary>
    /// Localhost'ta tek komut gönderip ilk yanıtı okur. Reddedilen bağlantı boş dizge,
    /// ölçüm hatası veya protokol yanıtı olmadan kapanan bağlantı null döndürür.
    /// </summary>
    /// <remarks>
    /// GÜRÜLTÜ AYRIMI: "bağlantı reddedildi" bileşen dururken BEKLENEN yanıttır ve saniyede
    /// birkaç kez oluşabilir — Debug'a yazılır. Onun DIŞINDAKİ her hata (zaman aşımı, akış hatası,
    /// yetki) ölçümün kendisinin başarısız olduğu anlamına gelir: 127.0.0.1'de bir şey DİNLİYOR ama
    /// konuşmuyor. Bu durumda "Durduruldu" sonucuna varılmaz; ölçüm bilinmiyor olarak kalır.
    /// </remarks>
    private async Task<string?> ProbeTextAsync(int port, string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Windows can take just over two seconds to report ConnectionRefused
            // on loopback. Let that measured result arrive before timing out.
            cts.CancelAfter(4000);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            cts.CancelAfter(2000);

            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(command), cts.Token);

            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read <= 0) return null;
            var response = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            return response.Length == 0 ? null : response;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            logger?.LogDebug("127.0.0.1:{Port} baglanti reddetti (bilesen durmus kabul ediliyor).", port);
            return string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning(
                exception,
                "127.0.0.1:{Port} sondasi OLCUM YAPAMADI; calisma durumu bilinmiyor.",
                port);
            return null;
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

        // KAYIT ile DİSK ÇELİŞİYOR: bileşen "Installed" görünüyor ama dizini/çalıştırılabiliri yok.
        // Panel bileşeni yine KURULU gösterir (IsInstalled kayda bakar); kullanıcı "Başlat"a
        // bastığında anlamsız bir 404 alır. Çelişkinin sunucuda izi olmalı.
        if (installedComponent is not null && executablePath.Length == 0)
        {
            logger?.LogWarning(
                "{Bilesen} kayitta 'kurulu' ama {Dosya} diskte bulunamadi (dizin: {Dizin}). Panel kurulu gosterecek, baslatma basarisiz olacak.",
                descriptor.DisplayName,
                descriptor.ExecutableName,
                string.IsNullOrWhiteSpace(installDir) ? "(kayitli degil)" : installDir);
        }

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

        bool? registered = descriptor.RunsAsScheduledTask
            ? await ScheduledTaskExistsAsync(unitName, cancellationToken)
            : serviceStatus == "Unknown" ? null : serviceStatus is not null;

        // Zamanlanmış görevde tek dürüst "çalışıyor" kanıtı portun yanıt vermesidir: görev
        // "Running" görünüp sarmalayıcı .cmd'nin altındaki süreç çoktan ölmüş olabilir.
        bool? alive = descriptor.RunsAsScheduledTask && installedComponent is not null
            ? await IsAliveAsync(descriptor, cancellationToken)
            : string.Equals(serviceStatus, "Running", StringComparison.OrdinalIgnoreCase);

        // Redis çalışıyorsa canlı metrikleri (RESP INFO) oku — salt-okunur, best-effort.
        CacheMetricsDto? metrics = null;
        if (descriptor.ComponentId == "redis" && alive == true)
        {
            metrics = await ReadRedisInfoAsync(descriptor.DefaultPort, cancellationToken);
        }

        var status = installedComponent is null
            ? "NotInstalled"
            : descriptor.RunsAsScheduledTask
                ? alive == true ? "Running" : alive == false && registered.HasValue ? "Stopped" : "Unknown"
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
            IsServiceRegistered = registered == true,
            Status = status,
            Metrics = metrics
        };
    }

    /// <summary>
    /// Zamanlanmış görev kayıtlı mı? (memcached bir Windows SERVİSİ değildir; kurulum onu ONSTART
    /// görev olarak kaydeder.)
    /// </summary>
    /// <remarks>
    /// null means the inspection failed; false means the task list was read and the task
    /// really was absent. A failed query must not become a reinstall recommendation.
    /// </remarks>
    private async Task<bool?> ScheduledTaskExistsAsync(string taskName, CancellationToken cancellationToken)
    {
        try
        {
            // Enumerate first so a genuinely missing task is a successful empty result,
            // while permission/module/CIM failures still terminate the inspection.
            var result = await InspectPowerShellAsync(
                $"$task = Get-ScheduledTask -ErrorAction Stop | Where-Object {{ $_.TaskName -eq '{taskName.Replace("'", "''")}' }} | Select-Object -First 1\n[bool]($null -ne $task)",
                cancellationToken);
            if (!result.Success || !string.IsNullOrWhiteSpace(result.StandardError)) throw new IOException(Detay(result));
            if (!bool.TryParse(result.StandardOutput.Trim(), out var registered)) throw new InvalidDataException("Task inspection returned no valid boolean result.");
            return registered;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning(exception, "Zamanlanmis gorev sorgusu OKUNAMADI: {Gorev}. Kayit durumu bilinmiyor.", taskName);
            return null;
        }
    }

    // Redis'e RESP ile bağlanıp INFO komutunu çalıştırır, metrikleri ayrıştırır. Şifre korumalıysa (NOAUTH)
    // veya erişilemezse null döner (panel kendi AdaRedis'inde parola yok). Sadece localhost, 2.5 sn timeout.
    //
    // SIR SIZINTISI KORUMASI: INFO yanıtının GÖVDESİ günlüğe HİÇ yazılmaz. Yanıt, panelin kendi
    // Redis'inde masum olsa da kullanıcının kendi yapılandırdığı bir örnekte çalıştırılan komutları
    // ve bağlantı adreslerini içerebilir. Günlüğe yalnız "okunamadı" bilgisi ve port düşer.
    private async Task<CacheMetricsDto?> ReadRedisInfoAsync(int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(2500);
            using var client = new TcpClient();
            Stream connectedStream;
            if (OpenRedisInfoStreamAsync is { } openStream)
            {
                connectedStream = await openStream(port, cts.Token);
            }
            else
            {
                await client.ConnectAsync("127.0.0.1", port, cts.Token);
                connectedStream = client.GetStream();
            }
            await using var stream = connectedStream;
            var command = "INFO\r\n"u8.ToArray();
            await stream.WriteAsync(command, cts.Token);

            var header = await ReadRedisReplyHeaderAsync(stream, cts.Token);
            if (header is not null && (header.Equals("-NOAUTH", StringComparison.OrdinalIgnoreCase) ||
                                      header.StartsWith("-NOAUTH ", StringComparison.OrdinalIgnoreCase)))
            {
                // Metrik kartı BOŞ görünür ve kullanıcı sebebini hiçbir yerde göremezdi.
                // Bu bir arıza değil, yapılandırma gerçeği — ama söylenmeyen bir gerçek.
                logger?.LogInformation(
                    "Redis metrikleri okunamadi: 127.0.0.1:{Port} kimlik dogrulamasi istiyor (NOAUTH).",
                    port);
                return null;
            }

            // INFO is one RESP bulk string. Its size counts bytes, not characters;
            // a TCP packet boundary says nothing about whether all metrics arrived.
            if (header is null || !header.StartsWith('$') ||
                !int.TryParse(header.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length is <= 0 or > 131072)
            {
                return null;
            }

            var body = new byte[length];
            await stream.ReadExactlyAsync(body, cts.Token);
            var trailer = new byte[2];
            await stream.ReadExactlyAsync(trailer, cts.Token);
            if (trailer[0] != '\r' || trailer[1] != '\n') return null;

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
            cancellationToken.ThrowIfCancellationRequested();
            return ParseRedisInfo(text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Redis CANLI ölçülmüşken INFO'nun patlaması olağan değildir; metrikler sessizce
            // kaybolur ve panel "sorun yok" görüntüsü verir.
            logger?.LogWarning(exception, "Redis INFO okunamadi: 127.0.0.1:{Port}. Metrikler gosterilmeyecek.", port);
            return null;
        }
    }

    private static async Task<string?> ReadRedisReplyHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var header = new StringBuilder();
        // Bound even an error reply without ever logging its potentially private text.
        while (header.Length < 512)
        {
            if (await stream.ReadAsync(buffer, cancellationToken) == 0) return null;
            if (buffer[0] == '\r')
            {
                if (await stream.ReadAsync(buffer, cancellationToken) == 0 || buffer[0] != '\n') return null;
                return header.ToString();
            }
            if (buffer[0] == '\n' || buffer[0] > 127) return null;
            header.Append((char)buffer[0]);
        }
        return null;
    }

    private static CacheMetricsDto? ParseRedisInfo(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metricKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "redis_version", "used_memory_human", "connected_clients", "keyspace_hits", "keyspace_misses", "uptime_in_seconds"
        };
        var databases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inKeyspace = false;
        var measuredKeyspace = false;
        long keys = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                inKeyspace = line.Equals("# Keyspace", StringComparison.OrdinalIgnoreCase);
                measuredKeyspace |= inKeyspace;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (metricKeys.Contains(name))
            {
                if (!map.TryAdd(name, value)) return null;
            }
            else if (inKeyspace && Regex.IsMatch(name, "^db[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (!databases.Add(name)) return null;
                string? keyCount = null;
                foreach (var attribute in value.Split(','))
                {
                    var pair = attribute.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (pair.Length != 2 || !pair[0].Equals("keys", StringComparison.OrdinalIgnoreCase)) continue;
                    if (keyCount is not null) return null;
                    keyCount = pair[1];
                }
                if (!TryCount(keyCount, out var dbKeys) || keys > long.MaxValue - dbKeys) return null;
                keys += dbKeys;
            }
        }

        // An empty, measured Keyspace section means zero keys. A missing section,
        // missing field or malformed count is not a measured zero.
        if (!measuredKeyspace ||
            !map.TryGetValue("redis_version", out var version) || string.IsNullOrWhiteSpace(version) ||
            !map.TryGetValue("used_memory_human", out var memory) || string.IsNullOrWhiteSpace(memory) ||
            !int.TryParse(map.GetValueOrDefault("connected_clients"), NumberStyles.None, CultureInfo.InvariantCulture, out var clients) ||
            !TryCount(map.GetValueOrDefault("keyspace_hits"), out var hits) ||
            !TryCount(map.GetValueOrDefault("keyspace_misses"), out var misses) ||
            !TryCount(map.GetValueOrDefault("uptime_in_seconds"), out var uptime)) return null;

        return new CacheMetricsDto
        {
            Version = version,
            UsedMemory = memory,
            ConnectedClients = clients,
            Hits = hits,
            Misses = misses,
            UptimeSeconds = uptime,
            Keys = keys
        };

        static bool TryCount(string? value, out long count)
            => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count);
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

        // NOT: `command` Redis yapılandırma DOSYA YOLUNU içerir, parola içermez (AdaRedis parolasız
        // ve bind 127.0.0.1). Yine de günlüğe komutun tamamı değil yalnız servis adı yazılır.
        var result = await ProcessRunner.RunPowerShellStrictAsync(command, cancellationToken);
        if (!result.Success)
        {
            logger?.LogError(
                "{Bilesen} servis kaydi olusturulamadi: {Servis} (cikis kodu {CikisKodu}). {Detay}",
                descriptor.DisplayName,
                runtime.ServiceName,
                result.ExitCode,
                Detay(result));
            throw new AppException("cache.service_install_failed", 500, 500, Detay(result));
        }

        logger?.LogInformation("{Bilesen} servis kaydi olusturuldu: {Servis}", descriptor.DisplayName, runtime.ServiceName);
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

    /// <remarks>
    /// null means the service was measured absent. Unknown means no trustworthy
    /// measurement was obtained; empty output alone cannot establish absence.
    /// </remarks>
    private async Task<string?> ReadServiceStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await InspectPowerShellAsync(
                $$"""
                $service = Get-Service -ErrorAction Stop | Where-Object { $_.Name -eq '{{serviceName.Replace("'", "''")}}' } | Select-Object -First 1
                if ($null -eq $service) { [pscustomobject]@{ Exists = $false } | ConvertTo-Json -Compress }
                else { [pscustomobject]@{ Status = $service.Status.ToString() } | ConvertTo-Json -Compress }
                """, cancellationToken);
            if (!result.Success || !string.IsNullOrWhiteSpace(result.StandardError)) throw new IOException(Detay(result));

            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Service inspection returned no object.");
            if (root.TryGetProperty("Exists", out var exists) && exists.ValueKind == JsonValueKind.False && !root.TryGetProperty("Status", out _)) return null;
            if (root.TryGetProperty("Status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
            {
                var status = statusElement.GetString();
                if (status is "Running" or "Stopped" or "StartPending" or "StopPending" or "ContinuePending" or "PausePending" or "Paused") return status;
            }
            throw new InvalidDataException("Service inspection returned no recognized state.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning(exception, "Servis durumu OKUNAMADI: {Servis}. Calisma durumu bilinmiyor.", serviceName);
            return "Unknown";
        }
    }

    private sealed record CacheDescriptor(
        string ComponentId,
        string DisplayName,
        string ServiceName,
        int DefaultPort,
        string ExecutableName,
        bool RunsAsScheduledTask);
}
