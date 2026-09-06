using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Billing;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Billing;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Kiracı bağı borcunun SON BEŞ UCU: kaydın konusu gerçekten ÖLÇÜLÜYOR mu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kaynak taraması yetmiyor:</b> <c>AuditCoverageTests</c> yalnız <i>biçimi</i> korur —
/// "kayıt çağrısı konu argümanı taşıyor mu". Oraya <c>Guid.NewGuid()</c> yazmak da testi yeşil
/// bırakırdı ve kayıt var olmayan bir kiracıya bağlanırdı. Burada ÖLÇÜLEN şey davranıştır: konu
/// doğru kiracı mı, ölçülemediğinde gerçekten boş mu kalıyor.
/// </para>
/// <para>
/// <b>Neden "boş kalması" da test ediliyor:</b> yanlış kiracıya bağlamak hiç bağlamamaktan
/// KÖTÜDÜR — kayıt komşunun görünümüne düşer (çapraz-kiracı sızıntı) ve gerçek sahibi yine göremez.
/// Bu yüzden her uçta hem "bağlanır" hem de "bağlanamaz" tarafı sabitleniyor; yalnız birincisini
/// test etmek, çağıranın kimliğini konu yazan bir kolaycılığı geçirirdi.
/// </para>
/// </remarks>
public sealed class AuditTenantLinkClosureTests
{
    private static readonly Guid Kiraci = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid IkinciKiraci = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Yonetici = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    // ------------------------------------------------------------------
    // 1) SiteHostingSettingsController — konu SİTENİN SAHİBİ
    // ------------------------------------------------------------------

    [Fact]
    public async Task Barindirma_ayari_kaydinin_konusu_SITENIN_SAHIBIDIR_calistiran_yonetici_degil()
    {
        // Asıl düzeltilen arıza: yönetici kiracının sitesinde Composer çalıştırdığında kaydın
        // UserId'si yöneticiydi ve konusu boştu → kayıt kiracının izinli kimlik kümesinin ikisinde
        // de yoktu, yani kiracı kendi sitesinde çalıştırılan komutu HİÇ göremiyordu.
        var kayitlar = new KayitDefteri();
        var denetleyici = SiteDenetleyici(kayitlar, new Dictionary<string, Guid> { ["musteri-site"] = Kiraci }, Yonetici);

        await denetleyici.ComposerInstallAsync("musteri-site", CancellationToken.None);

        var kayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Equal("sites.composer.install", kayit.Action);
        Assert.Equal(Yonetici, kayit.UserId);
        Assert.Equal(Kiraci, kayit.SubscriptionUserId);
    }

    [Fact]
    public async Task Sahipsiz_sitede_barindirma_kaydinin_konusu_BOS_kalir()
    {
        // Yöneticinin kendi sitesi hiçbir aboneliğe atanmamıştır. Çağıranın kimliğini konu yazmak,
        // panelin kendi altyapısındaki bir değişikliği "abonelik kaydı" gibi göstermek olurdu.
        var kayitlar = new KayitDefteri();
        var denetleyici = SiteDenetleyici(kayitlar, new Dictionary<string, Guid>(), Yonetici);

        await denetleyici.StartQueueAsync("panel-sitesi", CancellationToken.None);

        Assert.Null(Assert.Single(kayitlar.Kayitlar).SubscriptionUserId);
    }

    // ------------------------------------------------------------------
    // 2) CronController — konu GÖREV ADININ ad alanı önekinden
    // ------------------------------------------------------------------

    [Fact]
    public async Task Yonetici_KIRACININ_gorevini_silince_kayit_KIRACIYA_yazilir()
    {
        // Bu, kiracı bağının en çok gerektiği an: kiracı zamanlanmış görevinin silindiğini kendi
        // ekranında görebilmeli. Sahip, çağırandan değil GÖREV ADINDAN ölçülüyor.
        var kayitlar = new KayitDefteri();
        var zamanlayici = new SahteZamanlayici();
        var denetleyici = CronDenetleyici(kayitlar, zamanlayici, new SahteKapsam(), Yonetici, "Administrator");

        await denetleyici.DeleteTaskAsync($"u{Kiraci:N}--gece-yedegi", CancellationToken.None);

        var kayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Equal("cron.deleted", kayit.Action);
        Assert.Equal(Kiraci, kayit.SubscriptionUserId);

        // Görev, KAPSAMLI adıyla silinmiş olmalı: kaydın konusunu çözerken adı bozmuş olsaydık
        // yönetici yanlış görevi silerdi.
        Assert.Equal($"u{Kiraci:N}--gece-yedegi", Assert.Single(zamanlayici.Silinenler));
    }

