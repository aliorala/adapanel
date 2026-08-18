using System.IO.Compression;
using System.Net;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KAYNAK BASINA ALT KLASOR: <c>&lt;gun&gt;\&lt;kategori&gt;\&lt;kaynak&gt;</c> (0.6.1).
/// </summary>
/// <remarks>
/// <para>
/// ISTENEN DURUM: cok siteli bir sunucuda <c>2026-08-18\sites\</c> klasoru onlarca dosya oluyor ve
/// hangi dosyanin hangi siteye ait oldugu ancak DOSYA ADI okunarak anlasiliyordu. Kaynak klasoru
/// bunu gezinilebilir kilar. Sira bilincli: GUN &gt; KATEGORI &gt; KAYNAK -- gun en ustte kaldigi
/// icin "bir gunu komple tasi/sil" hala TEK islemdir.
/// </para>
/// <para>Bu testler duzenin kendisini degil KIRILGAN NOKTALARINI korur:</para>
/// <list type="number">
/// <item>yerel klasor ile UZAK yol AYNI kaynak seviyelerini uretir (tek kaynak),</item>
/// <item>kaynak adi KULLANICI VERISIDIR: dizin kacisi ne klasorde ne DOSYA ADINDA mumkun,</item>
/// <item>ESKI duzende (kaynak klasorsuz) alinmis yedek hala listelenir ve GERI YUKLENIR,</item>
/// <item>ESKI duzendeki uzak kopya budamada hala SILINIR (yol kayittan turer, hesaplanmaz),</item>
/// <item>artimsal zincirin halkalari ayni kaynak klasorunde toplanir ve zincir cozumu
///       yol degisiminden ETKILENMEZ,</item>
/// <item>SITEYE AIT bir veritabani <c>sites\&lt;site&gt;\databases\&lt;db&gt;</c> altina gider;
///       sahipsiz ya da sahipligi OKUNAMAYAN veritabani <c>databases\&lt;db&gt;</c> altinda kalir
///       (sessizce yanlis siteye KONMAZ).</item>
/// </list>
/// </remarks>
public sealed class BackupSourceFolderTests
{
    private const string Site = "shop.example.com";

    // ===========================================================================================
    // 1) TEK KAYNAK: yerel klasor ile uzak yol AYNI seviyeleri uretir
    // ===========================================================================================

    [Fact]
    public async Task Yerel_klasor_ve_uzak_yol_AYNI_kaynak_seviyelerini_uretir()
    {
        using var alan = new KaynakAlani();
        var kayitci = new IstekKayitcisi();
        var servis = alan.ServisKur(kayitci);

        var kayit = await servis.BackupSiteAsync(alan.SiteIstegi(), alan.Iptal);

        // (a) YEREL: <kok>\<gun>\sites\<site>
        Assert.Equal(
            Path.Combine(alan.YedekKoku, alan.Gun(kayit), "sites", Site),
            Path.GetDirectoryName(kayit.Path));

        // (b) UZAK yol YERELDEN OLCULEREK karsilastirilir, elle yazilmaz. Iki taraf ayri ayri
        // hesaplansaydi bir seviye eklemeyi/kaldirmayi unutmak ancak canli FTP'de fark edilirdi.
        Assert.Equal(alan.YerelSeviyeler(kayit), alan.UzakSeviyeler(kayit, "/dav/yedek"));

        // (c) Ara koleksiyonlar YUKARIDAN ASAGIYA acilir; kaynak seviyesi de aralarinda.
        var gun = alan.Gun(kayit);
        Assert.Equal(
            new[]
            {
                $"MKCOL https://bulut.ornek.com/dav/yedek/{gun}/",
                $"MKCOL https://bulut.ornek.com/dav/yedek/{gun}/sites/",
                $"MKCOL https://bulut.ornek.com/dav/yedek/{gun}/sites/{Site}/",
                $"PUT https://bulut.ornek.com/dav/yedek/{gun}/sites/{Site}/{kayit.Name}"
            },
            kayitci.Istekler);
    }

    // ===========================================================================================
    // 2) KAYNAK ADI KULLANICI VERISIDIR
    // ===========================================================================================

