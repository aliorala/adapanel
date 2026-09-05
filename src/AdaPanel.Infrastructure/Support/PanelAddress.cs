using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Panelin DIŞARIYA verilen adresini üreten tek yer (hoş geldin e-postası, parola sıfırlama
/// bağlantısı, tek kullanımlık giriş bağlantısı, abonelikten çıkma onay bağlantısı).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek yerde:</b> aynı üç satır (şema + adres + "443 ise portu yazma" + gizli giriş yolu)
/// iki serviste ayrı ayrı yazılıydı. Üçüncü çağrı yeri eklenince kopya sayısı üçe çıkacaktı; birinin
/// düzeltilip diğerinin unutulması, kullanıcıya AÇILMAYAN bir bağlantı göndermek demektir ve bunu
/// kimse hata olarak bildirmez, sadece "panel çalışmıyor" der.
/// </para>
/// <para>
/// <b>ADRES İSTEMCİDEN ALINMAZ — kaldırılan <c>fallbackHost</c> yedeği.</b> Bu tip eskiden, ayar
/// boşsa isteğin <c>Host</c> başlığına düşen bir yedek kabul ediyordu. <c>Host</c> başlığı
/// İSTEMCİNİN yazdığı serbest metindir: <c>appsettings.json</c>'da <c>AllowedHosts</c> <c>"*"</c>
/// ve boru hattında <c>UseHostFiltering</c> YOK (ikisi de bilerek öyle — gerekçesi aşağıda). Yani
/// yedek yolu, saldırganın seçtiği bir ana adı panelin KENDİ itibarıyla giden postaya ve tek
/// kullanımlık giriş bağlantısına sokabilen bir kapıydı.
/// </para>
/// <para>
/// <b>Yedeği kaldırmak neden hiçbir şeyi bozmuyor — ÖLÇÜLDÜ:</b> yedek yalnız
/// <paramref name="serverAddress"/> BOŞKEN devreye girebilirdi, ama panel adresini üreten tek
/// kaynak olan <c>PanelPreferencesService.GetAsync</c> bu alanı asla boş bırakmaz: değer boşsa
/// <c>ResolveServerAddress()</c> makinenin ilk loopback-olmayan IPv4 adresini, o da yoksa
/// <c>Environment.MachineName</c>'i yazar. İkisi birden boş olamaz. Yani yedek yolu üretimde zaten
/// ULAŞILAMAZDI; duran tek şey istismar yüzeyiydi. Bu ölçüm
/// <c>PanelAdresiHostBasligiTests</c> içinde nöbetçi testle sabitlendi — <c>ResolveServerAddress</c>
/// bir gün kaldırılırsa o test kırılır ve buradaki yer tutucunun e-postalara sızacağını haber verir.
/// </para>
/// <para>
/// <b>Neden <c>AllowedHosts</c> kısılmadı (fazla kapatma sınırı):</b> panele meşru olarak
/// <c>localhost</c>, <c>127.0.0.1</c>, makinenin IP'si, makine adı, kurulumda verilen panel alan
/// adı ve müşterinin SONRADAN eklediği alan adı üzerinden erişilir. Bu küme çalışma zamanında
/// değişir; statik bir listeye çevrilseydi listede olmayan her adres 400 alırdı — yani müşteriyi
/// kendi panelinden kilitlerdi. Bu, kapatmaya çalıştığımız açıktan daha kötü bir arızadır.
/// Doğru sınır, isteği reddetmek değil <b>Host başlığını güvenlik-etkili karara hiç sokmamaktır</b>;
/// bu tip tam olarak o sınırdır.
/// </para>
/// </remarks>
public static class PanelAddress
{
    /// <summary>
    /// Adres hiç bilinmiyorsa e-posta metnine yazılan, insanın düzeltmesi beklenen yer tutucu.
    /// </summary>
    /// <remarks>
    /// Üretimde görünmemesi beklenir (yukarıdaki ölçüme bakınız); GÖRÜNÜRSE de zararsızdır:
    /// bağlantı açılmaz ve düzeltilmesi gerektiği okuyanın gözüne çarpar. Yerine istemcinin
    /// verdiği ana adı yazmak ise sessizce ÇALIŞAN ama saldırgana giden bir bağlantı üretirdi —
    /// yanlış yön bilerek "ölü bağlantı" tarafına seçildi.
    /// </remarks>
    public const string UnknownHostPlaceholder = "<sunucu-adresi>";

    /// <summary>
    /// Panelin kök adresi (şema + host + gerekiyorsa port), sonunda eğik çizgi YOK.
    /// </summary>
    /// <param name="serverAddress">
    /// Ayarlardaki panel adresi. Kaynağı <c>PanelPreferencesService</c> olduğunda boş olamaz.
    /// </param>
    /// <param name="httpsPort">Panelin tercih ettiği HTTPS portu (443 ise adrese yazılmaz).</param>
    public static string BuildBaseUrl(string? serverAddress, int httpsPort)
    {
        // ÖLÇÜLDÜ (düzeltmeden önceki gerçek çıktılar): ayar değeri buraya ÇİĞ olarak
        // yapıştırılıyordu ve yöneticinin makul girdileri geçersiz URL üretiyordu —
        //   "panel.ornek.com:8890"  -> https://panel.ornek.com:8890:8888   (iki port)
        //   "https://panel.ornek.com" -> https://https://panel.ornek.com:8888 (iki şema)
        //   "fd00::1"               -> https://fd00::1:8888                (parantezsiz IPv6)
        //   "panel.ornek.com/"      -> https://panel.ornek.com/:8888       (port yolun içinde)
        // Bu bağlantılar müşteriye giden postalarda (parola sıfırlama, hoş geldin, abonelikten
        // çıkma, tek kullanımlık giriş) taşınıyordu; hiçbiri açılmaz, panel "gönderildi" der.
        if (!TryParseAddress(serverAddress, out var parcalar))
        {
            // Ayrıştırılamayan değer için host UYDURULMAZ. Yer tutucu, sınıfın en başında
            // gerekçelendirilen "ölü ama gözle görülür bağlantı" doktrininin aynısıdır.
            return $"https://{UnknownHostPlaceholder}";
        }

        // PORT ÖNCELİĞİ — bilinçli karar: ADRESTE AÇIKÇA YAZILMIŞ PORT KAZANIR.
        //
        // Bu dal yalnız ESKİ KAYITLAR için bir kurtarma yoludur: kaydetme yolu
        // (PanelPreferencesService.UpdateAsync) artık portu ya eler ya reddeder, yani yeni
        // kayıtlarda ServerAddress içinde port oluşmaz. Diskte zaten "host:8890" yazan bir
        // kurulumda ise seçim şudur: ya yöneticinin yazdığı portu kullan, ya ayardaki portu.
        // Yazılan port seçildi çünkü yönetici o adresi panele GİREBİLDİĞİ tarayıcı çubuğundan
        // kopyalar — dışarıdan çalıştığı kanıtlı tek değer odur. Ters vekil/port yönlendirme
        // kurulumunda panelin iç dinleme portu (httpsPort) dışarıdan zaten erişilemez.
        var port = parcalar.Port ?? httpsPort;
        var portParcasi = port == 443 ? string.Empty : $":{port}";
        return $"https://{parcalar.UrlHost}{portParcasi}";
    }

    /// <summary>
    /// Panel adresinin kanonik parçaları: URL'ye yazılabilir host + (varsa) açık port.
    /// </summary>
    public readonly record struct PanelAddressParts
    {
        /// <summary>
        /// Kanonik host — küçük harf, punycode (ASCII), IPv6 PARANTEZSİZ.
        /// <para>
        /// Parantezsiz olması bilinçli: <c>ServerAddress</c> bu kod tabanında yalnız URL üretmez;
        /// DNS A kaydının değeri (<c>DnsService.ResolveDefaultIpv4Address</c>), FTP istemcisine
        /// gösterilen sunucu adresi, posta DNS yayını ve uzak veritabanı bağlantı host'u da aynı
        /// alandan okunur. Bu tüketiciler köşeli parantez ya da port beklemez.
        /// </para>
        /// </summary>
        public string Host { get; init; }

        /// <summary>Adres metninde AÇIKÇA yazılmış port; yoksa <c>null</c>. Şemadan çıkarım YAPILMAZ.</summary>
        public int? Port { get; init; }

        /// <summary><see cref="Host"/> bir IPv6 adresi mi (URL'de köşeli parantez gerektirir).</summary>
        public bool IsIpV6 { get; init; }

        /// <summary>URL'nin otorite bölümüne yazılacak biçim: IPv6 ise köşeli parantezli.</summary>
        public string UrlHost => IsIpV6 ? $"[{Host}]" : Host;
    }

    /// <summary>
    /// Yöneticinin yazdığı serbest metni kanonik host + port'a ayrıştırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kurallar ve NEDEN böyle seçildikleri:</b>
    /// </para>
    /// <para>
    /// 1) <b>Şema (<c>http://</c> / <c>https://</c>) sökülür ve YOK SAYILIR.</b> Şemadan port
    /// çıkarımı (https → 443) bilerek YAPILMAZ: <c>https://</c> yazmak çok masum bir alışkanlıktır
    /// ve bundan 443 çıkarmak, panel 8888'de dinlerken çalışan bir kurulumu sessizce ölü bağlantıya
    /// çevirirdi. Üretilen bağlantı her hâlükârda <c>https</c>'tir — parola sıfırlama bağlantısını
    /// <c>http</c>'ye düşürmek bir güvenlik gerilemesi olurdu.
    /// </para>
    /// <para>
    /// 2) <b>Yol / sorgu / parça (<c>/…</c>, <c>?…</c>, <c>#…</c>) sökülür.</b> Burada üretilen şey
    /// KÖK adrestir; yolu <c>BuildEntryUrl</c> kendisi ekler. Sökülmediğinde port yolun ARDINA
    /// yazılıyordu (<c>https://host/:8888</c>) — ölçüldü.
    /// </para>
    /// <para>
    /// 3) <b>Küçültme <c>ToLowerInvariant</c> ile yapılır.</b> Türkçe kültürde <c>ToLower</c>,
    /// <c>"I"</c> harfini <c>"ı"</c> yapar: <c>IZMIR.COM</c> → <c>ızmır.com</c> — var olmayan bir
    /// alan adı. Panel Türkçe kurulumlarda çalışıyor, bu tuzak teoriik değil.
    /// </para>
    /// <para>
    /// 4) <b>Unicode alan adı punycode'a çevrilir</b> (<c>örnek.com.tr</c> →
    /// <c>xn--rnek-hpa.com.tr</c>). REDDEDİLMEZ, ÇEVRİLİR: Türkçe harfli alan adları meşrudur, ama
    /// e-posta gövdesindeki bir <c>href</c> içinde çiğ Unicode host taşıyan bağlantıyı her istemci
    /// doğru kodlamaz — tam da kapatmaya çalıştığımız "müşteri bağlantıyı açamıyor" sınıfı.
    /// </para>
    /// <para>
    /// 5) <b>IPv6 parantezli de parantezsiz de kabul edilir</b> ve kanonik biçime getirilir;
    /// URL'ye yazılırken <see cref="PanelAddressParts.UrlHost"/> parantezi ekler.
    /// </para>
    /// <para>
    /// <b>Neden <c>AcmeArgumentRules.IsCommandSafeHost</c> yeniden kullanılmadı:</b> o kural
    /// win-acme KOMUT SATIRI güvenliği içindir ve <c>*.ornek.com</c> joker önekini KABUL EDER —
    /// joker bir sertifika adı olarak meşru, panel adresi olarak anlamsızdır. Ayrıca port, şema,
    /// yol ve IPv6 köşeli parantezi hakkında hiçbir şey bilmez. İki kural aynı görünüp farklı
    /// sorulara cevap veriyor; birleştirmek ikisini de yanlışlardı.
    /// </para>
    /// </remarks>
    public static bool TryParseAddress(string? serverAddress, out PanelAddressParts parts)
    {
        parts = default;

        var adres = (serverAddress ?? string.Empty).Trim();
        if (adres.Length == 0)
        {
            return false;
        }

        // 1) Şema.
        foreach (var sema in new[] { "https://", "http://" })
        {
            if (adres.StartsWith(sema, StringComparison.OrdinalIgnoreCase))
            {
                adres = adres[sema.Length..];
                break;
            }
        }

        // 2) Yol / sorgu / parça.
        var kesme = adres.IndexOfAny(['/', '?', '#', '\\']);
        if (kesme >= 0)
        {
            adres = adres[..kesme];
        }

        adres = adres.Trim().TrimEnd('.');
        if (adres.Length == 0)
        {
            return false;
        }

        // 3) Host ile portu ayır.
        string hostMetni;
        int? port = null;

        if (adres[0] == '[')
        {
            var kapanis = adres.IndexOf(']', StringComparison.Ordinal);
            if (kapanis < 2)
            {
                return false;
            }

            hostMetni = adres[1..kapanis];
            var kalan = adres[(kapanis + 1)..];
            if (kalan.Length > 0)
            {
                if (kalan[0] != ':' || !TryParsePort(kalan[1..], out var kapaliPort))
                {
                    return false;
                }

                port = kapaliPort;
            }
        }
        else if (adres.Count(karakter => karakter == ':') > 1)
        {
            // Birden fazla ':' → parantezsiz yazılmış IPv6. Port EKLENEMEZ (ayrıştırılamaz),
            // zaten "fd00::1:8890" gibi bir metin IPv6'nın kendisinden ayırt edilemez.
            hostMetni = adres;
        }
        else
        {
            var ikiNokta = adres.LastIndexOf(':');
            if (ikiNokta >= 0)
            {
                if (!TryParsePort(adres[(ikiNokta + 1)..], out var acikPort))
                {
                    return false;
                }

                port = acikPort;
                hostMetni = adres[..ikiNokta];
            }
            else
            {
                hostMetni = adres;
            }
        }

        hostMetni = hostMetni.Trim().TrimEnd('.');
        if (hostMetni.Length == 0)
        {
            return false;
        }

        // 4) IP adresi mi? Öyleyse kanonik biçime getir (küçük harf, sıkıştırılmış IPv6).
        if (IPAddress.TryParse(hostMetni, out var ip))
        {
            parts = new PanelAddressParts
            {
                Host = ip.ToString(),
                Port = port,
                IsIpV6 = ip.AddressFamily == AddressFamily.InterNetworkV6,
            };
            return true;
        }

        // Parantezli yazılmış ama IP olmayan değer ("[ornek.com]") geçersizdir.
        if (adres[0] == '[')
        {
            return false;
        }

        if (!TryNormalizeDnsName(hostMetni, out var dnsAdi))
        {
            return false;
        }

        parts = new PanelAddressParts { Host = dnsAdi, Port = port, IsIpV6 = false };
        return true;
    }

    /// <summary>
    /// Kanonik host metni (port ve şema OLMADAN); ayrıştırılamazsa boş dize.
    /// <c>PanelPreferencesService</c> kaydetme yolu bunu kullanır.
    /// </summary>
    public static string NormalizeHost(string? serverAddress)
        => TryParseAddress(serverAddress, out var parcalar) ? parcalar.Host : string.Empty;

    private static bool TryParsePort(string metin, out int port)
    {
        port = 0;
        if (metin.Length is 0 or > 5)
        {
            return false;
        }

        foreach (var karakter in metin)
        {
            if (karakter is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(metin, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;
    }

    /// <summary>
    /// DNS adını kanonik ASCII biçime getirir (küçük harf + punycode) ve biçimsel olarak doğrular.
    /// </summary>
    /// <remarks>
    /// <b>FAZLA KAPATMA SINIRI — kural kasten GENİŞ:</b> geçmesi gerekenler düz alan adı, alt alan
    /// adı, uzun TLD (<c>.com.tr</c>, <c>.travel</c>), tireli ad, punycode (<c>xn--</c>), Türkçe
    /// harfli ad ve TEK ETİKETLİ makine adıdır. Sonuncusu kritik: panel adresi hiç ayarlanmamışsa
    /// <c>PanelPreferencesService.ResolveServerAddress()</c> <c>Environment.MachineName</c> yazar
    /// (alt çizgi içerebilir) — bunu reddetmek paneli kendi varsayılan değeriyle kilitlerdi.
    /// Reddedilen tek şey, bir host adında ZATEN geçerli olmayan karakter/yapılardır.
    /// </remarks>
    private static bool TryNormalizeDnsName(string value, out string normalized)
    {
        normalized = string.Empty;

        // Türkçe kültür tuzağı için Invariant (bkz. TryParseAddress notu 3).
        var ad = value.ToLowerInvariant();

        if (ad.Contains("..", StringComparison.Ordinal) || ad[0] == '.' || ad[0] == '-')
        {
            return false;
        }

        // ASCII dışı karakter varsa punycode'a çevir. IdnMapping geçersiz Unicode dizilerinde
        // fırlatır; o durum "geçersiz adres"tir, çökme değil.
        if (ad.Any(karakter => karakter > 127))
        {
            try
            {
                ad = new IdnMapping { AllowUnassigned = true, UseStd3AsciiRules = false }.GetAscii(ad);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // 253: DNS adının tam uzunluk sınırı (punycode ÇEVRİMİNDEN SONRA ölçülür — Unicode ad
        // kısa görünüp ASCII karşılığı sınırı aşabilir).
        if (ad.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var etiket in ad.Split('.'))
        {
            if (etiket.Length is 0 or > 63)
            {
                return false;
            }

            foreach (var karakter in etiket)
            {
                var gecerli = karakter is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
                if (!gecerli)
                {
                    return false;
                }
            }
        }

        normalized = ad;
        return true;
    }

    /// <summary>Panelin GİRİŞ adresi: kök adres + gizli giriş yolu (sonunda eğik çizgi YOK).</summary>
    public static string BuildEntryUrl(string? serverAddress, int httpsPort, string securePath)
    {
        var yol = (securePath ?? string.Empty).Trim('/');
        var kok = BuildBaseUrl(serverAddress, httpsPort);
        return yol.Length == 0 ? kok : $"{kok}/{yol}";
    }

    /// <summary>
    /// Panel adresi İNTERNETTEN açılabilecek bir ad/adres mi? (Yalnızca BİÇİMSEL sınıflandırma.)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden var — ölçülmüş arıza:</b> panel adresi ayarlanmadığında
    /// <c>PanelPreferencesService.ResolveServerAddress()</c> makinenin ilk loopback-olmayan IPv4
    /// adresini yazar. NAT arkasındaki bir sunucuda bu <c>192.168.1.5</c> gibi bir İÇ adrestir
    /// (ölçüldü: aday adreslerin tamamı özel aralıkta olabiliyor, üstelik ilk sıraya bir SANAL
    /// ağ adaptörünün adresi düşebiliyor — yani seçilen değer gerçek LAN adresi bile
    /// olmayabilir). Panelin müşteriye GÖNDERDİĞİ bağlantılar
    /// (parola sıfırlama, hoş geldin postası, abonelikten çıkma onayı, tek kullanımlık giriş)
    /// bu adresi taşır; müşteri bağlantıyı AÇAMAZ, panel ise "gönderildi" der. Panel kendi
    /// adresinin ölü olduğunu hiç fark etmiyordu.
    /// </para>
    /// <para>
    /// <b>Bu metot ENGELLEMEZ, SÖYLER.</b> Dönüş değeri hiçbir gönderimi durdurmaz; yalnız
    /// yöneticiye gösterilen bir uyarıyı besler (Ayarlar &gt; Genel formu + Danışman ekranı).
    /// Gerekçe: <c>192.168.x.x</c> bir kurulumda ARIZA, ötekinde DOĞRU adrestir (şirket içi
    /// hosting, geliştirme kurulumu). Gönderimi kesmek, çalışan kurulumu kırardı — kapatılan
    /// sorundan kötü. Plesk de aynı yerde durur: hostname/IP'yi doğrular ve NAT arkası için ayrı
    /// bir "public IP" alanı sunar, ama posta göndermeyi engellemez.
    /// </para>
    /// <para>
    /// <b>Sınıflandırma yalnız BİÇİMSELDİR</b> — DNS sorulmaz, bağlantı denenmez. "Genel" dönen
    /// bir ad için DNS kaydı olmayabilir; ölçülen tek şey, adresin genel internette
    /// ÇÖZÜLEBİLME İHTİMALİ olup olmadığıdır. Ağa çıkan bir kontrol, ayar okuma yolunu (her
    /// istekte çağrılıyor) ağ gecikmesine bağlardı.
    /// </para>
    /// </remarks>
    public static bool IsExternallyReachable(string? serverAddress)
    {
        // Ayrıştırma URL üretimiyle AYNI kaynaktan gelir: köşeli parantez, port, şema ve yol
        // soyma kuralları burada bir daha yazılsaydı, ikisinin bir gün ayrışması "uyarı yok ama
        // bağlantı ölü" (ya da tersi) demek olurdu. Ayrıştırılamayan değer için "erişilebilir"
        // demek, tam da kapatılan sessiz başarı bildirimi olurdu.
        if (!TryParseAddress(serverAddress, out var parcalar))
        {
            return false;
        }

        var adres = parcalar.Host;

        if (IPAddress.TryParse(adres, out var ip))
        {
            return IsExternallyReachableAddress(ip);
        }

        // TEK ETİKETLİ ad (nokta yok): makine adı / NetBIOS adı. Genel DNS'te çözülmez —
        // `ResolveServerAddress()` hiç IPv4 bulamadığında tam olarak bunu yazıyor.
        if (!adres.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        // Genel DNS'e DELEGE EDİLMEYEN adlar (RFC 6761 `.test`/`.invalid`/`.example`/`localhost`,
        // RFC 6762 `.local` mDNS, RFC 8375 `.home.arpa`, ICANN'in özel kullanıma ayırdığı
        // `.internal`). Bunlar biçim olarak alan adına benzer ama internetten çözülmez.
        foreach (var sonEk in CozulmeyenSonEkler)
        {
            if (adres.EndsWith(sonEk, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] CozulmeyenSonEkler =
    [
        ".localhost", ".local", ".home.arpa", ".internal", ".invalid", ".test", ".example"
    ];

    private static bool IsExternallyReachableAddress(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv4-eşlenmiş IPv6 ("::ffff:192.168.1.5") gerçekte bir IPv4 adresidir.
            if (ip.IsIPv4MappedToIPv6)
            {
                return IsExternallyReachableAddress(ip.MapToIPv4());
            }

            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            {
                return false;
            }

            // fc00::/7 — benzersiz yerel adresler (IPv6'nın 10.x karşılığı).
            var v6 = ip.GetAddressBytes();
            return (v6[0] & 0xFE) != 0xFC;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            0 => false,                                  // 0.0.0.0/8 — "bu ağ"
            10 => false,                                 // 10/8 özel
            127 => false,                                // 127/8 loopback
            100 => b[1] < 64 || b[1] > 127,               // 100.64/10 taşıyıcı NAT (RFC 6598)
            169 => b[1] != 254,                          // 169.254/16 bağlantı-yerel
            172 => b[1] < 16 || b[1] > 31,                // 172.16/12 özel
            192 => b[1] != 168,                          // 192.168/16 özel
            >= 224 => false,                             // çoklu gönderim + ayrılmış + 255.255.255.255
            _ => true
        };
    }
}
