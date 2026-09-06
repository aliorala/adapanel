using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Git;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Notifications;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DIŞARI GİDEN HEDEF ADRESLERİN tek kaynaklı denetimi: hem kararın kendisi
/// (<see cref="OutboundAddressGuard.FindBlockedHostAsync"/>) hem de beş çağrı yerine GERÇEKTEN
/// bağlanmış olması.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN İKİ KATMAN TEST:</b> bu depoda "koruma yazıldı ama çağrı yerine bağlanmadı" hatası
/// ölçülmüş biçimde yaşandı (<c>CronController</c>'ın kendi kopyası, <c>MeasureIsolation</c>'ın
/// yalnız öneke bakması). Yalnız saf fonksiyonu sınamak, korumanın var olduğunu kanıtlar ama
/// KULLANILDIĞINI kanıtlamaz. Bu yüzden her çağrı yeri için ayrıca bir "servis GERÇEKTEN
/// reddediyor mu" testi var.
/// </para>
/// <para>
/// <b>HİÇBİR TEST GERÇEK BİR SSRF İSTEĞİ ATMAZ.</b> Çözümleyici sahtedir; <c>169.254.169.254</c>
/// (bulut meta verisi) bu dosyada yalnızca bir DİZE olarak geçer, hiçbir sokete verilmez. Açık
/// kapı testlerinde kullanılan tek gerçek soket <c>127.0.0.1:1</c>'dir — loopback'te kesin
/// reddedilen port, veri taşımaz (aynı kalıp <c>MigrationObservabilityTests</c>'te de kullanılıyor).
/// </para>
/// </remarks>
public sealed class OutboundTargetGuardTests
{
    // =====================================================================================
    // KATMAN 1 — KARARIN KENDİSİ
    // =====================================================================================

    /// <summary>
    /// ASIL İDDİA: ad denetlemek YETMEZ, ad ÇÖZÜLÜR ve ÇIKAN ADRES denetlenir.
    /// </summary>
    /// <remarks>
    /// Saldırganın kendi alan adı hiçbir engel listesinde geçmez; onu tehlikeli yapan tek şey
    /// A kaydının nereyi gösterdiğidir. MUTASYON: <c>FindBlockedHostAsync</c> içindeki çözümleme
    /// dalı silinip yalnız ad/literal denetimi bırakılırsa bu test düşer.
    /// </remarks>
    [Theory]
    [InlineData("127.0.0.1")]          // geri döngü
    [InlineData("169.254.169.254")]    // BULUT META VERİSİ
    [InlineData("10.0.0.5")]           // iç ağdaki başka müşteri
    [InlineData("::ffff:169.254.169.254")] // IPv4-eşlemeli yazılmış meta veri
    public async Task Saldirganin_kendi_alan_adi_IC_ADRESE_cozuluyorsa_reddedilir(string cozulenAdres)
    {
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            "kotu.example",
            Cozumleyici(("kotu.example", cozulenAdres)),
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.NotNull(engellenen);
    }

