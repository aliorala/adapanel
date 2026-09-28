using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Advisor;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Yenilenen sertifika GERÇEKTEN sunuluyor mu — ve panel bunu SÖYLÜYOR mu?
/// </summary>
/// <remarks>
/// <para>
/// <b>CANLIDA ÖLÇÜLMÜŞ ARIZA.</b> 26–27 Eylül 2026'da <c>adapanel.host:443</c> süresi DOLMUŞ bir
/// sertifika sunuyordu; geçerli, yenilenmiş sertifika 22 Ağustos'tan beri depodaydı. Panel hiçbir
/// ekranda uyarı vermedi.
/// </para>
/// <para>
/// Zincir şöyleydi: win-acme yeniler → <c>RenewAllAsync</c> kaydı depodan tazeler → kayıt artık
/// YENİ ve uzak bir bitiş tarihi taşır → Danışman yalnız o tarihe baktığı için susar → IIS bağı
/// eski sertifikada kalmışsa site onu sunmaya devam eder ve dolunca HTTPS düşer.
/// </para>
/// <para>
/// <b>Asıl ders:</b> kaydı tazeleme adımı YANLIŞ değildi — her gün gönderilen yalancı "süresi
/// doldu" alarmını kapatıyordu. Ama bağ doğrulanmadığı için gürültülü bir YANLIŞ POZİTİFİ sessiz
/// bir YANLIŞ NEGATİFE çevirdi. Bu testler eksik yarıyı tutuyor.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA BURADA GERÇEK RİSK:</b> "bağ şüpheliyse kritik uyarı ver" çözümü, kapatılan
/// gürültünün aynısını geri getirirdi. Bu yüzden testlerin yarısı "uyarı ÇIKMAMALI" nöbetçisidir.
/// </para>
/// </remarks>
public sealed class SslBaglamaSapmasiTests
{
    private static readonly DateTime Simdi = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    // =====================================================================================
    // KARAR KURALI — dört dalın hepsi ayrı bir gerçek
    // =====================================================================================

    /// <summary>Bağ bizim sertifikamızı taşıyor: iddia yok.</summary>
    [Fact]
    public void Bag_dogruysa_iddia_yok()
        => Assert.Equal(
            SslBindingDrift.Verdict.NoClaim,
            SslBindingDrift.Evaluate(true, Simdi.AddDays(-1), Simdi));

    /// <summary>
    /// ÖLÇÜLEMEDİ, "sapma" DEĞİL — geçici bir IIS okuma hatası kalıcı kritik uyarıya dönmemeli.
    /// </summary>
    [Fact]
    public void Olculemedi_sapma_sayilmaz()
        => Assert.Equal(
            SslBindingDrift.Verdict.NoClaim,
            SslBindingDrift.Evaluate(null, Simdi.AddDays(-1), Simdi));

    /// <summary>Damgasız kayıt (bu alan eklenmeden önce yazılmış): ne zaman ölçüldüğü bilinmiyor.</summary>
    [Fact]
    public void Damgasiz_kayit_iddia_uretmez()
        => Assert.Equal(
            SslBindingDrift.Verdict.NoClaim,
            SslBindingDrift.Evaluate(false, null, Simdi));

    /// <summary>TAZE ölçüm + bağ bizim sertifikamızı taşımıyor: TEK olumsuz karar.</summary>
    [Fact]
    public void Taze_olcumde_sapma_bildirilir()
        => Assert.Equal(
            SslBindingDrift.Verdict.Drifted,
            SslBindingDrift.Evaluate(false, Simdi.AddDays(-1), Simdi));

    /// <summary>
    /// BAYAT ölçüm iddia DEĞİLDİR — bu, yalancı alarmın geri gelmesini engelleyen sınır.
    /// </summary>
    /// <remarks>
    /// Otomasyon kapalıysa elde aylar önceki bir ölçüm kalır. Onunla "sertifikanız sunulmuyor"
    /// demek, yönetici bağı çoktan elle düzeltmiş olsa bile sonsuza kadar kırmızı göstermek olurdu.
    /// </remarks>
    [Fact]
    public void Bayat_olcum_iddia_uretmez()
        => Assert.Equal(
            SslBindingDrift.Verdict.NoClaim,
            SslBindingDrift.Evaluate(
                false,
                Simdi - SslBindingDrift.MeasurementFreshness - TimeSpan.FromHours(1),
                Simdi));

