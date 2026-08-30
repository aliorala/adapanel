using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Kayıtlar yayınlandı" HÜKMÜ ölçülüyor mu, yoksa varsayılıyor mu?
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen yalan:</b> <c>DnsService.UpsertRecordsAsync</c> bölge İKİNCİL ise kayıtları yalnız
/// panel durumuna yazar, Windows DNS'e HİÇ dokunmaz ve BAŞARIYLA döner (posta kutusu açmayı
/// DNS'e bağlamamak için bilinçli bir karar). Windows DNS rolü kurulu değilse ya da bölge
/// sunucuda yoksa sonuç aynıdır. Buna rağmen panel her iki yolda da "yayınlandı" diyordu:
/// <c>MailService</c> koşulsuz bilgi logu yazıyor, <c>MailController</c> ise <c>AppliedCount</c>'u
/// yazılan kayıttan değil gönderilmesi düşünülen kayıttan üretiyordu.
/// </para>
/// <para>
/// <b>Neden bu, posta teslimatının kendisidir:</b> Stalwart alan adı açılırken DKIM anahtarını
/// üretir ve giden HER postayı imzalar. TXT kaydı yayınlanmazsa alıcı imzayı doğrulayamaz —
/// posta imzalı ama DOĞRULANAMAZ gider, DKIM başarısız sayılır ve spam'e düşer. Panel
/// "yayınlandı" dediği sürece kimse sebebi aramaz.
/// </para>
/// </remarks>
public sealed class PostaDnsYayinDurustlugTests
{
    private static SiteDnsZoneDto Bolge(bool ikincil = false, bool dnsVar = true, bool bolgeVar = true) => new()
    {
        ZoneName = "ornek.com",
        IsSecondary = ikincil,
        IsWindowsDnsAvailable = dnsVar,
        IsWindowsZonePresent = bolgeVar
    };

    [Fact]
    public void Ikincil_bolgede_YAYINLANDI_denmez()
    {
        var sonuc = MailDnsPublication.Evaluate(Bolge(ikincil: true), requestedCount: 5);

        Assert.False(sonuc.Published);
        Assert.Equal(0, sonuc.AppliedCount);
        Assert.Equal(MailDnsPublication.SecondaryZoneKey, sonuc.StatusKey);
    }

    [Fact]
    public void Windows_DNS_rolu_yoksa_YAYINLANDI_denmez()
    {
        var sonuc = MailDnsPublication.Evaluate(Bolge(dnsVar: false, bolgeVar: false), requestedCount: 5);

        Assert.False(sonuc.Published);
        Assert.Equal(0, sonuc.AppliedCount);
        Assert.Equal(MailDnsPublication.NoDnsServerKey, sonuc.StatusKey);
    }

    [Fact]
    public void Bolge_sunucuda_yoksa_YAYINLANDI_denmez()
    {
        var sonuc = MailDnsPublication.Evaluate(Bolge(bolgeVar: false), requestedCount: 5);

        Assert.False(sonuc.Published);
        Assert.Equal(MailDnsPublication.ZoneMissingKey, sonuc.StatusKey);
    }

