using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GERI YUKLEMEDE ARSIVIN ISLENMESI: cikarma kokunun disina yazma/silme, kaynak tavanlari ve
/// yarim kalan ise "restored" dememe kurallari.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayri bir dosya:</b> geri yukleme, panelde SALDIRGANIN VERDIGI bir dosyanin
/// LocalSystem yetkisiyle islendigi tek yuzeydir. Arsivin ICI (girdi adlari, envanter JSON'lari,
/// silme listesi) tamamen o dosyayi uretenin denetimindedir; canli kok ise KIRACININ denetiminde
/// (kendi sitesinde PHP calistiran her kiraci orada bir KAVSAK birakabilir -- Windows'ta ayricalik
/// gerektirmez).
/// </para>
/// </remarks>
public sealed class BackupRestoreArchiveGuardTests
{
    // =====================================================================================
    // A -- KAVSAK (junction) KACISI: dizge denetiminin GOREMEDIGI yol
    //
    // RestoreItemsAsync'te bu koruma 0.8.6'da kondu (SiteBuilderPathGuard). KARDES YOLLAR --
    // birlesik site zinciri (files/*), abonelik zinciri (sites/<ad>/files/*) ve silme listesi
    // (deleted.json) -- ayni kapiyi kullanmiyordu.
    // =====================================================================================

