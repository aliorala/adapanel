using AdaPanel.Core.Common;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Alt alan adı açılırken üst bölgede ZATEN var olan kayıt: doğru mu, başka yeri mi gösteriyor?
/// </summary>
/// <remarks>
/// Karar doğrudan kullanıcıya ne söyleneceğini belirliyor ve iki yönde de zarar verebiliyor:
/// gereksiz uyarı gerçek uyarıların okunmamasına yol açar, eksik uyarı ise sitenin neden
/// açılmadığının anlaşılamamasına. Bu yüzden her iki yön de ayrı ayrı sınanıyor.
/// </remarks>
public sealed class DnsSubdomainDecisionTests
{
    private const string Sunucu = "185.7.242.225";
    private const string Bolge = "ornek.com";

    [Fact]
    public void Panelin_kendi_varsayilan_kaydi_dogru_sayilir()
    {
        // Varsayılan şablon www/mail/ftp/webmail/ns1/ns2 kayıtlarını KENDİSİ oluşturuyor.
        // "mail.ornek.com" sitesi açan kullanıcı, panelin kendi doğru kaydı için uyarı alıyordu.
        Assert.True(DnsSubdomainDecision.ZatenDogruMu("A", "1.2.3.4", panelinKendiKaydi: true, Sunucu, Bolge));
    }

    [Fact]
    public void Bu_sunucuyu_gosteren_kayit_dogru_sayilir()
    {
        Assert.True(DnsSubdomainDecision.ZatenDogruMu("A", Sunucu, panelinKendiKaydi: false, Sunucu, Bolge));
    }

    [Theory]
    [InlineData("ornek.com")]
    [InlineData("ornek.com.")]  // sondaki nokta DNS'te aynı adı gösterir
    [InlineData("ORNEK.COM")]
    public void Bolgenin_kendisine_CNAME_dogru_sayilir(string deger)
    {
        // Şablonun ürettiği www/ftp kayıtları bölgeye CNAME'dir.
        Assert.True(DnsSubdomainDecision.ZatenDogruMu("CNAME", deger, panelinKendiKaydi: false, Sunucu, Bolge));
    }

    [Theory]
    [InlineData("A", "9.9.9.9")]              // başka sunucu
    [InlineData("CNAME", "baska-site.net")]   // başka hedef
    [InlineData("CNAME", "cdn.saglayici.com")]
    public void Baska_yeri_gosteren_kayit_DOGRU_SAYILMAZ(string tur, string deger)
    {
        // Bu durumda kullanıcı UYARILMALI: kaydın üstüne yazmıyoruz ve site açılmayacak.
        Assert.False(DnsSubdomainDecision.ZatenDogruMu(tur, deger, panelinKendiKaydi: false, Sunucu, Bolge));
    }

    [Fact]
    public void Sunucu_adresi_OLCULEMEDIYSE_eslesme_iddia_edilmez()
    {
        // Boş adresi boş değere eşit sayıp "doğru" demek, hiçbir yeri göstermeyen bir kaydı
        // onaylamak olurdu.
        Assert.False(DnsSubdomainDecision.ZatenDogruMu("A", "9.9.9.9", panelinKendiKaydi: false, sunucuAdresi: "", Bolge));
        Assert.False(DnsSubdomainDecision.ZatenDogruMu("A", "", panelinKendiKaydi: false, sunucuAdresi: "", Bolge));
    }

    [Fact]
    public void A_kaydinin_degeri_bolgeye_esit_olsa_bile_CNAME_kurali_uygulanmaz()
    {
        // "ornek.com" bir IP değildir; A kaydında böyle bir değer zaten bozuktur ve doğru sayılmamalı.
        Assert.False(DnsSubdomainDecision.ZatenDogruMu("A", Bolge, panelinKendiKaydi: false, Sunucu, Bolge));
    }

    [Fact]
    public void Bos_deger_dogru_sayilmaz()
    {
        Assert.False(DnsSubdomainDecision.ZatenDogruMu("A", "", panelinKendiKaydi: false, Sunucu, Bolge));
        Assert.False(DnsSubdomainDecision.ZatenDogruMu("A", null, panelinKendiKaydi: false, Sunucu, Bolge));
    }
}
