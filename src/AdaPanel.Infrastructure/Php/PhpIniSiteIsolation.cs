using System.Globalization;
using System.Text;

namespace AdaPanel.Infrastructure.Php;

/// <summary>
/// Site başına PHP kiracı izolasyonunun (open_basedir + temp/oturum yolları) TEK KAYNAĞI:
/// PAYLAŞILAN <c>php.ini</c> içine yazılan <c>[PATH=...]</c> bölümleri.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden .user.ini DEĞİL — bu makinede ÖLÇÜLDÜ.</b> İzolasyon eskiden site kökündeki
/// <c>.user.ini</c> dosyasına yazılıyordu. O dosya kiracının KENDİ klasöründedir: site kökü
/// app pool kimliğine <c>Modify</c> ile açıktır (Laravel <c>storage/</c>, WordPress
/// <c>wp-content/uploads</c> yazabilsin diye), üst klasördeki <c>FILE_DELETE_CHILD</c> hakkı
/// dosyanın kendi ACL'inden BAĞIMSIZ silme yetkisi verir. Yani savunmayı, savunulan taraf
/// kaldırabiliyordu — hacklenmiş bir WordPress'in ilk yapacağı iş budur.
/// </para>
/// <para>
/// <b>Ölçüm (php-cgi 8.3.33, bu depodaki gerçek php.exe):</b> <c>[PATH=&lt;kök&gt;]</c> bölümü
/// <c>open_basedir</c>'i verirken, aynı anda site kökündeki <c>.user.ini</c> içine konan
/// <c>open_basedir = "C:/"</c> HİÇ dikkate alınmadı (PHP: <i>"[PATH=] bölümlerindeki yönergeler
/// kullanıcı tanımlı ini dosyalarıyla ya da çalışma anında EZİLEMEZ"</i>). Bölümü olmayan bir
/// site ise <c>open_basedir=''</c> ile komşusunun dosyasını okudu. Yani bu mekanizma kiracının
/// kaldıramayacağı tek mekanizmadır ve php.ini kiracının klasöründe değildir.
/// </para>
/// <para>
/// <b>FastCGI argümanı GEREKMEZ.</b> Panelde tek bir global FastCGI kaydı vardır (arguments boş)
/// ve <c>[PATH=]</c> bölümleri o TEK php.ini'nin içindedir — yani site başına <c>-c</c> argümanı
/// olmadan çalışır. Ölçümde <c>Loaded Configuration File</c> tam olarak paylaşılan php.ini'ydi.
/// </para>
/// <para>
/// <b>Neden temp/oturum de burada.</b> <c>upload_tmp_dir</c> ve <c>sys_temp_dir</c>
/// PHP_INI_SYSTEM'dir: <c>.user.ini</c>'den AYARLANAMAZ, ama php.ini'nin <c>[PATH=]</c>
/// bölümünden AYARLANABİLİR (ölçüldü: <c>ini_get</c> değeri döndürdü, <c>tempnam()</c> site
/// içinde dosya açtı). Eski çözüm bunu yapamadığı için <c>%SystemRoot%\Temp</c>'i open_basedir'e
/// eklemek ZORUNDAYDI — yani bütün kiracıların ORTAK yazdığı bir klasör her sitenin izin
/// listesindeydi (yüklenen dosyalar ve oturum dosyaları oradan okunabilirdi). Üçünü de site içi
/// <c>tmp</c>'ye çevirince open_basedir SADECE site kökü olabiliyor; ortak temp izin listesinden
/// tamamen düşüyor.
/// </para>
/// <para>
/// <b>Kırılmasın diye kök = uygulama kökü.</b> Laravel'de IIS fiziksel yolu <c>&lt;proje&gt;\public</c>
/// olur; open_basedir oraya konsaydı <c>vendor/</c> ve <c>storage/</c> erişilemez olurdu.
/// <c>SiteDto.ApplicationRootPath</c> zaten public'in üstünü çözer; ölçümde <c>public/</c> içinden
/// koşan betik <c>vendor/</c>'u okuyabildi, komşu siteyi okuyamadı.
/// </para>
/// <para>
/// <b>CLI etkilenmez (ölçüldü).</b> <c>[PATH=]</c> yalnız CGI/FastCGI'de çalışır; aynı ini ile
/// <c>php.exe</c> çalıştırıldığında <c>open_basedir</c> boş kaldı. Yani panelin sınırlı kimlikle
/// koşturduğu composer/wp-cli bu değişiklikten ETKİLENMEZ.
/// </para>
/// </remarks>
public static class PhpIniSiteIsolation
{
    /// <summary>Bloğun başlangıç işareti (site adı eklenir).</summary>
    public const string BlockStartPrefix = "; >>> AdaPanel site izolasyonu: ";

    /// <summary>Bloğun bitiş işareti (site adı eklenir).</summary>
    public const string BlockEndPrefix = "; <<< AdaPanel site izolasyonu: ";

