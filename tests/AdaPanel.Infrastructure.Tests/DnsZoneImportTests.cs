using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Dns;
using Xunit;
using static AdaPanel.Infrastructure.Tests.DnsZoneFileTests;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>İki aşamalı içe aktarma: önizleme yazmaz, uygulama yalnız önizlenen metni ve onayla yazar.</summary>
public sealed class DnsZoneImportTests
{
    private const string Zone = "ornek.com";

    private static Func<IReadOnlyList<string>, CancellationToken, Task> Onay => (_, _) => Task.CompletedTask;

    // --- Önizleme: fark + çakışma ---

    [Fact]
    public void Onizleme_ekle_degistir_degismedi_ayirir()
    {
        var bolge = Bolge(
            Kayit("A", "@", "203.0.113.5", id: "a1"),                 // aynı
            Kayit("A", "app", "203.0.113.9", id: "a2"),               // değer değişecek
            Kayit("CNAME", "www", "ornek.com", id: "c1", ttl: 300));  // yalnız TTL değişecek

        var onizleme = DnsZoneImport.Preview(bolge, string.Join('\n',
            "@ 3600 IN A 203.0.113.5",
            "app 3600 IN A 203.0.113.10",
            "www 3600 IN CNAME ornek.com.",
            "yeni 3600 IN A 203.0.113.77"));

        Assert.Empty(onizleme.Errors);
        Assert.Equal(1, onizleme.UnchangedCount);
        Assert.Equal(2, onizleme.ChangeCount);
        Assert.Equal(1, onizleme.AddCount);
        var degisen = onizleme.Items.Single(i => i.Host == "app");
        Assert.Equal("change", degisen.Action);
        Assert.Equal("a2", degisen.ExistingId);
        Assert.Equal("203.0.113.9", degisen.ExistingValue);
        Assert.True(onizleme.CanApply);
    }

    [Fact]
    public void Onizleme_ayni_konakta_birden_cok_deger_varsa_EZMEZ_ekler()
    {
        var bolge = Bolge(Kayit("A", "@", "203.0.113.5", id: "a1"), Kayit("A", "@", "203.0.113.6", id: "a2"));
        var onizleme = DnsZoneImport.Preview(bolge, "@ IN A 203.0.113.7");

        Assert.Equal("add", onizleme.Items.Single().Action);
    }

    [Fact]
    public void Yonetilen_kayitla_cakisma_isaretlenir()
    {
        var bolge = Bolge(Kayit("A", "mail", "203.0.113.5", id: "m1", yonetilen: true));
        var onizleme = DnsZoneImport.Preview(bolge, "mail IN A 198.51.100.1");

        var satir = onizleme.Items.Single();
        Assert.True(satir.ConflictsWithManaged);
        Assert.True(satir.IsMailRelated);
        Assert.Equal(1, onizleme.ManagedConflictCount);
    }

    [Fact]
    public void Posta_kaydi_YONETILEN_bayragi_olmasa_da_cakisma_sayilir()
    {
        // Posta yayını kayıtları "yönetilmeyen" olarak yazar (UpsertRecordsAsync → IsManaged=false);
        // bu yüzden yalnız bayrağa bakmak MX/SPF/DKIM/DMARC'ı korumasız bırakırdı.
        var bolge = Bolge(
            Kayit("MX", "@", "mail.ornek.com", 10, id: "mx"),
            Kayit("TXT", "@", "v=spf1 a mx ~all", id: "spf"),
            Kayit("TXT", "_dmarc", "v=DMARC1; p=none", id: "dmarc"),
            Kayit("TXT", "s1._domainkey", "v=DKIM1; k=rsa; p=ABC", id: "dkim"));

        var onizleme = DnsZoneImport.Preview(bolge, string.Join('\n',
            "@ IN MX 5 baska.net.",
            "@ IN TXT \"v=spf1 -all\"",
            "_dmarc IN TXT \"v=DMARC1; p=reject\"",
            "s1._domainkey IN TXT \"v=DKIM1; k=rsa; p=XYZ\""));

        Assert.Empty(onizleme.Errors);
        Assert.Equal(4, onizleme.Items.Count(i => i.ConflictsWithManaged));
    }

    [Fact]
    public void Posta_olmayan_yonetilmeyen_kayit_cakisma_SAYILMAZ()
    {
        var bolge = Bolge(Kayit("A", "app", "203.0.113.9", id: "a2"));
        var onizleme = DnsZoneImport.Preview(bolge, "app IN A 203.0.113.10");

        Assert.False(onizleme.Items.Single().ConflictsWithManaged);
    }

