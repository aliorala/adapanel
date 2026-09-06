using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KULLANICI SİLİNİNCE SİTELERİ GERÇEKTEN SİLİNSİN; YARIM KALIRSA PANEL "BAŞARILI" DEMESİN
/// (denetim Y7'nin ikinci yarısı).
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK:</b> <c>UserManagementService.DeleteUserAsync</c> her site için cascade'i
/// çağırıyor ama dönen <c>SiteDeletionResultDto</c>'yu tamamen atıyordu. <c>DeleteAsync</c> istisna
/// fırlatmadığı için <c>catch</c> de tetiklenmiyordu. Sonra kullanıcı kaydı siliniyor ve uç
/// <c>200 OK</c> dönüyordu: veritabanı düşmemiş / mail alan adı kalmış / dosyaları yarım silinmiş
/// bir site, artık <b>sahibi olmayan bir yetim</b> olarak sunucuda kalıyordu. WHMCS bu çağrıya
/// "success" deyip hizmeti iptal ediyordu.
/// </para>
/// <para>
/// Buradaki testler ZİNCİRİN TAMAMINI ölçer: cascade'in raporu → servis → uç → HTTP durumu.
/// Kaynak metni değil DAVRANIŞI kontrol ederler; kodu eski hâline döndüren her mutasyon
/// (kalıntıları atmak, 409 yerine 200 dönmek, adları düşürmek) en az bir testi kırar.
/// </para>
/// </remarks>
public sealed class UserDeletionHonestyTests
{
    private static readonly Guid YoneticiId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- Servis: cascade'in raporu artık yanıt nesnesine giriyor ----

    /// <summary>
    /// Site YARIM silindiyse sonuç "tamamlandı" DEĞİLDİR ve kalan kaynak ADIYLA görünür.
    /// </summary>
    [Fact]
    public async Task WhenASiteIsOnlyHalfDeleted_TheResultIsNotComplete_AndNamesWhatRemains()
    {
        using var duzenek = Duzenek.Kur();
        duzenek.Kapsam.Siteler = ["musteri.com"];
        duzenek.Cascade.Sonuclar["musteri.com"] = new SiteDeletionResultDto
        {
            SiteName = "musteri.com",
            Leftovers =
            {
                new SiteLeftoverDto
                {
                    Kind = SiteLeftoverKinds.Files,
                    Name = @"D:\web\musteri.com",
                    Reason = "KISMEN silindi — 12 dosya kaldı"
                },
                new SiteLeftoverDto
                {
                    Kind = SiteLeftoverKinds.Database,
                    Name = "mysql:musteri_db",
                    Reason = "düşürülemedi"
                }
            }
        };

        var rapor = await duzenek.Servis.DeleteUserAsync(duzenek.MusteriId, YoneticiId, removeSites: true, ipAddress: null);

        // ASIL İDDİA: yarım kalan silme BAŞARI SAYILMAZ.
        Assert.False(rapor.Completed);

        // ...ama "hiçbir şey olmadı" da denmiyor: kullanıcı kaydı gerçekten silindi.
        Assert.True(rapor.UserDeleted);
        Assert.Empty(rapor.DeletedSites);

        // Kalıntılar ADIYLA: hangi site, hangi klasör, hangi veritabanı.
        Assert.Equal(2, rapor.Leftovers.Count);
        Assert.All(rapor.Leftovers, kalinti => Assert.Equal("musteri.com", kalinti.SiteName));
        Assert.Contains(rapor.Leftovers, kalinti => kalinti.Name == @"D:\web\musteri.com" && kalinti.Kind == SiteLeftoverKinds.Files);
        Assert.Contains(rapor.Leftovers, kalinti => kalinti.Name == "mysql:musteri_db" && kalinti.Kind == SiteLeftoverKinds.Database);
        Assert.All(rapor.Leftovers, kalinti => Assert.False(kalinti.Deliberate));
    }

