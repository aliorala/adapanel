using AdaPanel.Core.Common;

namespace AdaPanel.Api.Security;

/// <summary>
/// Belge kökünün kiracı sınırı içinde kalıp kalmadığına karar veren TEK yer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı sınıf:</b> karar denetleyicinin içinde satır içi yazılıydı ve doğrudan test
/// edilemiyordu — DI, IIS ve kimlik gerektiriyordu. Kural buraya alınınca hem sınanabilir oldu
/// hem de ikinci bir çağrı yeri doğarsa aynı kararı kullanır.
/// </para>
/// <para>
/// <b>SINIR SİTE DEĞİL KİRACIDIR</b> — ve bu, kullanıcı bildirimiyle düzeltildi. Önceki hâl yalnız
/// düzenlenen sitenin kökünü kabul ediyordu. Oysa alt alan adı AYRI BİR SİTEDİR ve en olağan
/// kullanım tam da şudur: <c>webtv.ornek.com</c> alt alan adını ana sitedeki
/// <c>httpdocs/iptv/public</c> klasörüne yöneltmek. Site sınırı bunu 403 ile reddediyordu; yani
/// koruma, kiracıyı BAŞKASININ klasöründen korurken KENDİ klasöründen de koruyordu.
/// </para>
/// <para>
/// <b>Çapraz kiracı koruması aynen duruyor.</b> İzinli kökler kümesi KAPSAM SERVİSİNDEN gelir,
/// istekten değil. Bu deponun kapanmış hata sınıfı tam olarak şuydu: "kapsam denetimi doğru ama
/// denetlenen değeri kiracının kendisi seçiyor".
/// </para>
/// </remarks>
public static class SiteDocumentRootScope
{
    /// <summary>
    /// İstenen belge kökü izinli mi?
    /// </summary>
    /// <param name="istenenYol">Kullanıcının belge kökü olarak verdiği yol.</param>
    /// <param name="duzenlenenSiteKoku">Düzenlenen sitenin kendi kökü. HER ZAMAN izinlidir —
    /// <c>public</c> klasöründen proje köküne geri dönmek en olağan işlemdir.</param>
    /// <param name="kiracininSiteKokleri">
    /// Kiracının SAHİP OLDUĞU sitelerin kökleri. <c>null</c> ise kapsamsız bir rol söz konusudur
    /// (operator) ve DAVRANIŞ DEĞİŞMEZ: yalnız düzenlenen sitenin kökü geçerlidir. Bu düzeltmenin
    /// amacı kiracıya alan açmaktı, kapsamsız role yeni yetki vermek değil.
    /// </param>
    public static bool IsAllowed(
        string? istenenYol,
        string? duzenlenenSiteKoku,
        IEnumerable<string>? kiracininSiteKokleri)
    {
        if (string.IsNullOrWhiteSpace(istenenYol))
        {
            return false;
        }

        // Boş kökler ELENİR. Boş bir dizeyi köke geçirmek, kapsama denetimini anlamsız
        // kılabilirdi -- "hiçbir şeyin içinde" sorusu güvenli tarafta yanıtlanmalı.
        var kokler = new List<string>();
        if (!string.IsNullOrWhiteSpace(duzenlenenSiteKoku))
        {
            kokler.Add(duzenlenenSiteKoku);
        }

        if (kiracininSiteKokleri is not null)
        {
            kokler.AddRange(kiracininSiteKokleri.Where(kok => !string.IsNullOrWhiteSpace(kok)));
        }

        return kokler.Count > 0
            && kokler.Any(kok => PathContainment.IsRootOrInside(kok, istenenYol));
    }
}
