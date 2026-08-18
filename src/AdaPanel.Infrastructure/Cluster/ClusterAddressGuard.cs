using System.Net;
using System.Net.Sockets;

namespace AdaPanel.Infrastructure.Cluster;

/// <summary>Bir ana makine adını IP adreslerine çözen taraf. Testte gerçek DNS'e çıkılmasın diye ayrı.</summary>
public delegate Task<IPAddress[]> ClusterHostResolver(string host, CancellationToken cancellationToken);

/// <summary>
/// Merkezin İSTEK ATACAĞI adresi denetler. Bu sınıf panelin SSRF kapısıdır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Yüzey neden var:</b> düğüm adresini kullanıcı yazar ve panel o adrese, üstelik bir yönetici
/// belirteci taşıyarak, sunucunun KENDİ ağından istek atar. Denetimsiz bırakılsaydı panel, dışarıya
/// kapalı her şeye açılan bir vekile dönerdi: <c>http://127.0.0.1:3306</c> (yerel veritabanı),
/// <c>http://10.0.0.5</c> (iç ağdaki başka müşteri), <c>http://169.254.169.254</c> (bulut meta veri
/// servisi — burada sunucunun bulut kimlik bilgileri durur; SSRF'in en pahalı hedefi budur).
/// </para>
/// <para>
/// <b>ÜÇ katman, çünkü tek katman yetmez:</b>
/// </para>
/// <list type="number">
/// <item><b>Biçim</b> (<see cref="TryNormalize"/>): şema http/https, kullanıcı bilgisi yok, yol/sorgu
/// yok, düz IP yazıldıysa engelli aralıkta değil.</item>
/// <item><b>Çözümleme</b> (<see cref="FindBlockedAddressAsync"/>): ad DNS'te engelli bir adrese
/// çözülüyorsa reddedilir. <c>ic-sunucu.ornek.com → 10.0.0.5</c> yalnız burada yakalanır.</item>
/// <item><b>Bağlantı anı</b> (<c>ClusterHttpClientFactory</c> içindeki <c>ConnectCallback</c>):
/// GERÇEKTEN bağlanılan IP yeniden denetlenir. İlk iki katman DNS'e güvenir; DNS ise kayıt kaydını
/// iki çağrı arasında değiştirebilir (DNS rebinding) — o zaman doğrulanan adres ile bağlanılan
/// adres farklı olur. Yalnız üçüncü katman soketin gittiği yeri ölçer.</item>
/// </list>
/// <para>
/// <b>DNS çözülemezse REDDEDİLİR.</b> Bu, panelin başka yerindeki (müşteri cron adresi) kuraldan
/// bilerek farklıdır: orada istek anonimdir ve geçici bir DNS hatası meşru bir siteyi engellerdi.
/// Burada istek bir YÖNETİCİ BELİRTECİ taşır; nereye gittiğini ölçemediğimiz bir adrese sır
/// göndermek, ölçebildiğimiz bir riski almaktan daha kötüdür.
/// </para>
/// </remarks>
public static class ClusterAddressGuard
{
    /// <summary>
    /// Adı ÇÖZÜLMEDEN reddedilen ana makine adları/son ekleri.
    /// </summary>
    /// <remarks>
    /// Bunlar tanım gereği "buranın içi" demektir. <c>metadata.google.internal</c> ayrıca yazılmasa
    /// da <c>.internal</c> son ekiyle yakalanır; yine de açıkça duruyor çünkü bu adı okuyan biri
    /// korumanın bulut meta verisini kapsadığını görmeli.
    /// </remarks>
    private static readonly string[] BlockedHostSuffixes =
    [
        ".localhost",
        ".local",
        ".internal",
        ".home.arpa"
    ];

    private static readonly string[] BlockedHostNames =
    [
        "localhost",
        "metadata.google.internal",
        "metadata.goog"
    ];

