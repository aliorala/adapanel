using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Subscriptions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ABONELIK yedeginin kapsam kurallari + zincir kararlari + plan kotasi.
/// <para>
/// Buradaki tek gercek risk KIRACI IZOLASYONUDUR: bir aboneligin arsivine baska bir musterinin
/// veritabani, posta alani ya da FTP hesabi girerse, o arsivi indiren musteri baskasinin verisini
/// eline gecirir. Ayni varsayim (ad benzerligine guvenmek) panelde daha once gercek bir veri
/// kaybina yol acmisti; karar bu yuzden olculebilir, saf bir fonksiyonda durur.
/// </para>
/// </summary>
public sealed class SubscriptionBackupPlanningTests
{
    private static readonly Guid Musteri = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BaskaMusteri = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void BuildPlan_IncludesOnlyResourcesOfThisSubscription()
    {
        var plan = SubscriptionBackupPlanner.BuildPlan(
            Musteri,
            scopeSites: ["shop.example.com", "blog.example.com"],
            allowedMailDomains: ["shop.example.com"],
            ownership:
            [
                ("mysql", "shop_db", Musteri, "shop.example.com"),      // kendi sitesine bagli
                ("mysql", "kisisel", Musteri, ""),                       // sitesiz ama SAHIBI bu abonelik
                ("mysql", "kurban_db", BaskaMusteri, "kurban.com"),      // BASKA aboneligin sitesi
                ("mysql", "yetim", BaskaMusteri, ""),                    // sitesiz, BASKA sahip
                ("mysql", "shop_db_yedek", Musteri, "kurban.com")        // sahibi biz gorunuyor AMA site baskasinin
            ],
            ftpUsers:
            [
                ("shop_ftp", "shop.example.com", @"C:\sites\shop"),
                ("kurban_ftp", "kurban.com", @"C:\sites\kurban"),
                ("sunucu_ftp", "", @"C:\ftp")
            ]);

        Assert.Equal(["blog.example.com", "shop.example.com"], plan.Sites);

        var dbAdlari = plan.Databases.Select(db => db.DatabaseName).OrderBy(ad => ad, StringComparer.Ordinal).ToArray();
        Assert.Equal(["kisisel", "shop_db"], dbAdlari);

        // Ad benzerligi ("shop_db_yedek") YETMEZ: site otoritedir.
        Assert.DoesNotContain(plan.Databases, db => db.DatabaseName == "shop_db_yedek");
        Assert.DoesNotContain(plan.Databases, db => db.DatabaseName == "kurban_db");
        Assert.DoesNotContain(plan.Databases, db => db.DatabaseName == "yetim");

        Assert.Equal(["shop.example.com"], plan.MailDomains);

        // Siteye bagli OLMAYAN sunucu geneli FTP hesabi abonelik yedegine girmez.
        Assert.Equal(["shop_ftp"], plan.FtpUsers.Select(user => user.UserName).ToArray());

        // 2 site + 2 veritabani + 1 posta alani + 1 FTP = 6 oge.
        Assert.Equal(6, plan.ItemCount);
    }

    [Fact]
    public void BuildPlan_EmptyScope_ProducesEmptyArchivePlan()
    {
        var plan = SubscriptionBackupPlanner.BuildPlan(
            Musteri,
            scopeSites: [],
            allowedMailDomains: [],
            ownership: [("mysql", "baskasi", BaskaMusteri, "kurban.com")],
            ftpUsers: [("kurban_ftp", "kurban.com", @"C:\sites\kurban")]);

        // Sitesi olmayan bir abonelik BOS yedek alir; baskasinin verisi "en azindan bir sey olsun"
        // diye ARSIVE KONMAZ.
        Assert.Empty(plan.Sites);
        Assert.Empty(plan.Databases);
        Assert.Empty(plan.FtpUsers);
        Assert.Equal(0, plan.ItemCount);
    }

