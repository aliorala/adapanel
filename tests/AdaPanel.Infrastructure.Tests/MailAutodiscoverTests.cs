using System.Xml.Linq;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Middleware;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Outlook/Thunderbird otomatik yapılandırma uçlarının davranışını sabitler.
/// </summary>
/// <remarks>
/// <para>
/// Korunan üç şey: (1) tanımsız alan adına 404 — bu uçlar kimlik doğrulamasızdır, cevap veren
/// her ad "burada barınıyor" bilgisini bedava dağıtır; (2) XML'deki her değerin panelin GERÇEK
/// ayarından gelmesi — sabit port yazmak, sunucu o portu dinlemiyorken istemciye çalışmayan bir
/// ayar dağıtmaktır; (3) yanıtta hiçbir sır/hesap bilgisi olmaması.
/// </para>
/// </remarks>
public sealed class MailAutodiscoverTests
{
    private static MailClientSettingsDto Ayarlar(string domain = "ornek.com", string host = "mail.ornek.com") => new()
    {
        Domain = domain,
        HostName = host,
        Servers =
        [
            new MailClientServerDto { Protocol = "imap", Host = host, Port = 993, SocketType = "SSL" },
            new MailClientServerDto { Protocol = "smtp", Host = host, Port = 587, SocketType = "STARTTLS" }
        ]
    };

    // --- 1) Tanımsız alan adı → 404 ---

    [Fact]
    public async Task Autodiscover_panelde_TANIMSIZ_alan_adina_404_doner()
    {
        var denetleyici = Denetleyici(new SahteMailServisi(null), "autodiscover.bilinmeyen.com");

        var sonuc = await denetleyici.OutlookGetAsync("kullanici@bilinmeyen.com", CancellationToken.None);

        Assert.IsType<NotFoundResult>(sonuc);
    }

    [Fact]
    public async Task Autoconfig_panelde_TANIMSIZ_alan_adina_404_doner()
    {
        var denetleyici = Denetleyici(new SahteMailServisi(null), "autoconfig.bilinmeyen.com");

        var sonuc = await denetleyici.ThunderbirdAsync("kullanici@bilinmeyen.com", CancellationToken.None);

        Assert.IsType<NotFoundResult>(sonuc);
    }