    /// <summary>
    /// Adresi normalize eder ve BİÇİM düzeyinde denetler.
    /// </summary>
    /// <param name="raw">Kullanıcının yazdığı adres.</param>
    /// <param name="normalized">Başarılıysa <c>https://ana-makine:port</c> (yol yok, sondaki / yok).</param>
    /// <param name="errorKey">Başarısızsa çeviri anahtarı.</param>
    public static bool TryNormalize(string? raw, out string normalized, out string errorKey)
    {
        normalized = string.Empty;
        errorKey = string.Empty;

        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            errorKey = ClusterMessages.InvalidAddress;
            return false;
        }

        // Şema yazılmadıysa https varsayılır. http varsaymak, belirteci düz metin gönderilebilir
        // hâle getirirdi; kullanıcının unutması güvenli tarafa düşmeli.
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            errorKey = ClusterMessages.InvalidAddress;
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            // file://, gopher://, ftp:// … SSRF'te sık kullanılan şema kaçışları.
            errorKey = ClusterMessages.InvalidAddress;
            return false;
        }

        // KULLANICI BİLGİSİ YASAK. "https://gercek-sunucu.test@saldirgan.example" adresinde ana
        // makine SALDIRGAN'dır; insan gözü ilk parçayı okur, ayrıştırıcı ikincisini. Bu karışıklığı
        // çözmenin tek dürüst yolu biçimi hiç kabul etmemek.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errorKey = ClusterMessages.InvalidAddress;
            return false;
        }

        // Adres bir KÖKTÜR. Yol/sorgu/parça kabul edilseydi merkez, düğümün API'si yerine düğümdeki
        // rastgele bir yola çağrı yapmaya ikna edilebilirdi.
        if (uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            errorKey = ClusterMessages.InvalidAddress;
            return false;
        }

        var host = uri.DnsSafeHost;
        if (host.Length == 0)
        {
            errorKey = ClusterMessages.InvalidAddress;
            return false;
        }

        if (IsBlockedHostName(host))
        {
            errorKey = ClusterMessages.AddressForbidden;
            return false;
        }

        // Düz IP yazıldıysa DNS'i beklemeden burada karar verilir.
        if (IPAddress.TryParse(host, out var literal) && IsBlockedAddress(literal))
        {
            errorKey = ClusterMessages.AddressForbidden;
            return false;
        }

        var portPart = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
        normalized = $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{portPart}";
        return true;
    }

    /// <summary>Ad, çözülmeden reddedilenlerden mi?</summary>
    public static bool IsBlockedHostName(string host)
    {
        var normalized = (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return true;
        }

        return BlockedHostNames.Contains(normalized, StringComparer.Ordinal)
            || BlockedHostSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Adı çözer ve ENGELLİ bir adrese çözülüyorsa o adresi döner; temizse null.
    /// </summary>
    /// <remarks>
    /// TEK bir engelli adres yeterlidir. Bir ad hem genel hem iç adrese çözülüyorsa saldırgan
    /// hangisine bağlanılacağını seçemez ama şansı %50'dir; "çoğunluk temiz" diye geçmek,
    /// korumayı yazı-tura oyununa çevirirdi.
    /// </remarks>
    public static async Task<string?> FindBlockedAddressAsync(
        string host,
        ClusterHostResolver resolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (IsBlockedHostName(host))
        {
            return host;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsBlockedAddress(literal) ? literal.ToString() : null;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await resolver(host, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // ÖLÇEMEDİK = GEÇEMEZ. Gerekçe sınıf açıklamasında.
            return host;
        }

        if (addresses.Length == 0)
        {
            return host;
        }

        return addresses.FirstOrDefault(IsBlockedAddress)?.ToString();
    }

    /// <summary>
    /// Adres, dışarıya kapalı/özel/tuzak bir aralıkta mı?
    /// </summary>
    /// <remarks>
    /// Liste RFC aralıklarına göre yazıldı; her satırın yanında hangi aralık olduğu duruyor çünkü
    /// bir aralığı yanlışlıkla silmek, silinen aralığın tamamını panele açmak demektir.
    /// </remarks>
    public static bool IsBlockedAddress(IPAddress? ip)
    {
        if (ip is null)
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 ve :: önce elenir; aşağıdaki gömülü-IPv4 çıkarımı bunları da "IPv4 gibi" görür
            // (sonuçta yine engellenirlerdi ama sebep yanlış okunurdu).
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            // IPv4-EŞLEMELİ BİÇİMLER ÇÖZÜLÜR. "[::ffff:127.0.0.1]", "[::127.0.0.1]" ve 6to4
            // "[2002:7f00:1::]" birer IPv6 adresidir ama gittikleri yer 127.0.0.1'dir; eşlemeyi
            // açmadan bakan bir kontrol bunları "bilinmeyen IPv6" sayıp GEÇİRİRDİ.
            var embedded = ExtractEmbeddedIPv4(ip);
            if (embedded is not null)
            {
                return IsBlockedAddress(embedded);
            }

            var v6 = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal                 // fe80::/10
                || ip.IsIPv6SiteLocal                 // fec0::/10 (kullanımdan kalktı ama hâlâ yönlendirilmez)
                || ip.IsIPv6Multicast                 // ff00::/8
                || (v6[0] & 0xFE) == 0xFC;            // fc00::/7 benzersiz yerel (AWS IPv6 meta verisi burada)
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            // IPv4/IPv6 dışında bir aile (AppleTalk, IPX…): tanımadığımız şeye bağlanmayız.
            return true;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 0                                        // 0.0.0.0/8   "bu ağ"
            || b[0] == 10                                       // 10.0.0.0/8  özel
            || b[0] == 127                                      // 127.0.0.0/8 geri döngü
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)       // 100.64.0.0/10 CGNAT (Alibaba meta verisi burada)
            || (b[0] == 169 && b[1] == 254)                     // 169.254.0.0/16 link-local -> 169.254.169.254 BULUT META VERİSİ
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)        // 172.16.0.0/12 özel
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)          // 192.0.0.0/24 IETF protokol atamaları
            || (b[0] == 192 && b[1] == 168)                     // 192.168.0.0/16 özel
            || (b[0] == 198 && (b[1] & 0xFE) == 18)             // 198.18.0.0/15 kıyaslama
            || b[0] >= 224;                                     // 224.0.0.0/4 çoklu gönderim + 240.0.0.0/4 ayrılmış (255.255.255.255 dahil)
    }

    /// <summary>
    /// IPv6 adresinin İÇİNE gömülü IPv4 adresini çıkarır (yoksa null).
    /// </summary>
    /// <remarks>
    /// Üç biçim: IPv4-eşlemeli (<c>::ffff:a.b.c.d</c>), IPv4-uyumlu (<c>::a.b.c.d</c>) ve 6to4
    /// (<c>2002:aabb:ccdd::</c>). Hepsi de "IPv6 yazıp IPv4'e gitmenin" yollarıdır.
    /// </remarks>
    private static IPAddress? ExtractEmbeddedIPv4(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();

        if (ip.IsIPv4MappedToIPv6)
        {
            return ip.MapToIPv4();
        }

        // 6to4: 2002::/16, sonraki 4 bayt IPv4 adresidir.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return new IPAddress(bytes[2..6]);
        }

        // IPv4-uyumlu: ilk 12 bayt sıfır ve kalan sıfırdan farklı (::/128 ve ::1 buraya girmez).
        if (bytes.Take(12).All(value => value == 0) && bytes[12..].Any(value => value != 0))
        {
            return new IPAddress(bytes[12..]);
        }

        return null;
    }

    /// <summary>
    /// Gerçek DNS ile çözen varsayılan çözümleyici.
    /// </summary>
    /// <remarks>
    /// Tam nitelikli: bu derlemede <c>AdaPanel.Infrastructure.Dns</c> adında bir ad alanı var ve
    /// kısa <c>Dns</c> yazımı ona bağlanıp derlemeyi kırıyor.
    /// </remarks>
    public static readonly ClusterHostResolver SystemResolver =
        (host, cancellationToken) => System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);
}
