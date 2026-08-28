using System.Text;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Denetim kaydının OKUNMASI: kapsam izolasyonu, filtre/sayfalama, CSV dışa aktarma ve saklama politikası.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu testler:</b> panel denetim kaydını topluyordu ama hiçbir yerde göstermiyordu. Ekran
/// açılırken en büyük risk, kaydın YANLIŞ KİŞİYE gösterilmesidir: bir kiracının başka bir
/// kiracının eylem geçmişini (hangi alan adları, hangi posta adresleri, hangi dosya yolları)
/// görmesi, panelin bugüne kadar kapattığı bütün çapraz-kiracı açıklarını okuma yönünden geri
/// açardı. Bu yüzden izolasyon burada davranışsal olarak kanıtlanır.
/// </para>
/// </remarks>
public sealed class AuditLogReadTests : IDisposable
{
    private static readonly Guid Yonetici = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MusteriA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MusteriB = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AltKullaniciA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Bayi = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection baglanti;
    private readonly AdaPanelDbContext dbContext;
    private readonly string veriKoku;
    private readonly SahteKapsam kapsam = new();
    private readonly AuditService servis;

    public AuditLogReadTests()
    {
        baglanti = new SqliteConnection("Filename=:memory:");
        baglanti.Open();
        dbContext = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options);
        dbContext.Database.EnsureCreated();

        KullaniciEkle(Yonetici, "yonetici", UserRole.Administrator);
        KullaniciEkle(MusteriA, "musteri-a", UserRole.Customer);
        KullaniciEkle(MusteriB, "musteri-b", UserRole.Customer);
        KullaniciEkle(AltKullaniciA, "alt-a", UserRole.Customer);
        KullaniciEkle(Bayi, "bayi", UserRole.Reseller);
        dbContext.SaveChanges();

        // Kiracı grafiği: A'nın bir alt kullanıcısı var; bayi yalnız A'ya sahip (B başkasının).
        kapsam.AltKullanicilar[MusteriA] = [AltKullaniciA];
        kapsam.Ebeveynler[AltKullaniciA] = MusteriA;
        kapsam.SahipOlunanlar[Bayi] = [MusteriA];

