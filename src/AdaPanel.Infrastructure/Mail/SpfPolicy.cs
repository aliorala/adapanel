using System.Net;
using System.Net.Sockets;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Stalwart posta yolunun SPF politikasinin TEK KAYNAGI.
/// </summary>
/// <remarks>
/// <para>
/// Ayni SPF kurali eskiden UC ayri yerde ELLE yaziliydi: <c>MailService.BuildSpfValue</c>,
/// <c>DnsService.CreateDefaultRecords</c> ve <c>DnsService</c> bolge sablonundaki
/// <c>"v=spf1 mx a ip4:{ip} ~all"</c> literali. IPv4 girdide ucu de AYNI ciktiyi veriyordu;
/// ama <b>IPv4 cozulemeyen</b> (yalniz IPv6 / NIC yok) bir sunucuda sablon yolu
/// <c>{ip}</c>'yi bos string'e cevirip <c>"v=spf1 mx a ip4: ~all"</c> -- yani <c>ip4:</c>
/// mekanizmasi BOS -- BICIMSIZ bir SPF uretiyordu. Bicimsiz SPF = <c>permerror</c>; birçok
/// alici bunu SPF hatasi sayar ve MESRU giden posta reddedilir/isaretlenir. Diger iki uretici
/// ise ayni durumda dogru sekilde <c>"v=spf1 mx a ~all"</c>'e iniyordu. Ayrisma KAZAYDI
/// (ayni kuralin birden cok kopyasi), gerekceli degil: bu yuzden tek kaynaga indirildi.
/// </para>
/// <para>
/// <b>MailEnable yolu bilerek HARIC.</b> <c>MailEnableMailService</c> DKIM URETMEDIGINDEN
/// <c>v=spf1 a mx ~all</c> + <c>p=none</c> yazar; bu ayrisma GEREKCELIDIR (DKIM'siz bir
/// sunucuda <c>p=quarantine</c>'e cekmek teslimati bozabilir) ve bu tek kaynaga
/// KATILMAZ -- ayri backend, ayri politika.
/// </para>
/// </remarks>
public static class SpfPolicy
{
    /// <summary>
    /// Verilen sunucu adresine gore SPF TXT degerini uretir. Adres GERCEK bir IPv4 ise
    /// <c>ip4:</c> mekanizmasi eklenir; degilse (bos, hostname ya da IPv6) <c>ip4:</c>
    /// HIC yazilmaz -- <c>mx</c> ve <c>a</c> mekanizmalari zaten sunucunun A/MX kaydini kapsar.
    /// </summary>
    public static string Build(string? serverAddress)
        => IPAddress.TryParse(serverAddress, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? $"v=spf1 mx a ip4:{serverAddress} ~all"
            : "v=spf1 mx a ~all";
}
