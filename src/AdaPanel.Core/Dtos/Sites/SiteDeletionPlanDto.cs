namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Bir site silinmeden önce, siteye bağlı tüm kaynakların (profesyonel panel benzeri cascade)
/// önizlemesi. Kullanıcı silme onayında neyin gideceğini görür.
/// </summary>
public sealed class SiteDeletionPlanDto
{
    public string SiteName { get; set; } = string.Empty;

    public string PhysicalPath { get; set; } = string.Empty;

    public string ApplicationRootPath { get; set; } = string.Empty;

    /// <summary>Panel DNS bölgesi mevcut mu?</summary>
    public bool HasDnsZone { get; set; }

    /// <summary>Siteye/bağlamalara karşılık gelen mail domainleri (varsa).</summary>
    public string[] MailDomains { get; set; } = [];

    /// <summary>Siteye ait FTP kullanıcıları.</summary>
    public string[] FtpUsers { get; set; } = [];

    /// <summary>
    /// GERÇEKTEN silinecek veritabanları (motor:ad) — yalnızca sahiplik kaydı ya da açık
    /// site↔DB bağı olanlar.
    /// <para>
    /// Burada ad benzerliğine göre tahmin YAPILMAZ: adı siteye benzeyen ama sahipliği
    /// doğrulanamayan veritabanları silinmez, silme sonucundaki uyarılarda "silinmedi ve neden"
    /// olarak bildirilir. Onay ekranı bu listeyi "gidecekler" diye gösterdiği için liste
    /// silinecek olanla BİREBİR aynı olmak zorundadır; tahmin eklemek kullanıcıya yalan söyler.
    /// </para>
    /// </summary>
    public string[] Databases { get; set; } = [];

    public bool HasReverseProxy { get; set; }

    public bool HasDiskQuota { get; set; }

    public bool HasGitDeployment { get; set; }

    /// <summary>Bu siteye atanmış müşteri sayısı (atama temizlenecek).</summary>
    public int CustomerAssignments { get; set; }

    /// <summary>
    /// Sitenin KLASÖRÜ İÇİNDEN çalışan zamanlanmış görev sayısı (kaldırılacak).
    /// <para>
    /// Onayda ayrıca gösterilir çünkü kullanıcının en çok şaşıracağı kalem budur: bunlar site
    /// silinse de tetiklenmeye devam eden gerçek Windows görevleridir ve panelde artık hangi
    /// siteye ait olduklarını gösteren bir şey kalmaz.
    /// </para>
    /// </summary>
    public int ScheduledTasks { get; set; }
}