        veriKoku = Path.Combine(Path.GetTempPath(), "adapanel-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(veriKoku);

        servis = new AuditService(
            dbContext,
            kapsam,
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku })));
    }

    public void Dispose()
    {
        dbContext.Dispose();
        baglanti.Dispose();

        try
        {
            Directory.Delete(veriKoku, recursive: true);
        }
        catch
        {
            // Geçici klasör temizliği en iyi çaba; testin sonucunu etkilemez.
        }
    }

    // ------------------------------------------------------------------
    // KAPSAM İZOLASYONU
    // ------------------------------------------------------------------

    [Fact]
    public async Task Bir_kiraci_DIGER_kiracinin_kaydini_GOREMEZ()
    {
        await KayitYazAsync(MusteriA, "mail.account.created", "info@a.test");
        await KayitYazAsync(MusteriB, "mail.account.created", "info@b.test");
        await KayitYazAsync(null, "system.startup", "panel");

        var gorunurluk = await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer);
        var sonuc = await servis.SearchAsync(new AuditLogQuery(), gorunurluk);

        Assert.Equal(1, sonuc.TotalCount);
        Assert.Equal("info@a.test", Assert.Single(sonuc.Items).Resource);
        Assert.DoesNotContain(sonuc.Items, item => item.Resource.Contains("b.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Kiraci_FILTREYLE_de_digerinin_kaydina_ULASAMAZ()
    {
        await KayitYazAsync(MusteriB, "mail.account.password_changed", "ceo@b.test");

        var gorunurluk = await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer);

        // Kapsam, ölçütlerden ÖNCE ve ayrı uygulanır; hiçbir arama terimi onu genişletemez.
        var sonuc = await servis.SearchAsync(new AuditLogQuery { Search = "b.test", UserName = "musteri-b" }, gorunurluk);

        Assert.Equal(0, sonuc.TotalCount);
        Assert.Empty(sonuc.Items);
    }

    [Fact]
    public async Task CSV_disa_aktarma_da_ayni_kapsami_uygular()
    {
        await KayitYazAsync(MusteriA, "files.deleted", "sites/a.test/index.php");
        await KayitYazAsync(MusteriB, "files.deleted", "sites/b.test/index.php");

        var gorunurluk = await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer);

        var satirlar = new List<AuditLogDto>();
        await foreach (var item in servis.StreamAsync(new AuditLogQuery(), gorunurluk))
        {
            satirlar.Add(item);
        }

        // Dışa aktarma AYRI bir yol olsaydı kapsamı uygulamayı unutmak mümkün olurdu; aynı
        // filtreyi kullandığı burada kanıtlanır.
        Assert.Equal("sites/a.test/index.php", Assert.Single(satirlar).Resource);
    }

    [Fact]
    public async Task Alt_kullanici_ile_abonelik_sahibi_AYNI_izi_gorur()
    {
        await KayitYazAsync(MusteriA, "files.written", "sites/a.test/wp-config.php");
        await KayitYazAsync(AltKullaniciA, "files.deleted", "sites/a.test/eski.php");
        await KayitYazAsync(MusteriB, "files.deleted", "sites/b.test/eski.php");

        var ebeveyn = await servis.SearchAsync(new AuditLogQuery(), await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer));
        var alt = await servis.SearchAsync(new AuditLogQuery(), await servis.ResolveVisibilityAsync(AltKullaniciA, UserRole.Customer));

        // Plesk davranışı: parent ve alt kullanıcılar aynı aboneliği paylaşır → aynı geçmişi görür.
        Assert.Equal(2, ebeveyn.TotalCount);
        Assert.Equal(2, alt.TotalCount);
        Assert.DoesNotContain(alt.Items, item => item.Resource.Contains("b.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bayi_yalniz_KENDI_musterilerinin_kaydini_gorur()
    {
        await KayitYazAsync(MusteriA, "ftp.user.created", "a-ftp");
        await KayitYazAsync(AltKullaniciA, "ftp.user.deleted", "a-ftp2");
        await KayitYazAsync(MusteriB, "ftp.user.created", "b-ftp");

        var sonuc = await servis.SearchAsync(new AuditLogQuery(), await servis.ResolveVisibilityAsync(Bayi, UserRole.Reseller));

        Assert.Equal(2, sonuc.TotalCount);
        Assert.DoesNotContain(sonuc.Items, item => item.Resource == "b-ftp");
    }

    [Fact]
    public async Task Yonetici_sunucu_GENELINI_gorur()
    {
        await KayitYazAsync(MusteriA, "mail.account.created", "info@a.test");
        await KayitYazAsync(MusteriB, "mail.account.created", "info@b.test");
        await KayitYazAsync(null, "system.startup", "panel");

        var sonuc = await servis.SearchAsync(new AuditLogQuery(), await servis.ResolveVisibilityAsync(Yonetici, UserRole.Administrator));

        Assert.Equal(3, sonuc.TotalCount);
    }

    [Fact]
    public async Task Taninmayan_rol_HICBIR_sey_gormez()
    {
        await KayitYazAsync(MusteriA, "mail.account.created", "info@a.test");

        // Kapalı-varsayılan: yeni bir rol eklendiğinde kapsamı yazmayı unutan kişi, sunucu
        // genelini yanlışlıkla AÇMIŞ olmaz.
        var gorunurluk = await servis.ResolveVisibilityAsync(MusteriA, (UserRole)999);
        var sonuc = await servis.SearchAsync(new AuditLogQuery(), gorunurluk);

        Assert.False(gorunurluk.Unrestricted);
        Assert.Equal(0, sonuc.TotalCount);
    }

    [Fact]
    public async Task Kapsamli_rol_sahipsiz_system_kaydini_GORMEZ()
    {
        await KayitYazAsync(null, "system.startup", "panel");

        var sonuc = await servis.SearchAsync(new AuditLogQuery(), await servis.ResolveVisibilityAsync(MusteriA, UserRole.Customer));

        Assert.Equal(0, sonuc.TotalCount);
    }

    // ------------------------------------------------------------------
    // FİLTRE + SAYFALAMA
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sayfalama_TOPLAM_sayiyi_dondurur_ve_sayfayi_kirpar()
    {
        for (var i = 0; i < 7; i++)
        {
            await KayitYazAsync(MusteriA, "files.written", $"sites/a.test/{i}.php");
        }

        var sonuc = await servis.SearchAsync(
            new AuditLogQuery { Page = 2, PageSize = 3 },
            await servis.ResolveVisibilityAsync(Yonetici, UserRole.Administrator));

        // Toplam olmadan arayüz "sonraki sayfa" düğmesini ne zaman kapatacağını bilemez.
        Assert.Equal(7, sonuc.TotalCount);
        Assert.Equal(3, sonuc.Items.Count);
        Assert.Equal(2, sonuc.Page);
    }

    [Fact]
    public async Task Eylem_filtresi_NOKTASIZ_verildiginde_on_ek_gibi_calisir()
    {
        await KayitYazAsync(MusteriA, "mail.account.created", "info@a.test");
        await KayitYazAsync(MusteriA, "mail.alias.created", "satis@a.test");
        await KayitYazAsync(MusteriA, "files.deleted", "sites/a.test/x.php");

        var yonetim = await servis.ResolveVisibilityAsync(Yonetici, UserRole.Administrator);

        Assert.Equal(2, (await servis.SearchAsync(new AuditLogQuery { Action = "mail" }, yonetim)).TotalCount);
        Assert.Equal(1, (await servis.SearchAsync(new AuditLogQuery { Action = "mail.alias.created" }, yonetim)).TotalCount);
    }

    [Fact]
    public async Task Tarih_araligi_ve_serbest_metin_filtreleri_calisir()
    {
        await KayitYazAsync(MusteriA, "files.deleted", "sites/a.test/gizli.php", DateTime.UtcNow.AddDays(-40));
        await KayitYazAsync(MusteriA, "files.deleted", "sites/a.test/yeni.php", DateTime.UtcNow.AddDays(-1));

        var yonetim = await servis.ResolveVisibilityAsync(Yonetici, UserRole.Administrator);

        var sonGun = await servis.SearchAsync(new AuditLogQuery { FromUtc = DateTime.UtcNow.AddDays(-7) }, yonetim);
        Assert.Equal(1, sonGun.TotalCount);
        Assert.Equal("sites/a.test/yeni.php", Assert.Single(sonGun.Items).Resource);

        var arama = await servis.SearchAsync(new AuditLogQuery { Search = "GIZLI" }, yonetim);
        // Arama BÜYÜK/küçük harfe duyarlı olmamalı: kullanıcı yolu tam olarak hatırlamak zorunda değil.
        Assert.Equal(1, arama.TotalCount);
    }

    [Fact]
    public async Task Aramadaki_joker_karakter_HERSEYI_getirmez()
    {
        await KayitYazAsync(MusteriA, "files.deleted", "sites/a.test/x.php");

        var yonetim = await servis.ResolveVisibilityAsync(Yonetici, UserRole.Administrator);

        // "%" kaçırılmazsa LIKE'ta "her şey" anlamına gelir ve arama sessizce anlamsızlaşırdı.
        Assert.Equal(0, (await servis.SearchAsync(new AuditLogQuery { Search = "%" }, yonetim)).TotalCount);
    }

    [Fact]
    public async Task Kullanici_adi_filtresi_eslesmezse_sonuc_BOS_olur()
    {
        await KayitYazAsync(MusteriA, "files.deleted", "sites/a.test/x.php");

        var yonetim = await servis.ResolveVisibilityAsync(Yonetici, UserRole.Administrator);

        // "Eşleşen kullanıcı yok" durumu, "filtre yokmuş gibi davran"a DÜŞMEMELİ.
        Assert.Equal(0, (await servis.SearchAsync(new AuditLogQuery { UserName = "olmayan" }, yonetim)).TotalCount);
        Assert.Equal(1, (await servis.SearchAsync(new AuditLogQuery { UserName = "musteri-a" }, yonetim)).TotalCount);
    }

    // ------------------------------------------------------------------
    // SIR SIZINTISI
    // ------------------------------------------------------------------

    [Fact]
    public async Task PAROLA_denetim_kaydina_DUSMEZ()
    {
        await servis.WriteAsync(
            "mail.account.password_changed",
            "info@a.test",
            "Mailbox password was changed. password=CokGizli123! secret: BaskaSir",
            MusteriA,
            "203.0.113.9");

        var kayit = await dbContext.AuditLogs.SingleAsync();

        Assert.DoesNotContain("CokGizli123!", kayit.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("BaskaSir", kayit.Details, StringComparison.Ordinal);
        Assert.Contains(AuditSecretRedactor.Mask, kayit.Details, StringComparison.Ordinal);

        // Kayıt ANLAMINI kaybetmemeli: neyin olduğu hâlâ okunabilir olmalı.
        Assert.Contains("password was changed", kayit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("parola=Abc123!", "Abc123!")]
    [InlineData("Sifre: Abc123!", "Abc123!")]
    [InlineData("apiKey=\"sk-1234567890\"", "sk-1234567890")]
    [InlineData("ConnectionString=Server=x;Password=Abc123!", "Abc123!")]
    public void Sir_kaliplarinin_TAMAMI_maskelenir(string girdi, string sizmamasiGereken)
    {
        Assert.DoesNotContain(sizmamasiGereken, AuditSecretRedactor.Redact(girdi), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cok_uzun_ayrinti_KIRPILIR_ve_kayit_yine_de_yazilir()
    {
        // Details sütunu 2048; kırpılmazsa sıkı bir sağlayıcıda yazım patlar ve kayıt HİÇ oluşmazdı.
        await servis.WriteAsync("terminal.command.executed", "terminal", new string('x', 8000), Yonetici, null);

        var kayit = await dbContext.AuditLogs.SingleAsync();
        Assert.True(kayit.Details.Length <= AuditSecretRedactor.DetailsMaxLength + 1);
    }

    // ------------------------------------------------------------------
    // CSV
    // ------------------------------------------------------------------

    [Fact]
    public async Task CSV_basligi_ve_satiri_kacirilmis_bicimde_yazilir()
    {
        await KayitYazAsync(MusteriA, "files.deleted", "sites/a.test/x,y.php");

        using var akis = new MemoryStream();
        await AuditCsvWriter.WriteAsync(
            akis,
            servis.StreamAsync(new AuditLogQuery(), await servis.ResolveVisibilityAsync(Yonetici, UserRole.Administrator)));

        var metin = Encoding.UTF8.GetString(akis.ToArray());

        Assert.Contains(AuditCsvWriter.Header, metin, StringComparison.Ordinal);
        // Virgül içeren alan tırnağa alınmalı, yoksa sütunlar kayardı.
        Assert.Contains("\"sites/a.test/x,y.php\"", metin, StringComparison.Ordinal);
    }

    [Fact]
    public void CSV_FORMUL_enjeksiyonu_notrlenir()
    {
        var satir = AuditCsvWriter.BuildRow(new AuditLogDto
        {
            Id = 1,
            UserName = "musteri-a",
            Action = "files.written",
            Resource = "=cmd|'/c calc'!A1",
            Details = "x",
            IpAddress = "203.0.113.9",
            CreatedAtUtc = DateTime.UtcNow
        });

        // "=" ile başlayan hücre Excel'de FORMÜL olarak çalışırdı; denetim kaydının içeriği
        // kullanıcı girdisinden geldiği için bu gerçek bir saldırı yoludur.
        Assert.Contains("\"'=cmd", satir, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // SAKLAMA POLİTİKASI
    // ------------------------------------------------------------------

    [Fact]
    public async Task Saklama_suresinden_ESKI_kayitlar_silinir_yenisi_KALIR()
    {
        await KayitYazAsync(MusteriA, "files.deleted", "eski", DateTime.UtcNow.AddDays(-100));
        await KayitYazAsync(MusteriA, "files.deleted", "yeni", DateTime.UtcNow.AddDays(-2));

        await servis.SetRetentionAsync(30);
        var silinen = await servis.PurgeAsync();

        Assert.Equal(1, silinen);
        Assert.Equal("yeni", (await dbContext.AuditLogs.SingleAsync()).Resource);
    }

    [Fact]
    public async Task Saklama_suresi_SIFIR_ise_hicbir_sey_silinmez()
    {
        await KayitYazAsync(MusteriA, "files.deleted", "cok-eski", DateTime.UtcNow.AddYears(-5));

        await servis.SetRetentionAsync(0);

        // 0 = "sınırsız sakla". Bunu "her şeyi sil" gibi yorumlamak veri kaybının en sessiz hâli olurdu.
        Assert.Equal(0, await servis.PurgeAsync());
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Saklama_ayarlari_birikeni_OLCER()
    {
        await KayitYazAsync(MusteriA, "files.deleted", "x", DateTime.UtcNow.AddDays(-10));
        await KayitYazAsync(MusteriA, "files.deleted", "y");

        var ayarlar = await servis.GetRetentionAsync();

        Assert.Equal(2, ayarlar.TotalCount);
        Assert.NotNull(ayarlar.OldestAtUtc);
        // Varsayılan sınırsız DEĞİL: aksi hâlde politika "yönetici bulursa çalışan" bir süs olurdu.
        Assert.True(ayarlar.RetentionDays > 0);
    }

    [Fact]
    public async Task Eski_uc_GetRecent_calismaya_devam_eder()
    {
        // Bildirim çanı (stores/auth.ts) bu ucu düz dizi bekleyerek çağırıyor; sözleşmesi korunmalı.
        await KayitYazAsync(MusteriA, "files.deleted", "x");

        var kayitlar = await servis.GetRecentAsync(25);

        Assert.Single(kayitlar);
        Assert.Equal("musteri-a", kayitlar.First().UserName);
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    private void KullaniciEkle(Guid id, string ad, UserRole rol) => dbContext.Users.Add(new PanelUser
    {
        Id = id,
        UserName = ad,
        Email = $"{ad}@ornek.test",
        Role = rol,
        PasswordHash = "x",
        IsActive = true
    });

    /// <summary>Kayıt yazar; <paramref name="zaman"/> verilirse kaydın tarihini geriye çeker.</summary>
    private async Task KayitYazAsync(Guid? kullanici, string eylem, string kaynak, DateTime? zaman = null)
    {
        await servis.WriteAsync(eylem, kaynak, $"test detail for {kaynak}", kullanici, "203.0.113.9");

        if (zaman is { } t)
        {
            var kayit = await dbContext.AuditLogs.OrderByDescending(log => log.Id).FirstAsync();
            kayit.CreatedAtUtc = t;
            await dbContext.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Kiracı grafiğinin sahtesi: yalnız kapsam çözümünde kullanılan üç metodu gerçekten uygular.
    /// Diğerleri çağrılırsa test yanlış kurulmuş demektir → sessizce geçmek yerine patlar.
    /// <para>
    /// <c>internal</c>: aynı grafiği <see cref="AuditSubscriptionVisibilityTests"/> de kuruyor.
    /// İkinci bir kopya çıkarmak, birini güncelleyip diğerini unutmanın yoluydu.
    /// </para>
    /// </summary>
    internal sealed class SahteKapsam : ICustomerScopeService
    {
        public Dictionary<Guid, Guid[]> AltKullanicilar { get; } = [];

        public Dictionary<Guid, Guid> Ebeveynler { get; } = [];

        public Dictionary<Guid, Guid[]> SahipOlunanlar { get; } = [];

        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<Guid>>(AltKullanicilar.TryGetValue(parentUserId, out var alt) ? alt : []);

        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(Ebeveynler.TryGetValue(userId, out var ebeveyn) ? ebeveyn : (Guid?)null);

        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<Guid>>(SahipOlunanlar.TryGetValue(ownerId, out var sahipler) ? sahipler : []);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
