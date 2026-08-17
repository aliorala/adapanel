using System.Globalization;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Bir yedeğin YERİ: hangi gün klasörü, hangi kategori. Yerel diskteki klasör ile UZAK hedefteki
/// yol AYNI karardan türetilir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu tip var (tek kaynak):</b> 0.5.5'te gün klasörü YALNIZCA yerel yola eklendi. Uzak
/// yükleme dosya adından başka bir şey bilmediği için FTP/S3/WebDAV hedefinde klasör yapısı hiç
/// oluşmadı; bütün yedekler kökün altına düz bir yığın hâlinde indi. Kararı iki yerde ayrı ayrı
/// hesaplamanın ikinci tuzağı GECE YARISI'dır: yedek 23:59'da başlayıp 00:01'de yüklenirse yerel
/// klasör bir güne, uzak klasör başka bir güne düşer — aynı yedeğin iki kopyası farklı günlerde
/// görünür ve "dün ne aldık" sorusu bir daha güvenilir cevaplanamaz. Bu yüzden karar bir kez
/// verilir ve <see cref="BackupPlacement"/> içinde yükleme adımına kadar TAŞINIR.
/// </para>
/// </remarks>
internal sealed record BackupPlacement(string Day, string Category, string LocalDirectory)
{
    /// <summary>
    /// Uzak hedefte taban adresin ALTINA açılacak klasör seviyeleri, sırayla.
    /// </summary>
    /// <remarks>
    /// Yerel yol ile aynı sırayı verir (<c>&lt;gün&gt;/&lt;kategori&gt;</c>); FTP'de MKD, WebDAV'da
    /// MKCOL bu sırayla YUKARIDAN AŞAĞIYA uygulanmalıdır — ara seviye yokken alt seviyeyi açmak
    /// her iki protokolde de hata verir.
    /// </remarks>
    public string[] RemoteFolders => [Day, Category];
}

/// <summary>
/// Yedek klasörü kararının TEK kaynağı: <c>&lt;kök&gt;\&lt;GÜN&gt;\&lt;kategori&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gün klasörü:</b> önceden bütün yedekler <c>backups\sites\</c> gibi tek bir klasöre
/// yığılıyordu. Aylık yedeği olan bir sunucuda o klasör yüzlerce dosya oluyor, "dün gece ne
/// alındı" sorusu ancak dosya adındaki zaman damgasını okuyarak cevaplanabiliyordu. Günlük
/// klasör, hem panelden hem FTP'den hem de doğrudan diskten bakan biri için tarihi ilk bakışta
/// görünür kılar ve bir günün tamamını taşımayı/silmeyi tek işleme indirir.
/// </para>
/// <para>
/// <b>Neden yerel tarih, UTC değil:</b> klasör adı İNSAN içindir. Yedek 03:00'te alınıyorsa
/// yöneticinin takvimindeki gün neyse o klasöre girmeli; UTC kullanmak Türkiye'de gece yarısından
/// sonra alınan yedeği "bir önceki gün" klasörüne düşürürdü. Dosya adındaki zaman damgası UTC
/// olarak KALIR (sıralama ve çakışma için) — ikisi farklı işler.
/// </para>
/// <para>
/// <b>Geriye dönük:</b> eski yedeklerin yerel yolu kayıtta (<c>record.Path</c>), uzak yolu ise
/// <c>record.RemoteUri</c> içinde TAM olarak saklandığı için listeleme, geri yükleme ve saklama
/// temizliği eski düzendeki (kökün altında düz duran) dosyaları bulmaya devam eder. Yeni düzen
/// yalnızca YENİ yedekler için geçerlidir; hiçbir yerde yol YENİDEN HESAPLANMAZ.
/// </para>
/// </remarks>
internal static class BackupLayout
{
    internal const string DayFormat = "yyyy-MM-dd";

    /// <summary>
    /// Kararı verir ama diske DOKUNMAZ (ölçülebilir/testlenebilir olması için ayrıldı).
    /// </summary>
    internal static BackupPlacement Describe(string root, string category, DateTime localNow)
    {
        var gun = localNow.ToString(DayFormat, CultureInfo.InvariantCulture);
        var kategori = NormalizeCategory(category);
        var kok = Environment.ExpandEnvironmentVariables(root ?? string.Empty);
        return new BackupPlacement(gun, kategori, Path.Combine(kok, gun, kategori));
    }

    /// <summary>
    /// Yalnızca UZAK yol için yer kararı (yerel klasör yok, diske dokunulmaz): bağlantı testi gibi
    /// yerel bir yedek dosyası ÜRETMEYEN yüklemeler için. Aynı gün/kategori kuralını kullanır ki
    /// test, gecelik yedeğin gideceği yolun TA KENDİSİNİ denesin.
    /// </summary>
    internal static BackupPlacement RemoteOnly(string category, DateTime localNow) =>
        Describe(string.Empty, category, localNow) with { LocalDirectory = string.Empty };

    /// <summary>Kararı verir ve YEREL klasörü açar.</summary>
    internal static BackupPlacement Ensure(string root, string category)
    {
        var yer = Describe(root, category, DateTime.Now);
        Directory.CreateDirectory(yer.LocalDirectory);
        return yer;
    }

    /// <summary>
    /// Kategori bugün sabit bir listeden geliyor ("sites", "databases", ...) ama bu değer artık
    /// UZAK yol segmentine de giriyor. İleride kullanıcı verisinden bir kategori türetilirse
    /// içindeki <c>/</c>, <c>\</c> ya da <c>..</c> hem yerel yolu hem uzak anahtarı hedefin
    /// dışına kaçırabilir; temizlik bu yüzden yolun ÜRETİLDİĞİ yerde, bir kez yapılır.
    /// </summary>
    private static string NormalizeCategory(string? category)
    {
        var gecersizler = Path.GetInvalidFileNameChars();
        var temiz = new string((category ?? string.Empty)
                .Where(karakter => !gecersizler.Contains(karakter)
                    && karakter != Path.DirectorySeparatorChar
                    && karakter != Path.AltDirectorySeparatorChar)
                .ToArray())
            .Trim()
            .Trim('.');

        return temiz.Length == 0 ? "diger" : temiz;
    }
}
