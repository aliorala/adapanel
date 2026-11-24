using System.IO.Compression;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// YEDEK OKUYAMADIGI DOSYAYI SESSIZCE ATLAYIP "basarili" DIYEMEZ.
/// </summary>
/// <remarks>
/// <para>
/// OLCULEN HATA: arsive dosya ekleyen uc yardimci da (<c>AddFilesToArchive</c>,
/// <c>AddDirectoryToArchive</c>, <c>AddSharedFileToArchive</c>) kilitli/erisilemeyen dosyayi
/// ciplak <c>catch (IOException) { }</c> ile geciyordu. Ucu de <c>static</c> oldugu icin ne
/// gunlukcuye ne sayaca ne de kaydin durumuna erisebiliyordu: kayit "created" damgasiyla
/// kapaniyor, atlanan dosyanin adi HICBIR YERE dusmuyordu.
/// </para>
/// <para>
/// Bu, PANELIN KENDI YEDEGINDE de gecerliydi: <c>data/adapanel.db</c> (kullanicilar, 2FA gizleri,
/// denetim kayitlari) kilitliyse panel kendi veritabani OLMAYAN bir arsivi "basarili" diye
/// kaydediyordu. Bunu ancak geri yuklerken -- felaket aninda -- ogrenirsin.
/// </para>
/// <para>
/// Testler DAVRANISI degil KANITI olcer: her durumda (a) kaydin DURUMU, (b) uyari listesinde
/// atlanan dosyanin ADI ve (c) arsivin GERCEK icerigi ayri ayri dogrulanir. Yalniz duruma bakmak,
/// "durumu degistirip dosyayi yine de atlayan" bir regresyonu yesil gosterirdi.
/// </para>
/// </remarks>
public sealed class BackupSkippedFileTests
{
    private const string AtlandiAnahtari = "backup.source_file_unreadable";

    // ===========================================================================================
    // PANEL YEDEGI: adapanel.db + config/
    // ===========================================================================================

    [Fact]
    public async Task Panel_yedegi_okunamayan_adapanel_db_yi_SESSIZCE_atlamaz()
    {
        using var alan = new AtlamaAlani();
        alan.PanelVeritabaniUret();

        // Baska bir surec veritabanini PAYLASIMSIZ acti (gercek hayatta: yedekleme araci,
        // antivirus, kilitli WAL). Paylasimli okuma da bu durumda basarisiz olur.
        using (alan.Kilitle("data/adapanel.db"))
        {
            var kayit = await alan.Servis().BackupPanelAsync(new BackupPanelRequest(), alan.Iptal);

            // (a) DURUM: "created" DEGIL.
            Assert.Equal("created_with_errors", kayit.Status);

            // (b) ADIYLA: kullanici hangi dosyanin atlandigini gorebilmeli.
            var uyari = Assert.Single(kayit.Warnings, item => item.Target == "data/adapanel.db");
            Assert.Equal(AtlandiAnahtari, uyari.MessageKey);
            Assert.NotEmpty(uyari.Detail);

            // (c) OLCUM: arsivde gercekten yok. Duruma bakip icerige bakmamak, "durumu dogru
            //     yazan ama dosyayi yine de kaybeden" bir regresyonu kacirirdi.
            Assert.DoesNotContain("data/adapanel.db", alan.ArsivGirdileri(kayit));

            // (d) MANIFEST de yalan soylememeli: arsivi acan kisi "parts" listesinde
            //     adapanel.db gormemeli.
            Assert.DoesNotContain("adapanel.db", alan.Manifest(kayit));
        }
    }

    [Fact]
    public async Task Panel_yedegi_okunamayan_config_dosyasini_SESSIZCE_atlamaz()
    {
        using var alan = new AtlamaAlani();
        alan.PanelVeritabaniUret();
        alan.ConfigDosyasiUret("automation.json", "{\"backupTargetType\":\"local\"}");
        alan.ConfigDosyasiUret("customer-scopes.json", "[]");

        using (alan.Kilitle("config/automation.json"))
        {
            var kayit = await alan.Servis().BackupPanelAsync(new BackupPanelRequest(), alan.Iptal);

            Assert.Equal("created_with_errors", kayit.Status);
            Assert.Contains(
                kayit.Warnings,
                uyari => uyari.MessageKey == AtlandiAnahtari && uyari.Target == "config/automation.json");

            var girdiler = alan.ArsivGirdileri(kayit);
            Assert.DoesNotContain("config/automation.json", girdiler);

            // Yedegin GERI KALANI durmali: tek bir kilitli dosya butun yedegi dusurmemeli.
            Assert.Contains("config/customer-scopes.json", girdiler);
            Assert.Contains("data/adapanel.db", girdiler);
        }
    }

