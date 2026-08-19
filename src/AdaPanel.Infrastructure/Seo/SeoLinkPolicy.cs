namespace AdaPanel.Infrastructure.Seo;

/// <summary>Bir bağlantının tarama açısından ne olduğu.</summary>
public enum SeoLinkKind
{
    /// <summary>Hiç istek atılmaz ve raporda da yer almaz (fragman, javascript:, mailto: ...).</summary>
    Skipped,

    /// <summary>Sitenin kendi sayfası — GEZİLİR.</summary>
    Internal,

    /// <summary>Başka bir konak — RAPORLANIR ama ASLA İSTEK ATILMAZ.</summary>
    External
}

/// <param name="Kind">Karar.</param>
/// <param name="Value">İç bağlantıda site içi yol; dış bağlantıda tam adres; atlananlarda boş.</param>
/// <param name="Reason">Neden atlandı/dışarı sayıldı (günlük ve test için, dile bağımsız).</param>
public readonly record struct SeoLinkDecision(SeoLinkKind Kind, string Value, string Reason);

/// <summary>
/// TARAYICININ GÜVENLİK KAPISI: hangi adrese istek atılabileceğine BURASI karar verir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var (tehdit):</b> tarayıcı, sayfaların İÇİNDEN çıkan bağlantıları izler ve o sayfaların
/// içeriğini kiracının kendisi yazar. Bağlantılar körü körüne izlenseydi kiracı, kendi sayfasına
/// <c>&lt;a href="http://169.254.169.254/latest/meta-data/"&gt;</c> koyarak paneli bulut meta veri
/// servisine, <c>http://127.0.0.1:8890/api/v1/...</c> ile panelin KENDİ API'sine, ya da
/// <c>http://10.0.0.5/</c> ile iç ağdaki bir yönetim arayüzüne istek attırabilirdi. Panelin
/// LocalSystem yetkisiyle koştuğu düşünülürse bu, tarayıcıyı bir SSRF silahına çevirirdi.
/// </para>
/// <para>
/// <b>İKİ KATMANLI SAVUNMA.</b> Buradaki karar birinci katmandır. İkincisi taşımadadır:
/// <see cref="SeoHttpTransport"/> her soketi loopback'e sabitler, yani bu sınıfta bir hata olsa
/// bile istek MAKİNEDEN ÇIKAMAZ. Tek katmana güvenmiyoruz çünkü bu hata sınıfının bedeli yüksek.
/// </para>
/// <para>
/// <b>Neden yalnız 80/443:</b> arama motorları da yalnız bu portlardan tarar, yani kısıtlama
/// özelliği daraltmıyor. Buna karşılık, kiracının IIS'te açtığı bağlamanın portunu serbest
/// bırakmak, loopback üzerinde (panelin kendi portu dahil) rastgele bir porta istek atmanın kapısı
/// olurdu.
/// </para>
/// </remarks>
public static class SeoLinkPolicy
{
    /// <summary>Yolun üst uzunluğu; anlamlı bir sayfa yolu bunu aşmaz, aşan tipik olarak çöp/saldırıdır.</summary>
    public const int MaxPathLength = 512;

    private static readonly string[] AllowedSchemes = ["http", "https"];

    /// <summary>Tarama yalnız standart web portlarında yapılır.</summary>
    public static bool IsAllowedPort(int port) => port is 80 or 443;

    /// <summary>
    /// Kullanıcının verdiği başlangıç yolunu doğrular. Boş ise kök döner.
    /// </summary>
    /// <remarks>
    /// Bu, isteğin taşıdığı TEK serbest metindir; hedefi belirlemez ama yine de katı denetlenir:
    /// <c>//baskakonak/</c> biçimi (protokolsüz mutlak adres) <c>Uri</c> birleştirmesinde KONAĞI
    /// DEĞİŞTİRİR ve tam da bu yüzden reddedilir.
    /// </remarks>
    public static bool TryNormalizeStartPath(string? value, out string path)
    {
        path = "/";
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return true;
        }

        if (raw.Length > MaxPathLength)
        {
            return false;
        }

        // Denetim karakteri / boşluk: istek satırını bölebilir (başlık enjeksiyonu) ve hiçbir meşru
        // yolda bulunmaz.
        if (raw.Any(character => char.IsControl(character) || character == ' '))
        {
            return false;
        }

