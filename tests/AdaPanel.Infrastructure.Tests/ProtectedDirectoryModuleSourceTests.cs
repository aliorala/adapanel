using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// IIS MODULUNUN net48'e OZGU DAVRANISI -- kaynaktan nobet.
/// </summary>
/// <remarks>
/// <para>
/// Bu dosyadaki uc kural yalnizca <c>System.Web</c> tipleriyle ifade edilebiliyor
/// (<c>HttpResponse</c>), dolayisiyla net8 test surecinde CALISTIRILAMAZ. Davranisin kendisi
/// CANLI olculdu (bu turda, test.local uzerinde); burada nobette tutulan sey, o olcumu gecerli
/// kilan satirlarin kaynaktan SESSIZCE kaybolmamasi.
/// </para>
/// <para>
/// Aranan dizelerin YALNIZCA kodda gectigi dogrulanmistir: yorum metinleri Turkce yazildigi icin
/// bu tokenlar orada gecmiyor. (Gecseydi test, kod silinse bile yesil kalirdi.)
/// </para>
/// </remarks>
public sealed class ProtectedDirectoryModuleSourceTests
{
    /// <summary>
    /// CANLI OLCULMUS ACIK: korunan bir statik dosya bir kez parolayla sunulunca IIS yaniti
    /// HTTP.SYS cekirdek onbellegine aliyor ve sonraki KIMLIKSIZ istekler yonetilen ardisik
    /// duzene hic girmeden HTTP 200 aliyordu (olculdu: ayni URL'de kimliksiz 5 istek 200,
    /// sorgu eklenmis hali 401). Iki satir da gerekli: kullanici kipi ile cekirdek kipi AYRI
    /// onbelleklerdir.
    /// </summary>
    [Theory]
    [InlineData("response.DisableKernelCache();")]
    [InlineData("response.DisableUserCache();")]
    public void TheGrantedResponse_IsMarkedNonCacheable(string satir)
    {
        var kaynak = OkuKaynak("src", "AdaPanel.HtaccessModule", "HtaccessHttpModule.cs");

        Assert.Contains(satir, kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>WWW-Authenticate</c> YALNIZ 401 yolunda yazilir. Eski tasarimda baslik web.config'in
    /// <c>customHeaders</c> bolumundeydi ve HER yanita ekleniyordu -- canli olculdu: dogru
    /// parolayla donen 200'de de gidiyordu. Basligin 401 dalindan CIKMASI, bu duzeltmenin geri
    /// alinmasi demektir.
    /// </summary>
    [Fact]
    public void TheChallengeHeader_IsWrittenOnlyOnTheUnauthorizedPath()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.HtaccessModule", "HtaccessHttpModule.cs");

        var durum = kaynak.IndexOf("response.StatusCode = 401;", StringComparison.Ordinal);
        var baslik = kaynak.IndexOf("response.AddHeader(\"WWW-Authenticate\"", StringComparison.Ordinal);

        Assert.True(durum > 0, "401 yazan satir yok.");
        Assert.True(baslik > durum, "WWW-Authenticate, 401 dalinin disinda yaziliyor.");

        // Ve baska hicbir yerde yazilmiyor: tek gecis.
        Assert.Equal(baslik, kaynak.LastIndexOf("response.AddHeader(\"WWW-Authenticate\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Korumali dizin kapisi, <c>.htaccess</c> kapisindan ONCE ve ONDAN BAGIMSIZ calismali:
    /// <c>.htaccess</c> destegi varsayilan KAPALI oldugu icin arkasina konulan bir parola
    /// korumasi SESSIZCE calismazdi.
    /// </summary>
    [Fact]
    public void TheProtectionGate_RunsBeforeTheHtaccessSwitch()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.HtaccessModule", "HtaccessHttpModule.cs");

        var koruma = kaynak.IndexOf("if (ApplyDirectoryProtection(context))", StringComparison.Ordinal);
        var anahtar = kaynak.IndexOf("var mode = currentGate.Evaluate(siteName);", StringComparison.Ordinal);

        Assert.True(koruma > 0, "Korumali dizin cagrisi yok.");
        Assert.True(anahtar > koruma, ".htaccess anahtari korumadan ONCE degerlendiriliyor.");
    }

    private static string OkuKaynak(params string[] parts)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parts).ToArray());
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
