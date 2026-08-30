using System.Net;
using AdaPanel.Infrastructure.Support;

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
    /// <remarks>
    /// Liste ve karar <see cref="OutboundAddressGuard"/>'dadır; burada yalnızca kümenin sözleşmesi
    /// korunuyor. İki kopya tutmanın bedeli ölçüldü (bkz. o sınıfın açıklaması).
    /// </remarks>
    public static bool IsBlockedHostName(string host) => OutboundAddressGuard.IsBlockedHostName(host);

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

        // GÖVDE ARTIK BURADA DEĞİL (OutboundAddressGuard.FindBlockedHostAsync). Aynı yirmi satır
        // bu depoda DÖRT kez yazılmıştı (burası, CronController, SiteHostingSettingsService ve
        // yeni bağlanan beş hedef). Kopyaların bedeli bu dosyanın kendi açıklamasında ölçülmüş
        // olarak duruyor; kalan tek fark POLİTİKAYDI, o da artık parametreye taşındı.
        //
        // Kümenin politikası: ÖLÇEMEDİK = GEÇEMEZ (BlockUnresolvedHost). Gerekçe sınıf
        // açıklamasında — buradaki istek bir YÖNETİCİ BELİRTECİ taşır.
        return await OutboundAddressGuard.FindBlockedHostAsync(
            host,
            (ad, iptal) => resolver(ad, iptal),
            OutboundTargetPolicy.Strict,
            cancellationToken);
    }

    /// <summary>
    /// Adres, dışarıya kapalı/özel/tuzak bir aralıkta mı?
    /// </summary>
    /// <remarks>
    /// Aralık listesi <see cref="OutboundAddressGuard"/>'dadır. Kümeye özgü bir liste DEĞİLDİR:
    /// "buranın içi" tanımı panelin her dış isteği için aynıdır ve ikinci bir kopya, ölçülmüş
    /// biçimde, gerçek bir açık üretti (cron kopyası <c>[::ffff:169.254.169.254]</c>'ü geçiriyordu).
    /// </remarks>
    public static bool IsBlockedAddress(IPAddress? ip) => OutboundAddressGuard.IsBlockedAddress(ip);

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