    [Fact]
    public async Task Musterinin_kendi_gorevi_ABONELIK_kimligine_yazilir_alt_kullanicida_PARENT_e()
    {
        // Alt kullanıcı kendi kimliğiyle görev siler ama görev PARENT aboneliğin ad alanındadır
        // (Plesk davranışı). Konu da o ad alanından çözülmeli, yoksa parent kendi aboneliğindeki
        // değişikliği göremezdi.
        var altKullanici = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var kayitlar = new KayitDefteri();
        var kapsam = new SahteKapsam { Ebeveynler = { [altKullanici] = Kiraci } };
        var denetleyici = CronDenetleyici(kayitlar, new SahteZamanlayici(), kapsam, altKullanici, "Customer");

        await denetleyici.EnableTaskAsync("gece-yedegi", CancellationToken.None);

        var kayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Equal(altKullanici, kayit.UserId);
        Assert.Equal(Kiraci, kayit.SubscriptionUserId);
    }

    [Fact]
    public async Task Oneksiz_SUNUCU_gorevinin_konusu_BOS_kalir()
    {
        // Öneksiz ad, yöneticinin sunucu düzeyindeki kendi görevidir. Onu çağıran yöneticiye
        // bağlamak, sunucu geneli bir işi bir abonelik kaydı gibi göstermek olurdu.
        var kayitlar = new KayitDefteri();
        var denetleyici = CronDenetleyici(kayitlar, new SahteZamanlayici(), new SahteKapsam(), Yonetici, "Administrator");

        await denetleyici.DeleteTaskAsync("panel-bakim", CancellationToken.None);

        Assert.Null(Assert.Single(kayitlar.Kayitlar).SubscriptionUserId);
    }

    [Fact]
    public async Task Onege_BENZEYEN_ama_gecersiz_ad_konuyu_UYDURMAZ()
    {
        // Kural kaynağın adına bakıyor; ad kullanıcıdan gelebiliyor. "u" + 32 karakter + "--"
        // biçimine benzeyen ama Guid olmayan bir ad, konuyu boş bırakmalı — tahmine dönüşmemeli.
        var kayitlar = new KayitDefteri();
        var denetleyici = CronDenetleyici(kayitlar, new SahteZamanlayici(), new SahteKapsam(), Yonetici, "Administrator");

        await denetleyici.DeleteTaskAsync("uzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz--is", CancellationToken.None);

        Assert.Null(Assert.Single(kayitlar.Kayitlar).SubscriptionUserId);
    }

    [Fact]
    public async Task Musteri_gorev_adiyla_BASKA_bir_kiraciya_kayit_YAZDIRAMAZ()
    {
        // Sahip görev ADINDAN okunuyor ve ad kısmen kullanıcıdan geliyor — yani "kendi kaydımı
        // komşunun geçmişine yazdırabilir miyim" sorusu meşru. Cevap hayır: müşteri yolunda ad
        // ÖNCE panelin kendi ön ekiyle kapsanıyor ve çözüm dizginin BAŞINA bağlı. Uydurulan önek
        // ikinci sıraya düşer ve okunmaz.
        var kayitlar = new KayitDefteri();
        var denetleyici = CronDenetleyici(kayitlar, new SahteZamanlayici(), new SahteKapsam(), Kiraci, "Customer");

        await denetleyici.DeleteTaskAsync($"u{IkinciKiraci:N}--kurban", CancellationToken.None);

        var kayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Equal(Kiraci, kayit.SubscriptionUserId);
        Assert.NotEqual(IkinciKiraci, kayit.SubscriptionUserId);
    }

    [Fact]
    public async Task Sunucu_geneli_PATH_kaydi_bir_kiraciya_baglanmaz()
    {
        // Makine PATH'i bütün kiracıların görevlerini etkiler; tek bir aboneliğe yazmak, o kiracıya
        // komşularını da kapsayan bir işlemi göstermek olurdu.
        var kayitlar = new KayitDefteri();
        var denetleyici = CronDenetleyici(kayitlar, new SahteZamanlayici(), new SahteKapsam(), Yonetici, "Administrator");

        await denetleyici.AddPythonToPathAsync(CancellationToken.None);

        var kayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Equal("cron.python_path", kayit.Action);
        Assert.Null(kayit.SubscriptionUserId);
    }