    [Fact]
    public async Task Tanimli_alan_adina_XML_doner()
    {
        var denetleyici = Denetleyici(new SahteMailServisi(Ayarlar()), "autodiscover.ornek.com");

        var sonuc = Assert.IsType<ContentResult>(await denetleyici.OutlookGetAsync("info@ornek.com", CancellationToken.None));

        Assert.Equal("application/xml", sonuc.ContentType?.Split(';')[0]);
        Assert.Contains("Autodiscover", sonuc.Content ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sorulan alan adı, ana addaki hizmet önekinden ARINDIRILMALI: istemci uca tam olarak
    /// "autodiscover.&lt;alan&gt;" adından gelir ve o ad panelde bir posta alanı DEĞİLDİR.
    /// Arındırma olmadan her istek 404 alır — yani uç eklenmiş ama hiç çalışmamış olur.
    /// </summary>
    [Theory]
    [InlineData(null, "autodiscover.ornek.com", "ornek.com")]
    [InlineData(null, "autoconfig.ornek.com", "ornek.com")]
    [InlineData(null, "mail.ornek.com", "ornek.com")]
    [InlineData(null, "ornek.com", "ornek.com")]
    [InlineData("info@BASKA.com", "autodiscover.ornek.com", "baska.com")]
    public void Sorulan_alan_adi_adresten_ve_ana_addan_cozulur(string? adres, string host, string beklenen)
    {
        Assert.Equal(beklenen, MailAutodiscoverController.ResolveDomainFrom(adres, host));
    }

    // --- 2) XML panelin GERÇEK ayarlarından türer ---

    /// <summary>
    /// Aynı üretici, farklı ayarlarla FARKLI çıktı vermeli. Sabit kodlanmış bir port/ad olsaydı
    /// bu test, iki farklı sunucu için aynı XML'i görüp patlardı.
    /// </summary>
    [Fact]
    public void Outlook_XML_i_ayarlardaki_ad_ve_portlari_kullanir()
    {
        var ayarlar = new MailClientSettingsDto
        {
            Domain = "baska.com",
            HostName = "posta.baska.com",
            Servers =
            [
                new MailClientServerDto { Protocol = "imap", Host = "posta.baska.com", Port = 1143, SocketType = "STARTTLS" },
                new MailClientServerDto { Protocol = "smtp", Host = "posta.baska.com", Port = 2465, SocketType = "SSL" }
            ]
        };

        var xml = XDocument.Parse(MailAutodiscoverBuilder.BuildOutlookXml(ayarlar, "info@baska.com"));
        var protokoller = xml.Descendants().Where(item => item.Name.LocalName == "Protocol").ToList();

        Assert.Equal(2, protokoller.Count);

        var imap = protokoller.Single(item => Deger(item, "Type") == "IMAP");
        Assert.Equal("posta.baska.com", Deger(imap, "Server"));
        Assert.Equal("1143", Deger(imap, "Port"));
        // STARTTLS: bağlantı doğrudan TLS ile açılmaz ama YİNE ŞİFRELENİR. İkisini tek bayrağa
        // indirmek, STARTTLS'i istemciye "şifresiz" diye bildirmek olurdu.
        Assert.Equal("off", Deger(imap, "SSL"));
        Assert.Equal("TLS", Deger(imap, "Encryption"));

        var smtp = protokoller.Single(item => Deger(item, "Type") == "SMTP");
        Assert.Equal("2465", Deger(smtp, "Port"));
        Assert.Equal("on", Deger(smtp, "SSL"));
        Assert.Equal("SSL", Deger(smtp, "Encryption"));
    }

    [Fact]
    public void Autoconfig_XML_i_ayarlardaki_ad_ve_portlari_kullanir()
    {
        var ayarlar = new MailClientSettingsDto
        {
            Domain = "baska.com",
            HostName = "posta.baska.com",
            Servers =
            [
                new MailClientServerDto { Protocol = "pop3", Host = "posta.baska.com", Port = 1995, SocketType = "SSL" },
                new MailClientServerDto { Protocol = "smtp", Host = "posta.baska.com", Port = 2587, SocketType = "STARTTLS" }
            ]
        };

        var xml = XDocument.Parse(MailAutodiscoverBuilder.BuildAutoconfigXml(ayarlar));

        var gelen = xml.Descendants().Single(item => item.Name.LocalName == "incomingServer");
        Assert.Equal("pop3", gelen.Attribute("type")?.Value);
        Assert.Equal("1995", Deger(gelen, "port"));
        Assert.Equal("SSL", Deger(gelen, "socketType"));

        var giden = xml.Descendants().Single(item => item.Name.LocalName == "outgoingServer");
        Assert.Equal("2587", Deger(giden, "port"));
        Assert.Equal("STARTTLS", Deger(giden, "socketType"));
        Assert.Equal("posta.baska.com", Deger(giden, "hostname"));
    }

    /// <summary>
    /// Sunucuda dinleyici YOKSA o protokol XML'e hiç yazılmaz. "Her ihtimale karşı" 993/995/587
    /// yazmak, kullanıcıyı kapalı bir porta bağlanmaya çalıştırır ve elle kurmaktan daha kötü
    /// bir deneyim üretir.
    /// </summary>
    [Fact]
    public void Olcemedigimiz_protokol_XML_e_YAZILMAZ()
    {
        var yalnizImap = new MailClientSettingsDto
        {
            Domain = "ornek.com",
            HostName = "mail.ornek.com",
            Servers = [new MailClientServerDto { Protocol = "imap", Host = "mail.ornek.com", Port = 143, SocketType = "STARTTLS" }]
        };

        var outlook = MailAutodiscoverBuilder.BuildOutlookXml(yalnizImap, "info@ornek.com");
        var autoconfig = MailAutodiscoverBuilder.BuildAutoconfigXml(yalnizImap);

        Assert.DoesNotContain("SMTP", outlook, StringComparison.Ordinal);
        Assert.DoesNotContain("POP3", outlook, StringComparison.Ordinal);
        Assert.DoesNotContain("outgoingServer", autoconfig, StringComparison.Ordinal);
    }

    /// <summary>
    /// Üreticide SABİT KODLU sunucu adı/port bulunmamalı. Bu, yukarıdaki davranış testlerinin
    /// kaynak tarafındaki karşılığıdır: birisi "varsayılan olarak 993 yazalım" dediğinde
    /// davranış testleri hâlâ geçebilir ama bu test kırılır.
    /// </summary>
    [Fact]
    public void Uretici_kaynaginda_sabit_kodlu_port_YOKTUR()
    {
        var kaynak = File.ReadAllText(Path.Combine(
            KaynakKoku(), "src", "AdaPanel.Infrastructure", "Mail", "MailAutodiscoverBuilder.cs"));

        foreach (var port in new[] { "993", "995", "465", "587", "143", "110", "25" })
        {
            Assert.DoesNotContain(port, kaynak, StringComparison.Ordinal);
        }
    }

    // --- 3) Yanıtta sır / yabancı içerik yok ---

    /// <summary>
    /// Thunderbird yanıtında kullanıcı adı SUNUCUDAN gelmez; istemcinin kendi yer tutucusu
    /// kullanılır. Aksi halde kimlik doğrulamasız bir uç, hesap adlarını doğrulayan bir sözlük
    /// saldırısı aracına dönerdi.
    /// </summary>
    [Fact]
    public void Autoconfig_yaniti_hesap_bilgisi_tasimaz()
    {
        var xml = MailAutodiscoverBuilder.BuildAutoconfigXml(Ayarlar());

        Assert.Contains("%EMAILADDRESS%", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("info@ornek.com", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Outlook yanıtı, isteyenin yazdığı adresi YALNIZCA o alan adına aitse yansıtır. Aksi halde
    /// kimlik doğrulamasız uç, başkasının metnini kendi alan adı altından sunan bir yansıtıcı olur.
    /// </summary>
    [Theory]
    [InlineData("kotu@baskasite.com")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("")]
    public void Outlook_yaniti_yabanci_adresi_YANSITMAZ(string adres)
    {
        var xml = MailAutodiscoverBuilder.BuildOutlookXml(Ayarlar(), adres);

        Assert.DoesNotContain("baskasite.com", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("script", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Outlook_yaniti_kendi_alan_adindaki_adresi_yansitir()
    {
        var xml = MailAutodiscoverBuilder.BuildOutlookXml(Ayarlar(), "info@ORNEK.com");

        Assert.Contains("info@ornek.com", xml, StringComparison.Ordinal);
    }

    // --- 4) Uçlar gerçekten ERİŞİLEBİLİR mi ---

    /// <summary>
    /// Uçların GİZLİ GİRİŞ YOLU denetiminden muaf olduğunu sabitler.
    /// </summary>
    /// <remarks>
    /// Muafiyet olmadan panel bu yolları 404'lar: özellik kodda vardır ama HER istekte ölüdür.
    /// Bu depoda tam olarak bu sınıftan bir hata yaşandı (yolu hiç eşleşmeyen bir dize
    /// değiştirmesi üç özelliği sessizce ölü bıraktı), bu yüzden erişilebilirlik ayrıca ölçülüyor.
    /// </remarks>
    [Theory]
    [InlineData("/autodiscover/autodiscover.xml")]
    [InlineData("/Autodiscover/Autodiscover.xml")]
    [InlineData("/.well-known/autoconfig/mail/config-v1.1.xml")]
    [InlineData("/mail/config-v1.1.xml")]
    public void Otomatik_yapilandirma_yollari_gizli_giris_yolundan_muaftir(string yol)
    {
        // Yalnız yardımcı metot değil, ONU KULLANAN muafiyet listesi ölçülür: yardımcı doğru olup
        // listeye bağlanmamış olsaydı uçlar yine her istekte 404 dönerdi.
        Assert.True(SecureEntryPathMiddleware.IsAlwaysPublicPath(new PathString(yol)), yol + " gizli giris yoluna takiliyor.");
    }

    /// <summary>Muafiyet DAR olmalı: panelin kendi yolları bu kapıdan geçmemeli.</summary>
    [Theory]
    [InlineData("/api/v1/mail")]
    [InlineData("/mail")]
    [InlineData("/.well-known/acme-challenge/x")]
    public void Muafiyet_panelin_diger_yollarini_ACMAZ(string yol)
    {
        Assert.False(SecureEntryPathMiddleware.IsMailAutoconfigPath(new PathString(yol)), yol + " yanlislikla herkese acildi.");
    }

    private static string Deger(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(item => item.Name.LocalName == localName)?.Value ?? string.Empty;

    private static MailAutodiscoverController Denetleyici(IMailService servis, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        return new MailAutodiscoverController(servis)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static string KaynakKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        return dizin!.FullName;
    }

    /// <summary>Yalnız istemci ayarlarını döndüren sahte servis; kalan üyeler çağrılmaz.</summary>
    private sealed class SahteMailServisi(MailClientSettingsDto? ayarlar) : IMailService
    {
        public Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default)
            => Task.FromResult(ayarlar is not null && string.Equals(ayarlar.Domain, domain, StringComparison.OrdinalIgnoreCase) ? ayarlar : null);

        public Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // Ek adres (alias) uclari artik IMailService uzerinden geciyor; bu sahte servis posta isi yapmaz.
        public Task<MailWorkspaceDto> AddMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> RemoveMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailListenerSnapshotDto> GetListenersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