    /// <summary>Cascade HİÇ çalışamazsa site olduğu gibi duruyordur — en ağır kalıntı budur.</summary>
    [Fact]
    public async Task WhenTheCascadeThrows_TheSiteItselfIsReportedAsLeftover()
    {
        using var duzenek = Duzenek.Kur();
        duzenek.Kapsam.Siteler = ["patlayan.com"];
        duzenek.Cascade.Patlat.Add("patlayan.com");

        var rapor = await duzenek.Servis.DeleteUserAsync(duzenek.MusteriId, YoneticiId, removeSites: true, ipAddress: null);

        Assert.False(rapor.Completed);
        var kalinti = Assert.Single(rapor.Leftovers);
        Assert.Equal(SiteLeftoverKinds.Site, kalinti.Kind);
        Assert.Equal("patlayan.com", kalinti.Name);
    }

    /// <summary>Her şey gerçekten gittiyse sonuç temizdir — aksi hâlde test "her zaman kırmızı" olurdu.</summary>
    [Fact]
    public async Task WhenEverythingWasDeleted_TheResultIsComplete()
    {
        using var duzenek = Duzenek.Kur();
        duzenek.Kapsam.Siteler = ["temiz.com"];
        duzenek.Cascade.Sonuclar["temiz.com"] = new SiteDeletionResultDto { SiteName = "temiz.com" };

        var rapor = await duzenek.Servis.DeleteUserAsync(duzenek.MusteriId, YoneticiId, removeSites: true, ipAddress: null);

        Assert.True(rapor.Completed);
        Assert.Equal(["temiz.com"], rapor.DeletedSites);
        Assert.Empty(rapor.Leftovers);
    }

    /// <summary>
    /// BİLEREK bırakılan sonucu başarısız YAPMAZ ama yine de listelenir: <c>removeSites=false</c>
    /// seçildiğinde siteler sunucuda kalır ve o andan sonra sahiplerini gösteren hiçbir kayıt olmaz.
    /// </summary>
    [Fact]
    public async Task WhenSitesAreKeptOnPurpose_TheyAreStillListedByName()
    {
        using var duzenek = Duzenek.Kur();
        duzenek.Kapsam.Siteler = ["devredilen.com"];

        var rapor = await duzenek.Servis.DeleteUserAsync(duzenek.MusteriId, YoneticiId, removeSites: false, ipAddress: null);

        // İstenen buydu → başarı.
        Assert.True(rapor.Completed);

        // ...ama sunucuda duran şey ADIYLA bildiriliyor.
        var kalinti = Assert.Single(rapor.Leftovers);
        Assert.True(kalinti.Deliberate);
        Assert.Equal("devredilen.com", kalinti.Name);
        Assert.Empty(duzenek.Cascade.Silinenler);
    }

    /// <summary>
    /// <c>removeSites=true</c> "her şey gitsin" demektir. Hiçbir siteye bağlı olmadığı için
    /// cascade kapsamına girmeyen bir veritabanı motorda kalıyorsa bu bir ARIZADIR: panel kaydı
    /// birazdan silinecek ve o veritabanına panelden bir daha erişilemeyecek.
    /// </summary>
    [Fact]
    public async Task ADatabaseThatSurvivesAFullRemoval_MakesTheResultIncomplete()
    {
        using var duzenek = Duzenek.Kur();
        duzenek.Kapsam.Siteler = [];
        duzenek.DbSahiplik.Sahipli.Add(("mysql", "yetim_db"));

        var rapor = await duzenek.Servis.DeleteUserAsync(duzenek.MusteriId, YoneticiId, removeSites: true, ipAddress: null);

        Assert.False(rapor.Completed);
        var kalinti = Assert.Single(rapor.Leftovers);
        Assert.Equal("mysql:yetim_db", kalinti.Name);

        // Kayıt yine de temizleniyor (davranış korundu); değişen tek şey ARTIK BİLDİRİLMESİ.
        Assert.Contains(("mysql", "yetim_db"), duzenek.DbSahiplik.Silinenler);
    }