    [Fact]
    public void Gecersiz_dosya_onizlemede_UYGULANAMAZ_isaretlenir_ve_satir_verir()
    {
        var onizleme = DnsZoneImport.Preview(Bolge(), "$INCLUDE x\nwww IN A 203.0.113.5");

        Assert.False(onizleme.CanApply);
        Assert.Contains(onizleme.Errors, e => e.Code == "dns.import_directive_forbidden" && e.Line == 1);
        Assert.Empty(onizleme.Items);
    }

    [Fact]
    public void Ikincil_bolgeye_uygulanamaz()
    {
        var bolge = Bolge();
        bolge.IsSecondary = true;
        var onizleme = DnsZoneImport.Preview(bolge, "www IN A 203.0.113.5");

        Assert.False(onizleme.CanApply);
        Assert.Contains(onizleme.Errors, e => e.Code == "dns.zone_is_secondary");
    }

    // --- Uygulama ---

    private const string Metin = "yeni IN A 203.0.113.50\nmail IN A 198.51.100.1";

    private static DnsZoneImportApplyRequest Istek(SiteDnsZoneDto bolge, string metin = Metin, bool onay = false) => new()
    {
        ZoneText = metin, ConfirmOverwriteManaged = onay, PreviewHash = DnsZoneImport.ComputeHash(bolge.ZoneName, metin)
    };

    [Fact]
    public async Task Onizleme_hicbir_sey_YAZMAZ()
    {
        var bolge = Bolge(Kayit("A", "mail", "203.0.113.5", yonetilen: true));
        var servis = new YazanDns(bolge);

        _ = DnsZoneImport.Preview(await servis.GetZoneAsync("ornek"), Metin);

        Assert.Empty(servis.Yazilanlar);
    }

    [Fact]
    public async Task Hash_uyusmazsa_uygulanmaz()
    {
        var bolge = Bolge();
        var servis = new YazanDns(bolge);
        var istek = Istek(bolge);
        istek.ZoneText += "\nbaska IN A 203.0.113.99"; // önizlemeden SONRA değişmiş metin

        var hata = await Assert.ThrowsAsync<AppException>(
            () => DnsZoneImport.ApplyAsync(servis, "ornek", istek, Onay, CancellationToken.None));

        Assert.Equal("dns.import_hash_mismatch", hata.MessageKey);
        Assert.Empty(servis.Yazilanlar);
    }

    [Fact]
    public async Task Gecersiz_metin_hash_dogru_olsa_bile_uygulanmaz()
    {
        var bolge = Bolge();
        var servis = new YazanDns(bolge);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => DnsZoneImport.ApplyAsync(servis, "ornek", Istek(bolge, "$GENERATE 1-2 a$ A 1.1.1.1"), Onay, CancellationToken.None));