    /// <summary>Site içi geçici dizin adı: oturum + yükleme + sys temp buraya bağlanır.</summary>
    public const string TempDirectoryName = "tmp";

    /// <summary>
    /// Site içi geçici dizin. Oturum, yükleme ve <c>sys_get_temp_dir()</c> için TEK yol —
    /// üç ayarın da aynı klasörü göstermesi, izin verilecek yol listesini tek satırda tutar.
    /// </summary>
    public static string TempDirectoryFor(string applicationRoot)
        => Path.Combine(applicationRoot, TempDirectoryName);

    /// <summary>
    /// Geçici dizine konan <c>web.config</c>: o klasörün altındaki HİÇBİR dosya HTTP ile
    /// sunulmaz (IIS 404.7 verir).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden zorunlu:</b> WordPress gibi düz kurulumlarda uygulama kökü ile belge kökü AYNI
    /// klasördür. Oturum dosyalarını (<c>sess_*</c>, içinde giriş yapmış kullanıcının oturumu)
    /// ve yüklenen geçici dosyaları oraya koymak, onları <c>/tmp/sess_...</c> adresinden
    /// İNDİRİLEBİLİR yapardı — izolasyonu düzeltirken oturum çalmayı mümkün kılmak olurdu.
    /// Laravel'de kök <c>public</c>'in üstü olduğu için sorun yoktu; ayrım sessizce güvenli
    /// olan tarafa bırakılamaz.
    /// </para>
    /// <para>
    /// <b>Neden requestFiltering:</b> IIS'in ÇEKİRDEK modülüdür (URL Rewrite / URL Authorization
    /// gibi ayrıca kurulması gereken bir modül DEĞİL) ve bu depoda zaten site web.config'ine
    /// yazılıyor (WafService). <c>allowUnlisted="false"</c> + listelenmiş uzantı yok = her istek
    /// reddedilir; uzantısız istekler de dahil.
    /// </para>
    /// </remarks>
    public const string TempDirectoryWebConfig =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- AdaPanel: bu klasor PHP'nin oturum/yukleme/gecici dosyalarini tutar. HTTP ile ERISILEMEZ. -->
        <configuration>
          <system.webServer>
            <security>
              <requestFiltering>
                <fileExtensions allowUnlisted="false" />
              </requestFiltering>
            </security>
          </system.webServer>
        </configuration>
        """;

    /// <summary>
    /// GLOBAL bir yönergenin php.ini'de yazılabileceği son satır: ilk <c>[PATH=</c>/<c>[HOST=</c>
    /// başlığının indeksi (yoksa dosya sonu).
    /// </summary>
    /// <remarks>
    /// ÖLÇÜLMÜŞ TUZAK: <c>php.ini</c>'de bir <c>[PATH=]</c> başlığından SONRA gelen her satır o
    /// bölüme aittir. Eski <c>SetIniDirective</c> bulunamayan yönergeyi <c>lines.Add</c> ile dosya
    /// SONUNA ekliyordu; izolasyon blokları da sondadır. Yani "genel memory_limit" ayarı son
    /// sitenin bölümünün içine düşer, GENEL olarak hiç uygulanmazdı. Genel yazıcılar bu indeksi
    /// kullanmak ZORUNDA. <c>[PHP]</c>, <c>[Date]</c> gibi ADI OLAN bölümler PHP tarafından yok
    /// sayılır (kapsam açmazlar), bu yüzden sınır yalnız PATH/HOST başlığıdır.
    /// </remarks>
    public static int GlobalInsertIndex(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsScopedSectionHeader(lines[i]))
            {
                return i;
            }
        }

        return lines.Count;
    }

    /// <summary>php.ini'nin yalnız GENEL (kapsamsız) bölümü — per-site değerleri genel sanmamak için.</summary>
    public static IReadOnlyList<string> GlobalLines(IEnumerable<string> lines)
    {
        var all = lines as IReadOnlyList<string> ?? lines.ToList();
        return all.Take(GlobalInsertIndex(all)).ToList();
    }

    /// <summary>Verilen sitenin izolasyon bloğu dosyada var mı.</summary>
    public static bool IsEnabled(IEnumerable<string> lines, string siteName)
        => ReadRoot(lines, siteName) is not null;

    /// <summary>Sitenin izolasyon bloğundaki <c>open_basedir</c> kökü (yoksa <c>null</c>).</summary>
    public static string? ReadRoot(IEnumerable<string> lines, string siteName)
    {
        var all = lines as IReadOnlyList<string> ?? lines.ToList();
        var (start, end) = FindBlock(all, siteName);
        if (start < 0)
        {
            return null;
        }

        for (var i = start; i <= end; i++)
        {
            var trimmed = all[i].Trim();
            if (!trimmed.StartsWith("open_basedir", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            return trimmed[(equals + 1)..].Trim().Trim('"');
        }

        return null;
    }

    /// <summary>
    /// Sitenin izolasyon bloğunu yazar/günceller. Blok DOSYA SONUNA konur: <c>[PATH=]</c>
    /// başlığından sonra gelen her satır o bölüme ait olduğu için genel yönergelerin arkasında
    /// kalmak zorundadır.
    /// </summary>
    public static IReadOnlyList<string> Upsert(IEnumerable<string> lines, string siteName, string applicationRoot)
    {
        var site = EnsureSafeMarker(siteName, nameof(siteName));
        var root = EnsureSafePath(applicationRoot, nameof(applicationRoot));
        var temp = TempDirectoryFor(root);

        var result = Remove(lines, siteName).ToList();
        while (result.Count > 0 && result[^1].Trim().Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        result.Add(string.Empty);
        result.Add(BlockStartPrefix + site);
        result.Add(FormattableString.Invariant($"[PATH={root}]"));
        result.Add(FormattableString.Invariant($"open_basedir = \"{root}\""));
        result.Add(FormattableString.Invariant($"upload_tmp_dir = \"{temp}\""));
        result.Add(FormattableString.Invariant($"sys_temp_dir = \"{temp}\""));
        result.Add(FormattableString.Invariant($"session.save_path = \"{temp}\""));
        result.Add(BlockEndPrefix + site);
        return result;
    }

    /// <summary>
    /// Sitenin izolasyon bloğunu kaldırır (yöneticinin açık "kapat" isteği ya da blok yenilenirken).
    /// Aynı ada ait birden çok blok varsa hepsi düşer; başka hiçbir satıra dokunulmaz.
    /// </summary>
    public static IReadOnlyList<string> Remove(IEnumerable<string> lines, string siteName)
    {
        var all = (lines as IReadOnlyList<string> ?? lines.ToList()).ToList();
        var site = EnsureSafeMarker(siteName, nameof(siteName));
        var start = BlockStartPrefix + site;
        var end = BlockEndPrefix + site;

        var result = new List<string>(all.Count);
        var inside = false;
        foreach (var line in all)
        {
            var trimmed = line.Trim();
            if (!inside && string.Equals(trimmed, start, StringComparison.OrdinalIgnoreCase))
            {
                inside = true;
                continue;
            }

            if (inside)
            {
                if (string.Equals(trimmed, end, StringComparison.OrdinalIgnoreCase))
                {
                    inside = false;
                }

                continue;
            }

            result.Add(line);
        }

        // İşaretlenmiş blok kapanmadan dosya bittiyse (elle bozulmuş dosya) kalanı düşürmüş oluruz;
        // bu, yarım bir [PATH=] bölümünün geri kalan yönergeleri yutmasından iyidir.
        return result;
    }

    /// <summary>Bloğun satır aralığı (bulunamazsa <c>(-1, -1)</c>).</summary>
    private static (int Start, int End) FindBlock(IReadOnlyList<string> lines, string siteName)
    {
        var site = EnsureSafeMarker(siteName, nameof(siteName));
        var start = BlockStartPrefix + site;
        var end = BlockEndPrefix + site;
        var startIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (startIndex < 0)
            {
                if (string.Equals(trimmed, start, StringComparison.OrdinalIgnoreCase))
                {
                    startIndex = i;
                }

                continue;
            }

            if (string.Equals(trimmed, end, StringComparison.OrdinalIgnoreCase))
            {
                return (startIndex, i);
            }
        }

        return startIndex < 0 ? (-1, -1) : (startIndex, lines.Count - 1);
    }

    private static bool IsScopedSectionHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("[PATH=", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("[HOST=", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// İşaret satırına girecek metni doğrular. Satır sonu ya da <c>[</c>/<c>]</c> içeren bir ad,
    /// paylaşılan php.ini'ye sahte bir bölüm başlığı ekleyip TÜM sitelerin ayarını değiştirebilirdi;
    /// bu yüzden uydurulmuş bir ad kabul edilmez, HATA verilir (sessizce temizlenmez).
    /// </summary>
    private static string EnsureSafeMarker(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Site adı boş olamaz.", parameterName);
        }

        foreach (var character in value)
        {
            if (char.IsControl(character) || character is '[' or ']' or '"')
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.InvariantCulture, "php.ini işaretinde kullanılamayan karakter: {0}", value),
                    parameterName);
            }
        }

        return value.Trim();
    }

    private static string EnsureSafePath(string value, string parameterName)
    {
        var path = EnsureSafeMarker(value, parameterName);
        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException("İzolasyon kökü mutlak yol olmalıdır.", parameterName);
        }

        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Blok metnini (tanı/gösterim için) tek parça döndürür.</summary>
    public static string RenderBlock(string siteName, string applicationRoot)
    {
        var builder = new StringBuilder();
        var rendered = Upsert(Array.Empty<string>(), siteName, applicationRoot);
        foreach (var line in rendered)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }
}