    [Fact]
    public void OzetMetni_PartialFailure_SaysHowManyDidNotMakeIt()
    {
        var uyarilar = new[]
        {
            new BackupRestoreWarningDto { MessageKey = "database.restore_failed", Target = "mysql/shop", Detail = "mysql kapali" },
            new BackupRestoreWarningDto { MessageKey = "backup.site_unavailable", Target = "blog.example.com" }
        };

        var metin = SubscriptionBackupPlanner.OzetMetni(toplam: 8, basarili: 6, uyarilar);

        Assert.Contains("8 ogeden 6", metin, StringComparison.Ordinal);
        Assert.Contains("2 tanesi YUKLENMEDI", metin, StringComparison.Ordinal);
        Assert.Contains("mysql kapali", metin, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------
    // PLAN KOTASI: "gunde 2 kez yedek" satilabilir bir farktir.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Quota_Unlimited_KeepsRequestedInterval()
    {
        var sonuc = SubscriptionBackupQuota.Resolve(requestedIntervalHours: 1, maxBackupsPerDay: 0);

        Assert.Equal(1, sonuc.IntervalHours);
        Assert.False(sonuc.Throttled);
    }

    [Fact]
    public void Quota_TooFrequent_IsThrottledToPlanLimit()
    {
        // Plan gunde 2 yedege izin veriyor; kullanici saatlik istedi.
        var sonuc = SubscriptionBackupQuota.Resolve(requestedIntervalHours: 1, maxBackupsPerDay: 2);

        Assert.Equal(12, sonuc.IntervalHours);
        Assert.True(sonuc.Throttled);
    }

    [Fact]
    public void Quota_RoundsUp_SoTheDailyLimitCannotBeExceeded()
    {
        // 24/7 = 3,43 saat. Asagi yuvarlansaydi (3 saat) gunde 8 tur olur ve kota DELINIRDI.
        var sonuc = SubscriptionBackupQuota.Resolve(requestedIntervalHours: 1, maxBackupsPerDay: 7);

        Assert.Equal(4, sonuc.IntervalHours);
        Assert.True(sonuc.Throttled);
    }

    [Fact]
    public void Quota_WithinLimit_IsNotTouched()
    {
        var sonuc = SubscriptionBackupQuota.Resolve(requestedIntervalHours: 24, maxBackupsPerDay: 2);

        Assert.Equal(24, sonuc.IntervalHours);
        Assert.False(sonuc.Throttled);
    }

    // ------------------------------------------------------------------------------------
    // ZINCIR KURALLARI (saf).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Diff_WithoutPreviousIndex_TreatsEverythingAsChanged()
    {
        var simdi = Indeks(("files/a.txt", 10, 100), ("files/b.txt", 20, 200));

        var fark = BackupChainPlanner.Diff(null, simdi);

        // Temeli BILMIYORSAK her seyi aliriz: aksi halde geri yuklenemeyen bir halka uretilirdi.
        Assert.Equal(2, fark.Changed.Count);
        Assert.Empty(fark.Deleted);
    }

    [Fact]
    public void Diff_DetectsNewChangedAndDeleted()
    {
        var onceki = Indeks(("files/a.txt", 10, 100), ("files/b.txt", 20, 200), ("files/c.txt", 30, 300));
        var simdi = Indeks(("files/b.txt", 20, 999), ("files/c.txt", 31, 300), ("files/d.txt", 5, 5));

        var fark = BackupChainPlanner.Diff(onceki, simdi);

        Assert.Contains("files/b.txt", fark.Changed); // tarih degisti
        Assert.Contains("files/c.txt", fark.Changed); // boyut degisti
        Assert.Contains("files/d.txt", fark.Changed); // yeni
        Assert.Equal(3, fark.Changed.Count);
        Assert.Equal(["files/a.txt"], fark.Deleted);
    }

    [Fact]
    public void Diff_UnchangedFile_IsNotCarried()
    {
        var onceki = Indeks(("files/a.txt", 10, 100));
        var simdi = Indeks(("files/a.txt", 10, 100));

        var fark = BackupChainPlanner.Diff(onceki, simdi);

        Assert.Empty(fark.Changed);
        Assert.Empty(fark.Deleted);
    }

    [Fact]
    public void Resolve_MissingMiddleLink_MarksChainBrokenAndNamesIt()
    {
        var zincir = "z1";
        var kayitlar = new[]
        {
            Kayit("tam", zincir, 0, "full"),
            Kayit("inc2", zincir, 2, "incremental")
            // sira 1 KAYIP
        };

        var sonuc = BackupChainPlanner.Resolve(kayitlar, "inc2", _ => true);

        Assert.True(sonuc.Broken);
        Assert.Contains("#1", sonuc.MissingLinks);
    }

    [Fact]
    public void Resolve_LinkFileGone_MarksChainBroken()
    {
        var zincir = "z1";
        var kayitlar = new[]
        {
            Kayit("tam", zincir, 0, "full"),
            Kayit("inc1", zincir, 1, "incremental")
        };

        // Kayit duruyor ama DOSYASI yok: kayda guvenip "saglam" demek, olculmemis bir seye
        // guvenmek olurdu.
        var sonuc = BackupChainPlanner.Resolve(kayitlar, "inc1", kayit => kayit.Id != "tam");

        Assert.True(sonuc.Broken);
        Assert.Contains(sonuc.MissingLinks, ad => ad.Contains("tam", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_ChainlessRecord_IsAValidStandaloneRestorePoint()
    {
        var kayitlar = new[] { Kayit("tek", string.Empty, 0, "full") };

        var sonuc = BackupChainPlanner.Resolve(kayitlar, "tek", _ => true);

        Assert.False(sonuc.Broken);
        Assert.Single(sonuc.Links);
    }

    [Fact]
    public void GetDependents_ReturnsEverythingAfterTheGivenLink()
    {
        var zincir = "z1";
        var kayitlar = new[]
        {
            Kayit("tam", zincir, 0, "full"),
            Kayit("inc1", zincir, 1, "incremental"),
            Kayit("inc2", zincir, 2, "incremental"),
            Kayit("baska", "z2", 0, "full")
        };

        var bagimlilar = BackupChainPlanner.GetDependents(kayitlar, kayitlar[0]);

        Assert.Equal(["inc1", "inc2"], bagimlilar.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void IsChainPrunable_OnlyWhenEvenTheNewestLinkExpired()
    {
        var cutoff = DateTime.UtcNow.AddDays(-14);
        var zincir = new[]
        {
            Kayit("tam", "z1", 0, "full", DateTime.UtcNow.AddDays(-40)),
            Kayit("inc1", "z1", 1, "incremental", DateTime.UtcNow.AddDays(-1))
        };

        Assert.False(BackupChainPlanner.IsChainPrunable(zincir, cutoff));

        var eskiZincir = new[]
        {
            Kayit("tam", "z2", 0, "full", DateTime.UtcNow.AddDays(-40)),
            Kayit("inc1", "z2", 1, "incremental", DateTime.UtcNow.AddDays(-30))
        };

        Assert.True(BackupChainPlanner.IsChainPrunable(eskiZincir, cutoff));
    }

    [Fact]
    public void TamYedekGerekli_WhenBaseIndexCannotBeRead()
    {
        var son = Kayit("inc1", "z1", 1, "incremental");

        // Neyin degistigini OLCEMIYORSAK artimsal uretmek, geri yuklenemeyen bir halka uretmektir.
        Assert.True(BackupChainPlanner.TamYedekGerekli(son, DateTime.UtcNow, 7, temelIndeksOkunabildi: false, DateTime.UtcNow));

        Assert.False(BackupChainPlanner.TamYedekGerekli(son, DateTime.UtcNow, 7, temelIndeksOkunabildi: true, DateTime.UtcNow));
    }

    [Fact]
    public void TamYedekGerekli_WhenFullBackupPeriodExpired()
    {
        var son = Kayit("inc1", "z1", 1, "incremental");
        var tamZamani = DateTime.UtcNow.AddDays(-8);

        Assert.True(BackupChainPlanner.TamYedekGerekli(son, tamZamani, 7, true, DateTime.UtcNow));
        Assert.False(BackupChainPlanner.TamYedekGerekli(son, tamZamani, 30, true, DateTime.UtcNow));
    }

    [Fact]
    public void TamYedekGerekli_WhenThereIsNoChainYet()
    {
        Assert.True(BackupChainPlanner.TamYedekGerekli(null, null, 7, true, DateTime.UtcNow));
    }

    private static BackupFileIndex Indeks(params (string Path, long Size, long Ticks)[] girdiler) => new()
    {
        Entries = girdiler
            .Select(girdi => new BackupFileIndexEntry { Path = girdi.Path, Size = girdi.Size, ModifiedUtcTicks = girdi.Ticks })
            .ToList()
    };

    private static BackupRecordDto Kayit(string id, string zincir, int sira, string mod, DateTime? zaman = null) => new()
    {
        Id = id,
        Name = $"{id}.zip",
        Type = "site",
        Source = "shop",
        Path = $@"C:\yedek\{id}.zip",
        Format = "zip",
        ChainId = zincir,
        ChainIndex = sira,
        BackupMode = mod,
        CreatedAtUtc = zaman ?? DateTime.UtcNow
    };
}
