using System.Text;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// VAR OLAN sitelerdeki panel yer tutucusunu güncel sürüme tazeler.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR — kullanıcı canlıda iki kez bildirdi.</b> Karşılama sayfası yalnız site
/// OLUŞTURULURKEN diske yazılıyordu. Paneli güncelleyen kullanıcı sayfanın değiştiğini
/// görmüyordu; çünkü dosya çoktan diskteydi ve kimse onu bir daha yazmıyordu. Kullanıcı sonunda
/// <i>"o zaman sileyim tekrar eklerim"</i> dedi — yani panel, çalışan bir siteyi silip yeniden
/// kurmaya zorluyordu. Bu kabul edilemez bir çözümdü.
/// </para>
/// <para>
/// <b>SINIR — bu sınıfın tamamı bu kuralın üzerine kurulu:</b> yalnız <b>ZATEN VAR OLAN</b> ve
/// <see cref="StarterPage.Marker"/> işaretini taşıyan dosya yeniden yazılır.
/// </para>
/// <list type="number">
/// <item><b>Yeni dosya OLUŞTURULMAZ.</b> Yalnız "var olanı tazele" demek ile "eksikse yaz" demek
/// aynı şey değildir: içeriği olan ama açılış belgesi bulunmayan bir sitede (ör. yalnız bir API
/// yayınlayan site) yeni bir yer tutucu yazmak, çalışan siteyi birden yer tutucu gösterir hâle
/// getirirdi.</item>
/// <item><b>Kullanıcının dosyası ASLA değiştirilmez.</b> İşaret yoksa dosya kullanıcınındır ve
/// dokunulmaz — kullanıcı panelin sayfasını alıp kendi sayfası hâline getirmiş olabilir.</item>
/// <item><b>Hangi profil olduğu DİSKTEN okunur</b>, tahmin edilmez: hangi dosyanın var olduğu
/// sitenin profilini zaten söyler. Profili ayrıca servislerden sormak, iki kaynağın ayrışabileceği
/// üçüncü bir yer açardı.</item>
/// </list>
/// </remarks>
internal static class StarterPageRefresh
{
    private const string AspxDirective = "<%@ Page Language=\"C#\" %>";

    private static readonly string SunucuSaati =
        "<p class=\"row\"><span class=\"k\">Sunucu saati</span><span class=\"v\">";

    /// <summary>
    /// Sitedeki panel yer tutucularını tazeler ve tazelenen dosya adlarını döner.
    /// </summary>
    /// <returns>
    /// Tazelenen dosya adları. BOŞ liste "başarısız" DEĞİLDİR — tazelenecek panel yer tutucusu
    /// olmayan bir site (kullanıcı kendi içeriğini yüklemiş) en olağan durumdur.
    /// </returns>
    public static IReadOnlyCollection<string> Refresh(SiteDto site)
    {
        ArgumentNullException.ThrowIfNull(site);

        var kok = site.PhysicalPath;
        if (string.IsNullOrWhiteSpace(kok) || !Directory.Exists(kok))
        {
            return [];
        }

        var baglama = site.Bindings?.FirstOrDefault();
        var konak = baglama?.HostName ?? string.Empty;
        var port = baglama is { Port: > 0 } ? baglama.Port : 80;

        // KARMA mı KLASİK ASP mi: php-info.php'nin varlığı ayırır. Karma profil onu da yazar,
        // klasik ASP yazmaz.
        var karma = BizimMi(Path.Combine(kok, "php-info.php"));

        var yenilenenler = new List<string>();

        Tazele("index.php", new StarterPageOptions(
            DosyaAdi: "index.php",
            Rozet: "PHP",
            Aciklama: "Bu site PHP FastCGI profiline bağlandı."));

        Tazele("Default.aspx", new StarterPageOptions(
            DosyaAdi: "Default.aspx",
            Rozet: "ASP.NET",
            Aciklama: "CLR v4.0 ve Integrated pipeline otomatik olarak ayarlandı.",
            DinamikSatir: SunucuSaati + "<%= DateTime.Now.ToString(\"dd.MM.yyyy HH:mm:ss\") %></span></p>"),
            onEk: AspxDirective + "\n");

        Tazele("default.asp", karma
            ? new StarterPageOptions(
                DosyaAdi: "default.asp",
                Rozet: "ASP · ASP.NET · PHP",
                Aciklama: "Bu sunucu Classic ASP, ASP.NET ve PHP çalıştırmaya hazır.",
                DinamikSatir: SunucuSaati + "<%= Now() %></span></p>",
                EkBaglanti: "<p class=\"links\"><a href=\"php-info.php\">PHP doğrulaması →</a></p>")
            : new StarterPageOptions(
                DosyaAdi: "default.asp",
                Rozet: "Classic ASP",
                Aciklama: "Bu site Classic ASP çalıştırmaya hazır.",
                DinamikSatir: SunucuSaati + "<%= Now() %></span></p>"));

        Tazele("index.html", new StarterPageOptions(DosyaAdi: "index.html"));

        return yenilenenler;

        void Tazele(string dosyaAdi, StarterPageOptions secenekler, string onEk = "")
        {
            var yol = Path.Combine(kok, dosyaAdi);
            if (!BizimMi(yol))
            {
                return;
            }

            var html = onEk + StarterPage.Render(site.Name, konak, port, secenekler);
            File.WriteAllText(yol, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            yenilenenler.Add(dosyaAdi);
        }
    }

    /// <summary>
    /// Dosya VAR MI ve PANELİN kendi yer tutucusu MU.
    /// </summary>
    /// <remarks>
    /// Okuma hatası (kilit, erişim reddi) <b>"bizim değil"</b> sayılır — yani dokunulmaz. Ters
    /// karar, okuyamadığımız bir kullanıcı dosyasının üzerine yazmak olurdu.
    /// </remarks>
    private static bool BizimMi(string yol)
    {
        try
        {
            return File.Exists(yol) &&
                File.ReadAllText(yol).Contains(StarterPage.Marker, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