/// <summary>Cascade silme sonucu: silinenler + bloke olmayan uyarılar + SUNUCUDA KALANLAR.</summary>
public sealed class SiteDeletionResultDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Başarıyla silinen kaynakların kullanıcıya gösterilebilir özeti.</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>Silinemeyen/atlanan kaynaklar (best-effort; ana silmeyi bloke etmez).</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// Sunucuda GERÇEKTEN kalanlar — <b>tipi ve adıyla</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NEDEN <see cref="Warnings"/> YETMİYOR: uyarı bir CÜMLEdir. Onu okuyan tek şey insan gözüydü;
    /// çağıran kod (kullanıcı silme, WHMCS) "bir şey kaldı mı" sorusunu cümleden çıkaramadığı için
    /// sonucu tamamen atıyordu ve <c>200 OK</c> dönüyordu (denetim Y7). Burada aynı bilgi
    /// <b>makine tarafından okunabilir</b> duruyor: hangi tip (dosya/veritabanı/alan adı), hangi ad,
    /// hangi sebep ve bu <b>bilinçli bir karar mıydı</b>.
    /// </para>
    /// <para>
    /// <see cref="Warnings"/> aynen korunuyor: mevcut Web Siteleri ekranı onu gösteriyor ve bu alan
    /// onun yerine değil, YANINA eklendi.
    /// </para>
    /// </remarks>
    public List<SiteLeftoverDto> Leftovers { get; set; } = [];

    /// <summary>
    /// Dosya klasörünün ne kadarının silindiği ÖLÇÜLEBİLDİ mi. <c>false</c> ise panel
    /// "silindi" de "silinemedi" de DİYEMEZ — ölçüm ağacın tamamını göremedi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir bayrak (kapatılan açık):</b> "ne kadarı gitti" kararı iki bütçeli sayımın
    /// FARKINA bakar (<c>SiteCascadeDeletionService.ClassifyDeletion</c>). İki sayım da öge tavanına
    /// dayanırsa fark SIFIR çıkar ve eski kural bunu "hiç silinemedi" sayardı: panel, ağacın çoğu
    /// gitmiş olabilecekken kullanıcıya <b>kesin</b> bir cümleyle "dosyalar duruyor" diyordu.
    /// Bilgi kaynakta VARDI (<c>SafeDirectoryWalkResult.Truncated</c>) ama sözleşme taşımadığı için
    /// yolda düşüyordu — aynı hata sınıfı disk kotasında da yaşandı ve orada
    /// <c>DiskUsage(UsageMb, Measured, TruncatedReasonKey)</c> ile kapandı. Buradaki üçlü onun
    /// birebir kopyasıdır; yeni bir felsefe icat etmek, birini sıkılaştırıp diğerini unutmanın yolu
    /// olurdu.
    /// </para>
    /// <para>
    /// <b><see cref="FullyDeleted"/> bundan ETKİLENMEZ ve bu bir yuvarlama DEĞİLDİR:</b> ölçüm
    /// kesildiğinde bile klasörün DURDUĞU doğrudan gözlemlenmiştir (<c>Directory.Exists</c>), yani
    /// "sunucuda bir şey kaldı" iddiası ölçüme değil gözleme dayanır. Belirsiz olan tek şey
    /// klasörün İÇİNDEN ne kadarının gittiğidir ve onu bu bayrak taşır.
    /// </para>
    /// </remarks>
    public bool FilesDeletionMeasured { get; set; } = true;

    /// <summary>
    /// Ölçümün neden kesildiğinin çeviri anahtarı (<c>files.size_truncated_entries|_time|_denied</c>);
    /// ölçüm tamsa boş. <b>Boş olması "ölçüm tam" demek DEĞİLDİR</b> — tek doğru soru
    /// <see cref="FilesDeletionMeasured"/>'dır (aynı sözleşme: <c>DiskUsage.TruncatedReasonKey</c>).
    /// </summary>
    public string FilesDeletionTruncatedReasonKey { get; set; } = string.Empty;

    /// <summary>
    /// Site GERÇEKTEN tamamen gitti mi. <b>Bilinçli bırakılanlar</b> (ör. "dosyaları silme" seçildi,
    /// sahipliği kanıtlanmamış veritabanı korundu) sonucu başarısız yapmaz — istenen buydu.
    /// Bilinçsiz her kalıntı (silinemeyen klasör, düşürülemeyen veritabanı) bunu <c>false</c> yapar.
    /// </summary>
    public bool FullyDeleted => Leftovers.All(leftover => leftover.Deliberate);
}

/// <summary>Sunucuda kalan TEK bir kaynak — çağıranın adıyla gösterebileceği biçimde.</summary>
public sealed class SiteLeftoverDto
{
    /// <summary>Kaynak tipi — bkz. <see cref="SiteLeftoverKinds"/>.</summary>
    public string Kind { get; set; } = SiteLeftoverKinds.Other;

    /// <summary>
    /// Kaynağın ADI: veritabanı için <c>mysql:musteri_db</c>, alan adı için <c>musteri.com</c>,
    /// dosyalar için tam yol. "Bir şeyler kaldı" demek yöneticiyi aramaya gönderir; ad, doğrudan
    /// gidip temizleyebileceği yerdir.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Neden kaldığı (kullanıcıya gösterilebilir cümle).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Panel bunu BİLEREK mi bıraktı? <c>true</c> ise politika gereği korundu (ör. sahipliği
    /// kanıtlanamayan veritabanı silinmez) — arıza değildir, ama yine de sunucuda durduğu için
    /// bildirilir. <c>false</c> ise silinmesi gerekiyordu ve BAŞARISIZ olundu.
    /// </summary>
    public bool Deliberate { get; set; }
}

/// <summary>
/// Kalıntı tipleri. Dizge sabit çünkü JSON'a okunabilir çıkması gerekiyor (panelde enum'lar
/// sayı olarak serileşiyor) ve ön yüz bunlarla çeviri anahtarı kuruyor.
/// </summary>
public static class SiteLeftoverKinds
{
    public const string Site = "site";
    public const string Files = "files";
    public const string Database = "database";
    public const string MailDomain = "mailDomain";
    public const string FtpUser = "ftpUser";
    public const string DnsZone = "dnsZone";
    public const string Other = "other";
}