    /// <summary>
    /// Canli site kokunde duran bir KAVSAK, arsivden gelen bir girdinin kokun DISINA yazmasina
    /// izin vermemeli.
    /// </summary>
    /// <remarks>
    /// <b>OLCULEN GERCEK:</b> bu yolda kacis, "once yana al" adimi sayesinde ZATEN olmuyordu --
    /// kavsak kokun icinde bir girdidir ve yana alma onu da kardes klasore TASIR, yani cikarma
    /// gercek (bos) bir koke yazar. Test bunu kanitlamak icin duruyor: davranis KAZAYLA dogruydu
    /// ve o kazayi kiran bir degisiklik (yana almanin kosulunu daraltmak) burayi kirmizi yakar.
    /// Nobetci ayrica ResolveInsideRoot'ta acikca duruyor; onu olcen test silme yolundadir
    /// (yana almanin CALISMADIGI halka).
    /// </remarks>
    [Fact]
    public async Task SiteChainRestore_WritingThroughJunction_NeverLeavesTheRoot()
    {
        using var alan = new Alan();

        var canliKok = alan.Klasor("wwwroot", "shop");
        var disari = alan.Klasor("gizli");
        await File.WriteAllTextAsync(Path.Combine(disari, "kurban.txt"), "dokunulmadi");

        // Arsivde files/ ALTINDA baska bir dosya da var: "yana alma" tetiklenip kavsagi tasisa
        // bile senaryo olculebilsin diye kavsak yana-almadan SONRA kuruluyor (asagida).
        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["files/wp-content/ele-gecirildi.txt"] = "SALDIRGAN"
        });

        // Kavsak, geri yukleme SIRASINDA kokte duruyor olmali. "Once yana al" adimi kokun
        // ICINDEKI girdileri tasir; bu yuzden kokun kendisi BOS birakilip kavsak dogrudan
        // koke kurulur ve yana alma (icerik yok) hic calismaz.
        var baglanti = Path.Combine(canliKok, "wp-content");
        if (!Alan.JunctionKur(baglanti, disari))
        {
            return; // kavsak kurulamiyorsa olcum yapilamaz
        }

        var servis = alan.Servis(zip, canliKok);
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.False(
            File.Exists(Path.Combine(disari, "ele-gecirildi.txt")),
            "Arsiv girdisi KAVSAGI takip edip cikarma kokunun disina yazdi.");
        Assert.Equal("dokunulmadi", await File.ReadAllTextAsync(Path.Combine(disari, "kurban.txt")));

        // Dosya GERCEK koke yazildi (kavsak yana alindi) -- mesru is bozulmadi.
        Assert.Equal("restored", kayit.Status);
        Assert.Equal(
            "SALDIRGAN",
            await File.ReadAllTextAsync(Path.Combine(canliKok, "wp-content", "ele-gecirildi.txt")));
        Assert.False(string.IsNullOrWhiteSpace(kayit.PreservedPath));
    }

    /// <summary>
    /// Silme listesi (deleted.json) de kavsagi takip edip kokun DISINDAKI bir dosyayi silmemeli.
    /// </summary>
    /// <remarks>
    /// Bu, yazma yolundan DAHA sinsidir: arsivde hic <c>files/</c> girdisi olmayan bir artimsal
    /// halka "once yana al" adimini hic tetiklemez (dogru karar: yalniz veritabani iceren bir
    /// yedek canli siteyi bosaltmamali), yani kavsak yerinde kalir ve <c>File.Delete</c> onu
    /// takip eder.
    /// </remarks>
    [Fact]
    public async Task SiteChainRestore_DeletingThroughJunction_IsRefusedAndReported()
    {
        using var alan = new Alan();

        var canliKok = alan.Klasor("wwwroot", "shop");
        var disari = alan.Klasor("gizli");
        await File.WriteAllTextAsync(Path.Combine(disari, "kurban.txt"), "dokunulmadi");

        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["databases/mysql-shop.sql"] = "-- dump",
            ["deleted.json"] = "[\"files/wp-content/kurban.txt\"]"
        });

        var baglanti = Path.Combine(canliKok, "wp-content");
        if (!Alan.JunctionKur(baglanti, disari))
        {
            return;
        }

        var servis = alan.Servis(zip, canliKok);
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.True(
            File.Exists(Path.Combine(disari, "kurban.txt")),
            "Silme listesi KAVSAGI takip edip cikarma kokunun disindaki dosyayi SILDI.");
        Assert.Equal("restored_with_errors", kayit.Status);
        Assert.Contains(kayit.Warnings, uyari => uyari.MessageKey == "backup.invalid_entry_path");
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI: kavsak YOKKEN mesru bir yedek eksiksiz geri yuklenmeli.
    /// </summary>
    [Fact]
    public async Task SiteChainRestore_WithoutLinks_RestoresEverythingAndStaysClean()
    {
        using var alan = new Alan();

        var canliKok = alan.Klasor("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(canliKok, "index.php"), "eski");

        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["files/index.php"] = "yeni",
            ["files/wp-content/plugins/a/a.php"] = "eklenti",
            ["files/wp-content/uploads/2024/01/foto.jpg"] = "JPG"
        });

        var servis = alan.Servis(zip, canliKok);
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.Equal("restored", kayit.Status);
        Assert.Empty(kayit.Warnings);
        Assert.Equal("yeni", await File.ReadAllTextAsync(Path.Combine(canliKok, "index.php")));
        Assert.Equal("eklenti", await File.ReadAllTextAsync(Path.Combine(canliKok, "wp-content", "plugins", "a", "a.php")));
        Assert.Equal("JPG", await File.ReadAllTextAsync(Path.Combine(canliKok, "wp-content", "uploads", "2024", "01", "foto.jpg")));
    }

    // =====================================================================================
    // A -- MUTLAK / UNC / SURUCUYE GORELI GIRDI ADLARI
    // =====================================================================================

    [Theory]
    [InlineData("files/../../disari.txt")]                       // gorece kacis
    [InlineData("files/..\\..\\disari.txt")]                     // ters bolulu kacis
    [InlineData("files/C:/Windows/Temp/disari.txt")]             // MUTLAK yol
    [InlineData("files/C:Windows/disari.txt")]                   // SURUCUYE goreli (C:x)
    [InlineData("files///sunucu/pay/disari.txt")]                // UNC (normalizasyon sonrasi \\sunucu\pay)
    [InlineData("files/./../../disari.txt")]                     // nokta karisimi
    public async Task SiteChainRestore_EscapingEntryName_IsRefusedAndReported(string girdiAdi)
    {
        using var alan = new Alan();

        var canliKok = alan.Klasor("wwwroot", "shop");
        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            [girdiAdi] = "SALDIRGAN"
        });

        var servis = alan.Servis(zip, canliKok);
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.False(File.Exists(Path.Combine(alan.Kok, "wwwroot", "disari.txt")));
        Assert.False(File.Exists(Path.Combine(alan.Kok, "disari.txt")));
        Assert.Equal("restored_with_errors", kayit.Status);
        Assert.Contains(kayit.Warnings, uyari => uyari.MessageKey == "backup.invalid_entry_path");
    }

    /// <summary>
    /// Canli kokun KENDISI bir kavsaksa geri yukleme HIC BASLAMAMALI.
    /// </summary>
    /// <remarks>
    /// Kok kavsaksa iki sey ayni anda olur: (1) "once yana al" adimi kavsagin HEDEFINDEKI
    /// icerigi bosaltir (yani baska bir klasorun icini tasir), (2) sonraki her yazma kokun
    /// disina gider. Segment segment yoklamak bunu GORMEZ; kokun kendisi ayrica sorulmalidir
    /// (kural SiteBuilderPathGuard.EnsureUsableRoot'ta zaten yaziliydi).
    /// </remarks>
    [Fact]
    public async Task SiteChainRestore_WhenLiveRootItselfIsJunction_RefusesBeforeTouchingAnything()
    {
        using var alan = new Alan();

        var disari = alan.Klasor("gizli");
        await File.WriteAllTextAsync(Path.Combine(disari, "kurban.txt"), "dokunulmadi");

        var kokler = alan.Klasor("wwwroot");
        var canliKok = Path.Combine(kokler, "shop");
        if (!Alan.JunctionKur(canliKok, disari))
        {
            return;
        }

        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["files/ele-gecirildi.txt"] = "SALDIRGAN"
        });

        var servis = alan.Servis(zip, canliKok);
        var hata = await Record.ExceptionAsync(() =>
            servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" }));

        // ONCE OLCUM: kavsagin HEDEFI el degmemis mi?
        Assert.False(
            File.Exists(Path.Combine(disari, "ele-gecirildi.txt")),
            "Kok kavsakti; arsiv girdisi kavsagin HEDEFINE yazildi.");
        Assert.True(
            File.Exists(Path.Combine(disari, "kurban.txt")),
            "Kok kavsakti; 'once yana al' adimi kavsagin HEDEFINDEKI icerigi tasidi.");

        var appHata = Assert.IsType<AppException>(hata);
        Assert.Equal("backup.restore_target_is_link", appHata.MessageKey);
    }

    /// <summary>
    /// Genel zip yolu (panel yedegi / birlesik olmayan arsiv) da kavsagi takip etmemeli.
    /// </summary>
    [Fact]
    public async Task PlainZipRestore_WritingThroughJunctionInTarget_IsRefused()
    {
        using var alan = new Alan();

        var hedef = alan.Klasor("hedef");
        var disari = alan.Klasor("gizli");
        await File.WriteAllTextAsync(Path.Combine(disari, "kurban.txt"), "dokunulmadi");

        var baglanti = Path.Combine(hedef, "tasks");
        if (!Alan.JunctionKur(baglanti, disari))
        {
            return;
        }

        var zip = alan.Zip(new Dictionary<string, string> { ["tasks/ele-gecirildi.txt"] = "SALDIRGAN" });
        var servis = alan.Servis(zip, alan.Klasor("kullanilmayan"), tur: "panel");

        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1", TargetPath = hedef });

        Assert.False(
            File.Exists(Path.Combine(disari, "ele-gecirildi.txt")),
            "Duz zip cikarmasi KAVSAGI takip edip hedefin disina yazdi.");

        // Reddedilen girdi SESSIZ DEGIL; ama tek girdi yuzunden butun kurtarma iptal EDILMEZ.
        Assert.Equal("restored_with_errors", kayit.Status);
        var uyari = Assert.Single(kayit.Warnings, u => u.MessageKey == "backup.invalid_entry_path");
        Assert.Contains("link_on_path", uyari.Detail);
    }

    /// <summary>FAZLA KAPATMA NOBETCISI: kavsak yokken duz zip geri yuklemesi calismali.</summary>
    [Fact]
    public async Task PlainZipRestore_WithoutLinks_ExtractsEverything()
    {
        using var alan = new Alan();

        var hedef = alan.Klasor("hedef");
        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["config/panel.json"] = "{}",
            ["tasks/a.xml"] = "<Task/>"
        });

        var servis = alan.Servis(zip, alan.Klasor("kullanilmayan"), tur: "panel");
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1", TargetPath = hedef });

        Assert.Equal("restored", kayit.Status);
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(hedef, "config", "panel.json")));
        Assert.Equal("<Task/>", await File.ReadAllTextAsync(Path.Combine(hedef, "tasks", "a.xml")));
    }

    // =====================================================================================
    // B -- PANEL YALAN SOYLUYOR MU: yarim kalan geri yukleme
    // =====================================================================================

    /// <summary>
    /// Cikarma yarida patlarsa canli icerigin NEREYE tasindigi KAYBOLMAMALI.
    /// </summary>
    /// <remarks>
    /// "Once yana al" adimi kokun butun icerigini kardes klasore tasir. Cikarma sirasinda bir
    /// istisna cikarsa kayda hicbir sey yazilmiyordu: kullanicinin sitesi BOSTU ve panel eski
    /// icerigin nerede oldugunu SOYLEMIYORDU. Felaket aninda verilebilecek en pahali cevap budur.
    /// </remarks>
    [Fact]
    public async Task SiteChainRestore_WhenExtractionThrows_StillReportsWhereLiveContentWent()
    {
        using var alan = new Alan();

        var canliKok = alan.Klasor("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(canliKok, "index.php"), "eski");
        var yuklemeler = Path.Combine(canliKok, "uploads");
        Directory.CreateDirectory(yuklemeler);
        await File.WriteAllTextAsync(Path.Combine(yuklemeler, "fatura.pdf"), "musteri verisi");

        // Cikarmayi GERCEKTEN patlatan girdi: once "x" DOSYA olarak yazilir, hemen ardindan
        // "x/y" gelir -> "x" klasor olmadigi icin CreateDirectory IOException atar.
        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["files/index.php"] = "yeni",
            ["files/x"] = "dosya",
            ["files/x/y"] = "patlat"
        });

        var servis = alan.Servis(zip, canliKok);
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        // Islem tamamlandi ama YARIM: durum bunu soylemeli...
        Assert.Equal("restored_with_errors", kayit.Status);
        // ...ve yana alinan icerigin yeri kayitta DURMALI.
        Assert.False(string.IsNullOrWhiteSpace(kayit.PreservedPath));
        Assert.Equal(
            "musteri verisi",
            await File.ReadAllTextAsync(Path.Combine(kayit.PreservedPath, "uploads", "fatura.pdf")));

        var kalici = Assert.Single(await servis.GetBackupsAsync());
        Assert.Equal(kayit.PreservedPath, kalici.PreservedPath);
        Assert.Equal("restored_with_errors", kalici.Status);
    }

    /// <summary>
    /// Geri yukleme OLUMCUL bir hatayla kesilse bile KAYIT dogruyu soylemeli.
    /// </summary>
    /// <remarks>
    /// Eskiden istisna <c>UpsertRecordAsync</c>'e HIC ulasmiyordu: kayit "completed" kaliyor,
    /// panelde hicbir sey olmamis gibi gorunuyordu. Kullanicinin gordugu tek sey 500 hatasiydi ve
    /// ekranini yeniledigi anda o da kayboluyordu.
    /// </remarks>
    [Fact]
    public async Task SiteChainRestore_WhenPreserveFails_RecordStillSaysItWasNotRestored()
    {
        using var alan = new Alan();

        var canliKok = alan.Klasor("wwwroot", "shop");
        var kilitliYol = Path.Combine(canliKok, "kilitli.dat");
        await File.WriteAllTextAsync(kilitliYol, "canli");

        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["files/index.php"] = "yeni"
        });

        var servis = alan.Servis(zip, canliKok);

        // Dosya PAYLASIMSIZ acik: "once yana al" adimi tasiyamaz -> olumcul hata.
        using (new FileStream(kilitliYol, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var hata = await Assert.ThrowsAsync<AppException>(() =>
                servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" }));
            Assert.Equal("backup.restore_preserve_failed", hata.MessageKey);
        }

        // KAYIT SESSIZ KALMAZ: durum + sebep diskte durmali.
        var kalici = Assert.Single(await servis.GetBackupsAsync());
        Assert.Equal("restored_with_errors", kalici.Status);
        Assert.Contains(kalici.Warnings, uyari => uyari.MessageKey == "backup.restore_aborted");
    }

    // =====================================================================================
    // C -- KAYNAK TAVANI: arsivden gelen ENVANTER dosyalari
    // =====================================================================================

    /// <summary>
    /// Arsivden gelen silme listesi TAMAMEN bellege okunur. Tavan yoksa 4 MB'lik bir arsiv
    /// gigabaytlarca bellek isteyebilir (zip bombasinin envanter hali).
    /// </summary>
    [Fact]
    public async Task SiteChainRestore_OversizedDeletedList_IsRefusedInsteadOfBeingRead()
    {
        using var alan = new Alan();

        var canliKok = alan.Klasor("wwwroot", "shop");
        var buyuk = "[\"" + new string('a', (int)(BackupArchiveLimits.MaxMetadataBytes + 1024)) + "\"]";
        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["databases/mysql-shop.sql"] = "-- dump",
            ["deleted.json"] = buyuk
        });

        var servis = alan.Servis(zip, canliKok);
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.Equal("restored_with_errors", kayit.Status);
        Assert.Contains(kayit.Warnings, uyari => uyari.MessageKey == "backup.entry_too_large");
    }

    /// <summary>FAZLA KAPATMA NOBETCISI: normal boyutlu silme listesi CALISMAYA devam etmeli.</summary>
    [Fact]
    public async Task SiteChainRestore_NormalDeletedList_StillDeletesInsideRoot()
    {
        using var alan = new Alan();

        var canliKok = alan.Klasor("wwwroot", "shop");
        await File.WriteAllTextAsync(Path.Combine(canliKok, "eski-eklenti.php"), "kaldirildi");

        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["databases/mysql-shop.sql"] = "-- dump",
            ["deleted.json"] = "[\"files/eski-eklenti.php\"]"
        });

        var servis = alan.Servis(zip, canliKok);
        var kayit = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "b1" });

        Assert.Equal("restored", kayit.Status);
        Assert.Empty(kayit.Warnings);
        Assert.False(File.Exists(Path.Combine(canliKok, "eski-eklenti.php")));
    }

    // =====================================================================================
    // D -- KIRACI SINIRI: secerek geri yuklemenin HEDEFI
    // =====================================================================================

    /// <summary>
    /// Hedef yol verilmediginde SITE arsivi de ayrilmis geri yukleme klasorune cikarilmali --
    /// sitelerin ORTAK ust dizinine DEGIL.
    /// </summary>
    /// <remarks>
    /// Panelin varsayilan duzeninde kiraci siteleri duz olarak <c>&lt;kok&gt;\&lt;site&gt;</c>
    /// altina acilir; "uygulama kokunun ust dizini" o zaman BUTUN kiracilarin ortak kabi (ve IIS
    /// varsayilan sitesinin belge koku) olur. Bu uc kapsamli kullaniciya aciktir ve panel
    /// LocalSystem kosar, yani hicbir ACL durdurmaz.
    /// </remarks>
    [Fact]
    public async Task RestoreItems_WithoutTargetPath_DoesNotWriteIntoTheSharedSiteContainer()
    {
        using var alan = new Alan();

        var siteler = alan.Klasor("wwwroot");
        var canliKok = alan.Klasor("wwwroot", "shop");
        var komsuKok = alan.Klasor("wwwroot", "files");
        await File.WriteAllTextAsync(Path.Combine(komsuKok, "index.php"), "komsu site");

        // Gercek bir site yedegi gibi: files/ onekli girdiler.
        var zip = alan.Zip(new Dictionary<string, string>
        {
            ["manifest.json"] = "{}",
            ["files/index.php"] = "SALDIRGAN"
        });

        var servis = alan.Servis(zip, canliKok, tercihler: new SabitTercihler(alan.Klasor("yedekler")));

        var sonuc = await servis.RestoreItemsAsync(new RestoreBackupItemsRequest
        {
            BackupId = "b1",
            Items = ["files/"]
        });

        Assert.Equal(1, sonuc.RestoredCount);

        // Komsu site EL DEGMEMIS ve ortak kaba HICBIR SEY yazilmamis olmali.
        Assert.Equal("komsu site", await File.ReadAllTextAsync(Path.Combine(komsuKok, "index.php")));
        Assert.False(Directory.Exists(Path.Combine(siteler, "files", "index.php")));
        Assert.False(
            PathContainment.IsRootOrInside(siteler, sonuc.TargetRoot),
            $"Secerek geri yukleme sitelerin ORTAK kabina yazdi: {sonuc.TargetRoot}");

        // ...ve icerik gercekten cikarilmis olmali (fazla kapatma nobetcisi).
        Assert.Equal("SALDIRGAN", await File.ReadAllTextAsync(Path.Combine(sonuc.TargetRoot, "files", "index.php")));
    }

    // =====================================================================================
    // ORTAK ALAN
    // =====================================================================================

    private sealed class Alan : IDisposable
    {
        public Alan()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-restoreguard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            Depo = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
        }

        public string Kok { get; }

        public JsonFileStore Depo { get; }

        public string Klasor(params string[] parcalar)
        {
            var yol = Path.Combine([Kok, .. parcalar]);
            Directory.CreateDirectory(yol);
            return yol;
        }

        public string Zip(IDictionary<string, string> girdiler)
        {
            var yol = Path.Combine(Kok, $"yedek-{Guid.NewGuid():N}.zip");
            using var akis = new FileStream(yol, FileMode.Create, FileAccess.Write);
            using var arsiv = new ZipArchive(akis, ZipArchiveMode.Create);
            foreach (var (ad, icerik) in girdiler)
            {
                var girdi = arsiv.CreateEntry(ad, CompressionLevel.Fastest);
                using var yazici = new StreamWriter(girdi.Open());
                yazici.Write(icerik);
            }

            return yol;
        }

        public BackupService Servis(
            string zipYolu,
            string siteKoku,
            string tur = "site",
            IPanelPreferencesService? tercihler = null)
        {
            Depo.WriteAsync("config/backups.json", new[]
            {
                new BackupRecordDto
                {
                    Id = "b1",
                    Name = Path.GetFileName(zipYolu),
                    Type = tur,
                    Source = "shop.example.com",
                    Path = zipYolu,
                    Format = "zip",
                    CreatedAtUtc = DateTime.UtcNow
                }
            }).GetAwaiter().GetResult();

            return new BackupService(
                new SabitSiteServisi(siteKoku),
                new SessizVeritabaniServisi(),
                null!,
                tercihler!,
                null!,
                Depo,
                null!);
        }

        /// <summary>Kavsak kurar; kurulamadiysa <c>false</c> (olcum atlanir).</summary>
        public static bool JunctionKur(string baglanti, string hedef)
        {
            try
            {
                using var surec = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{baglanti}\" \"{hedef}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                surec?.WaitForExit(15000);
            }
            catch
            {
                return false;
            }

            return Directory.Exists(baglanti)
                   && new DirectoryInfo(baglanti).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }

        public void Dispose()
        {
            // HICBIR SEY SILINMEZ. Gecici alan %TEMP% altinda BIRAKILIR: bu takimda ozyinelemeli
            // silme bir KAVSAGI takip edip testin kendisini yikici hale getirmisti.
        }
    }

    private sealed class SabitTercihler(string yedekKoku) : IPanelPreferencesService
    {
        public Task<AdaPanel.Core.Dtos.Settings.PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdaPanel.Core.Dtos.Settings.PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

        public Task<AdaPanel.Core.Dtos.Settings.PanelPreferencesDto> UpdateAsync(
            AdaPanel.Core.Dtos.Settings.UpdatePanelPreferencesRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SabitSiteServisi(string kok) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = kok, ApplicationRootPath = kok });

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SessizVeritabaniServisi : IDatabaseService
    {
        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DatabaseDumpResultDto
            {
                Engine = request.Engine,
                DatabaseName = request.DatabaseName,
                FilePath = request.FilePath
            });

        public Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Databases.DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
