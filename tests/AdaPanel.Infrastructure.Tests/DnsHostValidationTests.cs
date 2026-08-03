using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DNS kayıt konaklarında ALT ÇİZGİYLE başlayan etiketler geçerlidir ve panelin birçok özelliği
/// doğrudan bunlara dayanır.
///
/// Canlı olarak doğrulanmış olayın regresyonu: konak deseni ilk karakterin harf/rakam olmasını şart
/// koşuyordu. Sonuç yalnızca "SRV eklenemiyor" değildi — yerleşik zone şablonunda <c>_dmarc</c>
/// bulunduğu için siteye ait DNS ekranı zone'u OKURKEN bile "dns.invalid_host" ile düşüyordu.
/// </summary>
public sealed class DnsHostValidationTests
{
    private const string Zone = "ornek.com";

    [Theory]
    [InlineData("_dmarc")]                                  // DMARC — yerleşik şablonda
    [InlineData("_domainkey")]
    [InlineData("v1-rsa-20260802._domainkey")]              // DKIM seçicisi (Stalwart üretiyor)
    [InlineData("_acme-challenge")]                         // joker SSL: ACME DNS-01
    [InlineData("_sip._tcp")]                               // SRV
    [InlineData("_25._tcp.mail")]                           // TLSA
    public void NormalizeHost_AcceptsServiceLabelsStartingWithUnderscore(string host)
    {
        Assert.Equal(host, DnsService.NormalizeHost(host, Zone));
    }

    [Theory]
    [InlineData("@", "@")]
    [InlineData("*", "*")]
    [InlineData("www", "www")]
    [InlineData("mail", "mail")]
    [InlineData("*.dev", "*.dev")]
    [InlineData("WWW", "www")]                              // büyük harf küçültülür
    [InlineData("www.ornek.com", "www")]                    // zone soneki atılır
    [InlineData("ornek.com", "@")]                          // zone'un kendisi kök olur
    [InlineData("", "@")]
    public void NormalizeHost_NormalisesOrdinaryHosts(string input, string expected)
    {
        Assert.Equal(expected, DnsService.NormalizeHost(input, Zone));
    }

    // Gevşetme yalnız alt çizgi içindir; bozuk adlar hâlâ reddedilmeli, aksi hâlde geçersiz bir kayıt
    // Windows DNS'e yazılmaya çalışılır ve hata çok daha geç, anlaşılmaz bir yerde çıkar.
    [Theory]
    [InlineData("-bozuk")]
    [InlineData("bozuk-")]
    [InlineData("iki..nokta")]
    [InlineData("_")]
    [InlineData("bosluk var")]
    public void NormalizeHost_StillRejectsMalformedHosts(string host)
    {
        var exception = Assert.Throws<AppException>(() => DnsService.NormalizeHost(host, Zone));
        Assert.Equal("dns.invalid_host", exception.Message);
    }

    // Surum 3 varsayilanlari: istemci otomatik yapilandirma (RFC 6186) ve TLS raporlama konaklari
    // COK etiketli alt cizgi tasir. Bunlar reddedilseydi sablon YAZILAMAZ, uustelik hata bolge
    // olusturulurken cikacagi icin site kurulumu bile bozulurdu.
    [Theory]
    [InlineData("_submissions._tcp")]
    [InlineData("_submission._tcp")]
    [InlineData("_imaps._tcp")]
    [InlineData("_pop3s._tcp")]
    [InlineData("_smtp._tls")]
    [InlineData("_mta-sts")]
    public void NormalizeHost_AcceptsAutoconfigurationHosts(string host)
    {
        Assert.Equal(host, DnsService.NormalizeHost(host, Zone));
    }
}
