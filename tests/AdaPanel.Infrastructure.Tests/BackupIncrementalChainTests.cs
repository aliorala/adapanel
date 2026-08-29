using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ARTIMSAL (incremental) yedek zinciri: gercek zip dosyalari uzerinde.
/// <para>
/// Zincir, panelin veri kaybina en yakin yapisidir: bir halka giderse ondan SONRAKI butun
/// halkalar geri yuklenemez hale gelir ama panelde "yedek" olarak gorunmeye devam eder.
/// Kullanici bunu ancak felaket aninda ogrenir. Burada olculen kurallar sunlardir:
/// </para>
/// <list type="number">
/// <item>artimsal halka yalniz DEGISENI tasir, silinenleri de kaydeder;</item>
/// <item>geri yukleme halkalari DOGRU SIRAYLA uygular ve silmeleri de uygular;</item>
/// <item>kopuk zincir geri yuklenmez, eksik halkanin ADI soylenir ve canli icerige DOKUNULMAZ;</item>
/// <item>bagimli halkasi olan yedek tek basina silinemez;</item>
/// <item>budama, zincirin ORTASINDAKI tam yedegi silip sonrakileri cope ceviremez.</item>
/// </list>
/// </summary>
public sealed class BackupIncrementalChainTests
{
    [Fact]
    public async Task Incremental_SecondLink_CarriesOnlyChangedFilesAndRecordsDeletions()
    {
        using var alan = new ZincirAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("a.txt", "birinci");
        alan.DosyaYaz("b.txt", "eski");

        var tam = await servis.BackupSiteAsync(alan.Istek());
        Assert.Equal("full", tam.BackupMode);
        Assert.Equal(0, tam.ChainIndex);
        Assert.False(string.IsNullOrWhiteSpace(tam.ChainId));

        // b degisti (boyut da degisti), c eklendi, a silindi.
        alan.DosyaYaz("b.txt", "yeni icerik daha uzun");
        alan.DosyaYaz("c.txt", "ucuncu");
        File.Delete(Path.Combine(alan.SiteKoku, "a.txt"));

        var artimsal = await servis.BackupSiteAsync(alan.Istek());

        Assert.Equal("incremental", artimsal.BackupMode);
        Assert.Equal(1, artimsal.ChainIndex);
        Assert.Equal(tam.ChainId, artimsal.ChainId);
        Assert.Equal(tam.Id, artimsal.BaseBackupId);

        using var arsiv = ZipFile.OpenRead(artimsal.Path);
        var dosyalar = arsiv.Entries
            .Select(entry => entry.FullName)
            .Where(ad => ad.StartsWith("files/", StringComparison.Ordinal))
            .OrderBy(ad => ad, StringComparer.Ordinal)
            .ToArray();

        // DEGISMEYEN a.txt zaten silindi; DEGISMEYEN bir dosya olsaydi da arsive girmemeliydi.
        Assert.Equal(["files/b.txt", "files/c.txt"], dosyalar);

        var silinenGirdi = arsiv.GetEntry("deleted.json");
        Assert.NotNull(silinenGirdi);
        using var okuyucu = new StreamReader(silinenGirdi!.Open());
        Assert.Contains("files/a.txt", await okuyucu.ReadToEndAsync());
    }

    [Fact]
    public async Task Incremental_UnchangedFile_IsNotCarriedAgain()
    {
        using var alan = new ZincirAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("sabit.txt", "hic degismeyecek");
        alan.DosyaYaz("degisken.txt", "ilk");

        await servis.BackupSiteAsync(alan.Istek());
        alan.DosyaYaz("degisken.txt", "ikinci deger daha uzun");

        var artimsal = await servis.BackupSiteAsync(alan.Istek());

        using var arsiv = ZipFile.OpenRead(artimsal.Path);
        Assert.Null(arsiv.GetEntry("files/sabit.txt"));
        Assert.NotNull(arsiv.GetEntry("files/degisken.txt"));
    }

