using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Billing;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// FATURALAMA (WHMCS) ENTEGRASYONU.
/// <para>
/// Korunan iki sinif: (1) yetkisiz cagri hicbir sey yapamaz, (2) yanlis hesap SILINEMEZ. Ikisi de
/// bu depoda daha once gerceklesmis hata siniflarinin akrabasi: ozyinelemeli silme musteri sitesini
/// silebiliyordu ve "anahtar REST'te calismiyor" hatasi entegrasyonu cerez oturumuna mahkum
/// etmisti.
/// </para>
/// <para>
/// KAPSAM NOTU: WHMCS'in kendisi burada AYAGA KALDIRILAMAZ (PHP + MySQL + lisans ister). Bu yuzden
/// olculen sey panelin SOZLESMEYI karsilayan tarafidir: birim cevrimi, silme karari, kapali/acik
/// kapisi ve yetkilendirme. Gercek WHMCS ile ucdan uca deneme YAPILMADI.
/// </para>
/// </summary>
public sealed class BillingIntegrationTests
{
    // --- 1) SILME KARARI: yanlis hesabi silmeye karsi ---

    [Fact]
    public void Dogru_musteri_ve_eslesen_alan_adi_SILINEBILIR()
    {
        var karar = BillingTerminationGuard.Evaluate(
            [Musteri("musteri1", "ornek.com")],
            "musteri1",
            "ornek.com");

        Assert.True(karar.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonOk, karar.Reason);
        Assert.Equal("ornek.com", karar.MatchedDomain);
    }

