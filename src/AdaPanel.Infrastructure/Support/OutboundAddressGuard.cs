using System.Net;
using System.Net.Sockets;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Panelin DIŞARI istek atarken kullandığı TEK adres denetimi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek yerde:</b> aynı liste bu depoda İKİ KEZ yazılmıştı — biri
/// <c>Cluster/ClusterAddressGuard.cs</c> (tam), diğeri <c>Api/Controllers/CronController.cs</c>
/// (eksik). İkinci kopya ÖLÇÜLDÜ ve şunları GEÇİRİYORDU:
/// </para>
/// <list type="bullet">
/// <item><c>[::ffff:169.254.169.254]</c> — IPv4-eşlemeli yazılmış BULUT META VERİ adresi.
/// <c>IPAddress.IsLoopback</c> false döner, <c>IsIPv6LinkLocal</c> false döner, <c>fc00::/7</c>
/// testi false döner; yani üç kontrol de "temiz" der ve soket 169.254.169.254'e gider.</item>
/// <item><c>[::ffff:10.0.0.5]</c>, <c>[::ffff:192.168.1.1]</c> — aynı yolla iç ağ.</item>
/// <item><c>[::]</c> — 0.0.0.0'ın IPv6 karşılığı, "bu makine".</item>
/// <item><c>100.64.0.0/10</c> — CGNAT; Alibaba Cloud meta verisi (<c>100.100.100.200</c>) burada.</item>
/// </list>
/// <para>
/// Bunlar teorik değil; her biri bu makinede <c>IPAddress.TryParse</c> + eski kontrol gövdesi
/// çalıştırılarak ölçüldü. Bir listeyi iki yerde tutmanın bedeli tam olarak budur: biri düzeltilir,
/// diğeri sessizce açık kalır. Bu sınıf o iki kopyanın ortak kaynağıdır.
/// </para>
/// <para>
/// <b>"Ad çözülemezse ne olur" kararı BURADA AMA SABİT DEĞİL</b>
/// (<see cref="OutboundTargetPolicy.BlockUnresolvedHost"/>): politika çağrı yerine göre BİLEREK
/// farklıdır (kümede istek bir yönetici belirteci taşır → çözülemeyen ad REDDEDİLİR; müşteri cron
/// adresinde istek anonimdir → geçici DNS hatası meşru bir siteyi engellememeli). Ortaklaştırılan
/// şey kararın KENDİSİ değil, kararın uygulandığı GÖVDEdir — gövde dört kez ayrı yazılmıştı.
/// </para>
/// </remarks>
public static class OutboundAddressGuard
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

    /// <summary>Ad, çözülmeden reddedilenlerden mi?</summary>
    /// <remarks>
    /// Sondaki nokta ATILIR: <c>localhost.</c> tarayıcı/çözümleyici için <c>localhost</c> ile aynı
    /// addır ama düz karşılaştırmada eşleşmez.
    /// </remarks>
    public static bool IsBlockedHostName(string? host)
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
    /// Bir ana makine adını IP adreslerine çözen taraf. Testte gerçek DNS'e çıkılmasın diye ayrı.
    /// </summary>
    public delegate Task<IPAddress[]> OutboundHostResolver(string host, CancellationToken cancellationToken);

    /// <summary>
    /// Gerçek DNS ile çözen varsayılan çözümleyici.
    /// </summary>
    /// <remarks>
    /// Tam nitelikli: bu derlemede <c>AdaPanel.Infrastructure.Dns</c> adında bir ad alanı var ve
    /// kısa <c>Dns</c> yazımı ona bağlanıp derlemeyi kırıyor (aynı tuzağa ClusterAddressGuard ve
    /// SiteHostingSettingsService da düşmüştü — üçüncü kez düşülmesin diye çözümleyici de artık
    /// burada).
    /// </remarks>
    public static readonly OutboundHostResolver SystemResolver =
        (host, cancellationToken) => System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    /// Adı çözer ve ENGELLİ bir adrese çözülüyorsa o adresi/adı döner; temizse <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN AD DENETLEMEK YETMEZ:</b> engel listesi IP ARALIKLARIDIR, ama kullanıcı bir AD
    /// yazar. Saldırgan kendi alan adının A kaydını <c>127.0.0.1</c> ya da <c>169.254.169.254</c>
    /// yapabilir; ad tarafında bakan her kontrol o adı "dış bir alan adı" diye geçirir. Bu yüzden
    /// ad ÇÖZÜLÜR ve ÇIKAN ADRESLERİN HEPSİ denetlenir.
    /// </para>
    /// <para>
    /// <b>TEK engelli adres yeterlidir</b> (<c>FirstOrDefault</c>, <c>All</c> değil). Bir ad hem
    /// genel hem iç adrese çözülüyorsa hangisine bağlanılacağını biz seçmiyoruz; "çoğunluk temiz"
    /// diye geçmek korumayı yazı-tura oyununa çevirirdi.
    /// </para>
    /// <para>
    /// <b>ONDALIK/SEKİZLİK/ONALTILIK IP YAZIMLARI — ÖLÇÜLDÜ, EK KOD GEREKMİYOR.</b> Bu makinede
    /// .NET 8 ile ölçüldü: <c>IPAddress.TryParse</c> <c>"2130706433"</c>, <c>"0x7f000001"</c>,
    /// <c>"017700000001"</c> ve <c>"127.1"</c> girdilerinin DÖRDÜNÜ DE kabul eder ve hepsini
    /// <c>127.0.0.1</c>'e normalleştirir — yani aşağıdaki literal dalı bunları zaten yakalar.
    /// Adres bir URL'den geliyorsa ikinci bir kalkan daha var: <c>Uri</c> aynı dört yazımı
    /// <c>DnsSafeHost</c> içinde kendiliğinden <c>127.0.0.1</c>'e çevirir (o da ölçüldü). Bu
    /// yazımlar için AYRI bir ayrıştırıcı YAZILMADI, çünkü yazılsaydı çerçevenin ayrıştırıcısıyla
    /// ayrışabilen İKİNCİ bir ayrıştırıcı olurdu — bu dosyanın var olma sebebi tam olarak o.
    /// </para>
    /// <para>
    /// <b>BU KAPI TEK BAŞINA YÖNLENDİRMEYİ ve DNS rebinding'i GÖRMEZ.</b> Burada ölçülen şey
    /// "istek atılmadan ÖNCE adın nereye çözüldüğü"dür. Dış bir adres <c>302 Location:
    /// http://169.254.169.254/</c> ile iç ağa atlarsa, ya da DNS kaydı iki çağrı arasında
    /// değişirse, bu denetim atlanmış olur. Kapatmanın tek dürüst yolu bağlantı ANINDA
    /// denetlemektir; o denetim <see cref="OutboundConnectGuard"/>'dadır ve AYNI GÖVDEYİ
    /// (<see cref="InspectAsync"/>) çağırır — yani kural burada tek nüshadır, ikinci bir liste
    /// yoktur. Bu metot hâlâ gerekli: kullanıcıya "adres yasak" hatasını kayıt/doğrulama anında,
    /// soket açılmadan ve anlaşılır bir hata koduyla verir.
    /// </para>
    /// </remarks>
    public static async Task<string?> FindBlockedHostAsync(
        string? host,
        OutboundHostResolver resolver,
        OutboundTargetPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        // AÇIK KAPI. Yasak KULLANICIDAN GELEN adreslere uygulanır; panelin kendi sabit yerel
        // adresleri (phpMyAdmin vekili, Stalwart yönetimi, sağlık kontrolü, küme) buradan HİÇ
        // geçmez. Ama bazı kurulumlarda kullanıcıdan gelen adres de MEŞRU biçimde içeridedir:
        // iç ağdaki bir NAS'a yedek atmak, LAN'daki bir SMTP aktarıcısı, aynı makinedeki bir
        // siteye vuran zamanlanmış görev. Körlemesine yasaklamak bu kurulumları kırardı.
        //
        // BURADA KISA DEVRE: açık kapı açıkken adı çözmenin bir anlamı yok (karar zaten "geç"),
        // ve kayıt anında gereksiz bir DNS gidiş-dönüşü eklemek istemiyoruz. Bağlantı anındaki
        // denetim aynı kararı verir ama adresleri ZATEN çözmek zorundadır (soketi açacak).
        if (policy.AllowInternalTargets)
        {
            return null;
        }

        var inceleme = await InspectAsync(host, resolver, policy, cancellationToken);
        return inceleme.BlockedTarget;
    }

    /// <summary>
    /// Adı çözer, ÇIKAN ADRESLERİ ve engel kararını BİRLİKTE döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir metot:</b> bağlantı ANINDA denetleyen taraf
    /// (<see cref="OutboundConnectGuard"/>) yalnız "yasak mı" cevabını değil, soketi açacağı
    /// ADRESLERİ de ister. Yalnız <see cref="FindBlockedHostAsync"/> olsaydı bağlantı yolu adı
    /// İKİNCİ kez çözmek zorunda kalırdı: iki çözümleme arasında kayıt değişebilir (tam olarak
    /// kapatmaya çalıştığımız DNS rebinding), yani denetlenen adres ile bağlanılan adres FARKLI
    /// olabilirdi. Tek çözümleme = denetlenen adres ile bağlanılan adres AYNI şeydir.
    /// </para>
    /// <para>
    /// Karar gövdesi (ad eleme → literal → çözümleme → aralık denetimi) TEK NÜSHA burada durur;
    /// bu dosyanın var olma sebebi ikinci nüshanın bedelinin ÖLÇÜLMÜŞ olmasıdır.
    /// </para>
    /// </remarks>
    public static async Task<OutboundInspection> InspectAsync(
        string? host,
        OutboundHostResolver resolver,
        OutboundTargetPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var normalized = (host ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            // Boş ada bağlanılamaz; açık kapı bile olsa bağlanacak bir yer yok.
            return new OutboundInspection([], policy.AllowInternalTargets ? null : string.Empty);
        }

        if (!policy.AllowInternalTargets && IsBlockedHostName(normalized))
        {
            return new OutboundInspection([], normalized);
        }

        // Düz IP yazıldıysa DNS beklenmez. (Sayısal yazımlar da bu dala düşer; gerekçe yukarıda.)
        if (IPAddress.TryParse(normalized, out var literal))
        {
            var literalYasak = !policy.AllowInternalTargets && IsBlockedAddress(literal);
            return new OutboundInspection([literal], literalYasak ? literal.ToString() : null);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await resolver(normalized, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // AÇIK KAPI AÇIKKEN "çözülemedi" bir YASAK değildir: bağlanacak adres yok, hata
            // çözümleme hatasıdır. Karışsaydı, iç ağ hedefine izin vermiş bir işletici geçici bir
            // DNS arızasında "adres yasak" hatası görüp bayrağı boşuna sorgulardı.
            return new OutboundInspection(
                [],
                policy.BlockUnresolvedHost && !policy.AllowInternalTargets ? normalized : null);
        }

        if (addresses.Length == 0)
        {
            // AÇIK KAPI AÇIKKEN "çözülemedi" bir YASAK değildir: bağlanacak adres yok, hata
            // çözümleme hatasıdır. Karışsaydı, iç ağ hedefine izin vermiş bir işletici geçici bir
            // DNS arızasında "adres yasak" hatası görüp bayrağı boşuna sorgulardı.
            return new OutboundInspection(
                [],
                policy.BlockUnresolvedHost && !policy.AllowInternalTargets ? normalized : null);
        }

        var blocked = policy.AllowInternalTargets
            ? null
            : addresses.FirstOrDefault(IsBlockedAddress)?.ToString();

        return new OutboundInspection(addresses, blocked);
    }
}

