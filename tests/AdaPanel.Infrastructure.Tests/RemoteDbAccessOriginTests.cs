using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Database;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Uzak erisim kaynagi (IP / CIDR / desen) ayristirmasi.
/// <para>
/// BU BIR GUVENLIK SINIRIDIR: buradan cikan metin MySQL'de bir SQL dizgi sabitine
/// ('kullanici'@'&lt;host&gt;') ve PostgreSQL'de pg_hba.conf satirina yaziliyor. Ayristirici
/// girdiden yalnizca SAYILARI aliyor ve ciktiyi kendisi kuruyor; bu testler ciktinin gercekten
/// yeniden uretildigini ve girdinin hicbir baytinin sizmadigini gosterir.
/// </para>
/// </summary>
public sealed class RemoteDbAccessOriginTests
{
    // ---------------------------------------------------------------------------------
    // Enjeksiyon: girdi ciktiya KOPYALANMAZ
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// SQL dizgi sabitinden kacmaya calisan girdiler REDDEDILIR (kacirilarak kabul edilmez).
    /// Kabul edilip kacirilsaydi, kacirmanin bir gun atlanmasi yeterdi.
    /// </summary>
    [Theory]
    [InlineData("1.2.3.4' OR '1'='1")]
    [InlineData("1.2.3.4'; DROP DATABASE musteri; --")]
    [InlineData("%' UNION SELECT 1 --")]
    [InlineData("1.2.3.4\\")]
    public void SqlInjectionAttempts_AreRejected(string girdi)
    {
        Assert.Throws<AppException>(() => RemoteAccessOrigin.Parse(girdi));
    }

    /// <summary>
    /// pg_hba.conf SATIR TABANLIDIR: girdiye sokulan bir satir sonu, dosyaya KENDI KURALINI
    /// yazardi (ornegin "host all all 0.0.0.0/0 trust"). Bu yuzden satir sonu iceren girdi
    /// hicbir kosulda kaynak olamaz.
    /// </summary>
    [Theory]
    [InlineData("1.2.3.4\nhost all all 0.0.0.0/0 trust")]
    [InlineData("1.2.3.4\r\nhost all all 0.0.0.0/0 trust")]
    [InlineData("1.2.3.4 host all all 0.0.0.0/0 trust")]
    [InlineData("1.2.3.4\ttrust")]
    public void HbaLineInjectionAttempts_AreRejected(string girdi)
    {
        Assert.Throws<AppException>(() => RemoteAccessOrigin.Parse(girdi));
    }

    /// <summary>
    /// Gecerli bir girdide bile cikti YENIDEN URETILIR: bosluklar kirpilir ve sayilar
    /// yeniden yazilir. Cikti girdiden turetilmis bir kopya degildir.
    /// </summary>
    [Fact]
    public void Output_IsRebuiltFromNumbers_NotCopiedFromInput()
    {
        var origin = RemoteAccessOrigin.Parse("  192.168.1.10  ");

        Assert.Equal("192.168.1.10", origin.Canonical);
        Assert.Equal("192.168.1.10", origin.ToMySqlHost());
        Assert.Equal("192.168.1.10/32", origin.ToPostgresAddress());
    }

    // ---------------------------------------------------------------------------------
    // Sayisal dogruluk
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Bastan sifirli oktet REDDEDILIR. IPAddress.TryParse "010.0.0.1"i kabul eder ve sekizlik
    /// olarak yorumlayabilir; kullanicinin ekranda gordugu adresle motora yazilan adresin farkli
    /// olmasi, erisim listesinde BEKLENMEDIK bir makineye izin vermek demektir.
    /// </summary>
    [Theory]
    [InlineData("010.0.0.1")]
    [InlineData("1.02.3.4")]
    public void LeadingZeroOctets_AreRejected(string girdi)
    {
        Assert.Throws<AppException>(() => RemoteAccessOrigin.Parse(girdi));
    }

