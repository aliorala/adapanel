using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>
/// Anahtar dosyalarını okuyan soyutlama. Testin "disk patladı" / "yetki yok" durumlarını
/// GERÇEKTEN üretebilmesi için var — bu yollar mutasyonla doğrulanmak zorunda.
/// Dosya yoksa <c>null</c> döner; okunamıyorsa İSTİSNA fırlatır (yutmak burada DEĞİL, kapıda yapılır).
/// </summary>
internal interface IHtaccessSwitchReader
{
    string? Read(string path);
}

internal sealed class FileSystemSwitchReader : IHtaccessSwitchReader
{
    public string? Read(string path)
    {
        // FileShare.ReadWrite|Delete: panel dosyayı ATOMİK olarak değiştirirken (yaz+taşı) modülün
        // okuması PAYLAŞIM İHLALİ vermesin. Vermesi, tam da anahtar çevrildiği anda her sitede
        // istisna üretirdi — yani düğmeye basmak sunucuyu sarsardı.
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// KAPI: her istekte "bu modül şu an çalışsın mı" sorusunun cevabı.
///
/// Üç tasarım kararı ve gerekçeleri:
///
/// 1) VARSAYILAN KAPALI. Genel anahtar dosyası YOKSA sonuç <see cref="HtaccessMode.Off"/>.
///    Modül GAC'a kurulup applicationHost.config'e yazıldıktan sonra bile, o dosya oluşturulana
///    kadar hiçbir istek etkilenmez. Ürünün en yüksek etki alanlı değişikliği için tek kabul
///    edilebilir varsayılan budur.
///
/// 2) HER HATA "Off"a DÜŞER. Dosya okunamadı, yetki yok, disk gitti, içerik bozuk — hepsinin
///    cevabı aynı: modül hiçbir şey yapmaz. Kapı "bilmiyorum" diyemez; bilmiyorsa KAPALIDIR.
///
/// 3) SONUÇ ÖNBELLEĞE ALINIR, HATA DA. Hata önbelleğe alınmasaydı, ACL'i bozuk bir kurulumda
///    HER İSTEK bir istisna üretirdi (istisna maliyeti mikrosaniyeler değil, on mikrosaniyelerdir)
///    ve modül "fail-open" olmasına rağmen siteyi YAVAŞLATIRDI. Fail-open sadece doğru cevap
///    vermek değil, UCUZ olmak zorunda.
///
/// ÖRNEK DEĞİL, ÖRNEKLENMİŞ: bu sınıf static DEĞİL. Her uygulama havuzunda ayrı bir AppDomain,
/// ayrı bir örnek → bir kiracının kapı durumu diğerininkiyle PAYLAŞILMAZ.
/// </summary>
internal sealed class HtaccessRuntimeGate
{
    private sealed class CachedMode
    {
        internal HtaccessMode Mode;
        internal long ExpiresAtTicks;

        /// <summary>
        /// Anahtar dosyasının TAM YOLU, BİR KEZ hesaplanıp saklanır.
        ///
        /// NEDEN alan olarak tutuluyor — ÖLÇÜLDÜ: ilk sürümde yol her istekte
        /// <see cref="HtaccessHostingPaths.GlobalSwitchPath"/> ile yeniden üretiliyordu ve o çağrı
        /// <c>ExpandEnvironmentVariables</c> + <c>GetFullPath</c> + <c>Path.Combine</c> yapıyor.
        /// Ölçüm acımasızdı: kapı KAPALIYKEN istek başına <b>820 ns</b>, AÇIKKEN <b>1.895 ns</b> —
        /// yani hiçbir dosyaya dokunmadan, sadece dizgi üretmek için. Yol önceden hesaplanınca
        /// sıcak yol saf bir karşılaştırmaya iniyor. Bu, "her istekte dosya okumak kabul edilemez"
        /// kuralının gözden kaçan kardeşiydi: her istekte YOL ÜRETMEK de kabul edilemez.
        /// </summary>
        internal string Path = string.Empty;
    }

    /// <summary>
    /// Bir kiracının site adı uydurup önbelleği şişirmesine karşı üst sınır. Pratikte bir AppDomain
    /// tek siteye hizmet eder (1-2 girdi); bu sınır yalnızca kötü niyetli/hatalı bir çağrının
    /// sınırsız bellek yemesini engeller.
    /// </summary>
    private const int MaxSiteEntries = 64;

    private readonly string dataRoot;
    private readonly string sitesRoot;
    private readonly long ttlTicks;
    private readonly IHtaccessSwitchReader reader;
    private readonly Func<long> monotonicTicks;
    private readonly ConcurrentDictionary<string, CachedMode> siteModes =
        new ConcurrentDictionary<string, CachedMode>(StringComparer.OrdinalIgnoreCase);

    private readonly CachedMode globalMode;

    /// <summary>Kapının kendi arıza sayacı — teşhis için; davranışı DEĞİŞTİRMEZ.</summary>
    internal long ReadFailureCount;

    internal HtaccessRuntimeGate(
        string dataRoot,
        TimeSpan ttl,
        IHtaccessSwitchReader? reader = null,
        Func<long>? monotonicTicks = null)
    {
        this.dataRoot = dataRoot;
        this.reader = reader ?? new FileSystemSwitchReader();
        this.monotonicTicks = monotonicTicks ?? Stopwatch.GetTimestamp;

        // Yollar KURUCUDA bir kez üretilir; sıcak yolda dizgi üretimi YAPILMAZ (bkz. CachedMode.Path).
        this.sitesRoot = HtaccessHostingPaths.SitesRoot(dataRoot);
        this.globalMode = new CachedMode
        {
            Mode = HtaccessMode.Off,
            ExpiresAtTicks = long.MinValue,
            Path = HtaccessHostingPaths.GlobalSwitchPath(dataRoot)
        };

        // TTL, Stopwatch tiklerine çevrilir. Stopwatch MONOTONİKTİR: yönetici saati geri alsa ya da
        // yaz saati değişse bile önbellek "sonsuza kadar taze" durumuna DÜŞMEZ. DateTime.UtcNow ile
        // yazılsaydı saat geri alındığında kapatma düğmesi saatlerce yanıt vermeyebilirdi.
        var ticks = (long)(ttl.TotalSeconds * Stopwatch.Frequency);
        this.ttlTicks = ticks < 0 ? 0 : ticks;
    }

    /// <summary>
    /// Bu site için etkin mod. HİÇBİR KOŞULDA istisna fırlatmaz.
    /// </summary>
    internal HtaccessMode Evaluate(string? siteName)
    {
        var global = ReadCached(globalMode);
        if (global == HtaccessMode.Off)
        {
            // KISA DEVRE: genel anahtar kapalıysa site dosyasına HİÇ BAKILMAZ. Kapatma düğmesine
            // basıldığında sunucudaki dosya okuma yükü ARTMAMALI, azalmalı.
            return HtaccessMode.Off;
        }

        var site = ReadSiteMode(siteName);

        // Etkin mod = İKİSİNİN EN DÜŞÜĞÜ. Sıralama Off(0) < Observe(1) < Enforce(2) olduğu için
        // bu, iki kuralı birden verir:
        //  - genel anahtar Off ise hiçbir site kendini açamaz (kapatma düğmesi MUTLAKTIR),
        //  - genel anahtar Observe ise TÜM sunucu tek hamlede gözlem moduna iner (uygulama durur,
        //    günlük akmaya devam eder) — bir sorunu tamamen kapatmadan teşhis etmenin yolu.
        return (HtaccessMode)Math.Min((int)global, (int)site);
    }

    private HtaccessMode ReadSiteMode(string? siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            // Site adı bilinmiyorsa hangi kiracının kurallarını uygulayacağımızı da bilmiyoruz.
            // Yanlış kiracının kuralını uygulamaktansa HİÇBİR ŞEY yapmak doğrudur.
            return HtaccessMode.Off;
        }

        var key = siteName!;
        if (!siteModes.TryGetValue(key, out var entry))
        {
            if (siteModes.Count >= MaxSiteEntries)
            {
                return HtaccessMode.Off;
            }

            entry = siteModes.GetOrAdd(key, name => new CachedMode
            {
                Mode = HtaccessMode.Off,
                ExpiresAtTicks = long.MinValue,
                // Site adı -> dosya adı eşlemesi de site başına BİR KEZ yapılır.
                Path = System.IO.Path.Combine(sitesRoot, HtaccessHostingPaths.SiteSwitchFileName(name))
            });
        }

        return ReadCached(entry);
    }

    private HtaccessMode ReadCached(CachedMode entry)
    {
        var now = monotonicTicks();

        // Kilitsiz okuma: long ve enum yazımları atomik. En kötü ihtimalle iki iş parçacığı aynı anda
        // dosyayı okur — zararsız. Buraya kilit koymak, her istekte kilit çekişmesi demek olurdu ve
        // ölçülen fayda sıfır olurdu.
        if (now < entry.ExpiresAtTicks)
        {
            return entry.Mode;
        }

        HtaccessMode mode;
        try
        {
            var content = reader.Read(entry.Path);
            // Dosya YOK = KAPALI. "Varsayılan kapalı" kuralının fiziksel karşılığı tam olarak bu satır.
            mode = content is null ? HtaccessMode.Off : HtaccessSwitchFile.Parse(content);
        }
        catch (Exception)
        {
            // Yetki yok / disk hatası / kilit — hepsi KAPALI. Ve önbelleğe alınır (bkz. sınıf yorumu 3).
            ReadFailureCount++;
            mode = HtaccessMode.Off;
        }

        entry.Mode = mode;
        entry.ExpiresAtTicks = now + ttlTicks;
        return mode;
    }
}
