
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PANEL GUVENLIK BASLIKLARI — hem VAR OLANI hem BILEREK YOK OLANI sabitler.
///
/// <para>
/// Basliklar canli panelde OLCULDU (0.8.6): siki CSP (<c>script-src 'self'</c>,
/// <c>frame-ancestors 'none'</c>), <c>X-Frame-Options: DENY</c>, <c>nosniff</c>,
/// <c>Referrer-Policy: same-origin</c>. Bu test onlarin sessizce dusmesini engeller.
/// </para>
/// <para>
/// <b>Ama asil sebep ikinci kisim: HSTS'in YOKLUGUNU korumak.</b> Bir eksigi korumak icin test
/// yazmak ilk bakista tuhaf gorunur; sebebi su ki buradaki "eksik" bir unutma degil KARARDIR ve
/// iyi niyetli bir katkici onu "guvenlik acigi" sanip kapatabilir. Kapattigi anda butun
/// self-signed kurulumlar KALICI olarak disarida kalir.
/// </para>
/// </summary>
public sealed class PanelSecurityHeaderTests
{
    /// <summary>
    /// Panelin kendi arayuzune HSTS EKLENEMEZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Panel varsayilan olarak KENDI IMZALADIGI sertifikayla gelir
    /// (<c>scripts/initialize-panel-certificate.ps1</c> → <c>New-SelfSignedCertificate</c>).
    /// Tarayici boyle bir sertifikada uyari gosterir ve kullanici "yine de devam et" ile gecer.
    /// HSTS gonderilirse o gecis KAYBOLUR: tarayici sertifika hatasinda devam ettirmez, yani panel
    /// yoneticisini kendi paneline sokmaz.
    /// </para>
    /// <para>
    /// <b>Ve bu GERI ALINAMAZ.</b> Baslik kaldirilsa bile tarayicidaki kayit <c>max-age</c> dolana
    /// kadar durur; kullanici ancak tarayici ayarlarindan elle temizleyerek asabilir. Yani hatali
    /// bir "iyilestirme", bir sonraki surumle DUZELMEZ.
    /// </para>
    /// <para>
    /// Kiracinin KENDI sitesi icin HSTS ayri bir ozelliktir ve orada aciktir
    /// (<c>SiteHostingSettings.HstsEnabled</c>) — bu test ona dokunmaz, yalniz PANELIN kendi
    /// yanitina bakar.
    /// </para>
    /// </remarks>
    [Fact]
    public void Panelin_kendi_yanitina_HSTS_eklenmemis()
    {
        // HSTS iki yerden de eklenebilir: basliklarin TEK KAYNAGI (PanelSecurityHeaders) ya da
        // ara katman zincirinin kuruldugu Program.cs. Yasak IKISINI birden kapsar; biri
        // taranmasa, ekleme oradan sizardi.
        var kaynak = Oku("src", "AdaPanel.Api", "Security", "PanelSecurityHeaders.cs")
                     + Environment.NewLine
                     + Oku("src", "AdaPanel.Api", "Program.cs");

        // YASAK, DIZGEYE DEGIL KODUN SEKLINE KONUR -- ve bunu testin ilk kosusu yazdirdi.
        //
        // Ilk halim once yorumlari eliyor, sonra "Strict-Transport-Security" kelimesini ariyordu.
        // Iki sorun cikti: (1) HSTS'in NEDEN olmadigini anlatan yorumun kendisi o kelimeyi
        // geciriyor; (2) yorum temizleyicim DIZE ICINDEKI blok-yorum acilislarini gercek yorum
        // sandi -- Program.cs'te dort acilisa karsi tek kapanis var, ucu dize icinde. Temizleyici
        // ilk acilistan tek kapanisa kadar HER SEYI sildi, basliklarin tanimi da o araliktaydi,
        // ve "basliklar yerinde" testi HAKLI olarak kirmizi yandi.
        //
        // Dizeyi anlamayan tarayici, bu oturumda defalarca bulunan hata sinifidir. Cozum
        // tarayiciyi kurtarmak degil, ARANAN SEYI daraltmak: yasak olan kelimenin gecmesi degil,
        // basligin ATANMASI. Asagidaki uc kalip yalniz KODDA gecebilir; boylece gerekce yorumu
        // serbestce yazilabilir ve temizleyiciye hic gerek kalmaz.
        //
        // (Bu blok satir-yorumu olarak yazildi: blok yorum icinde blok-yorum kapanisi anlatmak
        //  yorumu erken kapatiyor -- ilk denemede tam olarak bu oldu.)
        Assert.DoesNotContain("Headers[\"Strict-Transport-Security\"]", kaynak, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseHsts(", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHsts(", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Var olan dort baslik sessizce dusmemeli.
    /// </summary>
    /// <remarks>
    /// Deger degil VARLIK olculur: CSP'nin icerigi zamanla degisebilir (yeni bir kaynak turu
    /// eklenebilir), ama basligin kendisi ve iki kritik yonergesi kalmalidir. Tam metne
    /// tutunmak, mesru bir degisiklikte yanlis yerde kirmizi yakardi.
    /// </remarks>
    [Fact]
    public void Dort_temel_baslik_yerinde()
    {
        // BASLIKLAR ARTIK TEK KAYNAKTA. Bu testin ilk hali Program.cs'e bakiyordu ve basliklar
        // PanelSecurityHeaders'a tasininca HAKLI olarak kirmizi yandi: tasima gercekten olculdu.
        // Tek kaynak, hata yanitlarinin ayni basliklari alabilmesi icin gerekliydi --
        // Response.Clear() onlari siliyordu ve satir ici yazim tekrar uygulamayi imkansiz kiliyordu.
        var kaynak = Oku("src", "AdaPanel.Api", "Security", "PanelSecurityHeaders.cs");

        Assert.Contains("X-Content-Type-Options", kaynak, StringComparison.Ordinal);
        Assert.Contains("X-Frame-Options", kaynak, StringComparison.Ordinal);
        Assert.Contains("Referrer-Policy", kaynak, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", kaynak, StringComparison.Ordinal);

        // CSP'nin iki yonergesi PAZARLIK KONUSU DEGIL: satir ici betik ve cerceveye gomulme.
        Assert.Contains("script-src 'self'", kaynak, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", kaynak, StringComparison.Ordinal);
    }

    private static string Oku(params string[] parcalar)
    {
        var yol = Path.Combine(KaynakKoku(), Path.Combine(parcalar));
        Assert.True(File.Exists(yol), $"kaynak bulunamadi: {yol}");
        return File.ReadAllText(yol);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