    [Fact]
    public void Panelde_olmayan_kullanici_SILINEMEZ()
    {
        var karar = BillingTerminationGuard.Evaluate(
            [Musteri("musteri1", "ornek.com")],
            "baskasi",
            "ornek.com");

        Assert.False(karar.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonNotFound, karar.Reason);
        Assert.Null(karar.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Bos_kullanici_adi_HICBIR_hesapla_eslesmez(string? kullaniciAdi)
    {
        // Bos ad "hepsi eslesir" diye yorumlansaydi tek cagri toplu silmeye kapi acardi.
        var karar = BillingTerminationGuard.Evaluate(
            [Musteri("musteri1", "ornek.com"), Musteri("musteri2", "ikinci.com")],
            kullaniciAdi,
            "ornek.com");

        Assert.False(karar.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonNotFound, karar.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Bos_kullanici_adi_ADI_BOS_OLAN_hesapla_da_eslesmez(string bozukAd)
    {
        // MUTASYONLA BULUNDU: "bos ad hicbir seyle eslesmez" korumasi, butun hesaplarin adi
        // doluyken OLCULEMIYORDU (bos arama zaten hicbir sey bulmuyor, koruma kaldirilinca test
        // kirilmiyordu). Korumanin ASIL isini yaptigi durum bu: veri bozulmus ve bir hesabin adi
        // bos/bosluk kalmis. O anda bos bir WHMCS kullanici adi, o hesapla ESLESIR ve faturalama
        // sistemi onu silmeye kalkardi.
        var karar = BillingTerminationGuard.Evaluate(
            [new BillingTerminationGuard.Candidate(Guid.NewGuid(), bozukAd, UserRole.Customer, ["ornek.com"])],
            bozukAd,
            "ornek.com");

        Assert.False(karar.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonNotFound, karar.Reason);
    }

    [Fact]
    public void Ayni_ada_uyan_IKI_hesap_varsa_SILINEMEZ()
    {
        // "Ilkini sil" demek, kura cekerek veri silmektir.
        var karar = BillingTerminationGuard.Evaluate(
            [Musteri("musteri1", "ornek.com"), Musteri("musteri1", "baska.com")],
            "musteri1",
            "ornek.com");

        Assert.False(karar.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonAmbiguous, karar.Reason);
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.Reseller)]
    [InlineData(UserRole.Operator)]
    public void Musteri_OLMAYAN_hesap_SILINEMEZ(UserRole rol)
    {
        // Faturalama sistemi yalnizca musteri hesabi acar. Baska bir rolu silmeye kalkmasi ad
        // cakismasi ya da yanlis yapilandirmadir; ikisinde de dogru cevap "hayir".
        var karar = BillingTerminationGuard.Evaluate(
            [new BillingTerminationGuard.Candidate(Guid.NewGuid(), "musteri1", rol, ["ornek.com"])],
            "musteri1",
            "ornek.com");

        Assert.False(karar.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonRoleNotCustomer, karar.Reason);
    }

    [Fact]
    public void Alan_adi_hesabin_kapsaminda_yoksa_SILINEMEZ()
    {
        // Hizmetin alan adi hesabin kapsaminda degilse, bu hesabin o hizmete ait oldugu
        // KANITLANAMAZ -- kanitsiz silme yapilmaz.
        var karar = BillingTerminationGuard.Evaluate(
            [Musteri("musteri1", "baskasinin-sitesi.com")],
            "musteri1",
            "ornek.com");

        Assert.False(karar.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonDomainMismatch, karar.Reason);
    }

    [Fact]
    public void Hicbir_sitesi_olmayan_hesap_alan_adi_beklenirken_SILINEMEZ()
    {
        var karar = BillingTerminationGuard.Evaluate(
            [new BillingTerminationGuard.Candidate(Guid.NewGuid(), "musteri1", UserRole.Customer, [])],
            "musteri1",
            "ornek.com");

        Assert.False(karar.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonDomainMismatch, karar.Reason);
    }

    [Theory]
    [InlineData("www.ornek.com", "ornek.com")]
    [InlineData("ornek.com", "www.ornek.com")]
    [InlineData("ORNEK.COM", "ornek.com")]
    [InlineData("ornek.com.", "ornek.com")]
    [InlineData(" ornek.com ", "ornek.com")]
    public void Alan_adi_karsilastirmasi_www_ve_buyuk_harf_farkini_YOK_SAYAR(string paneldeki, string whmcsteki)
    {
        // Koruma dogru hesabi reddederse kullanilamaz hale gelir ve devre disi birakilmasi istenir;
        // yani asiri kati bir esitlik, korumanin kendisini olduren seydir.
        var karar = BillingTerminationGuard.Evaluate(
            [Musteri("musteri1", paneldeki)],
            "musteri1",
            whmcsteki);

        Assert.True(karar.SafeToTerminate);
    }

    [Fact]
    public void Kullanici_adi_buyuk_kucuk_harf_farkini_YOK_SAYAR()
    {
        var karar = BillingTerminationGuard.Evaluate(
            [Musteri("Musteri1", "ornek.com")],
            "musteri1",
            "ornek.com");

        Assert.True(karar.SafeToTerminate);
    }

    [Fact]
    public void Alan_adi_verilmezse_denetim_UYGULANMAZ_ama_rol_denetimi_SURER()
    {
        // Alan adi olmayan paketler (yalniz hesap satan urunler) var; ama rol kapisi acik kalmali.
        var izinli = BillingTerminationGuard.Evaluate([Musteri("musteri1", "ornek.com")], "musteri1", "");
        Assert.True(izinli.SafeToTerminate);

        var yasak = BillingTerminationGuard.Evaluate(
            [new BillingTerminationGuard.Candidate(Guid.NewGuid(), "yonetici", UserRole.Administrator, [])],
            "yonetici",
            "");
        Assert.False(yasak.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonRoleNotCustomer, yasak.Reason);
    }

    // --- 2) BIRIM CEVRIMI: yanlis birim = yanlis fatura ---

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1048576L, 1L)]
    [InlineData(1572864L, 1L)]      // 1.5 MB -> asagi yuvarlanir (musteri lehine)
    [InlineData(1073741824L, 1024L)]
    [InlineData(-5L, 0L)]           // bozuk gunluk: eksi kullanim diye bir sey yok
    public void Bayt_MB_cevrimi(long bayt, long beklenen) =>
        Assert.Equal(beklenen, BillingUnits.BytesToMegabytes(bayt));

    [Theory]
    [InlineData(0, 0L)]             // 0 = sinirsiz, carpilmadan korunur
    [InlineData(1, 1024L)]
    [InlineData(100, 102400L)]
    [InlineData(-3, 0L)]            // eksi kota WHMCS'te "hep asilmis" demek olurdu
    public void GB_MB_cevrimi(int gb, long beklenen) =>
        Assert.Equal(beklenen, BillingUnits.GigabytesToMegabytes(gb));

    [Theory]
    [InlineData(0, 0L)]
    [InlineData(2048, 2048L)]
    [InlineData(-1, 0L)]
    public void Disk_kotasi_normallestirme(int limit, long beklenen) =>
        Assert.Equal(beklenen, BillingUnits.NormalizeLimitMb(limit));

    // --- 3) KAPALI ENTEGRASYON: acik olmayan entegrasyon HICBIR SEY yapmaz ---

    [Fact]
    public async Task Entegrasyon_varsayilan_olarak_KAPALIDIR()
    {
        // Kurulumdan sonra kimse acmadan calisir durumda olsaydi, hesap acip silebilen uclar
        // varsayilan olarak canli olurdu.
        await using var duzenek = await Duzenek.OlusturAsync();

        var ayar = await duzenek.Servis.GetSettingsAsync();

        Assert.False(ayar.Enabled);
    }

    [Fact]
    public async Task Kapaliyken_kullanim_raporu_REDDEDILIR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        var hata = await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.BuildUsageReportAsync());

