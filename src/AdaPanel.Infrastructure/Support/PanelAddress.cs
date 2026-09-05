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
        var adres = (serverAddress ?? string.Empty).Trim();
        if (adres.Length == 0)
        {
            return $"https://{UnknownHostPlaceholder}";
        }

        var portParcasi = httpsPort == 443 ? string.Empty : $":{httpsPort}";
        return $"https://{adres}{portParcasi}";
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
        var adres = (serverAddress ?? string.Empty).Trim().TrimEnd('.');
        if (adres.Length == 0)
        {
            return false;
        }

        // IPv6 köşeli parantezli de yazılabilir ("[2001:db8::1]").
        var cikarilan = adres.Length > 2 && adres[0] == '[' && adres[^1] == ']'
            ? adres[1..^1]
            : adres;

        if (IPAddress.TryParse(cikarilan, out var ip))
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
