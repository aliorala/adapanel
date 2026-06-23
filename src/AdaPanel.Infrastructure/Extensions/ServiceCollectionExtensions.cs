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
        services.AddScoped<IWebDbClientService, WebDbClient.WebDbClientService>();
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
        services.AddScoped<IBackupService, Backup.BackupService>();
        services.AddScoped<IAppStoreService, AppStore.AppStoreService>();
        services.AddScoped<IWordPressToolkitService, AppStore.WordPressToolkitService>();
        services.AddScoped<ISelfUpdateService, SelfUpdate.SelfUpdateService>();
        services.AddSingleton<IUpdateInstallerLauncher, SelfUpdate.WindowsUpdateInstallerLauncher>();
        services.AddScoped<ITerminalService, Terminal.TerminalService>();
        services.AddScoped<ISecretProtector, SecretProtector>();
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
        services.AddScoped<IDockerService, Docker.DockerService>();
        services.AddScoped<IAutomationService, Automation.AutomationService>();
        // K1 güvenlik: veri kökü hassas klasörlerini her açılışta SYSTEM/Admins'e kilitle (AutomationWorker'dan ÖNCE).
        services.AddHostedService<Automation.AutomationWorker>();

        return services;
    }
}