        if (!raw.StartsWith('/'))
        {
            return false;
        }

        // "//konak/yol" protokolsüz mutlak adrestir, yol DEĞİLDİR.
        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (raw.Contains('\\') || raw.Contains(".."))
        {
            return false;
        }

        path = raw;
        return true;
    }

    /// <summary>
    /// HTML'den çıkan bir bağlantıyı sınıflandırır.
    /// </summary>
    /// <param name="href">Ham <c>href</c> değeri.</param>
    /// <param name="currentPath">Bağlantının bulunduğu sayfanın yolu (göreli çözümleme için).</param>
    /// <param name="target">Doğrulanmış tarama hedefi.</param>
    public static SeoLinkDecision Classify(string? href, string currentPath, SeoCrawlTarget target)
    {
        var raw = (href ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return new SeoLinkDecision(SeoLinkKind.Skipped, string.Empty, "empty");
        }

        if (raw.StartsWith('#'))
        {
            return new SeoLinkDecision(SeoLinkKind.Skipped, string.Empty, "fragment");
        }

        // ŞEMA HAM METİNDE, ADRES KURULMADAN ÖNCE elenir. Aşağıda ayrıştırma sonrası ikinci bir
        // şema denetimi daha var ve sonuç aynı ("atlandı"); yani bu satır güvenlik için DEĞİL,
        // MALİYET için erken çıkıştır: "data:text/html;base64,..." biçimindeki bir değer
        // megabaytlarca olabilir ve onu önce bir Uri nesnesine çevirmek, sonra atmak, sayfadaki
        // her bağlantı için bedava bellek/CPU harcamak olurdu.
        //
        // MUTASYONLA ÖLÇÜLDÜ: bu satır silindiğinde davranış değişmediği için hiçbir test
        // kırılmıyordu. Karar HANGİ kapının çalıştığını da söylesin diye gerekçe kodu ayrıldı
        // ("scheme_prefix" erken çıkış, "scheme" ayrıştırma sonrası) ve test bunu sabitliyor.
        if (HasForeignScheme(raw))
        {
            return new SeoLinkDecision(SeoLinkKind.Skipped, string.Empty, "scheme_prefix");
        }

        var baseUri = BuildBaseUri(target, currentPath);
        if (!Uri.TryCreate(baseUri, raw, out var absolute))
        {
            return new SeoLinkDecision(SeoLinkKind.Skipped, string.Empty, "invalid");
        }

        if (!AllowedSchemes.Contains(absolute.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return new SeoLinkDecision(SeoLinkKind.Skipped, string.Empty, "scheme");
        }

        // "http://kullanici@169.254.169.254/" — bazı ayrıştırıcılar konağı yanlış okur; biz
        // kimlik taşıyan adresi hiç değerlendirmeyiz.
        if (!string.IsNullOrEmpty(absolute.UserInfo))
        {
            return new SeoLinkDecision(SeoLinkKind.Skipped, string.Empty, "userinfo");
        }

        if (!IsOwnHost(absolute, target))
        {
            return new SeoLinkDecision(SeoLinkKind.External, absolute.AbsoluteUri, "foreign_host");
        }

        if (!IsAllowedPort(absolute.Port))
        {
            return new SeoLinkDecision(SeoLinkKind.External, absolute.AbsoluteUri, "port");
        }

        // Fragman DÜŞÜRÜLÜR: "/a" ile "/a#bolum" aynı sayfadır, ikisini de indirmek boşuna istek olur.
        var path = absolute.PathAndQuery;
        return path.Length > MaxPathLength
            ? new SeoLinkDecision(SeoLinkKind.Skipped, string.Empty, "too_long")
            : new SeoLinkDecision(SeoLinkKind.Internal, path, "own_host");
    }

    /// <summary>
    /// Gezilecek yolu İSTEK ADRESİNE çevirir.
    /// </summary>
    /// <remarks>
    /// Adres, hedefin konak adıyla kurulur (soket zaten loopback'e sabitli olduğundan konak adı
    /// yalnız <c>Host</c> başlığını ve SNI'yı belirler). Kurulan adresin konağı SON KEZ doğrulanır:
    /// <c>//baskasite/yol</c> gibi bir değer <c>Uri</c> birleştirmesinde konağı değiştirebilir ve
    /// bu kontrol o sınıfın son kapısıdır.
    /// </remarks>
    public static bool TryBuildRequestUri(SeoCrawlTarget target, string path, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        // TERS EĞİK ÇİZGİ REDDEDİLİR. ÖLÇÜLDÜ: .NET, <c>/\konak/yol</c> değerini <c>//konak/yol</c>
        // yoluna çevirir ama KONAĞI DEĞİŞTİRMEZ (taban adres mutlak olduğu için yetki alanı ondan
        // gelir) — yani bu tek başına bir SSRF vektörü DEĞİL. Yine de reddedilir: hiçbir meşru
        // sayfa yolunda bulunmaz, ayrıştırıcılar arasında davranışı tutarsızdır ve
        // <see cref="TryNormalizeStartPath"/> ile aynı kuralı uygulamak, iki kapının aynı şeyi
        // söylemesini sağlar.
        if (path.Any(character => char.IsControl(character) || character == '\\'))
        {
            return false;
        }

        var baseUri = BuildBaseUri(target, "/");
        if (!Uri.TryCreate(baseUri, path, out var candidate))
        {
            return false;
        }

        // İKİNCİ KAPI. DÜRÜST NOT (mutasyonla ölçüldü): yukarıdaki biçim denetimleri geçtikten
        // sonra bu kontrolü SİLMEK hiçbir testi kırmıyor, çünkü tek eğik çizgiyle başlayan göreli
        // bir adres <c>Uri</c> birleştirmesinde konağı DEĞİŞTİREMEZ. Yani bugün ulaşılamaz bir
        // savunmadır ve bilerek bırakılmıştır: biçim denetimlerinden biri ileride gevşetilirse
        // (ör. mutlak adres kabul edilirse) tek kalan kapı budur.
        if (!IsOwnHost(candidate, target) || candidate.Port != target.Port
            || !string.Equals(candidate.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    /// <summary>Sitenin sahiplendiği konak adları kümesi (bağlamalardan).</summary>
    public static IReadOnlySet<string> BuildAllowedHosts(IEnumerable<string?> hostNames)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hostName in hostNames)
        {
            foreach (var form in HostForms(hostName))
            {
                hosts.Add(form);
            }
        }

        return hosts;
    }

    /// <summary>
    /// Bir konak adının karşılaştırmada kullanılacak biçimleri: küçük harfli hâli ve punycode
    /// (IDN) karşılığı. Türkçe alan adları (<c>örnek.test</c>) tarayıcıda punycode'a çevrilir;
    /// yalnız düz metin karşılaştırılsaydı kendi sayfası "dış bağlantı" sayılırdı.
    /// </summary>
    private static IEnumerable<string> HostForms(string? hostName)
    {
        var host = (hostName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == "*")
        {
            yield break;
        }

        yield return host;

        if (Uri.TryCreate($"https://{host}/", UriKind.Absolute, out var parsed))
        {
            yield return parsed.IdnHost.ToLowerInvariant();
            yield return parsed.Host.ToLowerInvariant();
        }
    }

    private static bool IsOwnHost(Uri uri, SeoCrawlTarget target)
        => target.AllowedHosts.Contains(uri.Host.TrimEnd('.'))
            || target.AllowedHosts.Contains(uri.IdnHost.TrimEnd('.'));

    private static Uri BuildBaseUri(SeoCrawlTarget target, string currentPath)
    {
        var path = string.IsNullOrEmpty(currentPath) || !currentPath.StartsWith('/') ? "/" : currentPath;
        return new Uri($"{target.Scheme}://{target.Host}:{target.Port}{path}", UriKind.Absolute);
    }

    /// <summary>http/https DIŞINDA bir şema ile başlıyor mu (şemasız/göreli değerlerde false).</summary>
    private static bool HasForeignScheme(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        // "/a:b" ya da "a/b:c" gibi değerlerde iki nokta şema ayıracı değildir.
        var prefix = value[..separator];
        if (prefix.Any(character => character is '/' or '?' or '#'))
        {
            return false;
        }

        return !AllowedSchemes.Contains(prefix, StringComparer.OrdinalIgnoreCase);
    }
}
