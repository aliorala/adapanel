using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddScoped<IPasswordHasher<PanelUser>, PasswordHasher<PanelUser>>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISetupService, SetupService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IServicePlanService, Services.ServicePlanService>();
        services.AddScoped<ICustomerScopeService, Security.CustomerScopeService>();
        services.AddScoped<IPanelSecurityService, PanelSecurityService>();
        services.AddScoped<IPanelPreferencesService, PanelPreferencesService>();
        services.AddSingleton<IConcurrencyLock, ConcurrencyLock>();
        services.AddSingleton<IOperationProgressService, Services.OperationProgressService>();
        services.AddSingleton<JsonFileStore>();
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
        // Mail: kurulu sunucuya göre yönlendirilen dispatcher. Stalwart sağlayıcısı hiç
        // değişmeden korunur; MailEnable kuruluysa router ona yönlendirir.
        services.AddScoped<Mail.MailService>();
        services.AddScoped<Mail.MailEnableMailService>();
        services.AddScoped<IMailService, Mail.MailServiceRouter>();
        // Otomatik yapılandırmayı müşterinin alan adına bağlar (uçlar çalışıyordu ama istemciler
        // panele hiç ulaşmıyordu — istek IIS'e düşüyordu).
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

        // Abonelik iş modeli: kotayı DOLDURANDA uygulayan taraf (askıya alma) arka planda koşar.
        services.AddScoped<ISubscriptionEnforcementService, Subscriptions.SubscriptionEnforcementService>();
        // Bayi tavanı (bayinin KENDİ dağıtım sınırı). CustomerScopeService bunu okur; kayıt burada
        // olmak zorunda çünkü kapsam servisi de burada kaydediliyor (grafik aynı kapsayıcıda çözülür).
        services.AddScoped<IResellerQuotaService, Subscriptions.ResellerQuotaService>();
        services.AddHostedService<Subscriptions.SubscriptionEnforcementWorker>();

        // Sorgu geçmişi: örnekleyici arka planda koşar (panel açık olmasa da kayıt tutulur).
        services.AddScoped<IQueryLogService, Monitoring.QueryLogService>();
        services.AddHostedService<Monitoring.QueryLogWorker>();
        services.AddScoped<IAntivirusService, Antivirus.AntivirusService>();
        // K1 güvenlik: veri kökü hassas klasörlerini her açılışta SYSTEM/Admins'e kilitle (AutomationWorker'dan ÖNCE).
        // NOT: DataRootHardeningService artık hosted service DEĞİL — Program.cs'te DB açılmadan ÖNCE
        // statik HardenAsync ile çağrılır (açık db'ye icacls /T uygulanıp ACL'in bozulmasını önlemek için).
        services.AddHostedService<Automation.AutomationWorker>();

        // Denetim kaydı saklama temizliği. Kayıt buraya bağlanmadan temizlik YALNIZCA elle
        // (POST /api/v1/audit-logs/purge) çalışırdı; ölçüldü: AuditLogs tablosunu silen başka
        // hiçbir kod yoktu, yani kayıtlar sonsuza kadar birikiyordu.
        services.AddHostedService<Audit.AuditRetentionWorker>();

        return services;
    }
}