        Assert.Equal("billing.disabled", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    [Fact]
    public async Task Kapaliyken_silme_denetimi_REDDEDILIR()
    {
        // Kapatma dugmesi YALNIZCA raporu degil butun entegrasyonu kesmeli.
        await using var duzenek = await Duzenek.OlusturAsync();

        var hata = await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.CheckTerminationAsync("musteri1", "ornek.com"));

        Assert.Equal("billing.disabled", hata.MessageKey);
    }

    [Fact]
    public async Task Acildiktan_sonra_calisir_ve_kapatilinca_YENIDEN_durur()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        await duzenek.Servis.UpdateSettingsAsync(true);
        var rapor = await duzenek.Servis.BuildUsageReportAsync();
        Assert.NotNull(rapor);

        await duzenek.Servis.UpdateSettingsAsync(false);
        await Assert.ThrowsAsync<AppException>(() => duzenek.Servis.BuildUsageReportAsync());
    }

    // --- 4) KULLANIM RAPORU ---

    [Fact]
    public async Task Rapor_panel_degerlerini_WHMCS_birimlerine_cevirir()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.Servis.UpdateSettingsAsync(true);

        var kimlik = duzenek.MusteriEkle(
            "musteri1",
            ["ornek.com"],
            new SubscriptionUsageDto
            {
                UserName = "musteri1",
                DiskUsageMb = 512,
                MaxDiskMb = 2048,
                TrafficBytesThisMonth = 3L * 1024 * 1024 * 1024,   // 3 GB
                TrafficLimitGb = 10,
                TrafficMeasured = true
            });

        var rapor = await duzenek.Servis.BuildUsageReportAsync();

        var hesap = Assert.Single(rapor.Accounts);
        Assert.Equal(kimlik, hesap.UserId);
        Assert.Equal("musteri1", hesap.UserName);
        Assert.Equal(512, hesap.DiskUsageMb);
        Assert.Equal(2048, hesap.DiskLimitMb);
        Assert.Equal(3072, hesap.BandwidthUsageMb);
        Assert.Equal(10240, hesap.BandwidthLimitMb);
        Assert.True(hesap.BandwidthMeasured);
        Assert.Equal(["ornek.com"], hesap.Domains);
    }

    [Fact]
    public async Task Olculemeyen_trafik_ISARETLENIR_ve_sayilir()
    {
        // Olculemeyen bir ayi "0 MB" diye yazmak, asim faturalamasini sessizce sifirlamak olurdu.
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.Servis.UpdateSettingsAsync(true);

        duzenek.MusteriEkle("olculemeyen", ["ornek.com"], new SubscriptionUsageDto
        {
            UserName = "olculemeyen",
            TrafficBytesThisMonth = 0,
            TrafficMeasured = false
        });

        var rapor = await duzenek.Servis.BuildUsageReportAsync();

        Assert.False(Assert.Single(rapor.Accounts).BandwidthMeasured);
        Assert.Equal(1, rapor.UnmeasuredTrafficCount);
    }

    [Fact]
    public async Task Tek_bir_abonelik_patlarsa_rapor_DIGERLERINI_dondurur()
    {
        // WHMCS turu 500 alirsa O SUNUCUDAKI HICBIR hizmetin kullanimi guncellenmez; bir hesabin
        // eksik kalmasi hepsinin eksik kalmasindan iyidir.
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.Servis.UpdateSettingsAsync(true);

        duzenek.MusteriEkle("saglam", ["ornek.com"], new SubscriptionUsageDto { UserName = "saglam", DiskUsageMb = 10 });
        duzenek.PatlayanMusteriEkle("bozuk");

        var rapor = await duzenek.Servis.BuildUsageReportAsync();

        Assert.Equal("saglam", Assert.Single(rapor.Accounts).UserName);
    }

    [Fact]
    public async Task Rapor_sonrasi_ayar_ekrani_son_cagriyi_GORUR()
    {
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.Servis.UpdateSettingsAsync(true);
        duzenek.MusteriEkle("musteri1", ["ornek.com"], new SubscriptionUsageDto { UserName = "musteri1" });

        await duzenek.Servis.BuildUsageReportAsync();
        var ayar = await duzenek.Servis.GetSettingsAsync();

        Assert.NotNull(ayar.LastCallAtUtc);
        Assert.Equal(1, ayar.LastAccountCount);
        Assert.Contains(ayar.RecentCalls, kayit => kayit.Action == "usage" && kayit.Outcome == "ok");
    }

    [Fact]
    public async Task Cagri_gunlugu_SINIRSIZ_buyumez()
    {
        await using var duzenek = await Duzenek.OlusturAsync();

        for (var sayac = 0; sayac < 30; sayac++)
        {
            await duzenek.Servis.UpdateSettingsAsync(sayac % 2 == 0);
        }

        var ayar = await duzenek.Servis.GetSettingsAsync();

        Assert.True(ayar.RecentCalls.Count <= 20, $"Gunluk sinirsiz buyuyor: {ayar.RecentCalls.Count} kayit.");
    }

    [Fact]
    public async Task Reddedilen_silme_denetimi_de_gunluge_DUSER()
    {
        // "Iptal neden calismadi" sorusunun yaniti panelde bulunabilmeli.
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.Servis.UpdateSettingsAsync(true);

        var karar = await duzenek.Servis.CheckTerminationAsync("olmayan", "ornek.com");
        var ayar = await duzenek.Servis.GetSettingsAsync();

        Assert.False(karar.SafeToTerminate);
        Assert.Contains(ayar.RecentCalls, kayit => kayit.Action == "terminate_check" && kayit.Outcome == "denied");
    }

    [Fact]
    public async Task Silme_denetimi_gercek_veritabanindaki_rolu_OKUR()
    {
        // Saf kural dogru olsa bile yanlis veriyle beslenirse ise yaramaz: burada zincirin
        // tamami (DbContext -> aday listesi -> karar) olculuyor.
        await using var duzenek = await Duzenek.OlusturAsync();
        await duzenek.Servis.UpdateSettingsAsync(true);

        duzenek.KullaniciEkle("yonetici", UserRole.Administrator);
        duzenek.MusteriEkle("musteri1", ["ornek.com"], new SubscriptionUsageDto { UserName = "musteri1" });

        var yonetici = await duzenek.Servis.CheckTerminationAsync("yonetici", "");
        var musteri = await duzenek.Servis.CheckTerminationAsync("musteri1", "ornek.com");

        Assert.False(yonetici.SafeToTerminate);
        Assert.Equal(BillingTerminationGuard.ReasonRoleNotCustomer, yonetici.Reason);
        Assert.True(musteri.SafeToTerminate);
    }

    // --- 5) YETKILENDIRME: kimliksiz/yetkisiz cagri hicbir sey yapamaz ---

    [Fact]
    public async Task Kimligi_dogrulanmamis_istek_faturalama_ucuna_GIREMEZ()
    {
        var (yetki, politika) = await PolitikaKurAsync();

        var sonuc = await yetki.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), resource: null, politika);

        Assert.False(sonuc.Succeeded);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Reseller")]
    [InlineData("Operator")]
    public async Task Yonetici_OLMAYAN_kimlik_faturalama_ucuna_GIREMEZ(string rol)
    {
        // Bu uclar butun kiracilarin kullanimini dokuyor ve silme karari veriyor; musteri ya da
        // bayi kimligiyle acilsaydi capraz-kiraci sizinti olurdu.
        var (yetki, politika) = await PolitikaKurAsync();

        var sonuc = await yetki.AuthorizeAsync(Kimlik(rol), resource: null, politika);

        Assert.False(sonuc.Succeeded);
    }

    [Fact]
    public async Task Yonetici_kimligi_faturalama_ucuna_GIREBILIR()
    {
        var (yetki, politika) = await PolitikaKurAsync();

        var sonuc = await yetki.AuthorizeAsync(Kimlik("Administrator"), resource: null, politika);

        Assert.True(sonuc.Succeeded);
    }

    [Fact]
    public void Faturalama_uclarinin_hepsi_OKUMA_ucudur()
    {
        // Neden onemli: panel anahtari YAZMA istegi icin AllowDestructive ister. Butun uclar GET
        // oldugu surece kullanim toplayan cron SALT-OKUR bir anahtarla calisabilir; yarin buraya
        // bir POST eklenirse entegrasyona yikici yetkili anahtar gerekir ve bu bilincli olmalidir.
        var yazanlar = typeof(BillingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(metot => metot.GetCustomAttributes<HttpPostAttribute>().Any() ||
                            metot.GetCustomAttributes<HttpDeleteAttribute>().Any() ||
                            metot.GetCustomAttributes<HttpPatchAttribute>().Any())
            .Select(metot => metot.Name)
            .ToList();

        Assert.Empty(yazanlar);
    }

    [Fact]
    public void Faturalama_servisi_DI_kabinde_KAYITLI()
    {
        // Kural dogru ama BAGLANMAMIS olabilirdi: kayit yoksa controller hic ayaga kalkmaz.
        var kaynak = KaynakOku("src", "AdaPanel.Infrastructure", "Extensions", "ServiceCollectionExtensions.cs");

        Assert.Contains("Billing.IBillingIntegrationService, Billing.BillingIntegrationService", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void WHMCS_modulu_silmeden_ONCE_panele_sorar()
    {
        // PHP tarafinin testi yok; en azindan silme yolunun denetimden GECTIGI kaynakta sabitlenir.
        // Bu kontrol kalkarsa modul yine yalnizca kullanici adina bakarak silmeye doner.
        var modul = KaynakOku("integrations", "whmcs", "adapanel", "adapanel.php");

        var iptal = modul[modul.IndexOf("function adapanel_TerminateAccount", StringComparison.Ordinal)..];
        iptal = iptal[..iptal.IndexOf("function adapanel_ChangePackage", StringComparison.Ordinal)];

        Assert.Contains("billing/terminate-check", iptal, StringComparison.Ordinal);
        Assert.Contains("safeToTerminate", iptal, StringComparison.Ordinal);
    }

    [Fact]
    public void WHMCS_modulu_UsageUpdate_islevini_SAGLAR()
    {
        // WHMCS bu islevi ADIYLA cagirir (modulename_UsageUpdate); adi degisirse kullanim
        // guncellemesi sessizce hic calismaz.
        var modul = KaynakOku("integrations", "whmcs", "adapanel", "adapanel.php");

        Assert.Contains("function adapanel_UsageUpdate", modul, StringComparison.Ordinal);
        // Resmi sozlesme: tblhosting satirlari guncellenir.
        Assert.Contains("tblhosting", modul, StringComparison.Ordinal);
        Assert.Contains("diskusage", modul, StringComparison.Ordinal);
        Assert.Contains("bwusage", modul, StringComparison.Ordinal);
        Assert.Contains("lastupdate", modul, StringComparison.Ordinal);
    }

    // --- duzenek ---

    private static BillingTerminationGuard.Candidate Musteri(string ad, params string[] siteler) =>
        new(Guid.NewGuid(), ad, UserRole.Customer, siteler);

    private static ClaimsPrincipal Kimlik(string rol) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, rol)],
            "TestSemasi"));

    /// <summary>
    /// Politika CONTROLLER'IN KENDI ozniteliginden kurulur (kopyalanmaz): oznitelik degisirse ya da
    /// silinirse bu testler kirilir.
    /// </summary>
    private static async Task<(IAuthorizationService Yetki, AuthorizationPolicy Politika)> PolitikaKurAsync()
    {
        var servisler = new ServiceCollection();
        servisler.AddLogging();
        servisler.AddOptions();
        servisler.AddAuthorization();
        var saglayici = servisler.BuildServiceProvider();

        var oznitelikler = typeof(BillingController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.NotEmpty(oznitelikler);

        var politika = await AuthorizationPolicy.CombineAsync(
            saglayici.GetRequiredService<IAuthorizationPolicyProvider>(),
            oznitelikler);

        Assert.NotNull(politika);
        return (saglayici.GetRequiredService<IAuthorizationService>(), politika!);
    }

    private static string KaynakOku(params string[] parcalar)
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return File.ReadAllText(Path.Combine([kok!, .. parcalar]));
    }

    private sealed class Duzenek : IAsyncDisposable
    {
        private readonly SqliteConnection baglanti;
        private readonly AdaPanelDbContext dbContext;
        private readonly string veriKoku;
        private readonly SahteKapsam kapsam;
        private readonly SahteUygulayici uygulayici;

        private Duzenek(
            SqliteConnection baglanti,
            AdaPanelDbContext dbContext,
            string veriKoku,
            SahteKapsam kapsam,
            SahteUygulayici uygulayici,
            IBillingIntegrationService servis)
        {
            this.baglanti = baglanti;
            this.dbContext = dbContext;
            this.veriKoku = veriKoku;
            this.kapsam = kapsam;
            this.uygulayici = uygulayici;
            Servis = servis;
        }

        public IBillingIntegrationService Servis { get; }

        public static async Task<Duzenek> OlusturAsync()
        {
            var baglanti = new SqliteConnection("Data Source=:memory:");
            await baglanti.OpenAsync();
            var secenekler = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(secenekler);
            await dbContext.Database.EnsureCreatedAsync();

            var veriKoku = Path.Combine(Path.GetTempPath(), "ada-billing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(veriKoku);

            var runtimeOptions = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku });
            var kapsam = new SahteKapsam();
            var uygulayici = new SahteUygulayici();

            var servis = new BillingIntegrationService(
                new JsonFileStore(runtimeOptions),
                kapsam,
                uygulayici,
                dbContext,
                NullLogger<BillingIntegrationService>.Instance);

            return new Duzenek(baglanti, dbContext, veriKoku, kapsam, uygulayici, servis);
        }

        public Guid KullaniciEkle(string ad, UserRole rol)
        {
            var kullanici = new PanelUser
            {
                Id = Guid.NewGuid(),
                UserName = ad,
                Email = ad + "@ornek.test",
                Role = rol,
                PasswordHash = "KARMA",
                IsActive = true
            };

            dbContext.Users.Add(kullanici);
            dbContext.SaveChanges();
            return kullanici.Id;
        }

        public Guid MusteriEkle(string ad, IReadOnlyList<string> siteler, SubscriptionUsageDto kullanim)
        {
            var kimlik = KullaniciEkle(ad, UserRole.Customer);
            kullanim.UserId = kimlik;
            kapsam.Siteler[kimlik] = siteler;
            kapsam.Abonelikler.Add(kimlik);
            uygulayici.Kullanimlar[kimlik] = kullanim;
            return kimlik;
        }

        /// <summary>Kullanimi okunamayan abonelik (ornek: gunlukleri erisilemez site).</summary>
        public Guid PatlayanMusteriEkle(string ad)
        {
            var kimlik = KullaniciEkle(ad, UserRole.Customer);
            kapsam.Siteler[kimlik] = ["bozuk.com"];
            kapsam.Abonelikler.Add(kimlik);
            return kimlik;
        }

        public async ValueTask DisposeAsync()
        {
            await dbContext.DisposeAsync();
            await baglanti.DisposeAsync();
            try
            {
                Directory.Delete(veriKoku, recursive: true);
            }
            catch
            {
                // Gecici klasor kalsa bile testi dusurmez.
            }
        }
    }

    /// <summary>
    /// Yalniz faturalama servisinin KULLANDIGI uyeler gercektir; geri kalani cagrilirsa test
    /// patlar. Sessiz varsayilan donmek, servisin yanlis uyeyi cagirdigini gizlerdi.
    /// </summary>
    private sealed class SahteKapsam : ICustomerScopeService
    {
        public Dictionary<Guid, IReadOnlyList<string>> Siteler { get; } = [];

        public List<Guid> Abonelikler { get; } = [];

        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Guid>>(Abonelikler);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(
                Siteler.TryGetValue(userId, out var siteler) ? siteler.ToList() : []);

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteUygulayici : ISubscriptionEnforcementService
    {
        public Dictionary<Guid, SubscriptionUsageDto> Kullanimlar { get; } = [];

        public Task<SubscriptionUsageDto> GetUsageAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Kullanimlar.TryGetValue(userId, out var kullanim)
                ? Task.FromResult(kullanim)
                : throw new InvalidOperationException("Bu aboneligin kullanimi okunamiyor.");

        public Task<IReadOnlyCollection<SubscriptionEnforcementChangeDto>> EnforceAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
