using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Logs;

/// <summary>Bir dosyaya dokunma girişiminin sonucu.</summary>
public enum LogFileGuardOutcome
{
    /// <summary>İşlem yapıldı.</summary>
    Done,

    /// <summary>Yol yönetilen klasörün ALTINDA değil (ya da doğrudan altında değil).</summary>
    OutsideRoot,

    /// <summary>Uzantı beklenenlerden biri değil.</summary>
    WrongExtension,

    /// <summary>Sembolik bağlantı / kavşak (junction): TAKİP EDİLMEZ.</summary>
    ReparsePoint,

    /// <summary>Başka bir süreç dosyayı açık tutuyor (IIS / Serilog).</summary>
    InUse,

    /// <summary>Dosya yok (başka bir tur ya da yönetici zaten kaldırmış).</summary>
    Missing,

    /// <summary>İzin yok ya da salt-okunur.</summary>
    AccessDenied
}

/// <summary>
/// Log döndürmenin SİLME KAPISI. Bu sınıfın dışında hiçbir yerde günlük dosyası silinmez.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu kadar sıkı:</b> bu depoda özyinelemeli bir silme, bir MÜŞTERİ SİTESİNİ silebilecek
/// bir hataya yol açtı (bkz. FTP klasör bağlantısı olayı). Log döndürme de silen bir özelliktir ve
/// üstelik yönetici uyanıkken değil, gece arka planda çalışır. Bu yüzden kapı "hata olursa yakala"
/// değil, "geçemezse hiç dokunma" mantığıyla kuruldu.
/// </para>
/// <para><b>Uygulanan kurallar (hepsi ölçülmüş bir riske karşılık gelir):</b></para>
/// <list type="number">
/// <item>
/// <b>Kapsam:</b> yol, yönetilen klasörün DOĞRUDAN altında olmalı — üst klasörü kökün KENDİSİ
/// olmalı. Böylece alt klasör diye bir ihtimal kalmaz ve alt klasör olmadığı için bir kavşağın
/// içine YÜRÜMEK de mümkün olmaz.
/// <para>
/// <b>ÖLÇÜM (mutasyonla):</b> buradaki <see cref="PathContainment.IsInsideRoot"/> çağrısı TEK
/// BAŞINA kaldırıldığında hiçbir test kırılmadı — "üst klasör = kök" kuralı onu mantıken kapsıyor.
/// İkisi birden kaldırılınca 3 test kırmızıya döndü. Çağrı yine de duruyor: "bu yol şu klasörün
/// altında mı" sorusunun panelde TEK adresi orasıdır ve kuralı burada da adıyla anmak, ileride
/// daha gevşek bir kontrole kaymayı zorlaştırır. Yani bilinçli, ölçülmüş bir fazlalık — sanılan
/// bir koruma değil.
/// </para>
/// </item>
/// <item>
/// <b>Uzantı:</b> yalnız beklenen uzantı silinir (<c>.log</c> ya da <c>.zst</c>). Klasöre düşmüş
/// başka bir dosya (yöneticinin notu, bir yedek) panelin işi değildir.
/// </item>
/// <item>
/// <b>Bağlantı noktası:</b> <see cref="FileAttributes.ReparsePoint"/> taşıyan yol REDDEDİLİR.
/// Açmak onu TAKİP ETMEK olurdu; hedefteki gerçek dosya silinirdi.
/// </item>
/// <item>
/// <b>Kilit:</b> silme <c>FileShare.None</c> + <see cref="FileOptions.DeleteOnClose"/> ile yapılır.
/// Bu, "önce yokla sonra sil" değildir; TEK bir çağrıdır, dolayısıyla yoklama ile silme arasında
/// dosyanın kullanıma girdiği bir aralık yoktur.
/// <para>
/// <b>ÖLÇÜLDÜ (bu makinede):</b> dosyayı <c>FILE_SHARE_DELETE</c> ile açmış bir yazan varken düz
/// <c>File.Delete</c> <b>BAŞARILI oluyor ve canlı günlüğü yok ediyor</b>; aynı dosyada
/// <c>FileShare.None + DeleteOnClose</c> ise paylaşım ihlaliyle reddediliyor ve dosya sağ kalıyor.
/// Yani "dene, IOException'ı yut" kalıbı bu iş için YETERSİZDİR — sessiz veri kaybı üretir.
/// </para>
/// </item>
/// </list>
/// <para>
/// <b>Klasör hiç silinmez.</b> Bu sınıfta <c>Directory.Delete</c> yoktur ve özyineleme yoktur.
/// </para>
/// </remarks>
public static class LogFileGuard
{
    /// <summary>
    /// Klasör yönetilebilir mi? Var olmalı ve KENDİSİ bir bağlantı noktası OLMAMALI.
    /// </summary>
    /// <remarks>
    /// Bir sitenin <c>W3SVC&lt;id&gt;</c> klasörü kavşak olarak kurulmuşsa (ör. <c>D:\musteri</c>
    /// hedefine), içindeki "*.log" dosyaları aslında müşterinin dosyalarıdır. Panelin böyle bir
    /// klasörde silme yapması, tam da bu depoda bir kez yaşanmış hata sınıfıdır. Yönetmiyoruz ve
    /// bunu durum olarak bildiriyoruz — sessizce geçmiyoruz.
    /// </remarks>
    public static bool IsManageableDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            var bilgi = new DirectoryInfo(directory);
            return bilgi.Exists && !bilgi.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Dosyayı, yukarıdaki dört kuralın hepsinden geçirerek siler. Geçemezse HİÇBİR ŞEY yapmaz.
    /// </summary>
    /// <param name="root">Yönetilen klasör (dosyanın doğrudan üst klasörü olmak zorunda).</param>
    /// <param name="path">Silinecek dosyanın tam yolu.</param>
    /// <param name="allowedExtensions">İzin verilen uzantılar (nokta dahil, ör. <c>.zst</c>).</param>
    public static LogFileGuardOutcome SafeDelete(string root, string path, IReadOnlyCollection<string> allowedExtensions)
    {
        var kontrol = Inspect(root, path, allowedExtensions);
        if (kontrol != LogFileGuardOutcome.Done)
        {
            return kontrol;
        }

        try
        {
            // TEK çağrı: özel olarak açılamıyorsa (başkası tutuyorsa) silme HİÇ başlamaz.
            using var akis = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return LogFileGuardOutcome.Done;
        }
        catch (FileNotFoundException)
        {
            return LogFileGuardOutcome.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return LogFileGuardOutcome.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            // Salt-okunur ya da izin yok. ÖLÇÜLDÜ: salt-okunur dosyada DeleteOnClose
            // UnauthorizedAccessException verir. Salt-okunur bayrağını KALDIRMIYORUZ — bir
            // yöneticinin bilerek koyduğu koruma, temizlik işinin kaldıracağı bir şey değildir.
            return LogFileGuardOutcome.AccessDenied;
        }
        catch (IOException)
        {
            return LogFileGuardOutcome.InUse;
        }
    }