/// <summary>
/// Bir ana makine adının ÇÖZÜLMÜŞ hâli ve engel kararı.
/// </summary>
/// <param name="Addresses">
/// Adın çözüldüğü adresler. Bağlantı kuran taraf SOKETİ BUNLARA açar — yeniden çözmez.
/// </param>
/// <param name="BlockedTarget">
/// Engelli adres/ad (yoksa <c>null</c>). Boş ana makine adında boş dize döner: "adres verilmedi"
/// ile "adres temiz" birbirine karışmasın.
/// </param>
public readonly record struct OutboundInspection(IPAddress[] Addresses, string? BlockedTarget);

/// <summary>
/// Bir dış hedefin denetlenme koşulları: iç ağ açık kapısı ve "ad çözülemedi" kararı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden iki bayrak ve neden ikisi de burada:</b> "bu adres dışarıya kapalı mı" sorusunun
/// cevabı panelin her yerinde AYNIDIR (<see cref="OutboundAddressGuard.IsBlockedAddress"/>), ama
/// "kapalıysa ne yapalım" sorusunun cevabı aynı değildir. İki bayrağı politikaya taşımak, çağrı
/// yerlerinin listeyi değil yalnız KARARI seçmesini sağlar; listeyi seçebilselerdi ikinci bir
/// kopya doğardı ve bu depoda o kopyanın bedeli ölçülmüş olarak biliniyor.
/// </para>
/// </remarks>
/// <param name="AllowInternalTargets">
/// Sunucu işleticisinin bilerek açtığı kapı: iç/özel adresler hedef olabilir.
/// <c>Panel:AllowInternalOutboundTargets</c> (appsettings.json) ile açılır, VARSAYILAN KAPALI.
/// <para>
/// <b>Neden panel arayüzünde DEĞİL:</b> bu bayrak SSRF korumasını kapatır. Panelin "Administrator"
/// rolü bir WINDOWS yöneticisi değildir (aynı gerekçe <c>UpdatePackageTrust</c>'ta da yazılı);
/// bayrağı HTTP yüzeyine koymak, korunmaya çalışılan yüzeyin kendisinden korumayı kapatmaya izin
/// vermek olurdu. Dosya sistemine erişim gerektirmesi, açık kapıyı sunucu işleticisinin BİLİNÇLİ
/// bir eylemi hâline getirir.
/// </para>
/// </param>
/// <param name="BlockUnresolvedHost">
/// Ad çözülemediğinde reddedilsin mi. Çözülemeyen ada BAĞLANILAMAZ da; bu yüzden bağlantı kuran
/// yollarda (yedek, göç, SMTP) <c>true</c> hiçbir çalışan senaryoyu kapatmaz. Yalnızca isteğin
/// anonim olduğu ve geçici bir DNS arızasının meşru bir hedefi kalıcı olarak engelleyeceği
/// yerlerde <c>false</c> seçilir.
/// </param>
public readonly record struct OutboundTargetPolicy(bool AllowInternalTargets, bool BlockUnresolvedHost)
{
    /// <summary>Bağlantı kuran yollar için: açık kapı kapalı, çözülemeyen ad reddedilir.</summary>
    public static readonly OutboundTargetPolicy Strict = new(false, true);

    /// <summary>Açık kapı bayrağını uygular, "çözülemedi = reddet" kuralını korur.</summary>
    public static OutboundTargetPolicy Connecting(bool allowInternalTargets) => new(allowInternalTargets, true);
}