    // ------------------------------------------------------------------
    // 3) BillingController — konu, panelin ÖLÇTÜĞÜ eşleşen hesap
    // ------------------------------------------------------------------

    [Fact]
    public async Task Silme_denetimi_kaydinin_konusu_ESLESEN_HESAPTIR()
    {
        var kayitlar = new KayitDefteri();
        var denetleyici = FaturaDenetleyici(kayitlar, new BillingTerminationCheckDto
        {
            UserId = Kiraci,
            UserName = "musteri1",
            SafeToTerminate = true,
            Reason = BillingTerminationGuard.ReasonOk
        });

        await denetleyici.CheckTerminationAsync("musteri1", "ornek.com", CancellationToken.None);

        var kayit = Assert.Single(kayitlar.Kayitlar);
        Assert.Equal("billing.terminate_checked", kayit.Action);
        Assert.Equal(Kiraci, kayit.SubscriptionUserId);
    }

    [Fact]
    public async Task Eslesme_bulunamayan_silme_denetiminde_konu_BOS_kalir()
    {
        // "not_found"/"ambiguous" hâllerinde panel bir kimlik ÖLÇEMEZ. Kayıt yine yazılır (iptalin
        // neden çalışmadığı sorulacaktır) ama konusu uydurulmaz.
        var kayitlar = new KayitDefteri();
        var denetleyici = FaturaDenetleyici(kayitlar, new BillingTerminationCheckDto
        {
            UserId = null,
            UserName = "yok-boyle-biri",
            SafeToTerminate = false,
            Reason = BillingTerminationGuard.ReasonNotFound
        });

        await denetleyici.CheckTerminationAsync("yok-boyle-biri", "ornek.com", CancellationToken.None);

        Assert.Null(Assert.Single(kayitlar.Kayitlar).SubscriptionUserId);
    }

    [Fact]
    public async Task Panel_geneli_faturalama_kayitlari_bir_kiraciya_baglanmaz()
    {
        // Entegrasyon anahtarı ve toplu kullanım raporu tanım gereği BÜTÜN abonelikleri kapsar.
        var kayitlar = new KayitDefteri();
        var denetleyici = FaturaDenetleyici(kayitlar, new BillingTerminationCheckDto());

        await denetleyici.UpdateSettingsAsync(new UpdateBillingSettingsRequest { Enabled = true }, CancellationToken.None);
        await denetleyici.GetUsageAsync(CancellationToken.None);

        Assert.Equal(2, kayitlar.Kayitlar.Count);
        Assert.All(kayitlar.Kayitlar, kayit => Assert.Null(kayit.SubscriptionUserId));
    }

    // ------------------------------------------------------------------
    // 4) MailListPublicController — KİMLİKSİZ uç, konu liste alan adından
    // ------------------------------------------------------------------

    [Fact]
    public async Task Abonelikten_cikma_onayinin_konusu_LISTENIN_ALAN_ADI_SAHIBIDIR()
    {
        // Kimliksiz uçta UserId yapısal olarak boştur; konu da boş olsaydı kayıt HİÇBİR kiracının
        // görünümüne düşmezdi. Bu, "benim listemden kim çıkarıldı" sorusunu kalıcı olarak
        // cevapsız bırakırdı.
        using var duzenek = new PostaDuzenegi();
        var belirtec = await duzenek.BekleyenBelirtecYazAsync("duyuru@ornek.com", "abone@baska.test");

        await duzenek.Denetleyici.ConfirmAsync(belirtec, CancellationToken.None);

        var kayit = Assert.Single(duzenek.Kayitlar.Kayitlar);
        Assert.Equal("mail.list.unsubscribe_confirmed", kayit.Action);
        Assert.Null(kayit.UserId);
        Assert.Equal(Kiraci, kayit.SubscriptionUserId);

        // Alan adı burada yalnız BAĞLAMADAN çözülebilir (site adı "musteri1"): derin aramanın
        // gerçekten çalıştığı, tesadüfen değil ölçerek sabitlenir.
        Assert.Equal(1, duzenek.Siteler.OkumaSayisi);
    }