    /// <summary>
    /// MUTASYON KONTROLU: kilit YOKKEN durum "created" kalmali ve tek bir uyari uretilmemeli.
    /// Bu olmadan "her yedege created_with_errors yaz" gibi bir degisiklik de testleri gecerdi.
    /// </summary>
    [Fact]
    public async Task Her_sey_okunabilirken_durum_created_kalir_ve_UYARI_URETILMEZ()
    {
        using var alan = new AtlamaAlani();
        alan.PanelVeritabaniUret();
        alan.ConfigDosyasiUret("automation.json", "{}");

        var kayit = await alan.Servis().BackupPanelAsync(new BackupPanelRequest(), alan.Iptal);

        Assert.Equal("created", kayit.Status);
        Assert.DoesNotContain(kayit.Warnings, uyari => uyari.MessageKey == AtlandiAnahtari);

        var girdiler = alan.ArsivGirdileri(kayit);
        Assert.Contains("data/adapanel.db", girdiler);
        Assert.Contains("config/automation.json", girdiler);
    }

    // ===========================================================================================
    // SITE YEDEGI: musteri dosyalari
    // ===========================================================================================

    [Fact]
    public async Task Site_yedegi_okunamayan_MUSTERI_dosyasini_bildirir()
    {
        using var alan = new AtlamaAlani();
        alan.SiteDosyasiUret("index.php", "MUSTERI ICERIGI");
        alan.SiteDosyasiUret("uploads/fatura.pdf", "PDF");

        using (alan.Kilitle(Path.Combine("wwwroot", "shop", "uploads", "fatura.pdf")))
        {
            var kayit = await alan.Servis().BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

            Assert.Equal("created_with_errors", kayit.Status);
            Assert.Contains(
                kayit.Warnings,
                uyari => uyari.MessageKey == AtlandiAnahtari && uyari.Target == "files/uploads/fatura.pdf");

            var girdiler = alan.ArsivGirdileri(kayit);
            Assert.DoesNotContain("files/uploads/fatura.pdf", girdiler);
            Assert.Contains("files/index.php", girdiler);
        }
    }

    /// <summary>
    /// Kayit DISKTE de eksik damgasini tasimali: panelin yedek listesi JSON'dan okunur, bellekteki
    /// nesneye bakmak dosyaya yazilmayan bir durumu yesil gosterirdi.
    /// </summary>
    [Fact]
    public async Task Eksik_yedegin_durumu_KAYIT_DOSYASINA_da_yazilir()
    {
        using var alan = new AtlamaAlani();
        alan.SiteDosyasiUret("index.php", "MUSTERI ICERIGI");

        using (alan.Kilitle(Path.Combine("wwwroot", "shop", "index.php")))
        {
            var kayit = await alan.Servis().BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

            var listeden = Assert.Single(await alan.Servis().GetBackupsAsync(alan.Iptal), item => item.Id == kayit.Id);
            Assert.Equal("created_with_errors", listeden.Status);
            Assert.Contains(listeden.Warnings, uyari => uyari.MessageKey == AtlandiAnahtari);
        }
    }

