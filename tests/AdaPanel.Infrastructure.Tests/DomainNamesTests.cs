using AdaPanel.Core.Common;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <see cref="DomainNames"/> — alan adı ilişkilerinin tek kaynağı.
/// </summary>
/// <remarks>
/// Bu sınıf hem GÜVENLİK denetiminde ("başkasının alanının altına site açılamaz") hem SİTE
/// AKIŞINDA ("alt alan adına üst bölgede A kaydı düş") kullanılıyor. Yani buradaki bir hata iki
/// yönde birden yanlış sonuç verir: fazla geniş olursa meşru müşteri kendi alan adını bağlayamaz,
/// fazla dar olursa saldırgan başkasının alanının altına girer. İkisi de ayrı ayrı sınanır.
/// </remarks>
public sealed class DomainNamesTests
{
    [Theory]
    [InlineData("blog.ornek.com", "ornek.com")]
    [InlineData("a.b.c.ornek.com", "ornek.com")]      // çok seviyeli
    [InlineData("test.ic.ornek.com", "ic.ornek.com")] // ara seviye üst alan
    [InlineData("BLOG.ORNEK.COM", "ornek.com")]       // büyük harf
    [InlineData("blog.ornek.com.", "ornek.com")]      // sondaki nokta
    [InlineData("  blog.ornek.com  ", " ornek.com ")] // kenar boşlukları
    public void Alt_alan_adi_dogru_taninir(string alt, string ust)
    {
        Assert.True(DomainNames.IsSubdomainOf(alt, ust));
    }

    [Theory]
    [InlineData("notornek.com", "ornek.com")]   // sonek benziyor, etiket sınırı yok
    [InlineData("bizimornek.com", "ornek.com")]
    [InlineData("ornek.com.tr", "ornek.com")]   // farklı uzantı
    [InlineData("ornek.com", "ornek.com")]      // kendisi alt alanı değildir
    [InlineData("ornek.com", "blog.ornek.com")] // ters yön
    [InlineData(".ornek.com", "ornek.com")]     // boş etiket
    [InlineData("", "ornek.com")]
    [InlineData("blog.ornek.com", "")]
    [InlineData(null, null)]
    public void Alt_alan_adi_OLMAYANLAR_reddedilir(string? alt, string? ust)
    {
        Assert.False(DomainNames.IsSubdomainOf(alt, ust));
    }

    [Theory]
    [InlineData("blog.com", "com")]             // tek etiketli "üst": bütün .com'u kilitlerdi
    [InlineData("site.localhost", "localhost")]
    public void Tek_etiketli_ad_ust_alan_sayilmaz(string alt, string ust)
    {
        // Adı "com" olan tek bir site, o uzantı altındaki her adı sahiplenmiş sayılırdı.
        Assert.False(DomainNames.IsSubdomainOf(alt, ust));
    }

    [Theory]
    [InlineData("blog.ornek.com", "ornek.com", "blog")]
    [InlineData("a.b.ornek.com", "ornek.com", "a.b")]
    [InlineData("TEST.Ornek.COM", "ornek.com", "test")]
    [InlineData("ornek.com", "ornek.com", "")]      // alt alan değil → etiket yok
    [InlineData("notornek.com", "ornek.com", "")]
    public void Etiket_dogru_cikarilir(string alt, string ust, string beklenen)
    {
        Assert.Equal(beklenen, DomainNames.LabelUnder(alt, ust));
    }

    [Fact]
    public void Etiket_ve_ust_alan_birlestirilince_ORIJINALI_verir()
    {
        // Kayıt üst bölgeye `etiket` olarak yazılıyor; bu iki parçanın birleşimi istenen adı
        // vermezse kayıt YANLIŞ isme düşer ve alan adı hiç çözmez.
        const string alt = "shop.magaza.ornek.com";
        const string ust = "ornek.com";

        var etiket = DomainNames.LabelUnder(alt, ust);

        Assert.Equal(alt, $"{etiket}.{ust}");
    }
}
