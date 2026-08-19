using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Infrastructure.Logs;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Log döndürme KARARLARI — diske, saate ve IIS'e dokunmadan.
/// </summary>
/// <remarks>
/// Kararın saf bir sınıfa çıkarılmasının sebebi budur: asıl servis IIS + disk + sıkıştırıcı
/// üçlüsüyle çalışır ve orada bırakılan bir mantık ancak gerçek bir sunucuda sınanabilirdi.
/// </remarks>
public sealed class LogRotationPlannerTests
{
    private static readonly DateTime Simdi = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// EN ÖNEMLİ KURAL: yazılmakta olan (en yeni) dosya, yaşı ve boyutu ne olursa olsun döndürülmez.
    /// </summary>
    /// <remarks>
    /// Bu kural olmasaydı trafiği olmayan bir sitenin AKTİF günlüğü (IIS onu açık tutmaya devam
    /// eder) yaş eşiğine takılır ve panel canlı bir dosyayı sıkıştırmaya çalışırdı.
    /// </remarks>
    [Fact]
    public void Aktif_dosya_ne_kadar_eski_ya_da_buyuk_olursa_olsun_dondurulmez()
    {
        var dosyalar = new[]
        {
            new LogFileSnapshot("u_ex260101.log", 900L * 1024 * 1024, Simdi.AddDays(-200)),
            new LogFileSnapshot("u_ex260102.log", 900L * 1024 * 1024, Simdi.AddDays(-190))
        };

        var plan = LogRotationPlanner.PlanRotation(dosyalar, maxAgeDays: 1, maxFileSizeMB: 1, Simdi);

        Assert.Equal("u_ex260102.log", plan.ActiveFileName);
        Assert.Equal(["u_ex260101.log"], plan.Rotate.Select(d => d.Name));
    }

    /// <summary>Tek dosya varsa o dosya aktiftir: klasörde döndürülecek hiçbir şey yoktur.</summary>
    [Fact]
    public void Tek_dosya_varsa_hicbir_sey_dondurulmez()
    {
        var dosyalar = new[] { new LogFileSnapshot("u_ex260101.log", 5_000_000, Simdi.AddDays(-99)) };

        var plan = LogRotationPlanner.PlanRotation(dosyalar, maxAgeDays: 1, maxFileSizeMB: 1, Simdi);

        Assert.Empty(plan.Rotate);
        Assert.Equal("u_ex260101.log", plan.ActiveFileName);
    }

    [Fact]
    public void Yas_esigi_esigi_gecen_kapali_dosyalari_secer()
    {
        var dosyalar = new[]
        {
            new LogFileSnapshot("eski.log", 100, Simdi.AddDays(-31)),
            new LogFileSnapshot("sinirda.log", 100, Simdi.AddDays(-30)),
            new LogFileSnapshot("yeni.log", 100, Simdi.AddDays(-3)),
            new LogFileSnapshot("aktif.log", 100, Simdi)
        };

        var plan = LogRotationPlanner.PlanRotation(dosyalar, maxAgeDays: 30, maxFileSizeMB: 0, Simdi);

        // Sınırdaki dosya DAHİL (>=), üç günlük olan hariç, aktif hariç.
        Assert.Equal(["eski.log", "sinirda.log"], plan.Rotate.Select(d => d.Name));
    }

    [Fact]
    public void Boyut_esigi_yasi_ne_olursa_olsun_secer()
    {
        var dosyalar = new[]
        {
            new LogFileSnapshot("kucuk.log", 5L * 1024 * 1024, Simdi.AddHours(-2)),
            new LogFileSnapshot("dev.log", 60L * 1024 * 1024, Simdi.AddHours(-1)),
            new LogFileSnapshot("aktif.log", 1, Simdi)
        };

        var plan = LogRotationPlanner.PlanRotation(dosyalar, maxAgeDays: 0, maxFileSizeMB: 50, Simdi);

        Assert.Equal(["dev.log"], plan.Rotate.Select(d => d.Name));
    }

    /// <summary>
    /// İki eşik de kapalıysa politika HİÇBİR ŞEY yapmaz. "Hepsini döndür" gibi bir varsayılana
    /// düşmek, ayarları sıfırlayan yöneticinin bütün günlüklerini sıkıştırmak olurdu.
    /// </summary>
    [Fact]
    public void Iki_esik_de_kapaliysa_hicbir_sey_dondurulmez()
    {
        var dosyalar = new[]
        {
            new LogFileSnapshot("cok-eski.log", 900L * 1024 * 1024, Simdi.AddYears(-3)),
            new LogFileSnapshot("aktif.log", 1, Simdi)
        };

        var plan = LogRotationPlanner.PlanRotation(dosyalar, maxAgeDays: 0, maxFileSizeMB: 0, Simdi);

        Assert.Empty(plan.Rotate);
    }

    /// <summary>
    /// Boş dosya sıkıştırılmaz: ZSTD boş girdide bile geçerli bir çerçeve üretir, yani dosyayı
    /// küçültmek yerine BÜYÜTÜRDÜK (0 bayt → 9 bayt).
    /// </summary>
    [Fact]
    public void Bos_dosya_dondurulmez()
    {
        var dosyalar = new[]
        {
            new LogFileSnapshot("bos.log", 0, Simdi.AddDays(-90)),
            new LogFileSnapshot("dolu.log", 10, Simdi.AddDays(-89)),
            new LogFileSnapshot("aktif.log", 10, Simdi)
        };

        var plan = LogRotationPlanner.PlanRotation(dosyalar, maxAgeDays: 1, maxFileSizeMB: 0, Simdi);

        Assert.Equal(["dolu.log"], plan.Rotate.Select(d => d.Name));
    }