    [Fact]
    public async Task Kaynak_adindaki_dizin_kacisi_ne_klasorde_ne_DOSYA_ADINDA_gecerlidir()
    {
        // Kaynak adi (site adi) hem klasore hem dosya adina giriyor. Yalniz klasoru temizlemek
        // yetmezdi: Path.Combine, ayirici iceren bir DOSYA ADINI da yol olarak kabul eder ve
        // yedek hedef klasorun DISINA yazilirdi.
        using var alan = new KaynakAlani();
        var kayitci = new IstekKayitcisi();
        var servis = alan.ServisKur(kayitci);

        var istek = alan.SiteIstegi();
        istek.SiteName = @"..\..\Windows\System32";

        var kayit = await servis.BackupSiteAsync(istek, alan.Iptal);

        // (a) Dosya GERCEKTEN yedek kokunun altinda kaldi.
        var tam = Path.GetFullPath(kayit.Path);
        var kok = Path.GetFullPath(alan.YedekKoku) + Path.DirectorySeparatorChar;
        Assert.StartsWith(kok, tam, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(tam));

        // (b) Ne klasor ne dosya adi ayirici/".." tasiyor.
        Assert.DoesNotContain("..", kayit.Name, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, kayit.Name);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, kayit.Name);

        // (c) UZAK tarafta da kacis yok: MKCOL/PUT adreslerinde ".." segmenti bulunmaz.
        Assert.All(alan.UzakSeviyeler(kayit, "/dav/yedek"), seviye => Assert.NotEqual("..", seviye));
        Assert.DoesNotContain(kayitci.Istekler, istekMetni => istekMetni.Contains("/../", StringComparison.Ordinal));
    }

    [Fact]
    public void Temizlendiginde_bosalan_kaynak_SABIT_bir_ada_duser()
    {
        // Bos birakmak yedegi bir ust klasore (kategoriye) tasirdi: farkli kaynaklarin dosyalari
        // birbirine karisirdi. Kaynak adi asla "yok" olamaz.
        var yer = BackupLayout.Describe(@"C:\yedek", new DateTime(2026, 8, 18), "sites", " ... ");

        Assert.Equal(BackupLayout.FallbackSegment, yer.Source);
        Assert.Equal(new[] { "2026-08-18", "sites", BackupLayout.FallbackSegment }, yer.RemoteFolders);
    }

    [Fact]
    public void Kaynagi_TEK_olan_kategoriler_fazladan_seviye_ACMAZ()
    {
        // Panel ve sunucu geneli posta yedeginin kaynagi TEKTIR; "panel\panel\" gibi bir seviye
        // hicbir seyi ayirmaz, yalnizca bir tik daha derine gomer.
        Assert.Equal(new[] { "2026-08-18", "panel" }, BackupLayout.Describe(@"C:\y", new DateTime(2026, 8, 18), "panel").RemoteFolders);
        Assert.Equal(new[] { "2026-08-18", "mail" }, BackupLayout.Describe(@"C:\y", new DateTime(2026, 8, 18), "mail").RemoteFolders);
    }

    [Fact]
    public void Temizlik_yedek_klasoru_ile_geri_yukleme_klasorunde_AYNI_kuraldir()
    {
        // Iki ayri temizlik yazilsaydi biri duzeltilip digeri unutuldugunda "bir yerde engellenen,
        // obur yerde serbest" bir dizin kacisi kalirdi. Ayni girdi, ayni sonuc.
        var kacis = @"..\..\Windows\System32";
        var klasor = BackupLayout.Describe(@"C:\y", new DateTime(2026, 8, 18), "sites", kacis).Source;
        var geriYukleme = BackupService.BuildRestoreFolderName(new BackupRecordDto { Type = "site", Source = kacis });

        Assert.DoesNotContain("..", klasor, StringComparison.Ordinal);
        Assert.Contains($"-{klasor}-", geriYukleme, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // 3) ESKI YEDEKLER BOZULMAZ
    // ===========================================================================================

    [Fact]
    public async Task ESKI_duzende_alinmis_yedek_hala_listelenir_ve_GERI_YUKLENIR()
    {
        // GERIYE DONUK ZORUNLULUK: 0.6.0 ve oncesinde alinmis yedekler <gun>\sites\ altinda DUZ
        // duruyor (kaynak klasoru yok); daha eskiler dogrudan kokun altinda. Yol her kayitta TAM
        // saklandigi icin yeniden HESAPLANMAZ -- hesaplansaydi butun eski yedekler bir gecede
        // "dosyasi bulunamadi" olurdu.
        using var alan = new KaynakAlani();
        var servis = alan.ServisKur();

        var eskiYol = Path.Combine(alan.YedekKoku, "2026-01-01", "sites", "shop-20260101030000.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(eskiYol)!);
        alan.ZipUret(eskiYol, "files/eski.txt", "ESKI DUZENDEN GELEN ICERIK");

        await alan.KayitlariYazAsync(new BackupRecordDto
        {
            Id = "eski-duzen",
            Name = Path.GetFileName(eskiYol),
            Type = "site",
            Source = Site,
            Path = eskiYol,
            Format = "zip",
            Status = "created",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-200)
        });

        Assert.Contains(await servis.GetBackupsAsync(alan.Iptal), kayit => kayit.Id == "eski-duzen");

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = "eski-duzen" }, alan.Iptal);

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal(
            "ESKI DUZENDEN GELEN ICERIK",
            await File.ReadAllTextAsync(Path.Combine(alan.SiteKoku, "eski.txt"), alan.Iptal));
    }

    [Fact]
    public async Task Budama_ESKI_ve_YENI_duzendeki_uzak_kopyayi_KAYITTAKI_adresten_siler()
    {
        // Uzak silme yolu YENIDEN HESAPLANSAYDI (kaynak klasoru eklenerek), eski duzende yuklenmis
        // dosyalar uzakta SONSUZA KADAR kalir ve kimse fark etmezdi.
        using var alan = new KaynakAlani();
        var kayitci = new IstekKayitcisi();

        const string eskiDuzen = "https://bulut.ornek.com/dav/yedek/2026-01-01/sites/shop-20260101.zip";
        const string yeniDuzen = "https://bulut.ornek.com/dav/yedek/2026-02-01/sites/shop.example.com/shop-20260201.zip";

        await alan.KayitlariYazAsync(
            alan.UzakKayit("eski", eskiDuzen, gunOnce: 90),
            alan.UzakKayit("yeni", yeniDuzen, gunOnce: 60),
            alan.TazeKayit("taze"));

        var silinen = await alan.ServisKur(kayitci).PruneAsync(
            retentionDays: 7,
            remote: new BackupPruneRemoteOptions { UserName = "u", Password = "p" },
            cancellationToken: alan.Iptal);

        Assert.Equal(2, silinen);
        Assert.Equal(
            new[] { $"DELETE {yeniDuzen}", $"DELETE {eskiDuzen}" },
            kayitci.Istekler);
    }

    // ===========================================================================================
    // 4) ARTIMSAL ZINCIR
    // ===========================================================================================

    [Fact]
    public async Task Zincirin_halkalari_AYNI_kaynak_klasorunde_toplanir_ve_zincir_cozulur()
    {
        using var alan = new KaynakAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("a.txt", "silinecek");
        alan.DosyaYaz("b.txt", "eski");
        var tam = await servis.BackupSiteAsync(alan.SiteIstegi(artimsal: true), alan.Iptal);

        alan.DosyaYaz("b.txt", "yeni icerik daha uzun");
        alan.DosyaYaz("c.txt", "sonradan eklendi");
        File.Delete(Path.Combine(alan.SiteKoku, "a.txt"));
        var halka = await servis.BackupSiteAsync(alan.SiteIstegi(artimsal: true), alan.Iptal);

        // (a) Iki halka da AYNI klasorde: elle bakan biri zincirin tamamini tek yerde gorur.
        Assert.Equal("incremental", halka.BackupMode);
        Assert.Equal(tam.ChainId, halka.ChainId);
        Assert.Equal(Path.GetDirectoryName(tam.Path), Path.GetDirectoryName(halka.Path));
        Assert.EndsWith(Path.Combine("sites", Site), Path.GetDirectoryName(halka.Path)!, StringComparison.OrdinalIgnoreCase);

        // (b) ZINCIR COZUMU YOLDAN DEGIL KAYITTAN gider: temel halkanin dosyasini bambaska bir
        // klasore tasisak bile geri yukleme calismali.
        var tasinmis = Path.Combine(alan.Kok, "elle-tasindi.zip");
        File.Move(tam.Path, tasinmis);
        await alan.KayitlariDegistirAsync(kayitlar =>
        {
            var temel = kayitlar.Single(kayit => kayit.Id == tam.Id);
            temel.Path = tasinmis;
        });

        foreach (var dosya in Directory.EnumerateFiles(alan.SiteKoku))
        {
            File.Delete(dosya);
        }

        var sonuc = await alan.ServisKur().RestoreAsync(new RestoreBackupRequest { BackupId = halka.Id }, alan.Iptal);

        Assert.Equal("restored", sonuc.Status);
        Assert.Equal("yeni icerik daha uzun", await File.ReadAllTextAsync(Path.Combine(alan.SiteKoku, "b.txt"), alan.Iptal));
        Assert.True(File.Exists(Path.Combine(alan.SiteKoku, "c.txt")));
        Assert.False(File.Exists(Path.Combine(alan.SiteKoku, "a.txt")));
    }

    // ===========================================================================================
    // 5) SITEYE AIT VERITABANI SITENIN KLASORUNE GIRER
    // ===========================================================================================

    [Fact]
    public async Task Siteye_ait_veritabani_SITENIN_klasorune_girer_yerelde_ve_uzakta()
    {
        using var alan = new KaynakAlani();
        var kayitci = new IstekKayitcisi();
        var servis = alan.ServisKur(kayitci, sahiplik: new SahteSahiplikServisi(
            [("mysql", "shop_db", Guid.NewGuid(), Site)]));

        var kayit = await servis.BackupDatabaseAsync(alan.VeritabaniIstegi("mysql", "shop_db"), alan.Iptal);

        Assert.Equal(
            Path.Combine(alan.YedekKoku, alan.Gun(kayit), "sites", Site, "databases", "mysql-shop_db"),
            Path.GetDirectoryName(kayit.Path));

        // Uzak taraf yerelin AYNISI: karar tek yerde verilir.
        Assert.Equal(alan.YerelSeviyeler(kayit), alan.UzakSeviyeler(kayit, "/dav/yedek"));
    }

    [Fact]
    public async Task Hicbir_siteye_bagli_olmayan_veritabani_databases_altinda_KALIR()
    {
        using var alan = new KaynakAlani();
        var servis = alan.ServisKur(sahiplik: new SahteSahiplikServisi(
            // Kayit VAR ama baska bir veritabanina ait: ad benzerligiyle esleme yapilmadigi
            // boylece olculur.
            [("mysql", "baska_db", Guid.NewGuid(), Site)]));

        var kayit = await servis.BackupDatabaseAsync(alan.VeritabaniIstegi("mysql", "shop_db"), alan.Iptal);

        Assert.Equal(
            Path.Combine(alan.YedekKoku, alan.Gun(kayit), "databases", "mysql-shop_db"),
            Path.GetDirectoryName(kayit.Path));
    }

    [Fact]
    public async Task Siteye_bagli_ama_SITESIZ_kayit_veritabanini_bir_siteye_KOYMAZ()
    {
        // Sahiplik kaydi var, site alani bos (dogrudan olusturulmus, siteye baglanmamis DB).
        using var alan = new KaynakAlani();
        var servis = alan.ServisKur(sahiplik: new SahteSahiplikServisi(
            [("mysql", "shop_db", Guid.NewGuid(), string.Empty)]));

        var kayit = await servis.BackupDatabaseAsync(alan.VeritabaniIstegi("mysql", "shop_db"), alan.Iptal);

        Assert.Equal(
            Path.Combine(alan.YedekKoku, alan.Gun(kayit), "databases", "mysql-shop_db"),
            Path.GetDirectoryName(kayit.Path));
    }

    [Fact]
    public async Task Sahiplik_OKUNAMAZSA_yedek_alinir_ama_bir_siteye_KONMAZ()
    {
        // Sahiplik okunamiyorsa bilinen tek gercek "hangi siteye ait oldugunu bilmiyoruz"tur.
        // Yedegi hic almamak da, tahminle bir siteye koymak da yanlis olurdu.
        using var alan = new KaynakAlani();
        var servis = alan.ServisKur(sahiplik: new PatlayanSahiplikServisi());

        var kayit = await servis.BackupDatabaseAsync(alan.VeritabaniIstegi("mysql", "shop_db"), alan.Iptal);

        Assert.Equal(
            Path.Combine(alan.YedekKoku, alan.Gun(kayit), "databases", "mysql-shop_db"),
            Path.GetDirectoryName(kayit.Path));
        Assert.True(File.Exists(kayit.Path));
    }

    [Fact]
    public async Task Sahiplik_servisi_HIC_YOKSA_veritabani_yedegi_yine_alinir()
    {
        // Dar kapsamli kurulumlarda (ve eski DI kayitlarinda) bu servis bos olabilir; yedegin
        // buna bagli olmasi, tek bir istege bagli bagimlilik yuzunden yedeksiz kalmak olurdu.
        using var alan = new KaynakAlani();
        var servis = alan.ServisKur(sahiplik: null);

        var kayit = await servis.BackupDatabaseAsync(alan.VeritabaniIstegi("mysql", "shop_db"), alan.Iptal);

        Assert.Equal(
            Path.Combine(alan.YedekKoku, alan.Gun(kayit), "databases", "mysql-shop_db"),
            Path.GetDirectoryName(kayit.Path));
    }

    [Fact]
    public async Task Motorun_TAMAMININ_dokumu_hicbir_siteye_baglanmaz()
    {
        // Veritabani adi bos = motorun tamaminin dokumu. Bu, tanimi geregi tek bir siteye ait
        // olamaz; sahiplik hic sorgulanmamalidir.
        using var alan = new KaynakAlani();
        var sahiplik = new SahteSahiplikServisi([("mysql", "shop_db", Guid.NewGuid(), Site)]);
        var servis = alan.ServisKur(sahiplik: sahiplik);

        var kayit = await servis.BackupDatabaseAsync(alan.VeritabaniIstegi("mysql", string.Empty), alan.Iptal);

        Assert.Equal(
            Path.Combine(alan.YedekKoku, alan.Gun(kayit), "databases", "mysql-all"),
            Path.GetDirectoryName(kayit.Path));
        Assert.Equal(0, sahiplik.OkumaSayisi);
    }

    // ===========================================================================================
    // Yardimcilar
    // ===========================================================================================

    private sealed class KaynakAlani : IDisposable
    {
        private readonly CancellationTokenSource _iptal = new(TimeSpan.FromSeconds(60));

        public KaynakAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-kaynakklasor-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = Path.Combine(Kok, "wwwroot", "shop");
            Directory.CreateDirectory(SiteKoku);
            File.WriteAllText(Path.Combine(SiteKoku, "index.php"), "MUSTERI ICERIGI");
            YedekKoku = Path.Combine(Kok, "backups");
        }

        public string Kok { get; }

        public string SiteKoku { get; }

        public string YedekKoku { get; }

        public JsonFileStore DosyaDeposu { get; }

        /// <summary>Testler ASILI KALMAMALI.</summary>
        public CancellationToken Iptal => _iptal.Token;

        public BackupService ServisKur(IstekKayitcisi? kayitci = null, IDatabaseOwnershipService? sahiplik = null) =>
            new(new SahteSiteServisi(SiteKoku),
                new SahteVeritabaniServisi(),
                null!,
                new SahteTercihServisi(YedekKoku),
                null!,
                DosyaDeposu,
                new SahteIstemciFabrikasi(kayitci ?? new IstekKayitcisi()),
                encryptionStore: null,
                logger: null,
                customerScopes: null,
                databaseOwnership: sahiplik);

        public BackupSiteRequest SiteIstegi(bool artimsal = false) => new()
        {
            SiteName = Site,
            IncludeFiles = true,
            IncludeConfig = false,
            IncludeMail = false,
            Incremental = artimsal,
            FullBackupEveryDays = 30,
            TargetType = "webdav",
            RemoteUrl = "https://bulut.ornek.com/dav/yedek"
        };

        public BackupDatabaseRequest VeritabaniIstegi(string motor, string ad) => new()
        {
            Engine = motor,
            DatabaseName = ad,
            TargetType = "webdav",
            RemoteUrl = "https://bulut.ornek.com/dav/yedek"
        };

        public void DosyaYaz(string ad, string icerik) =>
            File.WriteAllText(Path.Combine(SiteKoku, ad), icerik);

        public void ZipUret(string yol, string girdi, string icerik)
        {
            using var akis = new FileStream(yol, FileMode.Create, FileAccess.Write);
            using var arsiv = new ZipArchive(akis, ZipArchiveMode.Create);
            using var yazici = new StreamWriter(arsiv.CreateEntry(girdi).Open());
            yazici.Write(icerik);
        }

        public Task KayitlariYazAsync(params BackupRecordDto[] kayitlar) =>
            DosyaDeposu.WriteAsync("config/backups.json", kayitlar, Iptal);

        public async Task KayitlariDegistirAsync(Action<List<BackupRecordDto>> degistir)
        {
            var kayitlar = (await DosyaDeposu.ReadAsync("config/backups.json", Array.Empty<BackupRecordDto>(), Iptal)).ToList();
            degistir(kayitlar);
            await DosyaDeposu.WriteAsync("config/backups.json", kayitlar, Iptal);
        }

        public BackupRecordDto UzakKayit(string id, string uzakAdres, int gunOnce)
        {
            var yol = Path.Combine(Kok, $"{id}.zip");
            File.WriteAllText(yol, "eski yedek");
            return new BackupRecordDto
            {
                Id = id,
                Name = $"{id}.zip",
                Type = "site",
                Source = Site,
                Path = yol,
                Format = "zip",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-gunOnce),
                TargetType = "webdav",
                RemoteUri = uzakAdres,
                Status = "created_remote_synced"
            };
        }

        /// <summary>
        /// Budamanin "kaynak korumasi" kurali: bir kaynagin GUNCEL yedegi yoksa eskisine
        /// dokunulmaz. Eski kayitlarin silinebilmesi icin ayni kaynaktan taze bir yedek gerekir.
        /// </summary>
        public BackupRecordDto TazeKayit(string id)
        {
            var yol = Path.Combine(Kok, $"{id}.zip");
            File.WriteAllText(yol, "taze yedek");
            return new BackupRecordDto
            {
                Id = id,
                Name = $"{id}.zip",
                Type = "site",
                Source = Site,
                Path = yol,
                Format = "zip",
                CreatedAtUtc = DateTime.UtcNow,
                TargetType = "local",
                Status = "created"
            };
        }

        /// <summary>
        /// Kaydin YEREL yolundan gun klasorunu ADIYLA bulur. Sabit sayida "Parent" ile yukari
        /// cikmak, derinlik degistiginde (siteye ait veritabaninda bir kat fazla) testi yanlis
        /// yere bakmaya iterdi.
        /// </summary>
        public string Gun(BackupRecordDto kayit) => YerelParcalar(kayit)[0];

        /// <summary>Yedek kokunun ALTINDAKI klasor seviyeleri (dosya adi haric).</summary>
        public string[] YerelSeviyeler(BackupRecordDto kayit) => YerelParcalar(kayit);

        /// <summary>Uzak adresin TABANIN altindaki klasor seviyeleri (dosya adi haric).</summary>
        public string[] UzakSeviyeler(BackupRecordDto kayit, string taban)
        {
            var yol = new Uri(kayit.RemoteUri).AbsolutePath;
            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var tabanParcalari = taban.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parcalar[tabanParcalari.Length..^1];
        }

        private string[] YerelParcalar(BackupRecordDto kayit)
        {
            var goreceli = Path.GetRelativePath(YedekKoku, Path.GetDirectoryName(kayit.Path)!);
            return goreceli.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        }

        public void Dispose()
        {
            _iptal.Dispose();
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>HTTP/WebDAV tarafinda hangi istegin hangi adrese gittigini kaydeder.</summary>
    private sealed class IstekKayitcisi
    {
        public List<string> Istekler { get; } = [];
    }

    private sealed class SahteIstemciFabrikasi(IstekKayitcisi kayitci) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new KayitEdenIsleyici(kayitci));
    }

    private sealed class KayitEdenIsleyici(IstekKayitcisi kayitci) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Govde GERCEKTEN okunur: tuketilmeyen bir akis, gercek sunucuda patlayan kodu yesil
            // gosterebilir.
            if (request.Content is not null)
            {
                await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            lock (kayitci.Istekler)
            {
                kayitci.Istekler.Add($"{request.Method.Method} {request.RequestUri}");
            }

            return new HttpResponseMessage(HttpStatusCode.Created) { RequestMessage = request };
        }
    }

    private sealed class SahteTercihServisi(string yedekKoku) : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SahteSiteServisi(string uygulamaKoku) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = uygulamaKoku,
                ApplicationRootPath = uygulamaKoku
            });

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

    /// <summary>Dokumu GERCEKTEN dosyaya yazar: yedegin nereye dustugu olculebilsin.</summary>
    private sealed class SahteVeritabaniServisi : IDatabaseService
    {
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default)
        {
            File.WriteAllText(request.FilePath, $"-- dump of {request.Engine}/{request.DatabaseName}");
            return Task.FromResult(new DatabaseDumpResultDto
            {
                Engine = request.Engine,
                DatabaseName = request.DatabaseName,
                FilePath = request.FilePath
            });
        }

        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

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

        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteSahiplikServisi(
        IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> kayitlar)
        : IDatabaseOwnershipService
    {
        public int OkumaSayisi { get; private set; }

        public Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            OkumaSayisi++;
            return Task.FromResult(kayitlar);
        }

        public Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Sahiplik dosyasi bozuk/erisilemez: yedek yine de alinmali.</summary>
    private sealed class PatlayanSahiplikServisi : IDatabaseOwnershipService
    {
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("db-ownership.json okunamadi");

        public Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
