using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// HTTPS baglamasi kaldirmanin KARARLARI. Denetimde olculen durum: <c>RemoveSslCertificateRequest</c>
/// DTO'su yazilmisti ama tum kaynakta tek referansi kendi tanimiydi -- yani panelde HTTPS baglamasini
/// kaldirmanin HICBIR yolu yoktu.
/// <para>
/// Burada korunan uc kural:
/// (1) yikici islemde HEDEF TAHMIN EDILMEZ (birden fazla baglama varsa host adi sart),
/// (2) sitenin SON HTTPS baglamasi taninir (Force HTTPS acikken kaldirilirsa site erisilemez kalir),
/// (3) sertifikayi baskasi kullaniyorsa SILINMEZ -- ve bu OLCUMLE bilinir, olculemediyse silinmez.
/// </para>
/// </summary>
public sealed class SslRemovalPlanTests
{
    private static IisHttpsBinding Baglama(string site, string bilgi, string thumb = "aabbcc") =>
        new() { SiteName = site, BindingInformation = bilgi, Thumbprint = thumb, StoreName = "My", CertificateReadable = true };

    [Fact]
    public void BindingInformation_IsParsedIntoHostAndPort()
    {
        var baglama = Baglama("shop", "*:443:shop.example.com");
        Assert.Equal("shop.example.com", baglama.HostName);
        Assert.Equal(443, baglama.Port);

        // Host basliksiz baglama: "ip:port:" -> host bos, port yine okunur.
        var hostsuz = Baglama("shop", "10.0.0.5:8443:");
        Assert.Equal(string.Empty, hostsuz.HostName);
        Assert.Equal(8443, hostsuz.Port);
    }

    [Fact]
    public void SelectTarget_WithHostName_PicksThatBinding()
    {
        var baglamalar = new[]
        {
            Baglama("shop", "*:443:www.example.com"),
            Baglama("shop", "*:443:magaza.example.com")
        };

        var (hedef, hata) = SslRemovalPlan.SelectTarget(baglamalar, "magaza.example.com", 443);

        Assert.Equal(string.Empty, hata);
        Assert.Equal("magaza.example.com", hedef!.HostName);
    }

    /// <summary>
    /// Host adi verilmediginde ve sitede birden fazla HTTPS baglamasi oldugunda SECIM YAPILMAZ.
    /// "Herhalde ilkini kastetti" demek, yanlis alan adinin HTTPS'ini kapatmak olurdu.
    /// </summary>
    [Fact]
    public void SelectTarget_WithoutHostName_RefusesToGuessWhenSeveralExist()
    {
        var baglamalar = new[]
        {
            Baglama("shop", "*:443:www.example.com"),
            Baglama("shop", "*:443:magaza.example.com")
        };

        var (hedef, hata) = SslRemovalPlan.SelectTarget(baglamalar, null, 443);

        Assert.Null(hedef);
        Assert.Equal(SslRemovalPlan.HostNameRequiredKey, hata);
    }

    [Fact]
    public void SelectTarget_WithoutHostName_PicksTheOnlyBinding()
    {
        var baglamalar = new[] { Baglama("shop", "*:443:www.example.com") };

        var (hedef, hata) = SslRemovalPlan.SelectTarget(baglamalar, "  ", 443);

        Assert.Equal(string.Empty, hata);
        Assert.Equal("www.example.com", hedef!.HostName);
    }

    [Fact]
    public void SelectTarget_OnAnotherPort_IsNotFound()
    {
        var baglamalar = new[] { Baglama("shop", "*:443:www.example.com") };

        var (hedef, hata) = SslRemovalPlan.SelectTarget(baglamalar, "www.example.com", 8443);

        Assert.Null(hedef);
        Assert.Equal(SslRemovalPlan.HostNotFoundKey, hata);
    }

    [Fact]
    public void IsLastHttpsBinding_IsTrueOnlyWhenNoOtherHttpsBindingRemains()
    {
        var tek = Baglama("shop", "*:443:www.example.com");
        Assert.True(SslRemovalPlan.IsLastHttpsBindingOfSite([tek], tek));

        var ikinci = Baglama("shop", "*:443:magaza.example.com");
        Assert.False(SslRemovalPlan.IsLastHttpsBindingOfSite([tek, ikinci], tek));
    }

    /// <summary>
    /// PAYLASILAN SERTIFIKA (or. yuklenen wildcard) baska sitelerde de bagli olabilir. Kaldirilan
    /// baglama yuzunden sertifikayi silmek, o siteleri ANINDA bozardi.
    /// </summary>
    [Fact]
    public void FindOtherUsers_ReportsBindingsOfOtherSitesSharingTheCertificate()
    {
        var hedef = Baglama("shop", "*:443:www.example.com", "wildcard1");
        var tumu = new[]
        {
            hedef,
            Baglama("blog", "*:443:blog.example.com", "wildcard1"),
            Baglama("baska", "*:443:baska.com", "farkli")
        };

        var kullananlar = SslRemovalPlan.FindOtherUsersOfCertificate(tumu, hedef);

        Assert.Equal(["blog (*:443:blog.example.com)"], kullananlar);
    }

    [Fact]
    public void FindOtherUsers_IsEmptyWhenNobodyElseUsesTheCertificate()
    {
        var hedef = Baglama("shop", "*:443:www.example.com", "tekil");
        var tumu = new[] { hedef, Baglama("blog", "*:443:blog.example.com", "baska") };

        Assert.Empty(SslRemovalPlan.FindOtherUsersOfCertificate(tumu, hedef));
    }

    /// <summary>
    /// Parmak izi OLCULEMEDIYSE "kimse kullanmiyor" DENMEZ. Olcemedigimiz seye dayanip sertifika
    /// silmek, bilmedigimiz bir siteyi bozmak olurdu; bu yuzden sonuc BOS DEGIL doner ve cagiran
    /// silmeyi atlar.
    /// </summary>
    [Fact]
    public void FindOtherUsers_WhenThumbprintIsUnknown_DoesNotClaimTheCertificateIsUnused()
    {
        var hedef = Baglama("shop", "*:443:www.example.com", thumb: string.Empty);

        var kullananlar = SslRemovalPlan.FindOtherUsersOfCertificate([hedef], hedef);

        Assert.NotEmpty(kullananlar);
    }
}