    [Fact]
    public async Task Gecersiz_belirtecte_konu_BOS_kalir_ve_IIS_HIC_okunmaz()
    {
        // Geçersiz belirteçte liste adı boştur: bağlanacak bir kiracı YOKTUR. Ayrıca sahiplik
        // araması ilk adımda durmalı — kimliksiz uçtan sunucuya iş yaptırmanın kaldıracı olmasın.
        using var duzenek = new PostaDuzenegi();

        await duzenek.Denetleyici.ConfirmAsync("gecersiz-belirtec", CancellationToken.None);

        Assert.Null(Assert.Single(duzenek.Kayitlar.Kayitlar).SubscriptionUserId);
        Assert.Equal(0, duzenek.Siteler.OkumaSayisi);
    }

    [Fact]
    public async Task Istek_adiminda_DOGRULANMAMIS_liste_icin_IIS_taramasi_YAPILMAZ()
    {
        // `list` alanı burada tamamen istemcinin yazdığı serbest metindir ve uç kimliksiz + oran
        // sınırsızdır. IIS'teki bütün siteleri gezen derin arama, her anonim POST'ta çalışsaydı
        // tek satırlık bir istekle sunucuya iş yaptıran bir kaldıraç olurdu.
        using var duzenek = new PostaDuzenegi();

        await duzenek.Denetleyici.RequestAsync("her-neyse@rastgele.test", "kimse@rastgele.test", CancellationToken.None);

        var kayit = Assert.Single(duzenek.Kayitlar.Kayitlar);
        Assert.Equal("mail.list.unsubscribe_requested", kayit.Action);
        Assert.Null(kayit.SubscriptionUserId);
        Assert.Equal(0, duzenek.Siteler.OkumaSayisi);
    }

    [Fact]
    public async Task Istek_adiminda_ucuz_cozum_YINE_calisir_alan_adi_dogrudan_SITE_ADIYSA()
    {
        // Derin aramanın kapalı olması "konu hiç yazılmaz" demek değil: alan adının kendisi bir
        // site adıysa sahiplik tek JSON okumasıyla çözülür ve kiracı bağı kurulur.
        using var duzenek = new PostaDuzenegi();
        duzenek.Kapsam.SiteSahipleri["dogrudan.test"] = IkinciKiraci;

        await duzenek.Denetleyici.RequestAsync("liste@dogrudan.test", "kimse@baska.test", CancellationToken.None);

        Assert.Equal(IkinciKiraci, Assert.Single(duzenek.Kayitlar.Kayitlar).SubscriptionUserId);
        Assert.Equal(0, duzenek.Siteler.OkumaSayisi);
    }

    // ------------------------------------------------------------------
    // Düzenek
    // ------------------------------------------------------------------

    private static SiteHostingSettingsController SiteDenetleyici(
        KayitDefteri kayitlar,
        IReadOnlyDictionary<string, Guid> sahipler,
        Guid aktor)
    {
        var kapsam = new SahteKapsam();
        foreach (var (site, sahip) in sahipler)
        {
            kapsam.SiteSahipleri[site] = sahip;
        }

        return new SiteHostingSettingsController(
            new SahteBarindirmaAyarlari(),
            kapsam,
            new SahteGoc(),
            kayitlar)
        {
            ControllerContext = new ControllerContext { HttpContext = Baglam(aktor, "Administrator") }
        };
    }

    private static CronController CronDenetleyici(
        KayitDefteri kayitlar,
        ISchedulerService zamanlayici,
        ICustomerScopeService kapsam,
        Guid aktor,
        string rol)
        => new(zamanlayici, kapsam, new SahteSunucuSahibi(Yonetici), kayitlar)
        {
            ControllerContext = new ControllerContext { HttpContext = Baglam(aktor, rol) }
        };

    private static BillingController FaturaDenetleyici(KayitDefteri kayitlar, BillingTerminationCheckDto karar)
        => new(new SahteFaturalama(karar), kayitlar)
        {
            ControllerContext = new ControllerContext { HttpContext = Baglam(Yonetici, "Administrator") }
        };

