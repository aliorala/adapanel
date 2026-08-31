using System.Reflection;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SPF politikasinin TEK KAYNAK oldugunu ve UC ureticinin (MailService, DnsService managed,
/// DnsService sablon) ayni girdide ayni ciktiyi verdigini olcer. Onceki turda "uc ayri SPF
/// ureticisi" olculmus ama davranislari karsilastirilmamisti; olcum, IPv4 cozulemeyen sunucuda
/// sablon yolunun BICIMSIZ "ip4: ~all" urettigini, digerlerinin ise dogru "~all"e indigini
/// gosterdi. Bu testler ayrismayi kapatir ve GERI GELMESINI engeller.
/// </summary>
public sealed class SpfTekKaynakTests
{
    // --- TEK KAYNAK: SpfPolicy.Build ---

    [Theory]
    [InlineData("203.0.113.10", "v=spf1 mx a ip4:203.0.113.10 ~all")]
    [InlineData("192.0.2.1", "v=spf1 mx a ip4:192.0.2.1 ~all")]
    public void Build_Ipv4_IcinIp4MekanizmasiEkler(string ip, string beklenen)
        => Assert.Equal(beklenen, SpfPolicy.Build(ip));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("2001:db8::1")]        // IPv6 -> ip4 YAZILAMAZ
    [InlineData("mail.ornek.com")]     // hostname -> ip4 YAZILAMAZ
    public void Build_Ipv4Olmayan_IcinIp4YazMAZ(string? adres)
    {
        var spf = SpfPolicy.Build(adres);
        Assert.Equal("v=spf1 mx a ~all", spf);
        Assert.DoesNotContain("ip4:", spf, StringComparison.Ordinal);
    }

    // --- FAZLA-KAPATMA NOBETCISI: yaygin IPv4 durumunda cikti DEGISMEZ, ~all DURUR (-all DEGIL) ---

    [Fact]
    public void Build_MesruIpv4_CiktisiAynenKorunur_VeSoftFailKalir()
    {
        var spf = SpfPolicy.Build("203.0.113.10");
        Assert.Equal("v=spf1 mx a ip4:203.0.113.10 ~all", spf);
        Assert.EndsWith("~all", spf, StringComparison.Ordinal);   // -all'a SIKILASTIRILMADI
        Assert.Contains("mx", spf, StringComparison.Ordinal);
        Assert.Contains(" a ", " " + spf + " ", StringComparison.Ordinal);
    }

    // --- SABLON YOLU ARTIK TEK KAYNAKLA AYNI: bicimsiz "ip4:" URETMEZ ---

    [Theory]
    [InlineData("203.0.113.10", "v=spf1 mx a ip4:203.0.113.10 ~all")]
    [InlineData("", "v=spf1 mx a ~all")]   // eski hata: "v=spf1 mx a ip4: ~all"
    public void Sablon_SpfDegeri_TekKaynakla_AyniCiktiVerir(string ip, string beklenen)
    {
        var genisletilmis = ExpandPlaceholders("v=spf1 mx a ip4:{ip} ~all", "ornek.com", ip);
        Assert.Equal(beklenen, genisletilmis);
        Assert.Equal(SpfPolicy.Build(ip), genisletilmis);
    }

    [Fact]
    public void Sablon_IpBosken_BicimsizIp4Mekanizmasi_URETMEZ()
    {
        var spf = ExpandPlaceholders("v=spf1 mx a ip4:{ip} ~all", "ornek.com", string.Empty);
        Assert.DoesNotContain("ip4:", spf, StringComparison.Ordinal);
    }

    [Fact]
    public void Sablon_KullaniciKendiIp4unuYazdiysa_IpBoskenBileKORUNUR()
    {
        // Kullanicinin ELLE ekledigi ("{ip}" icermeyen) bir ip4/include mekanizmasi silinmemeli.
        var spf = ExpandPlaceholders("v=spf1 mx a ip4:198.51.100.7 include:_spf.ornek.com ~all", "ornek.com", string.Empty);
        Assert.Contains("ip4:198.51.100.7", spf, StringComparison.Ordinal);
        Assert.Contains("include:_spf.ornek.com", spf, StringComparison.Ordinal);
    }

    private static string ExpandPlaceholders(string input, string zone, string ip)
    {
        var method = typeof(AdaPanel.Infrastructure.Dns.DnsService)
            .GetMethod("ExpandPlaceholders", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [input, zone, ip, $"ns1.{zone}", $"ns2.{zone}"])!;
    }
}