    /// <summary>
    /// COK SAYIDA atlanan dosya: adlar sinirli, SAYI hicbir kosulda kaybolmaz. Bozuk bir ACL yuz
    /// binlerce dosyayi birden atlatabilir; hepsini kayda yazmak yedek listesini kullanilamaz
    /// hale getirirdi.
    /// </summary>
    [Fact]
    public async Task Cok_sayida_atlanan_dosyada_ADLAR_sinirlanir_ama_SAYI_kaybolmaz()
    {
        using var alan = new AtlamaAlani();
        const int adet = BackupSkipLog.MaxDetailed + 5;

        var kilitler = new List<FileStream>();
        try
        {
            for (var sira = 0; sira < adet; sira++)
            {
                alan.SiteDosyasiUret($"kilitli-{sira:00}.dat", "veri");
                kilitler.Add(alan.Kilitle(Path.Combine("wwwroot", "shop", $"kilitli-{sira:00}.dat")));
            }

            var kayit = await alan.Servis().BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

            Assert.Equal("created_with_errors", kayit.Status);
            Assert.Equal(
                BackupSkipLog.MaxDetailed,
                kayit.Warnings.Count(uyari => uyari.MessageKey == AtlandiAnahtari));

            var ozet = Assert.Single(kayit.Warnings, uyari => uyari.MessageKey == "backup.source_files_unreadable_more");
            Assert.Equal(adet.ToString(), ozet.Target);
        }
        finally
        {
            foreach (var kilit in kilitler)
            {
                kilit.Dispose();
            }
        }
    }

    // ===========================================================================================
    // GUNLUK OZETI (BackupSkipLog.Describe)
    // ===========================================================================================

    /// <summary>
    /// HIC DOSYA ATLANMADIGINDA OZET "0 dosya atlandi" DEMEZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OLCULEN HATA: <c>Any</c> kesilen taramayi da kapsamaya baslayinca "hic dosya atlanmadi ama
    /// bir agacin taramasi kesildi" durumu mumkun oldu ve ozet
    /// <c>"0 dosya atlandi: ; TARAMA KESILDI: ..."</c> uretiyordu -- sayi sifir, liste bos, iki
    /// noktali virgul.
    /// </para>
    /// <para>
    /// Yalniz sunucu gunlugune gidiyor, yani kozmetik; ama gunluk OKUNMAK icindir. Yangin aninda
    /// bakan kisinin gordugu ilk sey "0 dosya atlandi" olsaydi, asil bilgi -- BIR AGACIN HIC
    /// TARANMADIGI -- gurultunun arkasinda kalirdi.
    /// </para>
    /// <para>
    /// MUTASYON: dosya cumlesi yeniden KOSULSUZ yazilirsa (<c>Count</c> denetimi kaldirilirsa)
    /// bu test kizarir.
    /// </para>
    /// </remarks>
    [Fact]
    public void SIFIR_ATLANANDA_OZET_ANLAMSIZ_SATIR_URETMEZ()
    {
        var kayit = new BackupSkipLog();
        kayit.AddTruncatedScan("sites/x/files", "files.size_truncated_entries");

        var ozet = kayit.Describe();

        // Bildirilecek bir eksik VAR: kesilen tarama da eksiktir.
        Assert.True(kayit.Any);

        // Ama "0 dosya atlandi" YAZILMAZ, ve bos liste ardindan gelen ": ;" artigi da kalmaz.
        Assert.DoesNotContain("0 dosya", ozet, StringComparison.Ordinal);
        Assert.DoesNotContain(": ;", ozet, StringComparison.Ordinal);

        // ASIL BILGI YERINDE: nerede ve nicin kesildigi.
        Assert.StartsWith("TARAMA KESILDI:", ozet, StringComparison.Ordinal);
        Assert.Contains("sites/x/files", ozet, StringComparison.Ordinal);
        Assert.Contains("files.size_truncated_entries", ozet, StringComparison.Ordinal);
    }

    /// <summary>
    /// MUTASYON KONTROLU: dosya atlandiginda cumle HALA yaziliyor ve ikisi birlikte gorunuyor.
    /// </summary>
    /// <remarks>
    /// Bu olmadan "dosya cumlesini tamamen kaldir" gibi bir degisiklik de yukaridaki testi gecerdi;
    /// yani test, dogru ozeti degil yalnizca bir metnin YOKLUGUNU olcerdi.
    /// </remarks>
    [Fact]
    public void ATLANAN_DOSYA_VARSA_OZET_IKISINI_DE_TASIR()
    {
        var kayit = new BackupSkipLog();
        kayit.Add("files/kilitli.dat", new IOException("kilitli"));
        kayit.AddTruncatedScan("sites/x/files", "files.size_truncated_time");

        var ozet = kayit.Describe();

        Assert.Contains("1 dosya atlandi", ozet, StringComparison.Ordinal);
        Assert.Contains("files/kilitli.dat", ozet, StringComparison.Ordinal);
        Assert.Contains("TARAMA KESILDI: sites/x/files", ozet, StringComparison.Ordinal);
    }