    /// <summary>Pencerenin TAM sınırı hâlâ tazedir (sınır dışlayıcı değil).</summary>
    [Fact]
    public void Pencerenin_tam_siniri_taze_sayilir()
        => Assert.Equal(
            SslBindingDrift.Verdict.Drifted,
            SslBindingDrift.Evaluate(false, Simdi - SslBindingDrift.MeasurementFreshness, Simdi));

    /// <summary>
    /// Saat kayması bayatlık DEĞİLDİR: "gelecekte" görünen ölçüm taze sayılır.
    /// </summary>
    /// <remarks>
    /// Birkaç saniyelik kayma yüzünden GERÇEK bir sapmayı susturmak, bu korumanın amacının tersi
    /// olurdu.
    /// </remarks>
    [Fact]
    public void Saat_kaymasi_olcumu_susturmaz()
        => Assert.Equal(
            SslBindingDrift.Verdict.Drifted,
            SslBindingDrift.Evaluate(false, Simdi.AddMinutes(5), Simdi));

    // =====================================================================================
    // DANIŞMAN — ölçülen gerçek kullanıcının göreceği yere çıkıyor mu
    // =====================================================================================

    /// <summary>
    /// SAPMA KRİTİK MADDE OLUR — <b>bitiş tarihi sapasağlam görünürken bile.</b>
    /// </summary>
    /// <remarks>
    /// Bu testin can alıcı yanı <c>NotAfterUtc</c>'nin UZAK GELECEKTE olması: canlı arızada kayıt
    /// tam böyleydi (yenileme kaydı tazelemişti). Eski Danışman yalnız o tarihe baktığı için
    /// susuyordu. Uyarı bu koşulda çıkmıyorsa koruma hiçbir şey yapmıyor demektir.
    /// </remarks>
    [Fact]
    public async Task Bitis_tarihi_saglikli_gorunse_de_sapma_KRITIK_bildirilir()
    {
        using var alan = new DanismanAlani();
        await alan.SslKaydiYazAsync(new SslCertificateDto
        {
            HostName = "adapanel.host",
            SiteName = "adapanel.host",
            NotAfterUtc = DateTime.UtcNow.AddDays(54),
            IsHttpsBindingPresent = true,
            IsCertificateBound = false,
            BindingDetail = "beklenen=01D7C4..., okunan=E549DF...",
            BindingCheckedAtUtc = DateTime.UtcNow.AddHours(-2)
        });

        var rapor = await alan.Danisman().GetReportAsync();

        var madde = Assert.Single(rapor.Items, item => item.Id == "ssl-binding-drift:adapanel.host");
        Assert.Equal("critical", madde.Severity);
        Assert.Equal("advisor.ssl.bindingDrift", madde.TitleKey);

        // Ayrıntı ÖLÇÜLEN parmak izlerini taşır: yönetici iddiayı kendi doğrulayabilmeli.
        Assert.Contains("E549DF", madde.Detail, StringComparison.Ordinal);

        // Bitiş tarihi sağlıklı olduğu için süre uyarıları ÇIKMAMALI: iki gerçek karışmasın.
        Assert.DoesNotContain(rapor.Items, item => item.Id.StartsWith("ssl-expired:", StringComparison.Ordinal));
        Assert.DoesNotContain(rapor.Items, item => item.Id.StartsWith("ssl-expiring:", StringComparison.Ordinal));
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: bağ doğruysa, ölçülemediyse ya da ölçüm bayatsa uyarı ÇIKMAZ.
    /// </summary>
    /// <remarks>
    /// Üçü ayrı ayrı gerekli. Bunlar olmadan "bağ şüpheliyse kritik uyarı ver" çözümü yeşil
    /// görünürdü ve panel, kapatılan yalancı alarmın aynısını üretmeye başlardı.
    /// </remarks>
    [Theory]
    [InlineData(true, -2)]        // bağ doğru
    [InlineData(null, -2)]        // ölçülemedi
    [InlineData(false, -24 * 40)] // ölçüldü ama BAYAT
    public async Task Mesru_durumlarda_uyari_CIKMAZ(bool? bagli, int olcumSaatFarki)
    {
        using var alan = new DanismanAlani();
        await alan.SslKaydiYazAsync(new SslCertificateDto
        {
            HostName = "adapanel.host",
            SiteName = "adapanel.host",
            NotAfterUtc = DateTime.UtcNow.AddDays(54),
            IsHttpsBindingPresent = true,
            IsCertificateBound = bagli,
            BindingCheckedAtUtc = DateTime.UtcNow.AddHours(olcumSaatFarki)
        });

        var rapor = await alan.Danisman().GetReportAsync();

        Assert.DoesNotContain(rapor.Items, item => item.Id.StartsWith("ssl-binding-drift:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Süresi dolmuş VE sapmış kayıt iki maddeyi de üretir: ayrı gerçekler birbirini ezmez.
    /// </summary>
    [Fact]
    public async Task Suresi_dolmus_VE_sapmis_kayit_iki_madde_uretir()
    {
        using var alan = new DanismanAlani();
        await alan.SslKaydiYazAsync(new SslCertificateDto
        {
            HostName = "eski.example",
            SiteName = "eski.example",
            NotAfterUtc = DateTime.UtcNow.AddDays(-3),
            IsHttpsBindingPresent = true,
            IsCertificateBound = false,
            BindingCheckedAtUtc = DateTime.UtcNow.AddHours(-1)
        });

        var rapor = await alan.Danisman().GetReportAsync();

        Assert.Single(rapor.Items, item => item.Id == "ssl-expired:eski.example");
        Assert.Single(rapor.Items, item => item.Id == "ssl-binding-drift:eski.example");
    }

    /// <summary>
    /// Danışman SSL kontrolü OKUNAMAZSA sessizce "sorun yok" demez; eksik denetim raporlanır.
    /// </summary>
    /// <remarks>
    /// Bu davranış zaten vardı; buraya alınması, eklediğim dalın onu bozmadığının ölçümüdür
    /// (yeni bir başvuru hatası bu kanalı sessizce yutabilirdi).
    /// </remarks>
    [Fact]
    public async Task Ssl_kontrolu_okunamazsa_eksik_denetim_raporlanir()
    {
        var danisman = new AdvisorService(null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<AdvisorService>.Instance);

        var rapor = await danisman.GetReportAsync();

        Assert.Contains("ssl", rapor.FailedChecks);
    }

    // =====================================================================================

    private sealed class DanismanAlani : IDisposable
    {
        public DanismanAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-ssl-sapma-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
        }

        public string Kok { get; }

        public JsonFileStore DosyaDeposu { get; }

        public Task SslKaydiYazAsync(params SslCertificateDto[] kayitlar)
            => DosyaDeposu.WriteAsync("config/ssl.json", kayitlar);

        /// <summary>
        /// Yalnız SSL kontrolünün ihtiyaç duyduğu bağımlılık gerçek; kalanı <c>null!</c>.
        /// </summary>
        /// <remarks>
        /// Diğer kontroller böylece <c>FailedChecks</c>'e düşer — sessizce yeşil görünmezler. Aynı
        /// kalıp <c>AttentionUpdateFreshnessTests</c>'te de kullanılıyor.
        /// </remarks>
        public AdvisorService Danisman()
            => new(null!, null!, null!, null!, null!, null!, null!, null!, DosyaDeposu,
                NullLogger<AdvisorService>.Instance);

        // Geçici klasör BİLEREK silinmiyor: bu depoda silme yasağı var ve %TEMP% klasörü zararsız.
        public void Dispose()
        {
        }
    }
}
