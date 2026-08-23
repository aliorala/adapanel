using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>
/// Bir <c>.htaccess</c> dosyasının diskteki KİMLİĞİ. Üç alanın birlikte karşılaştırılması,
/// "dosya değişti mi" sorusunu TEK bir dosya sistemi çağrısıyla cevaplar.
/// </summary>
internal struct HtaccessFileStamp : IEquatable<HtaccessFileStamp>
{
    internal bool Exists;
    internal long LastWriteUtcTicks;
    internal long Length;

    public bool Equals(HtaccessFileStamp other)
        => Exists == other.Exists &&
           LastWriteUtcTicks == other.LastWriteUtcTicks &&
           Length == other.Length;

    public override bool Equals(object? obj) => obj is HtaccessFileStamp other && Equals(other);

    // HashCode.Combine net48'de YOK -> elle. (Bu dosya net48'e de derleniyor.)
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + Exists.GetHashCode();
            hash = (hash * 31) + LastWriteUtcTicks.GetHashCode();
            hash = (hash * 31) + Length.GetHashCode();
            return hash;
        }
    }

    /// <summary>
    /// Diskten TEK stat çağrısıyla damga alır.
    ///
    /// ÖLÇÜLDÜ (bu geliştirme makinesi, 200.000 yineleme):
    ///   File.Exists .................... 14,1 us
    ///   File.GetLastWriteTimeUtc ....... 13,5 us
    ///   FileInfo (Exists+mtime+Length).. 13,7 us   <-- ÜÇ BİLGİ, BİR ÇAĞRI FİYATINA
    /// Yani ayrı ayrı çağırmak üç katı maliyet demekti. FileInfo alanları ilk erişimde tek bir
    /// stat'tan doldurulup önbelleklenir; bu yüzden burada FileInfo kullanılıyor.
    /// </summary>
    internal static HtaccessFileStamp Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new HtaccessFileStamp { Exists = false, LastWriteUtcTicks = 0, Length = -1 };
        }

        return new HtaccessFileStamp
        {
            Exists = true,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            Length = info.Length
        };
    }
}

/// <summary>
/// <c>.htaccess</c> ÖNBELLEĞİ — "her istekte dosya okumak kabul edilemez" gereksinimini karşılar.
///
/// <b>NEDEN FileSystemWatcher DEĞİL — ölçüldü ve belgelendi, sonra ELENDİ:</b>
///
/// Ölçüm (bu makine, 200 izleyici):
///   olusturma ......... 188 us/izleyici        (baslangicta, onemsiz)
///   yonetilen bellek .. 9.638 bayt/izleyici
///   OS handle ......... 1,03 handle/izleyici
///   olay gecikmesi .... 2,6 ms                 (hizli)
///
/// Belge (FileSystemWatcher.InternalBufferSize, Microsoft Learn): varsayilan tampon 8 KB'dir,
/// "If there are many changes in a short time, the buffer can overflow. This causes the component
/// to lose track of changes in the directory" ve tamponu buyutmek "comes from non-paged memory
/// that cannot be swapped out to disk".
///
/// Bu ikisi birlikte izleyiciyi ELİYOR — ve eleyen şey performans değil, DOĞRULUK:
///  1) .htaccess Apache'de DİZİN BAŞINADIR. 500 dizinli bir sitede dizin başına izleyici
///     ~4,8 MB + 500 handle eder; sunucuda 200 site varsa bu tek başına 1 GB'a koşar.
///  2) Site kökünde TEK özyinelemeli izleyici alternatifi kaynağı çözer ama tamponu çözmez:
///     müşteri bir WordPress arşivini açtığında (binlerce dosya olayı, saniyeler içinde) tampon
///     TAŞAR ve olaylar KAYBOLUR. Kaybolan olay = SONSUZA KADAR BAYAT ÖNBELLEK. Kullanıcı
///     .htaccess'ini düzeltir, panel "kaydedildi" der, site eski kuralla çalışmaya devam eder.
///     Teşhisi imkansıza yakın, sessiz bir yalan olurdu.
///  3) Taşan tampon non-paged havuzdan yer yiyor: bir kiracının dosya fırtınası SUNUCUNUN TAMAMINI
///     etkileyebilecek tek kaynak burasıydı — yani izleyici, izolasyonu da deler.
///
/// <b>SEÇİLEN: TTL'li zaman damgası yoklaması.</b> Kaçırılan bir değişiklik en geç TTL kadar sonra
/// KENDİLİĞİNDEN düzelir (kendini iyileştirir), çekirdek kaynağı tüketmez, sınırlıdır.
/// Amortize maliyet: TTL=2sn ve 1.000 istek/sn'de istek başına 13,7us/2000 = <b>~7 ns</b> —
/// yani sözlük aramasının (48,7 ns) altında.
///
/// <b>ÖRNEKLENMİŞ, static DEĞİL:</b> her uygulama havuzu kendi AppDomain'inde kendi örneğini kurar.
/// Buraya static bir alan konulsaydı kiracılar arası doğal izolasyon KIRILIRDI.
/// </summary>
internal sealed class HtaccessFileCache<TValue> where TValue : class
{
    private sealed class Entry
    {
        internal HtaccessFileStamp Stamp;
        internal TValue? Value;
        internal long NextCheckTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> entries =
        new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    private readonly int maxEntries;
    private readonly long revalidateTicks;
    private readonly Func<long> monotonicTicks;
    private readonly Func<string, HtaccessFileStamp> stampReader;