    /// <summary>Kalıntı varsa KALICI denetim kaydı yazılır: kullanıcı satırı gidince tek kanıt odur.</summary>
    [Fact]
    public async Task LeftoversAreWrittenToTheAuditLog()
    {
        using var duzenek = Duzenek.Kur();
        duzenek.Kapsam.Siteler = ["musteri.com"];
        duzenek.Cascade.Sonuclar["musteri.com"] = new SiteDeletionResultDto
        {
            SiteName = "musteri.com",
            Warnings = { "Site dosyaları: KISMEN SİLİNDİ" },
            Leftovers = { new SiteLeftoverDto { Kind = SiteLeftoverKinds.Files, Name = @"D:\web\musteri.com", Reason = "kilitli dosya" } }
        };

        await duzenek.Servis.DeleteUserAsync(duzenek.MusteriId, YoneticiId, removeSites: true, ipAddress: null);

        Assert.Contains("users.delete.site_leftover", duzenek.Denetim.Actions);
        Assert.Contains("users.deleted", duzenek.Denetim.Actions);
    }

    // ---- Uç: yarım kalan silme 2xx DÖNMEZ ----

    /// <summary>
    /// KRİTİK ÖLÇÜT: WHMCS gibi bir istemci bu yanıta bakıp "hizmet tamamen kaldırıldı" sonucuna
    /// VARAMAMALI. WHMCS modülü (<c>AdaPanelClient::request</c>) zarftaki <c>success</c> alanına
    /// bakıp istisna fırlatır; ayrıca durum kodu da 2xx değildir.
    /// </summary>
    [Fact]
    public async Task AnIncompleteDeletion_DoesNotReturnSuccessToTheCaller()
    {
        var rapor = new UserDeletionResultDto
        {
            UserName = "musteri",
            UserDeleted = true,
            SiteRemovalRequested = true,
            Leftovers =
            {
                new UserDeletionLeftoverDto
                {
                    SiteName = "musteri.com",
                    Kind = SiteLeftoverKinds.Database,
                    Name = "mysql:musteri_db",
                    Reason = "düşürülemedi"
                }
            }
        };

        var yanit = await SilmeUcunuCalistir(rapor);
        var sonuc = Assert.IsType<ObjectResult>(yanit.Result);
        var zarf = Assert.IsType<ApiResponse<UserDeletionResultDto>>(sonuc.Value);

        // İki bağımsız işaret de "başarı değil" diyor: HTTP durumu ve zarfın success alanı.
        Assert.Equal(StatusCodes.Status409Conflict, sonuc.StatusCode);
        Assert.False(zarf.Success);
        Assert.Equal("users.delete.incomplete", zarf.Message);

        // Kalıntılar hem yapısal hem okunabilir biçimde dönüyor; ADLARI kaybolmuyor.
        Assert.Same(rapor, zarf.Data);
        Assert.Contains("mysql:musteri_db", zarf.Details, StringComparison.Ordinal);
        Assert.Contains("musteri.com", zarf.Details, StringComparison.Ordinal);
    }

    /// <summary>Temiz silme eskisi gibi 200 döner — 409 kuralı her silmeyi hataya çevirmemeli.</summary>
    [Fact]
    public async Task ACleanDeletion_StillReturnsSuccess()
    {
        var rapor = new UserDeletionResultDto { UserName = "musteri", UserDeleted = true };

        var yanit = await SilmeUcunuCalistir(rapor);
        var sonuc = Assert.IsType<OkObjectResult>(yanit.Result);
        var zarf = Assert.IsType<ApiResponse<UserDeletionResultDto>>(sonuc.Value);

        Assert.True(zarf.Success);
        Assert.Equal("users.delete.success", zarf.Message);
    }

