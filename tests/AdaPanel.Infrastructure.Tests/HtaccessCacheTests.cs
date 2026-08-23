using System.Diagnostics;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ÖNBELLEK + GEÇERSİZLEŞTİRME testleri.
///
/// Gereksinim: "her istekte dosya okumak kabul edilemez" + "dosya değişince fark et".
/// Bu ikisi birbiriyle çelişir; testler dengenin HANGI NOKTADA kurulduğunu sabitler.
/// </summary>
public sealed class HtaccessCacheTests
{
    private sealed class FakeDisk
    {
        internal readonly Dictionary<string, HtaccessFileStamp> Stamps = new(StringComparer.OrdinalIgnoreCase);
        internal int StatCount;

        internal HtaccessFileStamp Read(string path)
        {
            StatCount++;
            return Stamps.TryGetValue(path, out var stamp)
                ? stamp
                : new HtaccessFileStamp { Exists = false, LastWriteUtcTicks = 0, Length = -1 };
        }

        internal void Write(string path, long ticks, long length)
            => Stamps[path] = new HtaccessFileStamp { Exists = true, LastWriteUtcTicks = ticks, Length = length };

        internal void Delete(string path) => Stamps.Remove(path);
    }

    private const string Path1 = @"C:\sites\a\.htaccess";

    private static (HtaccessFileCache<string> Cache, FakeDisk Disk, long[] Clock) Build(
        int maxEntries = 512, double ttlSeconds = 2)
    {
        var disk = new FakeDisk();
        var clock = new long[] { 0 };
        var cache = new HtaccessFileCache<string>(
            maxEntries,
            TimeSpan.FromSeconds(ttlSeconds),
            () => clock[0],
            disk.Read);
        return (cache, disk, clock);
    }