    private static DefaultHttpContext Baglam(Guid aktor, string rol)
    {
        var baglam = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, aktor.ToString()), new Claim(ClaimTypes.Role, rol)],
                "test",
                ClaimTypes.NameIdentifier,
                ClaimTypes.Role))
        };
        baglam.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        return baglam;
    }

    /// <summary>
    /// Kimliksiz posta ucu için tam düzenek: gerçek <see cref="MailListUnsubscribeService"/>
    /// (sealed olduğu için taklit edilemez) + geçici veri kökü.
    /// </summary>
    private sealed class PostaDuzenegi : IDisposable
    {
        private readonly string kok = Path.Combine(Path.GetTempPath(), "adapanel-liste-" + Guid.NewGuid().ToString("N"));

        public PostaDuzenegi()
        {
            Directory.CreateDirectory(kok);
            // Tam nitelenmiş: bu ad alanında "Options" hem Microsoft.Extensions.Options'a hem de
            // AdaPanel.Infrastructure.Options'a karşılık gelir.
            Depo = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(
                new AdaPanel.Infrastructure.Options.PanelRuntimeOptions { DataRoot = kok }));

            // Alan adı bir SİTE BAĞLAMASINDA yaşıyor (site adı "musteri1", bağlama "ornek.com"):
            // konu ancak derin arama açıkken çözülebilir. Ucuz adım bilerek yetmiyor ki
            // "derin arama gerçekten yapıldı mı" ölçülebilsin.
            Kapsam = new SahteKapsam();
            Kapsam.SiteSahipleri["musteri1"] = Kiraci;
            Siteler = new SayanSiteler([
                new SiteDto
                {
                    Name = "musteri1",
                    Bindings = [new SiteBindingDto { Protocol = "https", HostName = "ornek.com", Port = 443 }]
                }
            ]);

            // Abonelikten çıkma isteğinin işi ARKA PLANA alındı (zamanlama sızıntısı); testte
            // sonucu ölçebilmek için kuyruk işi hemen, aynı iplikte çalıştırır.
            var abonelikServisi = new MailListUnsubscribeService(
                Depo, new SessizPostaServisi(), new SessizBildirim(), NullLogger<MailListUnsubscribeService>.Instance);
            var kuyruk = new HemenCalistiranIsKuyrugu(new SozlukSaglayici()
                .Ekle(abonelikServisi)
                .Ekle<ICustomerScopeService>(Kapsam)
                .Ekle<IIisSiteService>(Siteler)
                .Ekle<IAuditService>(Kayitlar));

            Denetleyici = new MailListPublicController(
                abonelikServisi,
                Kapsam,
                Siteler,
                new SabitTercihler(),
                Kayitlar,
                kuyruk,
                NullLogger<MailListPublicController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public JsonFileStore Depo { get; }

        public SahteKapsam Kapsam { get; }

        public SayanSiteler Siteler { get; }

        public KayitDefteri Kayitlar { get; } = new();

        public MailListPublicController Denetleyici { get; }

        /// <summary>Onay adımını sürebilmek için depoya doğrudan bekleyen bir kayıt yazar.</summary>
        public async Task<string> BekleyenBelirtecYazAsync(string liste, string abone)
        {
            var belirtec = MailListUnsubscribeService.CreateToken();
            var now = DateTimeOffset.UtcNow;

            await Depo.WriteAsync(
                MailListUnsubscribeService.StorePath,
                new Dictionary<string, MailListUnsubscribeService.PendingUnsubscribe>(StringComparer.Ordinal)
                {
                    [MailListUnsubscribeService.HashToken(belirtec)] = new()
                    {
                        ListAddress = liste,
                        Subscriber = abone,
                        CreatedAtUtc = now,
                        ExpiresAtUtc = now.AddHours(1)
                    }
                },
                CancellationToken.None);

            return belirtec;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(kok, recursive: true);
            }
            catch
            {
                // en iyi çaba
            }
        }
    }

    /// <summary>Kaydın BÜTÜN alanlarını tutar; konu doğrulanacaksa eylem adı yetmez.</summary>
    internal sealed class KayitDefteri : IAuditService
    {
        public List<(string Action, string Resource, string Details, Guid? UserId, Guid? SubscriptionUserId, string? IpAddress)> Kayitlar { get; } = [];

        public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default)
            => WriteForSubscriptionAsync(action, resource, details, userId, null, ipAddress, cancellationToken);

        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default)
        {
            Kayitlar.Add((action, resource, details, userId, subscriptionUserId, ipAddress));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<Core.Dtos.Audit.AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Audit.AuditLogPageDto> SearchAsync(Core.Dtos.Audit.AuditLogQuery query, Core.Dtos.Audit.AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<Core.Dtos.Audit.AuditLogDto> StreamAsync(Core.Dtos.Audit.AuditLogQuery query, Core.Dtos.Audit.AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Audit.AuditVisibility> ResolveVisibilityAsync(Guid userId, Core.Enums.UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Audit.AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Audit.AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Yalnız sahiplik ve ebeveynlik sorularını bilen kapsam; gerisi bilerek PATLAR ki çözüm
    /// beklenmedik bir yoldan veri toplarsa test sessizce geçmesin.
    /// </summary>
    internal sealed class SahteKapsam : ICustomerScopeService
    {
        public Dictionary<string, Guid> SiteSahipleri { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<Guid, Guid> Ebeveynler { get; } = [];

        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(SiteSahipleri.TryGetValue(siteName, out var sahip) ? sahip : (Guid?)null);

        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(Ebeveynler.TryGetValue(userId, out var parent) ? parent : (Guid?)null);

        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>?>(null);

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
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>IIS okumasının GERÇEKTEN yapılıp yapılmadığını sayar (derin arama kapısının ölçümü).</summary>
    internal sealed class SayanSiteler(IReadOnlyCollection<SiteDto> siteler) : IIisSiteService
    {
        public int OkumaSayisi { get; private set; }

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
        {
            OkumaSayisi++;
            return Task.FromResult(siteler);
        }

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Sahiplik burada ÖLÇÜLMÜYOR (kaydın kiracı bağı ölçülüyor); sabit cevap yeter.</summary>
    private sealed class SahteSunucuSahibi(Guid sahip) : IServerOwnerService
    {
        public Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(sahip);

        public Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(userId == sahip);
    }

    private sealed class SahteZamanlayici : ISchedulerService
    {
        public List<string> Silinenler { get; } = [];

        public Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default)
        {
            Silinenler.Add(name);
            return Task.CompletedTask;
        }

        public Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MachinePathMeasurement> AddPythonToMachinePathAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MachinePathMeasurement { Measured = true, Expected = 2, Requested = 2, Verified = 2 });

        public Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, ScheduledTaskPrivilege privilege, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RunTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskStatusDto> GetTaskStatusAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskOutputDto> GetTaskOutputAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskEnvironmentDto> GetEnvironmentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StopScheduledTaskResultDto> StopTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RepairTaskSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoveTasksUnderRootAsync(string siteRootPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteBarindirmaAyarlari : ISiteHostingSettingsService
    {
        public Task<ComposerRunResultDto> RunComposerInstallAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComposerRunResultDto { ExitCode = 0 });

        public Task<QueueWorkerStatusDto> StartQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(new QueueWorkerStatusDto { TaskName = "kuyruk" });

        public Task<SiteHostingSettingsDto> GetAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteHostingSettingsDto> UpdateAsync(string siteName, UpdateSiteHostingSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunComposerCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunArtisanCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunNodeCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunWpCliCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteImportResultDto> ImportSiteArchiveAsync(string siteName, string archiveUrl, bool cleanFirst, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> GetQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StopQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> GetLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StartLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StopLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteGoc : IFileMigrationService
    {
        public Task<RemoteFileMigrationResult> PullAsync(RemoteFileMigrationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteFaturalama(BillingTerminationCheckDto karar) : IBillingIntegrationService
    {
        public Task<BillingTerminationCheckDto> CheckTerminationAsync(string? userName, string? domain, CancellationToken cancellationToken = default)
            => Task.FromResult(karar);

        public Task<BillingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BillingSettingsDto> UpdateSettingsAsync(bool enabled, CancellationToken cancellationToken = default)
            => Task.FromResult(new BillingSettingsDto { Enabled = enabled });

        public Task<BillingUsageReportDto> BuildUsageReportAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BillingUsageReportDto());
    }

    /// <summary>Posta sunucusu OKUNMAZ: bu testlerde hiçbir liste doğrulanmıyor.</summary>
    private sealed class SessizPostaServisi : IMailService
    {
        public Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default)
            => Task.FromResult(new MailWorkspaceDto { Installed = true, Configured = true, SelectedDomain = domain ?? string.Empty });

        public Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new MailWorkspaceDto());

        public Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // Ek adres (alias) uclari artik IMailService uzerinden geciyor; bu sahte servis posta isi yapmaz.
        public Task<MailWorkspaceDto> AddMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> RemoveMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailListenerSnapshotDto> GetListenersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>SMTP yapılandırılmamış: istek adımı hiçbir posta göndermez (bu testlerde `sent` daima false).</summary>
    private sealed class SessizBildirim : INotificationService
    {
        public Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<Core.Dtos.Notifications.NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Core.Dtos.Notifications.NotificationSettingsDto> UpdateSettingsAsync(Core.Dtos.Notifications.UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TestAsync(Core.Dtos.Notifications.TestNotificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