    /// <summary>Düz IP yazımı: aralıkların hepsi kapalı olmalı.</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]   // Alibaba meta verisi (CGNAT)
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]         // çoklu gönderim
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("fe80::1")]           // bağlantı yerel
    [InlineData("fd00::1")]           // fc00::/7 benzersiz yerel
    public async Task Ic_ve_tuzak_araliklar_duz_IP_yazildiginda_reddedilir(string adres)
    {
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            adres,
            HicCagrilmayanCozumleyici,
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.NotNull(engellenen);
    }

    /// <summary>
    /// ONDALIK / SEKİZLİK / ONALTILIK IP YAZIMLARI. Hepsi 127.0.0.1'e çıkar.
    /// </summary>
    /// <remarks>
    /// Bu makinede ölçüldü: <c>IPAddress.TryParse</c> dördünü de kabul edip noktalı dörtlüye
    /// normalleştiriyor — yani ayrı bir ayrıştırıcıya gerek yok. Test yine de duruyor: gelecekte
    /// çerçeve davranışı değişirse ya da biri araya kendi ayrıştırıcısını koyarsa, sessizce
    /// açılan bu kapıyı yakalayacak tek şey burasıdır.
    /// </remarks>
    [Theory]
    [InlineData("2130706433")]     // ondalık
    [InlineData("0x7f000001")]     // onaltılık
    [InlineData("017700000001")]   // sekizlik
    [InlineData("127.1")]          // kısa yazım
    public async Task Sayisal_IP_yazimlari_da_ayni_adrese_cikar_ve_reddedilir(string yazim)
    {
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            yazim,
            HicCagrilmayanCozumleyici,
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.NotNull(engellenen);
    }

    /// <summary>Çözülmeden reddedilen adlar.</summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost.")]              // sondaki nokta aynı addır
    [InlineData("metadata.google.internal")]
    [InlineData("bir.sey.internal")]
    [InlineData("nas.local")]
    public async Task Tanim_geregi_ic_olan_adlar_cozulmeden_reddedilir(string ad)
    {
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            ad,
            HicCagrilmayanCozumleyici,
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.NotNull(engellenen);
    }

    /// <summary>
    /// TEK engelli adres yeterlidir: ad hem genel hem iç adrese çözülüyorsa reddedilir.
    /// </summary>
    /// <remarks>
    /// MUTASYON: <c>FirstOrDefault(IsBlockedAddress)</c> yerine <c>All(IsBlockedAddress)</c>
    /// yazılırsa bu test düşer — koruma "çoğunluk temizse geç" diyen bir yazı-turaya dönüşürdü.
    /// </remarks>
    [Fact]
    public async Task Ad_hem_genel_hem_ic_adrese_cozuluyorsa_reddedilir()
    {
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            "ikiyuzlu.example",
            Cozumleyici(("ikiyuzlu.example", "93.184.216.34"), ("ikiyuzlu.example", "169.254.169.254")),
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.Equal("169.254.169.254", engellenen);
    }

    /// <summary>MEŞRU DIŞ HEDEF GEÇMELİ — fazla kapatmak da bir hatadır.</summary>
    [Fact]
    public async Task Genel_adrese_cozulen_ad_gecer()
    {
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            "yedek.ornek.com",
            Cozumleyici(("yedek.ornek.com", "93.184.216.34")),
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.Null(engellenen);
    }

    /// <summary>"Ad çözülemedi" kararı politikadan gelir, sabit değildir.</summary>
    [Theory]
    [InlineData(true, false)]   // bağlanan yollar: ölçemedik = geçemez
    [InlineData(false, true)]   // anonim/cron yolu: geçici DNS hatası meşru hedefi engellemesin
    public async Task Cozulemeyen_ad_karari_politikadan_gelir(bool cozulemezseEngelle, bool beklenenGecer)
    {
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            "cozulemez.example",
            (_, _) => throw new System.Net.Sockets.SocketException(11001),
            new OutboundTargetPolicy(AllowInternalTargets: false, BlockUnresolvedHost: cozulemezseEngelle),
            CancellationToken.None);

        Assert.Equal(beklenenGecer, engellenen is null);
    }

    /// <summary>
    /// AÇIK KAPI: sunucu işleticisi bilerek açtıysa iç hedefler geçer.
    /// </summary>
    /// <remarks>
    /// Bu test korumanın "kapatılabildiğini" değil, KAPATILABİLİRLİĞİN ÇALIŞTIĞINI ölçer. Bayrak
    /// çalışmasaydı iç ağdaki NAS'a yedek atan ya da yerel SMTP kullanan her kurulum kırılırdı ve
    /// bu değişiklik güvenlik adına çalışan paneli bozmak olurdu.
    /// </remarks>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("localhost")]
    [InlineData("169.254.169.254")]
    public async Task Acik_kapi_acikken_ic_hedefler_gecer(string hedef)
    {
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            hedef,
            HicCagrilmayanCozumleyici,
            new OutboundTargetPolicy(AllowInternalTargets: true, BlockUnresolvedHost: true),
            CancellationToken.None);

        Assert.Null(engellenen);
    }

    // =====================================================================================
    // KATMAN 2 — ÇAĞRI YERLERİ. "Koruma var" değil, "koruma BAĞLI".
    // =====================================================================================

    /// <summary>
    /// YEDEK UZAK HEDEFİ (en yüksek öncelik). Bağlantı testi ucu, verilen adrese HEMEN dosya
    /// yükleyen en keskin uçtur; denetim yüklemeden ÖNCE olmak zorunda.
    /// </summary>
    [Theory]
    [InlineData("http", "http://127.0.0.1:9200/yedek")]
    [InlineData("webdav", "http://169.254.169.254/yedek")]
    [InlineData("http", "http://[::ffff:169.254.169.254]/yedek")]
    [InlineData("http", "http://2130706433/yedek")]      // ondalık yazılmış 127.0.0.1
    [InlineData("s3", "https://10.0.0.5/kova")]
    public async Task Yedek_baglanti_testi_ic_hedefi_YUKLEMEDEN_reddeder(string tur, string adres)
    {
        using var alan = new BulutAlani();
        var servis = YedekServisi(alan, icAgaIzin: false);

        var sonuc = await servis.TestRemoteTargetAsync(
            new BackupRemoteTestRequest { TargetType = tur, RemoteUrl = adres, RemoteUserName = "u", RemotePassword = "p" },
            alan.Iptal);

        Assert.False(sonuc.Success);
        Assert.Equal("backup.remote_target_forbidden", sonuc.Message);
    }

    /// <summary>Yedek hedefi bir AD ise de çözülür; ad denetimi tek başına yetmez.</summary>
    [Fact]
    public async Task Yedek_hedefi_ic_adrese_COZULEN_ad_ise_de_reddedilir()
    {
        using var alan = new BulutAlani();
        var servis = YedekServisi(alan, icAgaIzin: false, ("kotu.example", "169.254.169.254"));

        var sonuc = await servis.TestRemoteTargetAsync(
            new BackupRemoteTestRequest { TargetType = "http", RemoteUrl = "http://kotu.example/yedek" },
            alan.Iptal);

        Assert.False(sonuc.Success);
        Assert.Equal("backup.remote_target_forbidden", sonuc.Message);
    }

    /// <summary>
    /// OAUTH HEDEFLERİ DENETİMDEN GEÇMEZ. <c>dropbox:///yedekler</c> içindeki "host" bir ağ konağı
    /// değil klasör yoludur; çözümlemeye kalkmak çalışan bir hedefi "çözülemedi" diye reddederdi.
    /// </summary>
    [Theory]
    [InlineData("dropbox", "/yedekler")]
    [InlineData("onedrive", "/yedekler")]
    public async Task Sabit_uc_noktali_bulut_hedefleri_adres_denetiminden_ETKILENMEZ(string tur, string klasor)
    {
        using var alan = new BulutAlani();
        var servis = YedekServisi(alan, icAgaIzin: false);

        var sonuc = await servis.TestRemoteTargetAsync(
            new BackupRemoteTestRequest { TargetType = tur, RemoteUrl = klasor, RemotePassword = "jeton" },
            alan.Iptal);

        // Sahte bulutla başarılı da olabilir, başka bir sebeple de düşebilir; ölçtüğümüz tek şey
        // ADRES YASAĞINA TAKILMAMASI.
        Assert.NotEqual("backup.remote_target_forbidden", sonuc.Message);
    }

    /// <summary>Yedek için açık kapı: bayrak açıkken iç hedef yasağa TAKILMAZ.</summary>
    [Fact]
    public async Task Yedekte_acik_kapi_acikken_ic_hedef_yasaga_takilmaz()
    {
        using var alan = new BulutAlani();
        var servis = YedekServisi(alan, icAgaIzin: true);

        // 127.0.0.1:1 — loopback'te kesin reddedilen port. Yükleme başarısız olur ama sebebi
        // ADRES YASAĞI olmamalı; ölçtüğümüz tam olarak bu ayrım.
        var sonuc = await servis.TestRemoteTargetAsync(
            new BackupRemoteTestRequest { TargetType = "http", RemoteUrl = "http://127.0.0.1:1/yedek" },
            alan.Iptal);

        Assert.NotEqual("backup.remote_target_forbidden", sonuc.Message);
    }

    /// <summary>
    /// ZAMANLANMIŞ GÖREV URL'Sİ. Görev <c>curl.exe</c> ile SYSTEM olarak koşar; adres denetimi
    /// görev KURULMADAN önce olmalı.
    /// </summary>
    /// <remarks>
    /// Yalnız RED yolu servis üzerinden ölçülüyor: geçen bir adres gerçekten bir Görev
    /// Zamanlayıcı kaydı yaratırdı ve test makinesinde kalıcı iz bırakırdı.
    /// </remarks>
    [Theory]
    [InlineData("http://127.0.0.1:9200/tetikle")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://0x7f000001/tetikle")]
    [InlineData("http://192.168.1.1/tetikle")]
    public async Task Zamanlanmis_gorev_ic_adresi_hedefleyemez(string adres)
    {
        var servis = new SchedulerService(
            new HicCagrilmayanGocSiteServisi(),
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions()),
            NullLogger<SchedulerService>.Instance,
            HicCagrilmayanCozumleyici);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CreateTaskAsync(new CreateScheduledTaskRequest
        {
            Name = "gece-tetikleyici",
            TaskType = "url",
            Url = adres,
            ScheduleType = "daily",
            TimeOfDay = "03:00"
        }, ScheduledTaskPrivilege.Full));

        Assert.Equal("cron.url_forbidden", hata.MessageKey);
    }

    /// <summary>Zamanlanmış görevde de ad ÇÖZÜLÜR.</summary>
    [Fact]
    public async Task Zamanlanmis_gorevde_ic_adrese_cozulen_ad_reddedilir()
    {
        var servis = new SchedulerService(
            new HicCagrilmayanGocSiteServisi(),
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions()),
            NullLogger<SchedulerService>.Instance,
            Cozumleyici(("kotu.example", "127.0.0.1")));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CreateTaskAsync(new CreateScheduledTaskRequest
        {
            Name = "gece-tetikleyici",
            TaskType = "url",
            Url = "http://kotu.example/tetikle",
            ScheduleType = "daily",
            TimeOfDay = "03:00"
        }, ScheduledTaskPrivilege.Full));

        Assert.Equal("cron.url_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// DOSYA GÖÇÜ. <c>Host</c> + <c>Port</c> istek gövdesinden gelip doğrudan sokete gidiyordu:
    /// keyfi host:port TCP bağlantısı, yani iç ağ port taraması.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("2130706433")]   // ÇIPLAK dize; Uri'den geçmiyor
    [InlineData("localhost")]
    public async Task Dosya_gocu_ic_konaga_baglanamaz(string konak)
    {
        var servis = new FileMigrationService(
            new HicCagrilmayanGocSiteServisi(),
            outboundResolver: HicCagrilmayanCozumleyici);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.PullAsync(new RemoteFileMigrationRequest
        {
            SiteName = "ornek.test",
            Protocol = "ftp",
            Host = konak,
            Port = 21,
            UserName = "u",
            Password = "p",
            RemotePath = "/httpdocs"
        }));

        Assert.Equal("migration.host_forbidden", hata.MessageKey);
    }

    /// <summary>Göçte açık kapı: bayrak açıkken iç konak yasağa TAKILMAZ.</summary>
    [Fact]
    public async Task Gocte_acik_kapi_acikken_ic_konak_yasaga_takilmaz()
    {
        var hedefKok = Path.Combine(Path.GetTempPath(), "ada-goc-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hedefKok);
        try
        {
            var servis = new FileMigrationService(
                new SahteHedefliSiteServisi(hedefKok),
                runtimeOptions: Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { AllowInternalOutboundTargets = true }),
                outboundResolver: HicCagrilmayanCozumleyici);

            // 127.0.0.1:1 — bağlantı kesin reddedilir; ölçtüğümüz şey hatanın ADRES YASAĞI
            // OLMAMASI, yani açık kapının gerçekten açılması.
            var hata = await Assert.ThrowsAsync<AppException>(() => servis.PullAsync(new RemoteFileMigrationRequest
            {
                SiteName = "ornek.test",
                Protocol = "ftp",
                Host = "127.0.0.1",
                Port = 1,
                UserName = "u",
                Password = "p",
                RemotePath = "/httpdocs"
            }));

            Assert.NotEqual("migration.host_forbidden", hata.MessageKey);
        }
        finally
        {
            Directory.Delete(hedefKok, recursive: true);
        }
    }

    /// <summary>BİLDİRİM WEBHOOK'U — ayar kaydedilirken denetlenir.</summary>
    [Theory]
    [InlineData("http://127.0.0.1:9200/bildir", "notifications.webhook_forbidden")]
    [InlineData("http://169.254.169.254/bildir", "notifications.webhook_forbidden")]
    [InlineData("http://[::ffff:10.0.0.5]/bildir", "notifications.webhook_forbidden")]
    public async Task Bildirim_webhooku_ic_adresi_hedefleyemez(string adres, string beklenenAnahtar)
    {
        var servis = BildirimServisi(icAgaIzin: false);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.UpdateSettingsAsync(
            new UpdateNotificationSettingsRequest { WebhookUrl = adres, SmtpHost = string.Empty }));

        Assert.Equal(beklenenAnahtar, hata.MessageKey);
    }

    /// <summary>SMTP KONAĞI — çıplak dize, Uri'den geçmiyor.</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("10.0.0.5")]
    [InlineData("2130706433")]
    public async Task Bildirim_SMTP_konagi_ic_adres_olamaz(string konak)
    {
        var servis = BildirimServisi(icAgaIzin: false);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.UpdateSettingsAsync(
            new UpdateNotificationSettingsRequest { WebhookUrl = string.Empty, SmtpHost = konak }));

        Assert.Equal("notifications.smtp_host_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// AÇIK KAPI BİLDİRİMDE ÖZELLİKLE ÖNEMLİ: aynı makinedeki hMailServer/Stalwart ya da iç ağdaki
    /// bir SMTP aktarıcısı YAYGIN ve meşru bir yapılandırmadır.
    /// </summary>
    [Fact]
    public async Task Bildirimde_acik_kapi_acikken_yerel_SMTP_kaydedilebilir()
    {
        var servis = BildirimServisi(icAgaIzin: true);

        var ayarlar = await servis.UpdateSettingsAsync(new UpdateNotificationSettingsRequest
        {
            WebhookUrl = string.Empty,
            SmtpHost = "127.0.0.1",
            SmtpPort = 25,
            FromEmail = "panel@ornek.test",
            ToEmail = "yonetici@ornek.test"
        });

        Assert.Equal("127.0.0.1", ayarlar.SmtpHost);
    }

    /// <summary>GİT DEPOSU ADRESİ — şema beyaz listesi vardı, konak denetimi yoktu.</summary>
    [Theory]
    [InlineData("http://127.0.0.1:3000/ekip/site.git")]
    [InlineData("https://169.254.169.254/x.git")]
    [InlineData("ssh://git@10.0.0.5/ekip/site.git")]
    public async Task Git_deposu_ic_adresi_hedefleyemez(string adres)
    {
        var kok = Path.Combine(Path.GetTempPath(), "ada-git-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);
        try
        {
            var servis = new GitDeploymentService(
                new SahteHedefliSiteServisi(kok),
                new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok })),
                new AdaPanel.Infrastructure.Services.ConcurrencyLock(),
                NullLogger<GitDeploymentService>.Instance,
                outboundResolver: HicCagrilmayanCozumleyici);

            var hata = await Assert.ThrowsAsync<AppException>(() => servis.ConfigureAsync(
                "ornek.test",
                new ConfigureGitDeploymentRequest { RepositoryUrl = adres, Branch = "main" }));

            Assert.Equal("git.repository_forbidden", hata.MessageKey);
        }
        finally
        {
            Directory.Delete(kok, recursive: true);
        }
    }

    /// <summary>
    /// SİTE ARŞİVİ İÇE AKTARMA (<c>SiteHostingSettingsService</c>). Bu yolun gövdesi de tek
    /// kaynağa taşındı; taşındıktan sonra hâlâ ölçtüğünü kanıtlayan test YOKTU.
    /// </summary>
    /// <remarks>
    /// Bu yolun kendine ÖZGÜ iki kararı var ve ikisi de burada ölçülüyor: kullanıcı bilgisi
    /// reddi (<c>http://gercek.example@169.254.169.254/x.zip</c> — insan gözü ilk parçayı,
    /// ayrıştırıcı ikincisini okur) ve "çözülemedi = reddet".
    /// </remarks>
    [Theory]
    [InlineData("http://127.0.0.1/site.zip", "127.0.0.1")]
    [InlineData("http://169.254.169.254/site.zip", "169.254.169.254")]
    // Dönen etiket IPv6 YAZIMIDIR (::ffff:10.0.0.5), eşlemenin çözülmüş hâli değil: engel kararı
    // eşlemeyi açarak verilir ama günlüğe/ayrıntıya KULLANICININ YAZDIĞI biçim düşer. Bu bilinçli
    // -- yöneticinin ekranda gördüğü metin, istekte gönderilen metinle aynı olmalı.
    [InlineData("http://[::ffff:10.0.0.5]/site.zip", "::ffff:10.0.0.5")]
    [InlineData("http://gercek.example@169.254.169.254/site.zip", "userinfo")]
    [InlineData("bu bir adres degil", "invalid_uri")]
    public async Task Site_arsivi_ic_adresten_indirilemez(string adres, string beklenen)
    {
        var engellenen = await AdaPanel.Infrastructure.Iis.SiteHostingSettingsService.FindBlockedImportTargetAsync(
            adres,
            (host, _) => throw new InvalidOperationException($"DNS beklenmiyordu: {host}"),
            CancellationToken.None);

        Assert.Equal(beklenen, engellenen);
    }

    /// <summary>İçe aktarmada da ad ÇÖZÜLÜR; meşru dış adres geçer, çözülemeyen reddedilir.</summary>
    [Fact]
    public async Task Site_arsivi_adresinde_ad_cozulur()
    {
        var kotu = await AdaPanel.Infrastructure.Iis.SiteHostingSettingsService.FindBlockedImportTargetAsync(
            "https://kotu.example/site.zip",
            (host, ct) => Cozumleyici(("kotu.example", "127.0.0.1"))(host, ct),
            CancellationToken.None);
        Assert.Equal("127.0.0.1", kotu);

        var iyi = await AdaPanel.Infrastructure.Iis.SiteHostingSettingsService.FindBlockedImportTargetAsync(
            "https://arsiv.ornek.com/site.zip",
            (host, ct) => Cozumleyici(("arsiv.ornek.com", "93.184.216.34"))(host, ct),
            CancellationToken.None);
        Assert.Null(iyi);

        // Çözülemeyen ad REDDEDİLİR: indirmeyi yapan Invoke-WebRequest de aynı çözümleyiciyi
        // kullanır, yani reddetmek hiçbir çalışan senaryoyu kapatmaz.
        var cozulemez = await AdaPanel.Infrastructure.Iis.SiteHostingSettingsService.FindBlockedImportTargetAsync(
            "https://yok.example/site.zip",
            (_, _) => throw new System.Net.Sockets.SocketException(11001),
            CancellationToken.None);
        Assert.Equal("yok.example", cozulemez);
    }

    // =====================================================================================
    // YARDIMCILAR
    // =====================================================================================

    /// <summary>
    /// SAHTE DNS. Gerçek çözümleme yapılmaz — bir birim testi 169.254.169.254'e çözümleme
    /// yapmamalı (ve bu makinede yapamaz da; sonuç ağa göre değişir, test kararsız olurdu).
    /// </summary>
    private static OutboundAddressGuard.OutboundHostResolver Cozumleyici(params (string Ad, string Adres)[] kayitlar)
        => (host, _) =>
        {
            var adresler = kayitlar
                .Where(kayit => string.Equals(kayit.Ad, host, StringComparison.OrdinalIgnoreCase))
                .Select(kayit => IPAddress.Parse(kayit.Adres))
                .ToArray();

            return adresler.Length == 0
                ? throw new System.Net.Sockets.SocketException(11001)
                : Task.FromResult(adresler);
        };

    /// <summary>
    /// Çözümleyicinin HİÇ çağrılmaması gerektiğini ölçen çözümleyici.
    /// </summary>
    /// <remarks>
    /// Düz IP ve engelli ad girdilerinde DNS'e çıkmak gereksiz bir ağ gidiş-dönüşüdür; burada
    /// çağrılırsa test patlar ve sebebi de yazar. Aynı zamanda testin sessizce gerçek ağa
    /// açılmadığının güvencesidir.
    /// </remarks>
    private static readonly OutboundAddressGuard.OutboundHostResolver HicCagrilmayanCozumleyici =
        (host, _) => throw new InvalidOperationException(
            $"Bu girdide DNS cozumlemesi BEKLENMIYORDU ama '{host}' icin cagrildi.");

    private static BackupService YedekServisi(BulutAlani alan, bool icAgaIzin, params (string Ad, string Adres)[] dns)
        => new(new SahteSiteServisi(alan.SiteKoku),
            null!,
            null!,
            new SahteTercihServisi(Path.Combine(alan.Kok, "backups")),
            null!,
            alan.DosyaDeposu,
            new SahteIstemciFabrikasi(new SahteBulut()),
            secretProtector: new SahteKoruyucu(),
            auditService: alan.Denetim,
            runtimeOptions: Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions
            {
                DataRoot = alan.Kok,
                AllowInternalOutboundTargets = icAgaIzin
            }),
            outboundResolver: dns.Length == 0 ? HicCagrilmayanCozumleyici : Cozumleyici(dns));

    private static NotificationService BildirimServisi(bool icAgaIzin)
    {
        var kok = Path.Combine(Path.GetTempPath(), "ada-bildirim-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);
        return new NotificationService(
            new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok })),
            new SahteIstemciFabrikasi(new SahteBulut()),
            new SahteKoruyucu(),
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok, AllowInternalOutboundTargets = icAgaIzin }),
            HicCagrilmayanCozumleyici);
    }

    /// <summary>Adres denetimine takılan çağrılarda site servisine HİÇ uğranmamalı.</summary>
    private sealed class HicCagrilmayanGocSiteServisi : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    private sealed class SahteHedefliSiteServisi(string fizikselYol) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = fizikselYol });

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