    /// <summary>
    /// Silmeden (ve taşımadan) önceki DÖRT kural. Ayrı ayrı çağrılabilir olması bilinçli: sıkıştırma
    /// yolu da kaynağı okumadan önce aynı kapıdan geçer.
    /// </summary>
    public static LogFileGuardOutcome Inspect(string root, string path, IReadOnlyCollection<string> allowedExtensions)
    {
        var normalKok = PathContainment.Normalize(root);
        var normalYol = PathContainment.Normalize(path);
        if (normalKok is null || normalYol is null)
        {
            return LogFileGuardOutcome.OutsideRoot;
        }

        if (!PathContainment.IsInsideRoot(normalKok, normalYol))
        {
            return LogFileGuardOutcome.OutsideRoot;
        }

        // DOĞRUDAN altında mı? Alt klasöre inmek yok; dolayısıyla bir kavşağın içine yürümek de yok.
        var ustKlasor = Path.GetDirectoryName(normalYol);
        if (ustKlasor is null || !string.Equals(
                ustKlasor.TrimEnd(Path.DirectorySeparatorChar),
                normalKok.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return LogFileGuardOutcome.OutsideRoot;
        }

        var uzanti = Path.GetExtension(normalYol);
        if (!allowedExtensions.Contains(uzanti, StringComparer.OrdinalIgnoreCase))
        {
            return LogFileGuardOutcome.WrongExtension;
        }

        try
        {
            var bilgi = new FileInfo(normalYol);
            if (!bilgi.Exists)
            {
                // Klasör olarak var olabilir; o da bizim işimiz değil (FileInfo.Exists klasörde false).
                return Directory.Exists(normalYol) ? LogFileGuardOutcome.WrongExtension : LogFileGuardOutcome.Missing;
            }

            if (bilgi.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return LogFileGuardOutcome.ReparsePoint;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return LogFileGuardOutcome.AccessDenied;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return LogFileGuardOutcome.InUse;
        }

        return LogFileGuardOutcome.Done;
    }

    /// <summary>
    /// Dosyayı başkası tutuyor mu? Sıkıştırma başlamadan önce sorulur: kaynağı okuyabiliyor olmak
    /// yetmez, çünkü sıkıştırma bittiğinde kaynağı SİLECEĞİZ. Yazılmakta olan bir dosyayı
    /// sıkıştırmak, yarım bir arşiv üretip aslını silmeye çalışmak olurdu.
    /// </summary>
    public static bool IsInUse(string path)
    {
        try
        {
            using var akis = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // İzin sorunu "kullanımda" değildir; çağıran zaten silme kapısında AccessDenied alacak.
            return false;
        }
    }
}