    /// <summary>Kisaltilmis yazim ("10.1" = 10.0.0.1) reddedilir; dort oktet zorunludur.</summary>
    [Theory]
    [InlineData("10.1")]
    [InlineData("10.0.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("256.1.1.1")]
    public void MalformedIpv4_IsRejected(string girdi)
    {
        Assert.Throws<AppException>(() => RemoteAccessOrigin.Parse(girdi));
    }

    /// <summary>
    /// CIDR'da host bitleri MASKELENIR. Maskelenmezse pg_hba "invalid CIDR mask" ile REDDEDER
    /// ve dosya bozulurdu; kullanici da 192.168.1.55/24 yazip neyi kastettigini gormezdi.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.55/24", "192.168.1.0/24")]
    [InlineData("10.5.7.9/8", "10.0.0.0/8")]
    [InlineData("172.16.30.200/16", "172.16.0.0/16")]
    [InlineData("10.0.0.130/26", "10.0.0.128/26")]
    public void CidrHostBits_AreMasked(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, RemoteAccessOrigin.Parse(girdi).Canonical);
    }

    // ---------------------------------------------------------------------------------
    // "Her yer" tek bir kavramdir
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// "Her yer"in butun yazimlari AYNI kavrama coker. Aksi halde onay kapisi (ConfirmAnywhere)
    /// yalnizca "%" yazan kullaniciya uygulanir, "0.0.0.0/0" yazan onu atlardi.
    /// </summary>
    [Theory]
    [InlineData("%")]
    [InlineData("*")]
    [InlineData("any")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.20.30.40/0")]
    public void EverySpellingOfAnywhere_CollapsesToAnywhere(string girdi)
    {
        var origin = RemoteAccessOrigin.Parse(girdi);

        Assert.True(origin.IsAnywhere);
        Assert.Equal("%", origin.ToMySqlHost());
        Assert.Equal("0.0.0.0/0", origin.ToPostgresAddress());
        Assert.Equal("Any", origin.ToFirewallRemoteAddress());
    }

    // ---------------------------------------------------------------------------------
    // Motora gore olusturma
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// MySQL hesabinin host kismi /8, /16, /24, /32 disinda bir onegi IFADE EDEMEZ. Sessizce
    /// genisletmek (ornegin /26 yerine /24 yazmak) kullanicinin istemedigi 192 makineye kapi
    /// acardi; bu yuzden reddedilir.
    /// </summary>
    [Fact]
    public void MySqlHost_RejectsPrefixItCannotExpress()
    {
        var origin = RemoteAccessOrigin.Parse("10.0.0.0/26");

        var hata = Assert.Throws<AppException>(() => origin.ToMySqlHost());
        Assert.Equal("database.remote_cidr_unsupported", hata.MessageKey);

        // Ayni kaynak PostgreSQL'de sorunsuz ifade edilir: motorlar farkli, yalan yok.
        Assert.Equal("10.0.0.0/26", origin.ToPostgresAddress());
    }

    /// <summary>Ifade edilebilen onekler MySQL'de joker desene cevrilir.</summary>
    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.%")]
    [InlineData("172.16.0.0/16", "172.16.%")]
    [InlineData("10.0.0.0/8", "10.%")]
    [InlineData("203.0.113.7/32", "203.0.113.7")]
    public void MySqlHost_RendersSupportedPrefixesAsPatterns(string girdi, string beklenen)
    {
        Assert.Equal(beklenen, RemoteAccessOrigin.Parse(girdi).ToMySqlHost());
    }

    /// <summary>MySQL'in kendi joker yazimi da kabul edilir ve PostgreSQL'de CIDR'a cevrilir.</summary>
    [Theory]
    [InlineData("192.168.1.%", "192.168.1.%", "192.168.1.0/24")]
    [InlineData("172.16.%", "172.16.%", "172.16.0.0/16")]
    [InlineData("10.%", "10.%", "10.0.0.0/8")]
    public void WildcardPattern_WorksOnBothEngines(string girdi, string mysql, string postgres)
    {
        var origin = RemoteAccessOrigin.Parse(girdi);

        Assert.Equal(mysql, origin.ToMySqlHost());
        Assert.Equal(postgres, origin.ToPostgresAddress());
    }

    /// <summary>
    /// Ana bilgisayar ADI kabul EDILMEZ. DNS tabanli erisim listesi, saldirganin
    /// etkileyebildigi bir cozumlemeye guvenmek olurdu.
    /// </summary>
    [Theory]
    [InlineData("ofis.ornek.com")]
    [InlineData("%.ornek.com")]
    [InlineData("localhost")]
    public void HostNames_AreRejected(string girdi)
    {
        Assert.Throws<AppException>(() => RemoteAccessOrigin.Parse(girdi));
    }

    /// <summary>IPv6 tek adres desteklenir; cikti IPAddress'in kanonik yazimidir.</summary>
    [Fact]
    public void SingleIpv6Address_IsSupported()
    {
        var origin = RemoteAccessOrigin.Parse("2001:0DB8:0000:0000:0000:0000:0000:0001");

        Assert.Equal("2001:db8::1", origin.Canonical);
        Assert.Equal("2001:db8::1/128", origin.ToPostgresAddress());
    }

    /// <summary>
    /// IPv6 CIDR reddedilir: MySQL hesabinin host kisminda karsiligi YOKTUR. Yalnizca
    /// PostgreSQL'de desteklemek, ayni girdinin motora gore farkli davranmasi demek olurdu.
    /// </summary>
    [Fact]
    public void Ipv6Cidr_IsRejectedBecauseMySqlCannotExpressIt()
    {
        Assert.Throws<AppException>(() => RemoteAccessOrigin.Parse("2001:db8::/32"));
    }

    // ---------------------------------------------------------------------------------
    // Liste
    // ---------------------------------------------------------------------------------

    /// <summary>Tek IP yerine LISTE: eklenen ozelligin cekirdegi.</summary>
    [Fact]
    public void ParseAll_KeepsOrderAndDropsDuplicates()
    {
        var origins = RemoteAccessOrigin.ParseAll(
            ["88.1.2.3", "10.0.0.0/8", "  ", "88.1.2.3", "192.168.1.%"]);

        Assert.Equal(["88.1.2.3", "10.0.0.0/8", "192.168.1.%"], origins.Select(item => item.Canonical));
    }

    /// <summary>Listedeki TEK bir gecersiz deger butun istegi dusurur (yarim uygulama olmaz).</summary>
    [Fact]
    public void ParseAll_RejectsWholeListWhenOneEntryIsInvalid()
    {
        Assert.Throws<AppException>(() => RemoteAccessOrigin.ParseAll(["88.1.2.3", "bozuk deger"]));
    }
}
