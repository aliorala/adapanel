using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace AdaPanel.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddAdaPanelInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PanelRuntimeOptions>(configuration.GetSection(PanelRuntimeOptions.SectionName));
        services.AddDataProtection();
        services.AddHttpClient();
        services.AddHttpClient("AdaPanel.LocalHealth")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseProxy = false,
                // Sağlık kontrolü "yanıt veriyor mu"ya bakar; yönlendirmeyi İZLEME. phpMyAdmin/Roundcube
                // kökü çoğu zaman 302 (signon/login) döner — bu zaten "ayakta" demektir. Redirect'i izlersek
                // ağır kimlikli sayfayı bekleyip 3sn timeout'a takılırız (webdb.health.http_failed).
                AllowAutoRedirect = false
            });
        // B3 panel reverse-proxy: phpMyAdmin/Adminer'i yerel IIS'e ilet. Yönlendirmeleri (302 signon/login)
        // İZLEME — tarayıcıya geçir ki phpMyAdmin akışı doğru ilerlesin. Cookie'leri factory tutmasın (her
        // istek tarayıcının kendi cookie'siyle gelir/gider).
        services.AddHttpClient("DbToolsProxy")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false
            });
        // KULLANICIDAN ADRES ALAN İSTEKLERİN İSTEMCİSİ — bağlantı ANINDA adres denetimi.
        //
        // NEDEN AYRI BİR İSTEMCİ, NEDEN VARSAYILANI DEĞİŞTİRMİYORUZ: varsayılan istemciyi
        // denetime sokmak paneldeki HER CreateClient() çağrısını etkilerdi — bileşen indirme
        // (components.json), GitHub sürüm çözümleme, phpMyAdmin vekili, Stalwart yönetimi, sağlık
        // kontrolü. Bunların adresi kullanıcıdan GELMEZ; denetime sokmak hiçbir açığı kapatmaz ama
        // yönlendirme/vekil kısıtı yüzünden panelin kendini güncellemesini kırabilirdi. Ayrımın
        // nöbetçi testleri var (OutboundRedirectGuardTests).
        //
        // Politika HER BAĞLANTIDA okunur: Panel:AllowInternalOutboundTargets açık kapısı burada da
        // GEÇERLİDİR, yoksa iç ağdaki NAS'a yedek atan meşru kurulum kapıyı açtığı hâlde soket
        // düzeyinde reddedilirdi.
        services.AddHttpClient(Support.OutboundConnectGuard.GuardedClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => Support.OutboundConnectGuard.CreateHandler(
                () => Support.OutboundTargetPolicy.Connecting(
                    sp.GetService<Microsoft.Extensions.Options.IOptions<PanelRuntimeOptions>>()?.Value.AllowInternalOutboundTargets ?? false),
                onBlocked: (host, blocked) => sp
                    .GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    ?.CreateLogger(Support.OutboundConnectGuard.GuardedClientName)
                    .LogWarning(
                        "Giden baglanti REDDEDILDI: {Host} adi {Address} adresine cozuluyor (ic ag/geri dongu/bulut meta verisi).",
                        host,
                        blocked)));

        services.AddScoped<IPasswordHasher<PanelUser>, PasswordHasher<PanelUser>>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISetupService, SetupService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IServicePlanService, Services.ServicePlanService>();
        services.AddScoped<ICustomerScopeService, Security.CustomerScopeService>();
        services.AddScoped<IPanelSecurityService, PanelSecurityService>();
        services.AddScoped<IPanelPreferencesService, PanelPreferencesService>();
        services.AddScoped<IBrandingService, Branding.BrandingService>();
        services.AddSingleton<IConcurrencyLock, ConcurrencyLock>();
        services.AddSingleton<IOperationProgressService, Services.OperationProgressService>();
        services.AddSingleton<JsonFileStore>();
        // YANITI BEKLETMEMESİ gereken işlerin kuyruğu. SINGLETON olmak ZORUNDA: kuyruk süreç
        // genelinde tek olmalı, kapsamlı (scoped) kayıt her istekte YENİ ve BOŞ bir kuyruk üretir,
        // yani hiçbir iş çalıştıran işçiye ULAŞMAZDI (aynı gerekçe SessionRevocationStore'da).
        services.AddSingleton<ArkaPlanIsKuyrugu>();
        services.AddSingleton<IArkaPlanIsKuyrugu>(saglayici => saglayici.GetRequiredService<ArkaPlanIsKuyrugu>());
        // ÇIKIŞ YAPILMIŞ OTURUMLARIN KARA LİSTESİ. SINGLETON OLMAK ZORUNDA: liste süreç genelinde
        // bellekte tutuluyor ve her istekte okunuyor; kapsamlı (scoped) kayıt her istekte YENİ ve
        // BOŞ bir liste üretirdi, yani kara liste var görünüp hiçbir oturumu düşürmezdi. Aynı
        // gerekçe hız sınırlayıcı için de yazılı.
        services.AddSingleton<Security.SessionRevocationStore>();
        // Ağır uçlarda ANAHTAR BAŞINA eşzamanlılık tavanı. SINGLETON olmak zorunda: sayaç süreç
        // genelinde tek olmalı, yoksa her istek kendi tavanını taşır ve tavan hiç dolmaz.
        services.AddSingleton<ConcurrentWorkGate>();
        // Uzun süren işlerin (yedek/geri yükleme/göç/bileşen kurulumu) sunucu tarafı kaydı.
        // SINGLETON: durumu diskte tutar, istekten isteğe taşınacak alanı yoktur ve panel
        // açılışında yoklanan status uçları onu istekten bağımsız okur.
        services.AddSingleton<IRunningJobService, RunningJobs.RunningJobService>();
        services.AddScoped<Bootstrap.ComponentDiscoveryService>();
        services.AddScoped<IComponentBootstrapper, Bootstrap.ComponentBootstrapper>();
        services.AddScoped<IIisSiteService, Iis.WebAdministrationSiteService>();
        services.AddScoped<IHealthService, Health.HealthService>();
        services.AddScoped<ISiteProvisioningService, Iis.SiteProvisioningService>();
        services.AddScoped<ISiteDefaultsService, Iis.SiteDefaultsService>();
        services.AddScoped<ISiteCascadeDeletionService, Iis.SiteCascadeDeletionService>();
        services.AddScoped<ISiteHostingSettingsService, Iis.SiteHostingSettingsService>();
        services.AddScoped<IDirectoryProtectionService, Iis.DirectoryProtectionService>();
        services.AddScoped<IDnsService, Dns.DnsService>();
        services.AddScoped<IRdnsService, Dns.RdnsService>();
        services.AddScoped<IPhpService, Php.PhpService>();
        services.AddScoped<IAspNetService, AspNet.AspNetService>();
        services.AddScoped<IAppRuntimeService, AppRuntimes.AppRuntimeService>();
        services.AddScoped<IReverseProxyService, ReverseProxy.ReverseProxyService>();
        services.AddScoped<INginxService, Nginx.NginxService>();
        services.AddScoped<Database.DatabaseCredentialStore>();
        services.AddScoped<Database.IDatabaseEngineProvider, Database.MySqlDatabaseProvider>();
        services.AddScoped<Database.IDatabaseEngineProvider, Database.PostgreSqlDatabaseProvider>();
        services.AddScoped<Database.IDatabaseEngineProvider, Database.MsSqlDatabaseProvider>();
        services.AddScoped<IDatabaseService, Database.DatabaseService>();
        services.AddScoped<IDatabaseOwnershipService, Database.DatabaseOwnershipService>();
        services.AddScoped<ISiteDatabaseLinkService, Database.SiteDatabaseLinkService>();
        services.AddScoped<IWebDbClientService, WebDbClient.WebDbClientService>();
        services.AddScoped<IRemoteDbAccessService, Database.RemoteDbAccessService>();
        services.AddScoped<IWebmailService, Mail.WebmailService>();
        services.AddScoped<ICacheService, Cache.CacheService>();
        services.AddScoped<IFileService, Files.FileService>();
        services.AddScoped<ISslService, Ssl.WinAcmeSslService>();
        services.AddScoped<IFtpService, Ftp.FtpService>();
        services.AddScoped<IWafService, Waf.WafService>();
        services.AddScoped<ILogViewerService, Logs.LogViewerService>();
        // Log döndürme (rotation): kapanmış günlükleri ZSTD ile arşivler, saklama kuralını uygular.
        // Ölçüldü: IIS erişim günlüklerini küçülten HİÇBİR kod yoktu; otomasyondaki "log temizliği"
        // yalnızca siliyor ve varsayılan olarak kapalı — yani uzun çalışan sunucuda günlükler sınırsız birikiyordu.
        services.AddScoped<Logs.ILogRotationService, Logs.LogRotationService>();
        services.AddScoped<IFirewallService, Firewall.FirewallService>();
        services.AddScoped<ISchedulerService, Scheduler.SchedulerService>();
        services.AddScoped<INotificationService, Notifications.NotificationService>();
        // Yedek şifreleme parolasının DPAPI korumalı kasası (zamanlanmış yedekler için şart).
        services.AddScoped<Backup.BackupEncryptionStore>();
        services.AddScoped<IBackupService, Backup.BackupService>();
        services.AddScoped<IFileMigrationService, Migration.FileMigrationService>();
        services.AddScoped<ISiteMigrationService, Migration.SiteMigrationService>();
        services.AddScoped<IAppStoreService, AppStore.AppStoreService>();
        services.AddScoped<IWordPressToolkitService, AppStore.WordPressToolkitService>();
        services.AddScoped<ISelfUpdateService, SelfUpdate.SelfUpdateService>();
        services.AddSingleton<IUpdateInstallerLauncher, SelfUpdate.WindowsUpdateInstallerLauncher>();
        services.AddScoped<ITerminalService, Terminal.TerminalService>();
        services.AddScoped<ISecretProtector, SecretProtector>();

        // LİSANS / AKTİVASYON.
        // Varsayılan olarak KAPALI: Panel:License:VerificationUrl boş olduğu sürece panel hiçbir
        // yere istek atmaz ve hiçbir şey kısıtlanmaz. Ayrıntı: docs/lisans-aktivasyon.md
        services.Configure<Licensing.LicenseOptions>(configuration.GetSection(Licensing.LicenseOptions.SectionName));
        // Parmak izi süreç ömrü boyunca DEĞİŞMEZ -> Singleton: kayıt defteri ve install-id dosyası
        // Lazy ile bir kez okunur. Scoped olsaydı her istekte disk+registry okunurdu.
        services.AddSingleton<Licensing.IMachineFingerprintProvider, Licensing.MachineFingerprintProvider>();
        services.AddScoped<Licensing.LicenseStateStore>();
        services.AddScoped<Licensing.ILicenseVerificationClient, Licensing.HttpLicenseVerificationClient>();
        // Scoped olmak ZORUNDA: ISecretProtector Scoped, Singleton yapılırsa captive dependency olur.
        services.AddScoped<ILicenseService, Licensing.LicenseService>();

        services.AddSingleton<ITotpService, TotpService>();
        services.AddSingleton<ISystemMonitoringService, SystemMonitoringService>();
        services.AddScoped<IBruteForceProtectionService, Security.BruteForceProtectionService>();
        services.AddScoped<IDiskQuotaService, Iis.DiskQuotaService>();
        services.AddScoped<IGitDeploymentService, Git.GitDeploymentService>();
        services.AddScoped<IWebStatisticsService, Statistics.WebStatisticsService>();
        services.AddScoped<SiteBuilder.ISiteBuilderService, SiteBuilder.SiteBuilderService>();
        services.AddScoped<SiteBuilder.ISiteBuilderContactService, SiteBuilder.SiteBuilderContactService>();

        // HIZ SINIRLAYICI SINGLETON OLMAK ZORUNDA: sayaçlar bellekte tutuluyor ve kapsamlı
        // (scoped) kayıt her istekte YENİ bir örnek üretirdi — yani her istek boş bir sayaçla
        // başlar ve sınır hiçbir zaman dolmazdı. Sınırlayıcı var görünüp hiç sınırlamayan bir
        // kayıt, hiç sınırlayıcı olmamasından daha kötüdür (yanlış güven).
        services.AddSingleton<SiteBuilder.SiteBuilderContactRateLimiter>();
        // Mail: kurulu sunucuya göre yönlendirilen dispatcher. Stalwart sağlayıcısı hiç
        // değişmeden korunur; MailEnable kuruluysa router ona yönlendirir.
        services.AddScoped<Mail.StalwartRuntime>();
        services.AddScoped<Mail.MailService>();
        services.AddScoped<Mail.MailEnableMailService>();
        services.AddScoped<IMailService, Mail.MailServiceRouter>();
        // Dağıtım listesinden self-servis çıkma (kimliksiz genel uçtan çağrılır).
        services.AddScoped<Mail.MailListUnsubscribeService>();
        // TEK ALAN ADININ postasını (iletiler dahil) arşivler/geri yükler — sunucu geneli posta
        // yedeğinden farkı: servis DURDURULMAZ ve geri yükleme yalnız hedef alana yazar.
        services.AddScoped<IMailAccountArchiver, Mail.StalwartAccountArchiver>();
        // Otomatik yapılandırmayı müşterinin alan adına bağlar (uçlar çalışıyordu ama istemciler
        // panele hiç ulaşmıyordu — istek IIS'e düşüyordu).
        services.AddScoped<Mail.MailJmapProxyPublisher>();
        services.AddScoped<IMailAutodiscoverPublisher, Mail.MailAutodiscoverPublisher>();
        services.AddScoped<IDockerService, Docker.DockerService>();
        services.AddScoped<IAutomationService, Automation.AutomationService>();
        services.AddScoped<IAdvisorService, Advisor.AdvisorService>();
        // Dışarıdan bakan site sağlığı (DNS/HTTP/TLS/mail) + tüm dikkat sinyallerini tek listede
        // toplayan rozet servisi. İkisi de yalnız MEVCUT sinyalleri okur, kalıcı durum tutmaz → Scoped.
        services.AddScoped<ISiteHealthService, Health.SiteHealthService>();
        services.AddScoped<IDomainRefreshService, Health.DomainRefreshService>();
        // Panelin kendi alan adiyla disariya acilmasi (ters vekil + Let's Encrypt). Yalniz
        // mevcut servisleri sirayla cagirir, kalici durum tutmaz -> Scoped.
        services.AddScoped<IPanelPublicAccessService, Health.PanelPublicAccessService>();
        // Sunucudan sunucuya erisim anahtarlari (MCP baglantilari); bkz. PanelApiToken.
        services.AddScoped<IApiTokenService, Security.ApiTokenService>();
        // Hata bildirimi: maskelenmis kayit + toplayiciya gonderim. Toplayici adresi bos ise
        // hicbir sey gonderilmez, kayitlar yerelde birikir (guvenli varsayilan).
        services.AddScoped<IErrorReportService, Diagnostics.ErrorReportService>();
        services.AddScoped<IErrorReportSender, Diagnostics.ErrorReportSender>();
        services.AddScoped<IAttentionService, Advisor.AttentionService>();
        // Süreç izleyici BİLİNÇLİ İSTİSNA olarak Singleton: CPU yüzdesi iki ölçüm arasındaki
        // işlemci-zamanı farkından hesaplanır, yani servisin önceki anlık görüntüyü HATIRLAMASI
        // gerekir. Scoped olsaydı her istek yeni örnek alır ve CPU sütunu daima 0 görünürdü.
        services.AddSingleton<IProcessMonitorService, Monitoring.ProcessMonitorService>();
        // "Paylaşılan süreci kim kullanıyor" atfı: MySQL/PostgreSQL tek süreçtir, tüm siteler onu
        // paylaşır — CPU'yu kimin yediğini ancak sürecin İÇİNDEKİ aktif sorgulara bakarak söyleyebiliriz.
        // Süreç izleyicinin aksine çağrılar arası durum tutmaz (DbContext kullanır) → Scoped.
        services.AddScoped<IResourceAttributionService, Monitoring.ResourceAttributionService>();

        services.AddScoped<ISiteAliasService, Iis.SiteAliasService>();

        // .htaccess MOTORU — SINGLETON, ve bu motorun KENDİ sözleşmesi: sınıf yorumu
        // "iş parçacığı güvenlidir ve tekil olarak kaydedilmek üzere tasarlandı" diyor.
        // Ayrıştırma önbelleği (dosya damgası + boyut) ve desen önbelleği örnek alanlarında;
        // Scoped kaydedilseydi her panel isteğinde bütün .htaccess'ler yeniden ayrıştırılır ve
        // her düzenli ifade yeniden derlenirdi. Bayatlama riski YOK: önbellek dosya damgasına
        // bağlı, dosya değişince kendiliğinden düşer.
        services.AddSingleton<IHtaccessEngine, Htaccess.HtaccessEngine>();

        // .htaccess PANEL YÜZÜ. SCOPED — kendisi durumsuz ama IIisSiteService/IAspNetService
        // bağımlılıkları Scoped; Singleton olsaydı onları TUTSAK EDERDİ (captive dependency).
        //
        // ARAYÜZ YOK, SOMUT TİP KAYITLI: Core/Interfaces bu iş kolunun kapsamı dışında ve
        // depoda aynı desenin örnekleri var (ClusterNodeStore, MailJmapProxyPublisher).
        // MODÜLÜN IIS KAYDININ ÖLÇÜMÜ. SINGLETON: durumsuz ve Scoped bağımlılığı YOK (her çağrıda
        // kendi ServerManager'ını açıp kapatıyor). Önbellek BİLEREK yok — bu nesnenin tek işi
        // "IIS ŞU AN ne diyor" sorusuna cevap vermek; bayat bir cevap, panelin yalan söylemesinin
        // yeni bir yolu olurdu (aynı gerekçe HtaccessSiteReport.EngineLoadedInIis'te yazılı).
        services.AddSingleton<Htaccess.Hosting.IHtaccessModuleRegistry, Htaccess.Hosting.IisHtaccessModuleRegistry>();

        services.AddScoped<Htaccess.HtaccessPanelService>();

        // ÇOKLU SUNUCU (Plesk Multi Server karşılığı): merkez panel + kayıtlı hizmet düğümleri.
        // İstemci fabrikası SINGLETON çünkü düğüm başına HTTP işleyicisini (bağlantı havuzu)
        // önbelleğe alır; Scoped olsaydı her istekte yeni havuz açılır ve düzenli yoklama soket
        // tüketirdi. Depo ve servis ise Scoped: ISecretProtector Scoped'dır ve Singleton bir
        // tüketici onu tutsaydı captive dependency olurdu.
        services.AddSingleton<Cluster.ClusterHttpClientFactory>();
        services.AddScoped<Cluster.ClusterNodeStore>();
        services.AddScoped<Cluster.IClusterNodeClient, Cluster.ClusterNodeClient>();
        services.AddScoped<Cluster.IClusterService, Cluster.ClusterService>();

        // Abonelik iş modeli: kotayı DOLDURANDA uygulayan taraf (askıya alma) arka planda koşar.
        services.AddScoped<ISubscriptionEnforcementService, Subscriptions.SubscriptionEnforcementService>();
        // Bayi tavanı (bayinin KENDİ dağıtım sınırı). CustomerScopeService bunu okur; kayıt burada
        // olmak zorunda çünkü kapsam servisi de burada kaydediliyor (grafik aynı kapsayıcıda çözülür).
        services.AddScoped<IResellerQuotaService, Subscriptions.ResellerQuotaService>();
        services.AddHostedService<Subscriptions.SubscriptionEnforcementWorker>();
        // Abonelik başına yedek programı (Plesk paritesi). Sunucu geneli otomasyondan AYRI işçi
        // ve AYRI kuyruk: birinin saatlerce süren turu diğerini bekletmemeli.
        services.AddScoped<ISubscriptionBackupScheduleService, Subscriptions.SubscriptionBackupScheduleService>();
        services.AddHostedService<Subscriptions.SubscriptionBackupWorker>();

        // Sorgu geçmişi: örnekleyici arka planda koşar (panel açık olmasa da kayıt tutulur).
        services.AddScoped<IQueryLogService, Monitoring.QueryLogService>();
        services.AddHostedService<Monitoring.QueryLogWorker>();
        services.AddScoped<IAntivirusService, Antivirus.AntivirusService>();
        // K1 güvenlik: veri kökü hassas klasörlerini her açılışta SYSTEM/Admins'e kilitle (AutomationWorker'dan ÖNCE).
        // NOT: DataRootHardeningService artık hosted service DEĞİL — Program.cs'te DB açılmadan ÖNCE
        // statik HardenAsync ile çağrılır (açık db'ye icacls /T uygulanıp ACL'in bozulmasını önlemek için).
        services.AddHostedService<Automation.AutomationWorker>();

        // SEO ARAÇ KUTUSU. Kuyruk SINGLETON olmak zorunda: canlı tarama durumu (kaç sayfa gezildi,
        // iptal belirteci) istekler ARASINDA yaşar; scoped olsaydı her istek boş bir durum görür ve
        // "sürüyor mu" sorusu asla cevaplanamazdı. Tarayıcının kendisi scoped (IIS/dosya servislerini
        // kullanır) ve tek tüketicili işçi tarafından ayrı bir kapsamda koşturulur.
        services.AddSingleton<Seo.SeoScanQueue>();
        services.AddScoped<Seo.SeoReportStore>();
        services.AddScoped<Seo.SeoScanRunner>();
        services.AddScoped<Seo.ISeoService, Seo.SeoScanService>();
        services.AddHostedService<Seo.SeoScanWorker>();

        // SEO GÜNLÜK ANALİZİ. Kuyruk YOK: iş dosya/satır/süre olarak üç eksende sınırlı ve ölçülen
        // süresi saniyeler mertebesinde (gerekçe: SeoLogAnalysisService). Ters DNS çözücüsü AYRI
        // kayıt çünkü testlerde ağa çıkmayan bir ikizle değiştirilir; gerçek DNS'e bağlı bir test
        // ağın durumuna göre rastgele kırmızı verirdi.
        services.AddSingleton<Seo.ISeoReverseDnsResolver, Seo.SeoReverseDnsResolver>();
        services.AddScoped<Seo.SeoLogReportStore>();
        services.AddScoped<Seo.ISeoLogService, Seo.SeoLogAnalysisService>();

        // Denetim kaydı saklama temizliği. Kayıt buraya bağlanmadan temizlik YALNIZCA elle
        // (POST /api/v1/audit-logs/purge) çalışırdı; ölçüldü: AuditLogs tablosunu silen başka
        // hiçbir kod yoktu, yani kayıtlar sonsuza kadar birikiyordu.
        services.AddHostedService<Audit.AuditRetentionWorker>();

        // Faturalama (WHMCS) entegrasyonu: toplu kullanım raporu + silme öncesi güvenlik denetimi.
        // Scoped olmak ZORUNDA: DbContext ve müşteri kapsamı servisi de Scoped; Singleton yapılırsa
        // captive dependency olur (aynı gerekçe LicenseService'te yazılı).
        services.AddScoped<Billing.IBillingIntegrationService, Billing.BillingIntegrationService>();

        // Log döndürme turu. Kayıt yapılmazsa döndürme YALNIZCA elle (POST /api/v1/logs/rotation/run)
        // çalışırdı — yani "politika" değil temenni olurdu (AuditRetentionWorker'daki aynı gerekçe).
        services.AddHostedService<Logs.LogRotationWorker>();

        // Gönderen sahteciliği kuralı (MtaStageMail.isSenderAllowed + DATA aşaması From hizalaması).
        // AÇILIŞTA çalışmak ZORUNDA: kurulum adımına konsaydı yalnız Stalwart'ı bu sürümle KURAN
        // makineler korunurdu, açığı taşıyan MEVCUT kurulumlar hiç kapanmazdı (gerekçe:
        // MailSenderPolicyWorker).
        services.AddHostedService<Mail.MailSenderPolicyWorker>();

        // Arka plan iş kuyruğunun TÜKETİCİSİ. Kayıt yapılmazsa kuyruk dolar ve kuyruğa alınan hiçbir
        // iş çalışmaz — yani parola sıfırlama postası hiç gitmez. Bu yüzden kayıt burada, diğer
        // BackgroundService'lerin yanında.
        services.AddHostedService<Support.ArkaPlanIsKuyruguWorker>();

        return services;
    }
}