    [Fact]
    public async Task RestoreChain_AppliesLinksInOrderAndAppliesDeletions()
    {
        using var alan = new ZincirAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("a.txt", "silinecek");
        alan.DosyaYaz("b.txt", "eski");
        await servis.BackupSiteAsync(alan.Istek());

        alan.DosyaYaz("b.txt", "yeni icerik daha uzun");
        alan.DosyaYaz("c.txt", "sonradan eklendi");
        File.Delete(Path.Combine(alan.SiteKoku, "a.txt"));
        var artimsal = await servis.BackupSiteAsync(alan.Istek());

        // Siteyi bambaska bir hale getir: geri yukleme gercekten calismali.
        foreach (var dosya in Directory.EnumerateFiles(alan.SiteKoku))
        {
            File.Delete(dosya);
        }

        alan.DosyaYaz("bozuk.txt", "geri yuklemeden once buradaydi");

        var sonuc = await servis.RestoreAsync(new RestoreBackupRequest { BackupId = artimsal.Id });

        Assert.Equal("restored", sonuc.Status);
        Assert.Empty(sonuc.Warnings);

        // 1) SIRA: b.txt tam yedekteki ESKI degil, artimsal halkadaki YENI icerik olmali.
        Assert.Equal("yeni icerik daha uzun", await File.ReadAllTextAsync(Path.Combine(alan.SiteKoku, "b.txt")));

        // 2) Artimsal halkada eklenen dosya geldi.
        Assert.Equal("sonradan eklendi", await File.ReadAllTextAsync(Path.Combine(alan.SiteKoku, "c.txt")));

        // 3) SILME UYGULANDI: tam yedekte VAR olan a.txt geri gelmemeli. Aksi halde kullanicinin
        //    bilerek kaldirdigi bir dosya (temizlenmis zararli eklenti) geri yuklemeyle geri gelirdi.
        Assert.False(File.Exists(Path.Combine(alan.SiteKoku, "a.txt")));

        // 4) Uzerine yazmadan once canli icerik YANA ALINDI.
        Assert.False(string.IsNullOrWhiteSpace(sonuc.PreservedPath));
        Assert.True(File.Exists(Path.Combine(sonuc.PreservedPath, "bozuk.txt")));
    }

    [Fact]
    public async Task Restore_WhenChainLinkIsMissing_RefusesAndNamesTheMissingLink()
    {
        using var alan = new ZincirAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("a.txt", "ilk");
        var tam = await servis.BackupSiteAsync(alan.Istek());

        alan.DosyaYaz("a.txt", "ikinci deger daha uzun");
        var artimsal = await servis.BackupSiteAsync(alan.Istek());

        // Tam yedegin DOSYASI kayboldu (elle silinmis / disk temizligi). Kayit duruyor.
        File.Delete(tam.Path);

        alan.DosyaYaz("canli.txt", "geri yukleme buna dokunmamali");

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            servis.RestoreAsync(new RestoreBackupRequest { BackupId = artimsal.Id }));

        Assert.Equal("backup.chain_broken", hata.MessageKey);
        Assert.Contains(tam.Name, hata.Details, StringComparison.Ordinal);

