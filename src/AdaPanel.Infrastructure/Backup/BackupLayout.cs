using System.Globalization;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Bir yedeğin YERİ: gün klasörünün altındaki seviyeler (kategori, kaynak, ...). Yerel diskteki
/// klasör ile UZAK hedefteki yol AYNI karardan türetilir.
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
/// <para>
/// <b>Neden seviyeler bir LİSTE:</b> 0.6.1'de kaynak (site adı / veritabanı adı / abonelik)
/// kendi alt klasörünü aldı ve bir seviye daha eklendi; siteye ait bir veritabanı ise
/// <c>sites/&lt;site&gt;/databases/&lt;db&gt;</c> ile DÖRT seviye iner. Sabit "kategori" alanı
/// bunu taşıyamazdı; sabitlemeye çalışmak, uzak tarafın yereli takip etmediği bir ikinci karar
/// noktası doğururdu.
/// </para>
/// </remarks>
internal sealed record BackupPlacement(string Day, IReadOnlyList<string> Segments, string LocalDirectory)
{
    /// <summary>Gün klasörünün hemen altındaki seviye ("sites", "databases", ...).</summary>
    public string Category => Segments.Count > 0 ? Segments[0] : string.Empty;

    /// <summary>Yedeğin KAYNAĞINI taşıyan en alt seviye (kaynak klasörü yoksa kategoriye eşittir).</summary>
    public string Source => Segments.Count > 0 ? Segments[^1] : string.Empty;

    /// <summary>
    /// Uzak hedefte taban adresin ALTINA açılacak klasör seviyeleri, sırayla.
    /// </summary>
    /// <remarks>
    /// Yerel yol ile aynı sırayı verir (<c>&lt;gün&gt;/&lt;kategori&gt;/&lt;kaynak&gt;</c>); FTP'de
    /// MKD, WebDAV'da MKCOL bu sırayla YUKARIDAN AŞAĞIYA uygulanmalıdır — ara seviye yokken alt
    /// seviyeyi açmak her iki protokolde de hata verir.
    /// </remarks>
    public string[] RemoteFolders => [Day, .. Segments];
}

/// <summary>
/// Yedek klasörü kararının TEK kaynağı: <c>&lt;kök&gt;\&lt;GÜN&gt;\&lt;kategori&gt;\&lt;kaynak&gt;</c>.
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
/// <b>Neden kaynak klasörü (0.6.1):</b> gün klasörü tarihi çözdü ama SORUYU çözmedi: çok siteli
/// bir sunucuda <c>2026-08-18\sites\</c> yine onlarca dosya ve hangisinin hangi siteye ait olduğu
/// ancak DOSYA ADI okunarak anlaşılıyordu. Kaynak klasörü bunu gezinilebilir kılar. Sıra
/// bilinçlidir — GÜN &gt; KATEGORİ &gt; KAYNAK: gün en üstte kaldığı için "bir günü komple taşı/sil"
/// hâlâ TEK işlemdir; kaynak en üstte olsaydı bu, her kaynak klasörüne ayrı ayrı girmek olurdu.
/// </para>
/// <para>
/// <b>Neden yerel tarih, UTC değil:</b> klasör adı İNSAN içindir. Yedek 03:00'te alınıyorsa
/// yöneticinin takvimindeki gün neyse o klasöre girmeli; UTC kullanmak Türkiye'de gece yarısından
/// sonra alınan yedeği "bir önceki gün" klasörüne düşürürdü. Dosya adındaki zaman damgası UTC
/// olarak KALIR (sıralama ve çakışma için) — ikisi farklı işler.
/// </para>
/// <para>
/// <b>Geriye dönük:</b> eski yedeklerin yerel yolu kayıtta (<c>record.Path</c>), uzak yolu ise
/// <c>record.RemoteUri</c> içinde TAM olarak saklandığı için listeleme, geri yükleme, gözatma,
/// seçerek geri yükleme, zincir çözümü ve saklama temizliği eski düzendeki (kökün altında düz ya
/// da yalnız gün+kategori klasöründe duran) dosyaları bulmaya devam eder. Yeni düzen yalnızca
/// YENİ yedekler için geçerlidir; hiçbir yerde yol YENİDEN HESAPLANMAZ.
/// </para>
/// </remarks>
internal static class BackupLayout
{
    internal const string DayFormat = "yyyy-MM-dd";

    /// <summary>
    /// Bir seviye temizlendiğinde geriye hiçbir şey kalmazsa kullanılan sabit ad. Kaynak adı
    /// KULLANICI VERİSİDİR; boş bırakmak yedeği bir üst klasöre (kategori/gün) taşırdı — yani
    /// farklı kaynakların dosyaları birbirine karışırdı.
    /// </summary>
    internal const string FallbackSegment = "diger";

    /// <summary>
    /// Kararı verir ama diske DOKUNMAZ (ölçülebilir/testlenebilir olması için ayrıldı).
    /// </summary>
    /// <param name="segments">
    /// Gün klasörünün altındaki seviyeler, YUKARIDAN AŞAĞIYA: kategori, sonra (varsa) kaynak.
    /// Her seviye <see cref="BackupPathName"/> ile temizlenir — bu değerler artık hem yerel yola
    /// hem UZAK anahtara giriyor ve içlerinden en az biri (kaynak) doğrudan kullanıcı verisidir.
    /// </param>
    internal static BackupPlacement Describe(string root, DateTime localNow, params string[] segments)
    {
        var gun = localNow.ToString(DayFormat, CultureInfo.InvariantCulture);

        var temiz = (segments ?? [])
            .Select(parca => BackupPathName.Sanitize(parca, FallbackSegment))
            .ToArray();
        if (temiz.Length == 0)
        {
            temiz = [FallbackSegment];
        }

        var kok = Environment.ExpandEnvironmentVariables(root ?? string.Empty);
        return new BackupPlacement(gun, temiz, Path.Combine([kok, gun, .. temiz]));
    }

    /// <summary>
    /// Yalnızca UZAK yol için yer kararı (yerel klasör yok, diske dokunulmaz): bağlantı testi gibi
    /// yerel bir yedek dosyası ÜRETMEYEN yüklemeler için. Aynı gün/kategori kuralını kullanır ki
    /// test, gecelik yedeğin gideceği yolun TA KENDİSİNİ denesin.
    /// </summary>
    internal static BackupPlacement RemoteOnly(string category, DateTime localNow) =>
        Describe(string.Empty, localNow, category) with { LocalDirectory = string.Empty };

    /// <summary>Kararı verir ve YEREL klasörü açar.</summary>
    internal static BackupPlacement Ensure(string root, params string[] segments)
    {
        var yer = Describe(root, DateTime.Now, segments);
        Directory.CreateDirectory(yer.LocalDirectory);
        return yer;
    }
}