        Assert.Equal("dns.import_invalid", hata.MessageKey);
        Assert.Empty(servis.Yazilanlar);
    }

    [Fact]
    public async Task Onay_YOKSA_yonetilen_cakisma_ATLANIR_ve_raporlanir_digerleri_yazilir()
    {
        var bolge = Bolge(Kayit("A", "mail", "203.0.113.5", id: "m1", yonetilen: true));
        var servis = new YazanDns(bolge);

        var sonuc = await DnsZoneImport.ApplyAsync(servis, "ornek", Istek(bolge), Onay, CancellationToken.None);

        Assert.Equal(1, sonuc.Added);
        Assert.Single(sonuc.SkippedManaged);
        Assert.Equal("mail", sonuc.SkippedManaged[0].Host);
        Assert.True(sonuc.IsPartial);
        Assert.Single(servis.Yazilanlar);
        Assert.Equal("yeni", servis.Yazilanlar[0].Host);
        Assert.DoesNotContain(servis.Yazilanlar, y => y.Host == "mail");
    }

    [Fact]
    public async Task Onayla_yonetilen_kayit_MEVCUT_kimlikle_ezilir()
    {
        var bolge = Bolge(Kayit("A", "mail", "203.0.113.5", id: "m1", yonetilen: true));
        var servis = new YazanDns(bolge);

        var sonuc = await DnsZoneImport.ApplyAsync(servis, "ornek", Istek(bolge, onay: true), Onay, CancellationToken.None);

        Assert.Equal(1, sonuc.Added);
        Assert.Equal(1, sonuc.Changed);
        Assert.Empty(sonuc.SkippedManaged);
        Assert.False(sonuc.IsPartial);
        Assert.Equal("m1", servis.Yazilanlar.Single(y => y.Host == "mail").Id);
    }

    [Fact]
    public async Task Yazma_onay_geri_cagrisi_HER_kayit_icin_kullanilir()
    {
        var bolge = Bolge();
        var servis = new YazanDns(bolge);
        var cagri = 0;

        await DnsZoneImport.ApplyAsync(servis, "ornek", Istek(bolge), (_, _) => { cagri++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(2, cagri);
        Assert.Equal(2, servis.OnayliCagri);
    }

    [Fact]
    public async Task Kismi_basari_acikca_raporlanir_ilk_hatada_durulur()
    {
        var bolge = Bolge();
        var servis = new YazanDns(bolge) { IkinciYazmadaHata = true };
        var metin = "a1 IN A 203.0.113.1\na2 IN A 203.0.113.2\na3 IN A 203.0.113.3\na4 IN A 203.0.113.4";

        var sonuc = await DnsZoneImport.ApplyAsync(servis, "ornek", Istek(bolge, metin), Onay, CancellationToken.None);

        Assert.Equal(1, sonuc.Added);
        Assert.Single(sonuc.Failed);
        Assert.Equal("a2", sonuc.Failed[0].Host);
        Assert.Equal("dns.sync_failed", sonuc.Failed[0].Error);
        Assert.Equal(2, sonuc.NotAttempted);
        Assert.True(sonuc.IsPartial);
        Assert.Equal(2, servis.Yazilanlar.Count);
    }

    [Fact]
    public async Task Hicbir_sey_yazilmadan_yetki_reddi_oldugu_gibi_firlatilir()
    {
        var bolge = Bolge();
        var servis = new YazanDns(bolge);
        Task Red(IReadOnlyList<string> _, CancellationToken __) => throw new AppException("auth.site_forbidden", 403, 403);

        var hata = await Assert.ThrowsAsync<AppException>(
            () => DnsZoneImport.ApplyAsync(servis, "ornek", Istek(bolge), Red, CancellationToken.None));

        Assert.Equal(403, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Disarida_birakilan_mevcut_kayitlar_ASLA_silinmez()
    {
        var bolge = Bolge(Kayit("A", "eski", "203.0.113.5", id: "e1"));
        var servis = new YazanDns(bolge);

        await DnsZoneImport.ApplyAsync(servis, "ornek", Istek(bolge, "yeni IN A 203.0.113.50"), Onay, CancellationToken.None);

        Assert.Empty(servis.Silinenler);
    }

    private sealed class YazanDns(SiteDnsZoneDto bolge) : IDnsService
    {
        public List<UpsertDnsRecordRequest> Yazilanlar { get; } = [];
        public List<string> Silinenler { get; } = [];
        public bool IkinciYazmadaHata { get; set; }
        public int OnayliCagri { get; private set; }

        public Task<SiteDnsZoneDto> GetZoneAsync(string siteName, CancellationToken cancellationToken = default) => Task.FromResult(bolge);

        public async Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request,
            Func<IReadOnlyList<string>, CancellationToken, Task> approval, CancellationToken cancellationToken)
        {
            await approval([siteName], cancellationToken);
            OnayliCagri++;
            if (IkinciYazmadaHata && Yazilanlar.Count == 1) { Yazilanlar.Add(request); throw new InvalidOperationException("yayin basarisiz"); }
            Yazilanlar.Add(request);
            return bolge;
        }

        public Task<SiteDnsZoneDto> UpsertRecordAsync(string siteName, UpsertDnsRecordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> UpsertRecordsAsync(string siteName, IReadOnlyList<UpsertDnsRecordRequest> requests, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> InstallWindowsDnsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> UpdateZoneAsync(string siteName, UpdateDnsZoneRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> ResetDefaultsAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> SetZoneModeAsync(string siteName, SetDnsZoneModeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> TransferZoneNowAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> RepairDefaultsAsync(string siteName, bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetPublishedRecordKeysAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubdomainRecordResult> EnsureSubdomainRecordAsync(string parentSiteName, string label, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> DeleteRecordAsync(string siteName, string recordId, CancellationToken cancellationToken = default)
        { Silinenler.Add(recordId); return Task.FromResult(bolge); }
        public Task DeleteZoneAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureZonePublishedAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto> GetServerHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto> RepairServerAsync(bool applyDelayedAutoStart, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsServerHealthDto?> RunBindWatchdogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsTemplateDto> GetTemplateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DnsTemplateDto> UpdateTemplateAsync(UpdateDnsTemplateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDnsZoneDto> SetDnssecAsync(string siteName, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