    /// <summary>
    /// Bilerek bırakılanlar sonucu hataya ÇEVİRMEZ: <c>removeSites=false</c> seçen yönetici
    /// (ve WHMCS'in varsayılanı) her seferinde "kısmi silme" hatası almamalı.
    /// </summary>
    [Fact]
    public async Task DeliberateLeftovers_DoNotTurnTheResponseIntoAnError()
    {
        var rapor = new UserDeletionResultDto
        {
            UserName = "musteri",
            UserDeleted = true,
            Leftovers =
            {
                new UserDeletionLeftoverDto
                {
                    SiteName = "devredilen.com",
                    Kind = SiteLeftoverKinds.Site,
                    Name = "devredilen.com",
                    Reason = "siteleri silme seçilmedi",
                    Deliberate = true
                }
            }
        };

        var yanit = await SilmeUcunuCalistir(rapor);
        Assert.IsType<OkObjectResult>(yanit.Result);
    }

    // ---- "Zaten yok" işareti dizgeye değil KAYNAK TİPİNE bakıyor ----

    /// <summary>
    /// <b>Kırılganlık:</b> cascade, istisnanın mesajı <c>_not_found</c> ile bitiyorsa kaynağı
    /// "kaldırıldı" sayıyordu. Depoda 35 <c>*_not_found</c> anahtarı var ve bir kısmı kaynağın
    /// değil ARACIN/YOLUN yokluğunu bildiriyor. Bunlardan biri yıkıcı bir adımda yüzeye çıksa
    /// panel "temizlendi" derdi.
    /// </summary>
    [Theory]
    [InlineData("php.executable_not_found")]
    [InlineData("nginx.executable_not_found")]
    [InlineData("cron.script_not_found")]
    [InlineData("quota.path_not_found")]
    [InlineData("git.path_not_found")]
    public void AMissingToolOrPath_IsNeverCountedAsARemovedResource(string anahtar)
    {
        // ESKİ KURALLA hepsi "silindi" sayılırdı (mesaj `_not_found` ile bitiyor).
        Assert.EndsWith("_not_found", anahtar, StringComparison.Ordinal);
        Assert.False(SiteCascadeDeletionService.KaynakZatenYokMu(new AppException(anahtar)));
    }

    /// <summary>...ama gerçekten "aradım, yoktu" diyen istisna idempotent kalmalı.</summary>
    [Fact]
    public void AResourceThatIsGenuinelyAbsent_IsStillTreatedAsRemoved()
    {
        Assert.True(SiteCascadeDeletionService.KaynakZatenYokMu(AppException.Gone("mail.domain_not_found")));
    }

    /// <summary>Bayrağı taşımayan sıradan istisnalar da asla "silindi" değildir.</summary>
    [Fact]
    public void APlainExceptionIsNeverTreatedAsRemoved()
    {
        Assert.False(SiteCascadeDeletionService.KaynakZatenYokMu(new InvalidOperationException("site_not_found")));
        Assert.False(SiteCascadeDeletionService.KaynakZatenYokMu(new IOException("erişim reddedildi")));
    }

    // ---- Cascade'in KENDİSİ: başarısız adım kalıntı üretiyor mu? ----
    //
    // NEDEN AYRI BÖLÜM: yukarıdaki servis testleri cascade'i taklit ediyor, yani "cascade kalıntı
    // bildirirse ne olur" sorusunu ölçüyorlar. Cascade'in kalıntıyı GERÇEKTEN bildirip bildirmediği
    // ayrı bir sorudur ve bir mutasyon turunda tam olarak burası açık çıktı: `StepAsync`'teki
    // kalıntı satırı silindi ve HİÇBİR test kırılmadı.

