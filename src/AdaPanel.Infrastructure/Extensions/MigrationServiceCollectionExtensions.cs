using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdaPanel.Infrastructure.Extensions;

internal static class MigrationServiceCollectionExtensions
{
    internal static IServiceCollection AddFullMigration(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PortableMigrationPackageOptions>(configuration.GetSection("Migration:Packages"));
        services.Configure<LiveMigrationOptions>(configuration.GetSection("Migration:Source"));
        services.Configure<MigrationMailTargetOptions>(configuration.GetSection("Migration:Mail"));
        // Scoped native inventories are awaited inside each storage operation before creating directories.
        services.AddScoped<IMigrationPackageStore, PortableMigrationPackageStore>();
        services.AddScoped<MigrationJobStore>();
        services.AddScoped<IAdaPanelMigrationExporter, AdaPanelMigrationExporter>();
        services.AddScoped<IAdaPanelLiveMigrationSource, AdaPanelLiveMigrationSource>();
        services.AddScoped<ILiveMigrationSource, LiveMigrationSource>();
        services.AddScoped<CpanelBackupNormalizer>();
        services.AddScoped<PleskBackupNormalizer>();
        services.AddScoped<IMigrationDatabaseImporter>(provider => (Database.DatabaseService)provider.GetRequiredService<IDatabaseService>());
        services.AddScoped<IMigrationMailProvisioner, Mail.MigrationMailProvisionerRouter>();
        services.AddScoped<IMigrationDnsProvisioner>(provider => (Dns.DnsService)provider.GetRequiredService<IDnsService>());
        services.AddScoped<IMigrationTargetAdapter, MigrationSiteTarget>();
        services.AddScoped<IMigrationTargetAdapter, MigrationDatabaseTarget>();
        services.AddScoped<IMigrationTargetAdapter, MigrationMailTarget>();
        services.AddScoped<IMigrationTargetAdapter, MigrationDnsTarget>();
        services.AddScoped<IMigrationTargetAdapter, MigrationTaskTarget>();
        services.AddScoped<FullMigrationCoordinator>();
        services.AddScoped<IFullMigrationService>(provider => provider.GetRequiredService<FullMigrationCoordinator>());
        services.AddScoped<MigrationAcquisitionService>();
        services.AddScoped<IMigrationAcquisitionService>(provider => provider.GetRequiredService<MigrationAcquisitionService>());
        services.AddHostedService<FullMigrationWorker>();
        return services;
    }
}