    private static void Advance(long[] clock, double seconds)
        => clock[0] += (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void TTL_ICINDE_DISKE_HIC_GIDILMEZ()
    {
        var (cache, disk, _) = Build();
        disk.Write(Path1, 100, 10);

        var loads = 0;
        for (var i = 0; i < 1000; i++)
        {
            cache.GetOrLoad(Path1, (_, _) => { loads++; return "derlenmis"; });
        }

        Assert.Equal(1, loads);
        Assert.Equal(1, disk.StatCount); // BIR stat, BIN istek
        Assert.Equal(999, cache.Hits);
    }

    [Fact]
    public void YOKLUK_DA_ONBELLEGE_ALINIR()
    {
        // KRITIK: sitelerin cogu dizininde .htaccess YOKTUR. Yokluk onbelleklenmeseydi,
        // .htaccess KULLANMAYAN musteri bile her istekte bir stat oderdi -- yani ozelligi hic
        // kullanmayanlar en cok bedeli odeyen taraf olurdu.
        var (cache, disk, _) = Build();

        var loads = 0;
        for (var i = 0; i < 1000; i++)
        {
            var sonuc = cache.GetOrLoad(Path1, (_, _) => { loads++; return "olmamali"; });
            Assert.Null(sonuc);
        }

        Assert.Equal(0, loads);          // dosya yok -> yukleyici HIC cagrilmadi
        Assert.Equal(1, disk.StatCount); // ve yoklugu ogrenmek icin TEK stat yetti
    }

    [Fact]
    public void TTL_DOLUNCA_DEGISMEYEN_DOSYA_YENIDEN_AYRISTIRILMAZ()
    {
        // TTL dolmasi "yeniden derle" demek DEGIL, "bir daha bak" demektir. Ayrim onemli:
        // buyuk bir .htaccess'i her 2 saniyede bir yeniden ayristirmak bosa CPU olurdu.
        var (cache, disk, clock) = Build();
        disk.Write(Path1, 100, 10);

        var loads = 0;
        for (var i = 0; i < 10; i++)
        {
            cache.GetOrLoad(Path1, (_, _) => { loads++; return "derlenmis"; });
            Advance(clock, 2.1);
        }

        Assert.Equal(1, loads);           // BIR kez derlendi
        Assert.Equal(10, disk.StatCount); // ama 10 kez BAKILDI
        Assert.Equal(10, cache.Revalidations);
    }

    [Fact]
    public void ZAMAN_DAMGASI_DEGISINCE_YENIDEN_DERLENIR()
    {
        var (cache, disk, clock) = Build();
        disk.Write(Path1, 100, 10);

        var ilk = cache.GetOrLoad(Path1, (_, _) => "SURUM-1");
        Assert.Equal("SURUM-1", ilk);

        disk.Write(Path1, 200, 10); // kullanici dosyayi duzenledi
        Advance(clock, 2.1);

        var ikinci = cache.GetOrLoad(Path1, (_, _) => "SURUM-2");
        Assert.Equal("SURUM-2", ikinci);
    }

    [Fact]
    public void BOYUT_DEGISINCE_YENIDEN_DERLENIR()
    {
        // Zaman damgasi cozunurlugu (FAT/ag paylasimlarinda 2 saniye) ayni saniyede yapilan bir
        // duzenlemeyi gizleyebilir. Uzunlugu da karsilastirmak o kor noktayi buyuk olcude kapatir.
        var (cache, disk, clock) = Build();
        disk.Write(Path1, 100, 10);
        cache.GetOrLoad(Path1, (_, _) => "SURUM-1");

        disk.Write(Path1, 100, 25); // AYNI zaman damgasi, FARKLI boyut
        Advance(clock, 2.1);

        Assert.Equal("SURUM-2", cache.GetOrLoad(Path1, (_, _) => "SURUM-2"));
    }

    [Fact]
    public void DOSYA_SILININCE_ONBELLEK_TEMIZLENIR()
    {
        var (cache, disk, clock) = Build();
        disk.Write(Path1, 100, 10);
        Assert.Equal("VAR", cache.GetOrLoad(Path1, (_, _) => "VAR"));

        disk.Delete(Path1);
        Advance(clock, 2.1);

        Assert.Null(cache.GetOrLoad(Path1, (_, _) => "OLMAMALI"));
    }

    [Fact]
    public void PANELDEN_GECERSIZLESTIRME_ANINDA_ETKI_EDER()
    {
        // Kullanici panelden .htaccess'i kaydettiginde TTL beklemesin.
        var (cache, disk, _) = Build();
        disk.Write(Path1, 100, 10);
        cache.GetOrLoad(Path1, (_, _) => "ESKI");

        disk.Write(Path1, 200, 10);
        cache.Invalidate(Path1);

        Assert.Equal("YENI", cache.GetOrLoad(Path1, (_, _) => "YENI"));
    }

    [Fact]
    public void YUKLEYICI_ISTISNASI_YUTULMAZ_CAGIRANA_GECER()
    {
        // Onemli bir tasarim karari: fail-open sarmalayici bu sinifin DISINDA. Burada yutulsaydi
        // bozuk bir .htaccess "bos kural" olarak onbellege girer ve devre kesici HIC tetiklenmezdi
        // -- yani sessizce yanlis davranan bir site elde ederdik.
        var (cache, disk, _) = Build();
        disk.Write(Path1, 100, 10);

        Assert.Throws<InvalidOperationException>(
            () => cache.GetOrLoad(Path1, (_, _) => throw new InvalidOperationException("bozuk")));
    }

    [Fact]
    public void ONBELLEK_SINIRLIDIR_ve_SINIR_ASILINCA_BOSALIR()
    {
        // Binlerce dizinli bir site bellegi sinirsiz yiyemesin.
        var (cache, disk, _) = Build(maxEntries: 10);

        for (var i = 0; i < 100; i++)
        {
            var path = @"C:\sites\a\d" + i + @"\.htaccess";
            disk.Write(path, 1, 1);
            cache.GetOrLoad(path, (_, _) => "x");
        }

        Assert.True(cache.Count <= 10, $"onbellek sinirlanmali, su an {cache.Count}");
        Assert.True(cache.Flushes > 0, "sinir asilinca bosaltma olmali");
    }

    [Fact]
    public void IKI_AYRI_ONBELLEK_DURUM_PAYLASMAZ()
    {
        // Kiraci izolasyonu: onbellek ORNEKLENMISTIR. static olsaydi bir kiracinin .htaccess'i
        // ayni yola sahip baska bir kiracinin sitesinde kullanilabilirdi.
        var diskA = new FakeDisk();
        var diskB = new FakeDisk();
        diskA.Write(Path1, 100, 10);
        diskB.Write(Path1, 100, 10);

        var cacheA = new HtaccessFileCache<string>(512, TimeSpan.FromSeconds(2), () => 0L, diskA.Read);
        var cacheB = new HtaccessFileCache<string>(512, TimeSpan.FromSeconds(2), () => 0L, diskB.Read);

        Assert.Equal("A", cacheA.GetOrLoad(Path1, (_, _) => "A"));
        Assert.Equal("B", cacheB.GetOrLoad(Path1, (_, _) => "B"));
        Assert.Equal("A", cacheA.GetOrLoad(Path1, (_, _) => "DEGISMEMELI"));
    }

    [Fact]
    public void DAMGA_KARSILASTIRMASI_UC_ALANI_DA_KAPSAR()
    {
        var temel = new HtaccessFileStamp { Exists = true, LastWriteUtcTicks = 1, Length = 2 };

        Assert.True(temel.Equals(new HtaccessFileStamp { Exists = true, LastWriteUtcTicks = 1, Length = 2 }));
        Assert.False(temel.Equals(new HtaccessFileStamp { Exists = false, LastWriteUtcTicks = 1, Length = 2 }));
        Assert.False(temel.Equals(new HtaccessFileStamp { Exists = true, LastWriteUtcTicks = 9, Length = 2 }));
        Assert.False(temel.Equals(new HtaccessFileStamp { Exists = true, LastWriteUtcTicks = 1, Length = 9 }));
    }

    [Fact]
    public void GERCEK_DISKTE_DEGISIKLIK_YAKALANIR()
    {
        // Sahte disk degil, GERCEK dosya sistemi: damga okuyucusunun gercekten calistigini
        // gosterir (sahte okuyucuyla yazilan testlerin hepsi bu tek testin uzerine oturuyor).
        var dizin = Directory.CreateTempSubdirectory("adapanel-htaccess-cache");
        try
        {
            var yol = System.IO.Path.Combine(dizin.FullName, ".htaccess");

            var yok = HtaccessFileStamp.Read(yol);
            Assert.False(yok.Exists);

            File.WriteAllText(yol, "RewriteEngine On\n");
            var ilk = HtaccessFileStamp.Read(yol);
            Assert.True(ilk.Exists);
            Assert.True(ilk.Length > 0);

            File.WriteAllText(yol, "RewriteEngine On\nRewriteRule ^a$ /b [L]\n");
            var ikinci = HtaccessFileStamp.Read(yol);

            Assert.False(ilk.Equals(ikinci));
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }
}