        // CANLI ICERIGE DOKUNULMADI: "elimizdekiyle idare edelim" demek, sitenin hangi
        // dosyalarinin eski kaldigini kimsenin bilmemesi olurdu.
        Assert.True(File.Exists(Path.Combine(alan.SiteKoku, "canli.txt")));
    }

    [Fact]
    public async Task Delete_LinkWithDependents_IsRefusedAndKeepsTheFile()
    {
        using var alan = new ZincirAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("a.txt", "ilk");
        var tam = await servis.BackupSiteAsync(alan.Istek());
        alan.DosyaYaz("a.txt", "ikinci deger daha uzun");
        var artimsal = await servis.BackupSiteAsync(alan.Istek());

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.DeleteBackupAsync(tam.Id));

        Assert.Equal("backup.chain_has_dependents", hata.MessageKey);
        Assert.Contains(artimsal.Name, hata.Details, StringComparison.Ordinal);

        // Dosya ve kayit YERINDE: reddedilen bir silme hicbir sey silmemeli.
        Assert.True(File.Exists(tam.Path));
        Assert.Equal(2, (await servis.GetBackupsAsync()).Count);
    }

    [Fact]
    public async Task DeleteChain_RemovesTheLinkAndEverythingAfterIt()
    {
        using var alan = new ZincirAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("a.txt", "ilk");
        var tam = await servis.BackupSiteAsync(alan.Istek());
        alan.DosyaYaz("a.txt", "ikinci deger daha uzun");
        var artimsal = await servis.BackupSiteAsync(alan.Istek());

        var sonuc = await servis.DeleteBackupChainAsync(tam.Id);

        Assert.Equal(2, sonuc.DeletedIds.Length);
        Assert.Contains(artimsal.Id, sonuc.DeletedIds);
        Assert.False(File.Exists(tam.Path));
        Assert.False(File.Exists(artimsal.Path));
        Assert.Empty(await servis.GetBackupsAsync());
    }

    [Fact]
    public async Task GetChain_ReportsLinksInOrder()
    {
        using var alan = new ZincirAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("a.txt", "ilk");
        var tam = await servis.BackupSiteAsync(alan.Istek());
        alan.DosyaYaz("a.txt", "ikinci deger daha uzun");
        var artimsal = await servis.BackupSiteAsync(alan.Istek());

        var zincir = await servis.GetChainAsync(artimsal.Id);

        Assert.False(zincir.Broken);
        Assert.Equal(2, zincir.Links.Length);
        Assert.Equal(tam.Id, zincir.Links[0].BackupId);
        Assert.Equal("full", zincir.Links[0].BackupMode);
        Assert.Equal(artimsal.Id, zincir.Links[1].BackupId);
        Assert.Equal("incremental", zincir.Links[1].BackupMode);
        Assert.All(zincir.Links, halka => Assert.True(halka.Available));
    }

    [Fact]
    public async Task FullBackupPeriod_Expired_StartsANewChain()
    {
        using var alan = new ZincirAlani();
        var servis = alan.ServisKur();

        alan.DosyaYaz("a.txt", "ilk");
        var tam = await servis.BackupSiteAsync(alan.Istek());

        // Zincirin tam yedegi 30 gun oncesine cekilir: periyot dolmustur.
        await alan.KayitlariDegistirAsync(kayitlar =>
        {
            foreach (var kayit in kayitlar)
            {
                kayit.CreatedAtUtc = DateTime.UtcNow.AddDays(-30);
            }
        });

        alan.DosyaYaz("a.txt", "ikinci deger daha uzun");
        var ikinci = await servis.BackupSiteAsync(alan.Istek(tamYedekPeriyodu: 7));

        // YENI ZINCIR: zincir sonsuza kadar uzasaydi geri yukleme onlarca halkaya bagimli olurdu
        // ve tek bir bozuk halka her seyi goturebilirdi.
        Assert.Equal("full", ikinci.BackupMode);
        Assert.Equal(0, ikinci.ChainIndex);
        Assert.NotEqual(tam.ChainId, ikinci.ChainId);
    }

    // ------------------------------------------------------------------------------------
    // BUDAMA: zincirin ORTASINDAKI tam yedegi silmek sonraki artimsallari cope cevirir.
    // Bu testler kayitlari dogrudan yazar (gercek arsiv uretmeye gerek yok).
    // ------------------------------------------------------------------------------------

    [Fact]
    public async Task Prune_DoesNotRemoveExpiredFullBackupWhileItsChainIsStillFresh()
    {
        using var alan = new ZincirAlani();

        var zincir = Guid.NewGuid().ToString("N");
        var tam = alan.KayitUret("shop", gunOnce: 40, zincir: zincir, sira: 0, mod: "full");
        var artimsal = alan.KayitUret("shop", gunOnce: 1, zincir: zincir, sira: 1, mod: "incremental");

        var servis = alan.ServisKur(tam, artimsal);
        var silinen = await servis.PruneAsync(retentionDays: 14);

        // 40 gunluk tam yedek saklama suresinden eski AMA zincirin son halkasi dun alindi.
        // Tam yedegi silmek, dunku artimsal halkayi geri yuklenemez hale getirirdi.
        Assert.Equal(0, silinen.Removed);
        Assert.True(File.Exists(tam.Path));
        Assert.True(File.Exists(artimsal.Path));
    }

    [Fact]
    public async Task Prune_RemovesWholeChainOnceEvenItsNewestLinkExpired()
    {
        using var alan = new ZincirAlani();

        var zincir = Guid.NewGuid().ToString("N");
        var tam = alan.KayitUret("shop", gunOnce: 40, zincir: zincir, sira: 0, mod: "full");
        var artimsal = alan.KayitUret("shop", gunOnce: 30, zincir: zincir, sira: 1, mod: "incremental");
        // Kaynak korumasi icin GUNCEL bir yedek de olmali (yoksa hicbir sey budanmaz).
        var taze = alan.KayitUret("shop", gunOnce: 1);

        var servis = alan.ServisKur(tam, artimsal, taze);
        var silinen = await servis.PruneAsync(retentionDays: 14);

        Assert.Equal(2, silinen.Removed);
        Assert.False(File.Exists(tam.Path));
        Assert.False(File.Exists(artimsal.Path));
        Assert.True(File.Exists(taze.Path));
    }

    [Fact]
    public async Task PruneSubscription_OnlyTouchesItsOwnSubscription()
    {
        using var alan = new ZincirAlani();

        var benim = Guid.NewGuid();
        var baskasi = Guid.NewGuid();

        var benimEski = alan.KayitUret(benim.ToString("N"), gunOnce: 40, tur: "subscription", abonelik: benim.ToString("N"));
        var benimTaze = alan.KayitUret(benim.ToString("N"), gunOnce: 1, tur: "subscription", abonelik: benim.ToString("N"));
        var baskasininEski = alan.KayitUret(baskasi.ToString("N"), gunOnce: 40, tur: "subscription", abonelik: baskasi.ToString("N"));
        var baskasininTaze = alan.KayitUret(baskasi.ToString("N"), gunOnce: 1, tur: "subscription", abonelik: baskasi.ToString("N"));

        var servis = alan.ServisKur(benimEski, benimTaze, baskasininEski, baskasininTaze);
        var silinen = await servis.PruneSubscriptionAsync(benim, retentionDays: 14, keepCount: 0);

        // KIRACI IZOLASYONU: bir musterinin saklama ayari baska bir musterinin yedegini SILEMEZ.
        Assert.Equal(1, silinen.Removed);
        Assert.False(File.Exists(benimEski.Path));
        Assert.True(File.Exists(baskasininEski.Path));
        Assert.True(File.Exists(baskasininTaze.Path));
    }

    [Fact]
    public async Task PruneSubscription_KeepCount_KeepsNewestAndDropsTheRest()
    {
        using var alan = new ZincirAlani();
        var kullanici = Guid.NewGuid();
        var kimlik = kullanici.ToString("N");

        var kayitlar = Enumerable.Range(1, 5)
            .Select(gun => alan.KayitUret(kimlik, gunOnce: gun, tur: "subscription", abonelik: kimlik))
            .ToArray();

        var servis = alan.ServisKur(kayitlar);
        // Sure limiti YOK (0), yalniz sayi limiti: en yeni 2 kalir.
        var silinen = await servis.PruneSubscriptionAsync(kullanici, retentionDays: 0, keepCount: 2);

        Assert.Equal(3, silinen.Removed);
        var kalan = await servis.GetBackupsAsync();
        Assert.Equal(2, kalan.Count);
        Assert.All(kalan, kayit => Assert.True(kayit.CreatedAtUtc > DateTime.UtcNow.AddDays(-3)));
    }

    // ------------------------------------------------------------------------------------

    private sealed class ZincirAlani : IDisposable
    {
        private readonly string _yedekKlasoru;

        public ZincirAlani()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-zincir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
            DosyaDeposu = new JsonFileStore(
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
            SiteKoku = Path.Combine(Kok, "wwwroot", "shop");
            Directory.CreateDirectory(SiteKoku);
            _yedekKlasoru = DosyaDeposu.EnsureDirectory("kayitli-yedekler");
        }

        public string Kok { get; }

        public string SiteKoku { get; }

        public JsonFileStore DosyaDeposu { get; }

        public void DosyaYaz(string ad, string icerik) =>
            File.WriteAllText(Path.Combine(SiteKoku, ad), icerik);

        public BackupSiteRequest Istek(int tamYedekPeriyodu = 30) => new()
        {
            SiteName = "shop",
            IncludeFiles = true,
            IncludeConfig = false,
            IncludeMail = false,
            Incremental = true,
            FullBackupEveryDays = tamYedekPeriyodu
        };

        public BackupService ServisKur(params BackupRecordDto[] kayitlar)
        {
            if (kayitlar.Length > 0)
            {
                DosyaDeposu.WriteAsync("config/backups.json", kayitlar).GetAwaiter().GetResult();
            }

            return new BackupService(
                new ZincirSiteServisi(SiteKoku),
                null!,
                null!,
                new ZincirTercihServisi(Path.Combine(Kok, "backups")),
                null!,
                DosyaDeposu,
                null!);
        }

        public async Task KayitlariDegistirAsync(Action<List<BackupRecordDto>> degistir)
        {
            var kayitlar = (await DosyaDeposu.ReadAsync("config/backups.json", Array.Empty<BackupRecordDto>())).ToList();
            degistir(kayitlar);
            await DosyaDeposu.WriteAsync("config/backups.json", kayitlar);
        }

        public BackupRecordDto KayitUret(
            string kaynak,
            int gunOnce,
            string tur = "site",
            string zincir = "",
            int sira = 0,
            string mod = "full",
            string abonelik = "")
        {
            var id = Guid.NewGuid().ToString("N");
            var yol = Path.Combine(_yedekKlasoru, $"{id}.zip");
            File.WriteAllText(yol, "yedek");
            return new BackupRecordDto
            {
                Id = id,
                Name = Path.GetFileName(yol),
                Type = tur,
                Source = kaynak,
                Path = yol,
                Format = "zip",
                Status = "created",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-gunOnce),
                ChainId = zincir,
                ChainIndex = sira,
                BackupMode = mod,
                SubscriptionUserId = abonelik
            };
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ZincirTercihServisi(string yedekKoku) : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ZincirSiteServisi(string uygulamaKoku) : IIisSiteService
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
}