    /// <summary>Başarısız bir yıkıcı adım, kaynağı TİPİ VE ADIYLA kalıntı listesine yazar.</summary>
    [Fact]
    public async Task AFailedCascadeStep_RecordsTheResourceAsALeftover()
    {
        var sonuc = new SiteDeletionResultDto { SiteName = "musteri.com" };

        var basarili = await CascadeYalnizAdimIcin().StepAsync(
            sonuc,
            "Mail domaini (musteri.com)",
            () => Task.FromException(new AppException("mail.operation_failed", details: "Stalwart reddetti")),
            SiteLeftoverKinds.MailDomain,
            "musteri.com");

        Assert.False(basarili);
        Assert.Empty(sonuc.Removed);

        var kalinti = Assert.Single(sonuc.Leftovers);
        Assert.Equal(SiteLeftoverKinds.MailDomain, kalinti.Kind);
        Assert.Equal("musteri.com", kalinti.Name);
        Assert.False(kalinti.Deliberate);
        Assert.Contains("Stalwart reddetti", kalinti.Reason, StringComparison.Ordinal);
        Assert.False(sonuc.FullyDeleted);
    }

    /// <summary>
    /// KIRILGANLIĞIN GERÇEK YOLDA KAPANDIĞININ KANITI: "araç yok" hatası artık adımı
    /// <c>Removed</c>'a yazmıyor, kaynağı kalıntı sayıyor. Eski kuralda (mesaj <c>_not_found</c>
    /// ile bitiyor) bu adım "kaldırıldı" görünürdü.
    /// </summary>
    [Fact]
    public async Task AStepThatFailsBecauseAToolIsMissing_IsALeftover_NotARemovedResource()
    {
        var sonuc = new SiteDeletionResultDto { SiteName = "musteri.com" };

        var basarili = await CascadeYalnizAdimIcin().StepAsync(
            sonuc,
            "Disk kotası",
            () => Task.FromException(new AppException("quota.path_not_found", 404, 404)),
            SiteLeftoverKinds.Other,
            "musteri.com");

        Assert.False(basarili);
        Assert.Empty(sonuc.Removed);
        Assert.Single(sonuc.Leftovers);
        Assert.False(sonuc.FullyDeleted);
    }

    /// <summary>...ama kaynak GERÇEKTEN yoksa adım idempotenttir: kalıntı yazılmaz.</summary>
    [Fact]
    public async Task AStepWhoseResourceWasGenuinelyAbsent_LeavesNoLeftover()
    {
        var sonuc = new SiteDeletionResultDto { SiteName = "musteri.com" };

        var basarili = await CascadeYalnizAdimIcin().StepAsync(
            sonuc,
            "Mail domaini (musteri.com)",
            () => Task.FromException(AppException.Gone("mail.domain_not_found")),
            SiteLeftoverKinds.MailDomain,
            "musteri.com");

        Assert.True(basarili);
        Assert.Single(sonuc.Removed);
        Assert.Empty(sonuc.Leftovers);
        Assert.True(sonuc.FullyDeleted);
    }