    /// <summary>
    /// HIC EKSIK YOKKEN OZET BOS DIZE DONMEZ.
    /// </summary>
    /// <remarks>
    /// Cagiran su an <c>Any</c> ile koruyor, ama koruma bir gun kalkarsa gunluge konusuz bir
    /// "Yedek eksik alindi (...): " satiri duserdi: uyari seviyesinde ve sebepsiz.
    /// </remarks>
    [Fact]
    public void EKSIK_YOKKEN_OZET_KONUSUZ_KALMAZ()
    {
        var ozet = new BackupSkipLog().Describe();

        Assert.False(string.IsNullOrWhiteSpace(ozet));
        Assert.DoesNotContain("0 dosya", ozet, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // Yardimcilar
    // ===========================================================================================

    internal sealed class AtlamaAlani : IDisposable
    {
        private readonly CancellationTokenSource _iptal = new(TimeSpan.FromSeconds(120));

        public AtlamaAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-atlama-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = Path.Combine(Kok, "wwwroot", "shop");
            Directory.CreateDirectory(SiteKoku);
            YedekKoku = Path.Combine(Kok, "backups");
        }

        public string Kok { get; }

        public string SiteKoku { get; }

        public string YedekKoku { get; }

        public JsonFileStore DosyaDeposu { get; }

        public CancellationToken Iptal => _iptal.Token;

        /// <summary>
        /// IIS servisi panel yedeginde yalniz "en iyi caba" ile okunur (rol kurulu olmayabilir);
        /// site yedeginde ise sahtesi kullanilir. Uzak hedef YOK: olculen sey arsivin icerigi.
        /// </summary>
        public BackupService Servis(IBackupSnapshotRuntime? snapshotRuntime = null) =>
            new(new SahteSiteServisi(SiteKoku),
                null!,
                null!,
                new SahteTercihServisi(YedekKoku),
                null!,
                DosyaDeposu,
                null!, snapshotRuntime: snapshotRuntime)
            {
                TaskExportRunner = (_, _) => Task.FromResult(new CommandResult { StandardOutput = "[]" })
            };

        public BackupSiteRequest SiteIstegi() => new()
        {
            SiteName = "shop.example.com",
            IncludeFiles = true,
            IncludeConfig = false,
            IncludeMail = false,
            TargetType = "local"
        };

        public void PanelVeritabaniUret()
        {
            var yol = DosyaDeposu.ResolvePath("data/adapanel.db");
            Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
            File.WriteAllText(yol, "SQLITE FORMAT 3 (test)");
        }

        public void ConfigDosyasiUret(string ad, string icerik)
        {
            var yol = DosyaDeposu.ResolvePath($"config/{ad}");
            Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
            File.WriteAllText(yol, icerik);
        }

        public void SiteDosyasiUret(string goreliAd, string icerik)
        {
            var yol = Path.Combine(SiteKoku, goreliAd.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
            File.WriteAllText(yol, icerik);
        }

        /// <summary>
        /// Dosyayi PAYLASIMSIZ acar: yedegin okuma denemesi <c>IOException</c> ile duser.
        /// Gercek sunucudaki "kilitli dosya" durumunun birebir karsiligidir; ACL degistirmek
        /// (yonetici yetkisi gerektiren) bir yol secilmedi.
        /// </summary>
        public FileStream Kilitle(string goreliYol) =>
            new(Path.Combine(Kok, goreliYol.Replace('/', Path.DirectorySeparatorChar)),
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);

        /// <summary>Uretilen arsivin GERCEK girdi adlari.</summary>
        public IReadOnlyCollection<string> ArsivGirdileri(BackupRecordDto kayit)
        {
            using var arsiv = ZipFile.OpenRead(kayit.Path);
            return arsiv.Entries.Select(girdi => girdi.FullName).ToArray();
        }

        public string Manifest(BackupRecordDto kayit)
        {
            using var arsiv = ZipFile.OpenRead(kayit.Path);
            var girdi = arsiv.GetEntry("manifest.json");
            if (girdi is null)
            {
                return string.Empty;
            }

            using var okuyucu = new StreamReader(girdi.Open());
            return okuyucu.ReadToEnd();
        }

        public void Dispose()
        {
            _iptal.Dispose();
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