    [Fact]
    public void Hic_yazma_yapilmadiysa_YAYINLANDI_denmez()
    {
        // Kayıt üretilemediğinde (ör. panel sunucu adresi boş) yazma çağrısı HİÇ yapılmaz;
        // bölge DTO'su da yoktur. "0 kayıt uygulandı" bir başarı satırı olarak geçmemeli.
        Assert.False(MailDnsPublication.Evaluate(null, requestedCount: 3).Published);
        Assert.Equal(MailDnsPublication.NothingToApplyKey, MailDnsPublication.Evaluate(Bolge(), requestedCount: 0).StatusKey);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: sağlıklı birincil bölgede yayın GERÇEKTEN olur ve sayı doğrudur.
    /// Bu test kırmızı yanarsa sertleştirme meşru yayını öldürmüştür.
    /// </summary>
    [Fact]
    public void Saglikli_birincil_bolgede_yayin_YAPILIR_ve_sayi_dogrudur()
    {
        var sonuc = MailDnsPublication.Evaluate(Bolge(), requestedCount: 5);

        Assert.True(sonuc.Published);
        Assert.Equal(5, sonuc.AppliedCount);
        Assert.Equal(MailDnsPublication.AppliedKey, sonuc.StatusKey);
    }

    /// <summary>
    /// Ölçülen sıra: ikincil bölge, Windows DNS kurulu ve bölge mevcut OLSA BİLE yayınlayamaz.
    /// En dıştaki engel önce sorulmazsa sebep yanlış raporlanır.
    /// </summary>
    [Fact]
    public void Ikincil_bolge_diger_kosullar_saglikliyken_de_engeldir()
    {
        var sonuc = MailDnsPublication.Evaluate(Bolge(ikincil: true, dnsVar: true, bolgeVar: true), requestedCount: 2);

        Assert.Equal(MailDnsPublication.SecondaryZoneKey, sonuc.StatusKey);
    }

    // ------------------------------------------------------------------
    // İstek üretimi
    // ------------------------------------------------------------------

    [Fact]
    public void Degeri_BOS_olan_kayit_bolgeye_yazilmaz()
    {
        // Panel sunucu adresi boşken A kaydının değeri boş kalır. Boş değerli bir A kaydı yazmak
        // bölgeyi bozar; üstelik "1 kayıt uygulandı" diye de sayılırdı.
        var istekler = MailDnsPublication.BuildRequests(new MailDnsDto
        {
            Domain = "ornek.com",
            Records =
            [
                new MailDnsRecordDto { Type = "A", Host = "mail", Value = string.Empty },
                new MailDnsRecordDto { Type = "MX", Host = "@", Value = "mail.ornek.com", Priority = 10 }
            ]
        });

        Assert.Equal("MX", Assert.Single(istekler).Type);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: DEĞERİ OLAN her kayıt gerçekten yazılmaya gider — SPF, DKIM ve
    /// DMARC dahil. Süzgeç genişletilirse posta politikası sessizce yayınlanmaz olur.
    /// </summary>
    [Fact]
    public void Degeri_olan_TUM_posta_kayitlari_yazilmaya_gider()
    {
        var istekler = MailDnsPublication.BuildRequests(new MailDnsDto
        {
            Domain = "ornek.com",
            Records =
            [
                new MailDnsRecordDto { Type = "A", Host = "mail", Value = "203.0.113.10" },
                new MailDnsRecordDto { Type = "MX", Host = "@", Value = "mail.ornek.com", Priority = 10 },
                new MailDnsRecordDto { Type = "TXT", Host = "@", Value = "v=spf1 mx a ~all" },
                new MailDnsRecordDto { Type = "TXT", Host = "v1-rsa-20260831._domainkey", Value = "v=DKIM1; k=rsa; p=AAAA" },
                new MailDnsRecordDto { Type = "TXT", Host = "_dmarc", Value = "v=DMARC1; p=quarantine" }
            ]
        });

        Assert.Equal(5, istekler.Count);
        Assert.All(istekler, istek => Assert.Equal(3600, istek.Ttl));
        Assert.Contains(istekler, istek => istek.Value.StartsWith("v=DKIM1", StringComparison.Ordinal));
        Assert.Equal(10, istekler.Single(istek => istek.Type == "MX").Priority);
    }

    // ------------------------------------------------------------------
    // KABLOLAMA — davranış testi: "Uygula" ucu bölgeye KAÇ çağrı yapıyor?
    // ------------------------------------------------------------------

    /// <summary>
    /// Kayıtlar TEK TEK değil TOPLU yazılır.
    /// </summary>
    /// <remarks>
    /// Kırık desen bu depoda ÖLÇÜLMÜŞ ve iki ayrı dosyada yorumla yazılmıştı: her tek yazma
    /// bölgenin tamamını yeniden yazan bir senkron başlatır, uzun DKIM TXT'sinin bölge kilidi
    /// bir sonraki yazmayı düşürür. Buna rağmen "Uygula" ucu tek tek yazmaya devam ediyordu ve
    /// alan adı oluşturulurken bu döngü, düzeltilmiş toplu yazmanın hemen ardından AYNI istekte
    /// ikinci kez koşuyordu.
    /// </remarks>
    [Fact]
    public async Task Uygula_ucu_kayitlari_TEK_TEK_degil_TOPLU_yazar()
    {
        var dns = new SayanDnsServisi(Bolge());
        var denetleyici = Denetleyici(dns);

        await denetleyici.ApplyDnsAsync("ornek.com", dns, CancellationToken.None);

        Assert.Equal(0, dns.TekTekYazmaSayisi);
        Assert.Equal(1, dns.TopluYazmaSayisi);
        Assert.Equal(3, dns.SonToplu.Count);
    }

    [Fact]
    public async Task Uygula_ucu_ikincil_bolgede_UYGULANDI_demez()
    {
        var dns = new SayanDnsServisi(Bolge(ikincil: true));
        var denetleyici = Denetleyici(dns);

        var cevap = await denetleyici.ApplyDnsAsync("ornek.com", dns, CancellationToken.None);
        var zarf = Assert.IsType<Core.Dtos.Common.ApiResponse<ApplyMailDnsResultDto>>(
            Assert.IsType<OkObjectResult>(cevap.Result).Value);

        Assert.False(zarf.Data!.Published);
        Assert.Equal(0, zarf.Data.AppliedCount);
        Assert.Equal(MailDnsPublication.SecondaryZoneKey, zarf.Message);
    }

    /// <summary>FAZLA KAPATMA NÖBETÇİSİ: sağlıklı bölgede uç hâlâ dürüst bir başarı döner.</summary>
    [Fact]
    public async Task Uygula_ucu_saglikli_bolgede_gercek_sayiyi_doner()
    {
        var dns = new SayanDnsServisi(Bolge());
        var denetleyici = Denetleyici(dns);

        var cevap = await denetleyici.ApplyDnsAsync("ornek.com", dns, CancellationToken.None);
        var zarf = Assert.IsType<Core.Dtos.Common.ApiResponse<ApplyMailDnsResultDto>>(
            Assert.IsType<OkObjectResult>(cevap.Result).Value);

        Assert.True(zarf.Data!.Published);
        Assert.Equal(3, zarf.Data.AppliedCount);
        Assert.Equal(MailDnsPublication.AppliedKey, zarf.Message);
    }

    private static MailController Denetleyici(IDnsService dns)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "Administrator"), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                "test"))
        };

        return new MailController(
            new SahtePostaServisi
            {
                DnsKayitlari =
                [
                    new MailDnsRecordDto { Type = "MX", Host = "@", Value = "mail.ornek.com", Priority = 10 },
                    new MailDnsRecordDto { Type = "TXT", Host = "@", Value = "v=spf1 mx a ~all" },
                    new MailDnsRecordDto { Type = "TXT", Host = "_dmarc", Value = "v=DMARC1; p=quarantine" }
                ]
            },
            new KullanilmayanWebmail(),
            new AuditTenantLinkClosureTests.SahteKapsam(),
            new AuditTenantLinkClosureTests.SayanSiteler([]),
            new AuditTenantLinkClosureTests.KayitDefteri())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    /// <summary>Yazma çağrılarını SAYAR: kaç kez ve hangi biçimde yazıldığı ölçülebilsin diye.</summary>
    private sealed class SayanDnsServisi(SiteDnsZoneDto bolge) : IDnsService
    {
        public int TekTekYazmaSayisi { get; private set; }

        public int TopluYazmaSayisi { get; private set; }

        public IReadOnlyList<UpsertDnsRecordRequest> SonToplu { get; private set; } = [];

        public Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default)
        {
            TekTekYazmaSayisi++;
            return Task.FromResult(bolge);
        }

        public Task<SiteDnsZoneDto> UpsertRecordsAsync(string siteName, IReadOnlyList<UpsertDnsRecordRequest> requests, CancellationToken cancellationToken = default)
        {
            TopluYazmaSayisi++;
            SonToplu = requests;
            return Task.FromResult(bolge);
        }

        public Task<SiteDnsZoneDto> GetZoneAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> InstallWindowsDnsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> UpdateZoneAsync(string siteName, UpdateDnsZoneRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> ResetDefaultsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> SetZoneModeAsync(string siteName, SetDnsZoneModeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> TransferZoneNowAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> RepairDefaultsAsync(string siteName, bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetPublishedRecordKeysAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubdomainRecordResult> EnsureSubdomainRecordAsync(string parentSiteName, string label, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteZoneAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureZonePublishedAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto> GetServerHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto> RepairServerAsync(bool applyDelayedAutoStart, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto?> RunBindWatchdogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsTemplateDto> GetTemplateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsTemplateDto> UpdateTemplateAsync(UpdateDnsTemplateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> SetDnssecAsync(string siteName, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Bu testlerde webmail'e HİÇ dokunulmaz; her çağrı sesli hata verir.</summary>
    private sealed class KullanilmayanWebmail : IWebmailService
    {
        public Task<WebmailOpenDto> OpenAsync(string publicHost, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WebmailDiagnosticsDto> DiagnoseAsync(string publicHost, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SecureWebmailForDomainAsync(string domain, string contactEmail, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