    /// <summary>Teşhis sayaçları. Panelin "önbellek çalışıyor mu" sorusuna ölçülmüş cevap verir.</summary>
    internal long Hits;
    internal long Revalidations;
    internal long Loads;
    internal long Flushes;

    internal HtaccessFileCache(
        int maxEntries = 512,
        TimeSpan? revalidateAfter = null,
        Func<long>? monotonicTicks = null,
        Func<string, HtaccessFileStamp>? stampReader = null)
    {
        this.maxEntries = maxEntries < 1 ? 1 : maxEntries;
        this.monotonicTicks = monotonicTicks ?? Stopwatch.GetTimestamp;
        this.stampReader = stampReader ?? HtaccessFileStamp.Read;

        var ttl = revalidateAfter ?? TimeSpan.FromSeconds(2);
        var ticks = (long)(ttl.TotalSeconds * Stopwatch.Frequency);
        this.revalidateTicks = ticks < 0 ? 0 : ticks;
    }

    internal int Count => entries.Count;

    /// <summary>
    /// Yolun derlenmiş karşılığını döner. Dosya yoksa <c>null</c> döner ve <b>YOKLUK DA ÖNBELLEĞE
    /// ALINIR</b>.
    ///
    /// NEDEN yokluk önbelleği KRİTİK: sitelerin ezici çoğunluğunun çoğu dizininde .htaccess YOKTUR.
    /// Yokluk önbelleklenmeseydi, .htaccess KULLANMAYAN bir müşteri bile her istekte bir stat
    /// (~13,7 us) öderdi — yani modülü hiç kullanmayanlar en çok bedeli ödeyen taraf olurdu.
    ///
    /// <paramref name="loader"/> yalnızca dosya GERÇEKTEN değiştiğinde (ya da ilk kez görüldüğünde)
    /// çağrılır. Fırlatırsa istisna çağırana GEÇER — fail-open sarmalayıcısı bu sınıfın DIŞINDADIR
    /// (bkz. <see cref="HtaccessFailSafe"/>); burada yutulsaydı bozuk bir dosya "boş kural" gibi
    /// önbelleğe girer ve devre kesici hiç tetiklenmezdi.
    /// </summary>
    internal TValue? GetOrLoad(string path, Func<string, HtaccessFileStamp, TValue?> loader)
    {
        var now = monotonicTicks();

        if (entries.TryGetValue(path, out var entry) && now < entry.NextCheckTicks)
        {
            Hits++;
            return entry.Value;
        }

        // TTL doldu (ya da hiç görülmedi): TEK stat ile gerçekten değişmiş mi bak.
        Revalidations++;
        var stamp = stampReader(path);

        if (entry != null && entry.Stamp.Equals(stamp))
        {
            // Dosya AYNI. Yeniden ayrıştırma YOK; yalnızca pencere uzatılır.
            entry.NextCheckTicks = now + revalidateTicks;
            return entry.Value;
        }

        Loads++;
        var value = stamp.Exists ? loader(path, stamp) : null;

        // Kapasite aşıldıysa önbellek KOMPLE boşaltılır. Neden komple: LRU tutmak her isteğe
        // sıralama maliyeti eklerdi ve bu yol zaten nadir. Boşaltmanın etki alanı YALNIZCA bu
        // uygulama havuzu (yani o kiracının kendi sitesi) — başka kiracıya sıçramaz.
        if (entries.Count >= maxEntries && !entries.ContainsKey(path))
        {
            entries.Clear();
            Flushes++;
        }

        entries[path] = new Entry
        {
            Stamp = stamp,
            Value = value,
            NextCheckTicks = now + revalidateTicks
        };

        return value;
    }

    /// <summary>Panelin "bu siteyi hemen tazele" diyebilmesi için (kullanıcı panelden kaydedince).</summary>
    internal void Invalidate(string path) => entries.TryRemove(path, out _);

    internal void Clear() => entries.Clear();
}