    /// <summary>
    /// Y7'NİN MERKEZİ, GERÇEK DOSYALARLA: klasör yarım silinirse sonuç kalıntı taşır ve site
    /// "tamamen silindi" SAYILMAZ. Kilitli dosya, IIS'in yazmakta olduğu bir günlüğün taklididir.
    /// </summary>
    [Fact]
    public void APartiallyDeletedFolder_BecomesALeftover()
    {
        var siteKoku = Path.Combine(Path.GetTempPath(), $"ada-kalinti-{Guid.NewGuid():N}");
        var siteKlasoru = Path.Combine(siteKoku, "musteri.com");
        Directory.CreateDirectory(Path.Combine(siteKlasoru, "zkilit"));
        File.WriteAllText(Path.Combine(siteKlasoru, "index.php"), "<?php");
        var kilitliDosya = Path.Combine(siteKlasoru, "zkilit", "acik.log");
        File.WriteAllText(kilitliDosya, "canli");

        try
        {
            // Politika testin kendi ölçtüğü değerlerden kuruluyor: kapı GEVŞETİLMİYOR, yalnızca
            // geçici klasör meşru bir site kökü olarak tanıtılıyor (üretimde bunu panel tercihleri
            // ve IIS'in site listesi yapar).
            var politika = new SiteDeletionPathPolicy { KnownSiteRoots = [siteKoku] };
            var sonuc = new SiteDeletionResultDto { SiteName = "musteri.com" };

            using (var tutucu = new FileStream(kilitliDosya, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                CascadeYalnizAdimIcin().TryDeleteDirectory(sonuc, "Site dosyaları", siteKlasoru, politika);
            }

            // Klasör duruyorsa (kilit tuttu) kalıntı YAZILMIŞ olmalı; makine kilidi yok sayıp
            // her şeyi silebildiyse iddia da tersine döner — ikisi de DOĞRU davranıştır.
            if (Directory.Exists(siteKlasoru))
            {
                var kalinti = Assert.Single(sonuc.Leftovers);
                Assert.Equal(SiteLeftoverKinds.Files, kalinti.Kind);
                Assert.Equal(siteKlasoru, kalinti.Name, ignoreCase: true);
                Assert.False(sonuc.FullyDeleted);
            }
            else
            {
                Assert.Empty(sonuc.Leftovers);
                Assert.True(sonuc.FullyDeleted);
            }
        }
        finally
        {
            try { Directory.Delete(siteKoku, recursive: true); } catch { /* temizlik en iyi çaba */ }
        }
    }

    /// <summary>
    /// Kapı KAPALIYSA da kalıntı yazılır: klasör başka bir siteye ait olduğu için silinmedi, ama
    /// kullanıcı onun silinmesini istemişti — sessiz kalmak "silindi" izlenimi bırakırdı.
    /// <b>Kapının kendisi burada DOĞRULANIYOR, gevşetilmiyor.</b>
    /// </summary>
    [Fact]
    public void AFolderTheGuardRefusesToDelete_IsStillReportedAsALeftover()
    {
        var siteKoku = Path.Combine(Path.GetTempPath(), $"ada-kapi-{Guid.NewGuid():N}");
        var siteKlasoru = Path.Combine(siteKoku, "musteri.com");
        Directory.CreateDirectory(Path.Combine(siteKlasoru, "baska-site"));

        try
        {
            var politika = new SiteDeletionPathPolicy
            {
                KnownSiteRoots = [siteKoku],
                // Y8: silinecek klasör BAŞKA bir sitenin klasörünü kapsıyor.
                OtherSitePaths = [Path.Combine(siteKlasoru, "baska-site")]
            };
            var sonuc = new SiteDeletionResultDto { SiteName = "musteri.com" };

            CascadeYalnizAdimIcin().TryDeleteDirectory(sonuc, "Site dosyaları", siteKlasoru, politika);

            // Kapı tuttu: klasör DURUYOR.
            Assert.True(Directory.Exists(siteKlasoru));

            // ...ve bu sessizce geçilmedi.
            var kalinti = Assert.Single(sonuc.Leftovers);
            Assert.Equal(SiteLeftoverKinds.Files, kalinti.Kind);
            Assert.False(sonuc.FullyDeleted);
        }
        finally
        {
            try { Directory.Delete(siteKoku, recursive: true); } catch { /* temizlik en iyi çaba */ }
        }
    }

    // ---- Sonuç nesnelerinin "tamamlandı" tanımı ----

    [Fact]
    public void ADeliberateLeftoverDoesNotMakeASiteDeletionIncomplete()
    {
        var sonuc = new SiteDeletionResultDto
        {
            Leftovers = { new SiteLeftoverDto { Kind = SiteLeftoverKinds.Database, Name = "mysql:x", Deliberate = true } }
        };
        Assert.True(sonuc.FullyDeleted);

        sonuc.Leftovers.Add(new SiteLeftoverDto { Kind = SiteLeftoverKinds.Files, Name = @"D:\x" });
        Assert.False(sonuc.FullyDeleted);
    }

    // ---- Yardımcılar ----

    /// <summary>
    /// YALNIZCA <c>StepAsync</c>'i çalıştırmak için cascade örneği.
    /// </summary>
    /// <remarks>
    /// Bağımlılıklar <c>null!</c> geçiliyor ve bu BİLEREK: C# birincil kurucu parametreleri
    /// yalnız KULLANILDIKLARI yerde alan olarak yakalar, <c>StepAsync</c> ise bunlardan yalnız
    /// <c>logger</c>'a dokunuyor. Alternatif, tek bir metodu ölçmek için on sekiz arayüzün boş
    /// gövdesini yazmaktı; o kadar iskele testin okunmasını da yazılmasını da engelliyordu.
    /// Bir bağımlılık ileride bu yola girerse test <c>NullReferenceException</c> ile SESSİZ
    /// KALMADAN kırılır — yani seçim ölçülebilir kalıyor.
    /// </remarks>
    private static SiteCascadeDeletionService CascadeYalnizAdimIcin()
        => new(
            null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<SiteCascadeDeletionService>.Instance);

    /// <summary>
    /// Ucu GERÇEK denetleyiciyle çalıştırır: yanıtın durum kodu ve zarfı kaynak metninden değil
    /// çalışan koddan ölçülsün.
    /// </summary>
    private static async Task<ActionResult<ApiResponse<UserDeletionResultDto>>> SilmeUcunuCalistir(UserDeletionResultDto rapor)
    {
        var controller = new UsersController(new SabitKullaniciServisi(rapor))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, YoneticiId.ToString()),
                            new Claim(ClaimTypes.Role, "Administrator")
                        ],
                        "test",
                        ClaimTypes.Name,
                        ClaimTypes.Role))
                }
            }
        };

        return await controller.DeleteUserAsync(Guid.NewGuid(), removeSites: true, CancellationToken.None);
    }

    private sealed class Duzenek : IDisposable
    {
        private readonly SqliteConnection baglanti;
        private readonly AdaPanelDbContext dbContext;

        private Duzenek(
            SqliteConnection baglanti,
            AdaPanelDbContext dbContext,
            UserManagementService servis,
            SahteKapsam kapsam,
            SahteCascade cascade,
            SahteDbSahiplik dbSahiplik,
            RecordingAuditService denetim,
            Guid musteriId)
        {
            this.baglanti = baglanti;
            this.dbContext = dbContext;
            Servis = servis;
            Kapsam = kapsam;
            Cascade = cascade;
            DbSahiplik = dbSahiplik;
            Denetim = denetim;
            MusteriId = musteriId;
        }

        public UserManagementService Servis { get; }
        public SahteKapsam Kapsam { get; }
        public SahteCascade Cascade { get; }
        public SahteDbSahiplik DbSahiplik { get; }
        public RecordingAuditService Denetim { get; }
        public Guid MusteriId { get; }

        public static Duzenek Kur()
        {
            var baglanti = new SqliteConnection("Filename=:memory:");
            baglanti.Open();
            var options = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(baglanti).Options;
            var dbContext = new AdaPanelDbContext(options);
            dbContext.Database.EnsureCreated();

            var yonetici = new PanelUser
            {
                Id = YoneticiId,
                UserName = "yonetici",
                Email = "yonetici@ornek.test",
                Role = Core.Enums.UserRole.Administrator,
                PasswordHash = "x",
                IsActive = true
            };
            var musteri = new PanelUser
            {
                UserName = "musteri",
                Email = "musteri@ornek.test",
                Role = Core.Enums.UserRole.Customer,
                PasswordHash = "x",
                IsActive = true
            };
            dbContext.Users.AddRange(yonetici, musteri);
            dbContext.SaveChanges();

            var kapsam = new SahteKapsam();
            var cascade = new SahteCascade();
            var dbSahiplik = new SahteDbSahiplik();
            var denetim = new RecordingAuditService();

            var servis = new UserManagementService(
                dbContext,
                new PasswordHasher<PanelUser>(),
                kapsam,
                new ThrowingNotificationService(),
                new ThrowingPreferencesService(),
                new ThrowingSecurityService(),
                NullLogger<UserManagementService>.Instance,
                denetim,
                new ResellerSiteAssignmentTests.KullanilmayanSiteServisi(),
                dbSahiplik,
                new BosZamanlayici(),
                cascade,
                new ThrowingApiTokenService(),
                new SahipsizSunucuSahibiServisi());

            return new Duzenek(baglanti, dbContext, servis, kapsam, cascade, dbSahiplik, denetim, musteri.Id);
        }

        public void Dispose()
        {
            dbContext.Dispose();
            baglanti.Dispose();
        }
    }

    private sealed class SahteKapsam : ICustomerScopeService
    {
        public string[] Siteler { get; set; } = [];

        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerScope(Siteler, 0, 0, 0, 0, 0));

        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Guid>>([]);
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);

        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    /// <summary>Cascade'in ne döndüğünü test belirler; asıl ölçülen, dönenin NE YAPILDIĞIdır.</summary>
    private sealed class SahteCascade : ISiteCascadeDeletionService
    {
        public Dictionary<string, SiteDeletionResultDto> Sonuclar { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Patlat { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Silinenler { get; } = [];

        public Task<SiteDeletionResultDto> DeleteAsync(string siteName, bool deleteFiles, bool deleteDatabases, CancellationToken cancellationToken = default)
        {
            Silinenler.Add(siteName);
            if (Patlat.Contains(siteName))
            {
                throw new InvalidOperationException("iis_unreachable");
            }

            return Task.FromResult(Sonuclar.TryGetValue(siteName, out var sonuc)
                ? sonuc
                : new SiteDeletionResultDto { SiteName = siteName });
        }

        public Task<SiteDeletionPlanDto> GetPlanAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteDbSahiplik : IDatabaseOwnershipService
    {
        public List<(string Engine, string DatabaseName)> Sahipli { get; } = [];
        public List<(string Engine, string DatabaseName)> Silinenler { get; } = [];

        public Task<IReadOnlyCollection<(string Engine, string DatabaseName)>> GetOwnedAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<(string, string)>>(Sahipli.ToArray());

        public Task RemoveAsync(string engine, string databaseName, CancellationToken cancellationToken = default)
        {
            Silinenler.Add((engine, databaseName));
            return Task.CompletedTask;
        }

        public Task<int> CountForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task RecordAsync(string engine, string databaseName, Guid ownerUserId, string? siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveBySiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Silme yolu cron görevlerini de tarıyor; boş liste dönmek yeterli.</summary>
    private sealed class BosZamanlayici : ISchedulerService
    {
        public Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<ScheduledTaskDto>>([]);

        public Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, ScheduledTaskPrivilege privilege, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RunTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskStatusDto> GetTaskStatusAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScheduledTaskOutputDto> GetTaskOutputAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskEnvironmentDto> GetEnvironmentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MachinePathMeasurement> AddPythonToMachinePathAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StopScheduledTaskResultDto> StopTaskAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RepairTaskSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoveTasksUnderRootAsync(string siteRootPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Uç testleri için: servis yalnızca hazır bir rapor döner.</summary>
    private sealed class SabitKullaniciServisi(UserDeletionResultDto rapor) : IUserManagementService
    {
        public Task<UserDeletionResultDto> DeleteUserAsync(Guid userId, Guid actorUserId, bool removeSites, string? ipAddress, CancellationToken cancellationToken = default)
            => Task.FromResult(rapor);

        public Task<IReadOnlyCollection<UserSummaryDto>> GetUsersAsync(Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserSummaryDto> CreateUserAsync(CreateUserRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserSummaryDto> UpdateUserAsync(Guid userId, UpdateUserRequest request, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserSummaryDto> UnlockUserAsync(Guid userId, Guid actorUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SubUserDto>> GetSubUsersAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubUserDto> CreateSubUserAsync(Guid parentUserId, CreateSubUserRequest request, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubUserDto> UpdateSubUserAsync(Guid parentUserId, Guid subUserId, UpdateSubUserRequest request, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSubUserAsync(Guid parentUserId, Guid subUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