    [Fact]
    public void Bos_klasorde_karar_da_bostur()
    {
        var plan = LogRotationPlanner.PlanRotation([], maxAgeDays: 7, maxFileSizeMB: 10, Simdi);

        Assert.Empty(plan.Rotate);
        Assert.Null(plan.ActiveFileName);
    }

    // --- saklama ---

    [Fact]
    public void Adet_siniri_EN_ESKI_arsivleri_siler()
    {
        var arsivler = Arsivler(5);

        var silinecek = LogRotationPlanner.PlanRetention(arsivler, [], keepCount: 2, maxTotalSizeMB: 0);

        Assert.Equal(["a0.log.zst", "a1.log.zst", "a2.log.zst"], silinecek.Select(d => d.Name));
    }

    /// <summary>0 = SINIRSIZ (panelin diğer saklama ayarlarıyla aynı anlam).</summary>
    [Fact]
    public void Adet_siniri_sifirsa_hicbir_arsiv_silinmez()
    {
        var silinecek = LogRotationPlanner.PlanRetention(Arsivler(50), [], keepCount: 0, maxTotalSizeMB: 0);

        Assert.Empty(silinecek);
    }

    /// <summary>
    /// Toplam boyut sınırı HAM GÜNLÜKLERİ DE SAYAR (sınır diskin gerçeğini anlatmalı) ama onları
    /// asla silmez: yalnızca arşiv silinir.
    /// </summary>
    [Fact]
    public void Toplam_boyut_siniri_ham_gunlukleri_sayar_ama_silmez()
    {
        // 3 MB ham + 3 x 1 MB arşiv = 6 MB; sınır 4 MB → 2 arşiv silinmeli (kalan 3 + 1 = 4).
        var hamGunlukler = new[] { new LogFileSnapshot("aktif.log", 3L * 1024 * 1024, Simdi) };
        var arsivler = new[]
        {
            new LogFileSnapshot("a0.log.zst", 1024L * 1024, Simdi.AddDays(-3)),
            new LogFileSnapshot("a1.log.zst", 1024L * 1024, Simdi.AddDays(-2)),
            new LogFileSnapshot("a2.log.zst", 1024L * 1024, Simdi.AddDays(-1))
        };

        var silinecek = LogRotationPlanner.PlanRetention(arsivler, hamGunlukler, keepCount: 0, maxTotalSizeMB: 4);

        Assert.Equal(["a0.log.zst", "a1.log.zst"], silinecek.Select(d => d.Name));
        Assert.DoesNotContain(silinecek, d => d.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sınır yalnız arşiv silerek karşılanamıyorsa panel silmeyi ZORLAMAZ: elindeki bütün arşivleri
    /// verir, ham günlüğe dokunmaz. "Sınırı tuttur" diye canlı erişim kaydını silmek, çözümden
    /// pahalıya mal olurdu.
    /// </summary>
    [Fact]
    public void Sinir_arsivlerle_karsilanamiyorsa_ham_gunluge_dokunulmaz()
    {
        var hamGunlukler = new[] { new LogFileSnapshot("dev.log", 100L * 1024 * 1024, Simdi) };
        var arsivler = new[] { new LogFileSnapshot("a0.log.zst", 1024L * 1024, Simdi.AddDays(-1)) };

        var silinecek = LogRotationPlanner.PlanRetention(arsivler, hamGunlukler, keepCount: 0, maxTotalSizeMB: 1);

        Assert.Equal(["a0.log.zst"], silinecek.Select(d => d.Name));
    }

    /// <summary>Aynı arşiv iki kuraldan da seçilse listede BİR kez görünür (çift silme çağrısı olmaz).</summary>
    [Fact]
    public void Iki_kural_da_secse_arsiv_listede_bir_kez_durur()
    {
        var arsivler = Arsivler(4, birimBoyut: 10L * 1024 * 1024);

        var silinecek = LogRotationPlanner.PlanRetention(arsivler, [], keepCount: 1, maxTotalSizeMB: 5);

        Assert.Equal(silinecek.Select(d => d.Name).Distinct().Count(), silinecek.Count);
    }

    [Fact]
    public void Panel_ve_site_icin_farkli_yas_esigi_okunur()
    {
        // İki hedefin okuyucuları farklı: site günlüğü istatistik ekranını besler, panel günlüğü
        // yalnızca en yeni dosyadan okunur. Eşiklerin ayrı olması bu yüzden.
        var ayarlar = new LogRotationSettingsDto { SiteLogMaxAgeDays = 30, PanelLogMaxAgeDays = 2 };

        Assert.Equal(2, LogRotationPlanner.AgeThresholdFor(ayarlar, isPanelTarget: true));
        Assert.Equal(30, LogRotationPlanner.AgeThresholdFor(ayarlar, isPanelTarget: false));
    }

    private static LogFileSnapshot[] Arsivler(int adet, long birimBoyut = 1024) =>
        Enumerable.Range(0, adet)
            .Select(i => new LogFileSnapshot($"a{i}.log.zst", birimBoyut, Simdi.AddDays(-(adet - i))))
            .ToArray();
}
