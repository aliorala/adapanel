using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Şifresiz arşivde KURCALAMA ölçülüyor mu — ve "ölçemedim" ile "kurcalanmış" ayrı mı?
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN BOŞLUK:</b> şifreli arşivde bütünlük vardı (AES-GCM etiketi, düz metin yazılmadan
/// doğrulanır) ama şifresizde YOKTU. <see cref="BackupArchiveProbe"/> arşivin YAPISINI ölçüyor —
/// kurcalanmış bir zip ise yapısal olarak kusursuzdur, yani <c>sound</c> damgasını alıyor ve geri
/// yüklemede saldırganın dosyaları siteye açılıyordu.
/// </para>
/// <para>
/// <b>Tehdit hayalî değil:</b> panel yedeği FTP/S3 gibi ÜÇÜNCÜ TARAF hedeflere de yükler. Oraya
/// erişebilen biri arşivi değiştirebilir. Geri yükleme yolundaki hedef/yol denetimleri bunu
/// görmez — onlar dosyanın NEREYE açıldığını denetler, NE OLDUĞUNU değil.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA BURADA ASIL RİSK:</b> damgası olmayan ESKİ yedekleri reddeden bir çözüm, bu
/// sürümden önce alınmış bütün yedekleri felaket anında kullanılamaz yapardı. Bu yüzden
/// testlerin yarısı "hâlâ çalışıyor" nöbetçisidir.
/// </para>
/// </remarks>
public sealed class BackupIcerikDamgasiTests
{
    // =====================================================================================
    // KARAR KURALI (birim): dört dalın hepsi ayrı bir gerçek
    // =====================================================================================

    /// <summary>Kayıtta damga yoksa sonuç "kurcalanmış" DEĞİL, "damgasız"dır.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Damgasiz_ESKI_kayit_kurcalanmis_SAYILMAZ(string? damga)
    {
        using var alan = new DamgaAlani();
        var yol = alan.DosyaYaz("eski.zip", "icerik");

        Assert.Equal(
            BackupIntegrityCheck.NotStamped,
            await BackupContentHash.VerifyAsync(damga, yol, CancellationToken.None));
    }

    /// <summary>
    /// Geri yüklenmiş kayıt bir DİZİNİ gösterir; orada ölçülecek arşiv yoktur.
    /// </summary>
    /// <remarks>
    /// Aynı durum <see cref="BackupArchiveProbe"/>'ta da ele alınıyor. Dizine "eşleşmedi" demek,
    /// bir kez geri yüklenmiş her kaydı kalıcı olarak kurcalanmış ilan ederdi.
    /// </remarks>
    [Fact]
    public async Task Dizin_yolu_kurcalanmis_SAYILMAZ()
    {
        using var alan = new DamgaAlani();

        Assert.Equal(
            BackupIntegrityCheck.NotStamped,
            await BackupContentHash.VerifyAsync("sha256:abc", alan.Kok, CancellationToken.None));
    }

    /// <summary>Dosya okunamıyorsa ÖLÇÜLEMEDİ — "kurcalanmış" değil.</summary>
    /// <remarks>
    /// Var olmayan yol, kilitli/erişilemez dosyanın test edilebilir eşdeğeridir: iki durumda da
    /// <c>ComputeAsync</c> boş döner. Ayrım önemli çünkü kilitli bir dosyaya "kurcalanmış" demek,
    /// çalışan bir yedeği kullanıcıya çöp diye göstermek olurdu.
    /// </remarks>
    [Fact]
    public async Task Okunamayan_dosya_OLCULEMEDI_dir()
    {
        using var alan = new DamgaAlani();
        var yok = Path.Combine(alan.Kok, "hic-olmayan.zip");

        Assert.Equal(
            BackupIntegrityCheck.Unmeasurable,
            await BackupContentHash.VerifyAsync("sha256:abc", yok, CancellationToken.None));
    }

    /// <summary>Değişmemiş dosya EŞLEŞİR; tek bayt değişince EŞLEŞMEZ.</summary>
    [Fact]
    public async Task Tek_bayt_degisince_eslesmez()
    {
        using var alan = new DamgaAlani();
        var yol = alan.DosyaYaz("arsiv.zip", "MUSTERI ICERIGI");

        var damga = await BackupContentHash.ComputeAsync(yol, CancellationToken.None);
        Assert.StartsWith("sha256:", damga, StringComparison.Ordinal);

        Assert.Equal(
            BackupIntegrityCheck.Match,
            await BackupContentHash.VerifyAsync(damga, yol, CancellationToken.None));

        // Tek karakter: uzunluk aynı kalır, yani boyut ölçümü bunu ASLA yakalayamazdı.
        await File.WriteAllTextAsync(yol, "MUSTERI ICERIGJ", CancellationToken.None);

        Assert.Equal(
            BackupIntegrityCheck.Mismatch,
            await BackupContentHash.VerifyAsync(damga, yol, CancellationToken.None));
    }

    /// <summary>Damga yazımı büyük/küçük harften etkilenmez (kayıt elle düzenlenmiş olabilir).</summary>
    [Fact]
    public async Task Damga_yazimi_buyuk_kucuk_harfe_duyarsiz()
    {
        using var alan = new DamgaAlani();
        var yol = alan.DosyaYaz("arsiv.zip", "icerik");

        var damga = await BackupContentHash.ComputeAsync(yol, CancellationToken.None);

        Assert.Equal(
            BackupIntegrityCheck.Match,
            await BackupContentHash.VerifyAsync(damga.ToUpperInvariant(), yol, CancellationToken.None));
    }

    // =====================================================================================
    // GERÇEK YEDEK AKIŞI: damga yazılıyor mu, geri yükleme onu okuyor mu
    // =====================================================================================

    /// <summary>Yedek alınırken damga KAYDA yazılır ve diskteki dosyayı gerçekten tanımlar.</summary>
    [Fact]
    public async Task Yedek_alinirken_damga_yazilir()
    {
        using var alan = new DamgaAlani();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(
            Path.Combine(siteKoku, "index.php"),
            "MUSTERI ICERIGI",
            CancellationToken.None);

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(
            new BackupSiteRequest { SiteName = "shop.example.com", IncludeFiles = true, IncludeConfig = false },
            CancellationToken.None);

        Assert.StartsWith("sha256:", kayit.ContentHash, StringComparison.Ordinal);
        Assert.Equal(
            BackupIntegrityCheck.Match,
            await BackupContentHash.VerifyAsync(kayit.ContentHash, kayit.Path, CancellationToken.None));
    }

    /// <summary>
    /// KURCALANMIŞ ARŞİV GERİ YÜKLENMEZ — bu sınıfın var oluş sebebi.
    /// </summary>
    /// <remarks>
    /// Saldırganın arşivi YAPISAL OLARAK KUSURSUZDUR: geçerli bir zip, içinde girdi var. Yani
    /// <see cref="BackupArchiveProbe"/> ona <c>sound</c> der. Damga olmasaydı geri yükleme
    /// saldırganın <c>index.php</c>'sini canlı siteye yazardı.
    /// </remarks>
    [Fact]
    public async Task Kurcalanmis_arsiv_geri_yuklenmez()
    {
        using var alan = new DamgaAlani();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(
            Path.Combine(siteKoku, "index.php"),
            "MUSTERI ICERIGI",
            CancellationToken.None);

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(
            new BackupSiteRequest { SiteName = "shop.example.com", IncludeFiles = true, IncludeConfig = false },
            CancellationToken.None);

        // ARŞİVİ SALDIRGAN DEĞİŞTİRİR: geçerli bir zip, ama içinde onun kodu var.
        alan.ArsiviDegistir(kayit.Path, "files/index.php", "<?php /* SALDIRGAN */ ?>");

        // Yapısal ölçüm bunu YAKALAYAMAZ — kanıtı burada, yoksa damganın gereği tartışılırdı.
        using (var arsiv = ZipFile.OpenRead(kayit.Path))
        {
            Assert.NotEmpty(arsiv.Entries);
        }

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            servis.RestoreAsync(new RestoreBackupRequest { BackupId = kayit.Id }, CancellationToken.None));

        Assert.Equal("backup.integrity_mismatch", hata.Message);

        // CANLI SİTE DOKUNULMAMIŞ olmalı: reddetmek, yarım geri yüklemekten iyidir.
        Assert.Equal(
            "MUSTERI ICERIGI",
            await File.ReadAllTextAsync(Path.Combine(siteKoku, "index.php"), CancellationToken.None));
    }

    /// <summary>FAZLA KAPATMA NÖBETÇİSİ: sağlam yedek normal geri yüklenir.</summary>
    [Fact]
    public async Task Saglam_yedek_HALA_geri_yuklenir()
    {
        using var alan = new DamgaAlani();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(
            Path.Combine(siteKoku, "index.php"),
            "YEDEKTEKI",
            CancellationToken.None);

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(
            new BackupSiteRequest { SiteName = "shop.example.com", IncludeFiles = true, IncludeConfig = false },
            CancellationToken.None);

        await File.WriteAllTextAsync(
            Path.Combine(siteKoku, "index.php"),
            "BOZULDU",
            CancellationToken.None);

        var sonuc = await servis.RestoreAsync(
            new RestoreBackupRequest { BackupId = kayit.Id },
            CancellationToken.None);

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal(
            "YEDEKTEKI",
            await File.ReadAllTextAsync(Path.Combine(siteKoku, "index.php"), CancellationToken.None));
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: bu sürümden ÖNCE alınmış (damgasız) yedek HÂLÂ geri yüklenir.
    /// </summary>
    /// <remarks>
    /// Bu test olmadan "damga yoksa reddet" çözümü yeşil görünürdü ve panel, yükseltmeden önceki
    /// bütün yedekleri felaket anında reddederdi — kapatılan riskten kat kat kötüsü.
    /// </remarks>
    [Fact]
    public async Task Damgasiz_ESKI_yedek_HALA_geri_yuklenir()
    {
        using var alan = new DamgaAlani();
        var siteKoku = alan.KlasorAc("wwwroot", "shop");
        await File.WriteAllTextAsync(
            Path.Combine(siteKoku, "index.php"),
            "YEDEKTEKI",
            CancellationToken.None);

        var servis = alan.ServisKur();
        var kayit = await servis.BackupSiteAsync(
            new BackupSiteRequest { SiteName = "shop.example.com", IncludeFiles = true, IncludeConfig = false },
            CancellationToken.None);

        // Yükseltme öncesi kayıtların hâli: damga alanı boş.
        await alan.DamgayiSilAsync(kayit.Id);

        await File.WriteAllTextAsync(
            Path.Combine(siteKoku, "index.php"),
            "BOZULDU",
            CancellationToken.None);

        var sonuc = await servis.RestoreAsync(
            new RestoreBackupRequest { BackupId = kayit.Id },
            CancellationToken.None);

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal(
            "YEDEKTEKI",
            await File.ReadAllTextAsync(Path.Combine(siteKoku, "index.php"), CancellationToken.None));
    }

    // =====================================================================================

    private sealed class DamgaAlani : IDisposable
    {
        public DamgaAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-damga-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = Kok;
        }

        public string Kok { get; }

        public string SiteKoku { get; private set; }

        public JsonFileStore DosyaDeposu { get; }

        public string KlasorAc(params string[] parcalar)
        {
            var yol = Path.Combine([Kok, .. parcalar]);
            Directory.CreateDirectory(yol);
            SiteKoku = yol;
            return yol;
        }

        public string DosyaYaz(string ad, string icerik)
        {
            var yol = Path.Combine(Kok, ad);
            File.WriteAllText(yol, icerik);
            return yol;
        }

        /// <summary>Arşivin İÇİNDEKİ bir girdiyi değiştirir; sonuç YAPISAL OLARAK GEÇERLİ zip'tir.</summary>
        public void ArsiviDegistir(string zipYolu, string girdiAdi, string yeniIcerik)
        {
            using var arsiv = ZipFile.Open(zipYolu, ZipArchiveMode.Update);
            var girdi = arsiv.GetEntry(girdiAdi) ?? arsiv.CreateEntry(girdiAdi);
            using var akis = girdi.Open();
            akis.SetLength(0);
            using var yazici = new StreamWriter(akis);
            yazici.Write(yeniIcerik);
        }

        /// <summary>Yükseltme öncesi kayıtları taklit eder: damga alanını boşaltır.</summary>
        public async Task DamgayiSilAsync(string kayitId)
        {
            var kayitlar = await DosyaDeposu.ReadAsync("config/backups.json", new List<BackupRecordDto>());
            foreach (var kayit in kayitlar.Where(k => k.Id == kayitId))
            {
                kayit.ContentHash = string.Empty;
            }

            await DosyaDeposu.WriteAsync("config/backups.json", kayitlar);
        }

        public BackupService ServisKur() =>
            new(new SahteSiteServisi(SiteKoku),
                null!,
                null!,
                new SahteTercihServisi(Path.Combine(Kok, "backups")),
                null!,
                DosyaDeposu,
                null!);

        // Geçici klasör BİLEREK silinmiyor: bu depoda silme yasağı var ve testin bıraktığı
        // %TEMP% klasörü zararsızdır (aynı karar yeni test dosyalarının hepsinde geçerli).
        public void Dispose()
        {
        }
    }
}
