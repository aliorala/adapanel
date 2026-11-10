using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Extensions;
using AdaPanel.Infrastructure.Extensions;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FullMigrationRegistrationTests
{
    [Fact]
    public async Task ProductionRegistrationsResolveWithoutStartingNativeServices()
    {
        var root = Path.Combine(Path.GetTempPath(), "ada-migration-di-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Panel:DataRoot"] = root }).Build();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddLogging();
            services.AddAdaPanelData(config);
            services.AddAdaPanelInfrastructure(config);
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(FullMigrationWorker));
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            await using var scope = provider.CreateAsyncScope();
            var current = scope.ServiceProvider;
            Assert.Same(current.GetRequiredService<IFullMigrationService>(), current.GetRequiredService<FullMigrationCoordinator>());
            Assert.Same(current.GetRequiredService<IMigrationAcquisitionService>(), current.GetRequiredService<MigrationAcquisitionService>());
            Assert.Same(current.GetRequiredService<IDatabaseService>(), current.GetRequiredService<IMigrationDatabaseImporter>());
            Assert.NotNull(current.GetRequiredService<IMigrationMailProvisioner>());
            Assert.Same(current.GetRequiredService<IDnsService>(), current.GetRequiredService<IMigrationDnsProvisioner>());
            Assert.NotNull(current.GetRequiredService<IAdaPanelLiveMigrationSource>());
            Assert.NotNull(current.GetRequiredService<IAdaPanelMigrationExporter>());
            Assert.NotNull(current.GetRequiredService<ILiveMigrationSource>());
            Assert.NotNull(current.GetRequiredService<CpanelBackupNormalizer>());
            Assert.NotNull(current.GetRequiredService<PleskBackupNormalizer>());
            Assert.Equal(new[] { "database", "dns", "mail", "site", "task" }, current.GetServices<IMigrationTargetAdapter>().Select(target => target.Kind).OrderBy(kind => kind));
            Assert.False(Directory.Exists(Path.Combine(root, "migration-packages")));
            Assert.False(Directory.Exists(Path.Combine(root, "migration-private")));
        }
        finally { if (Directory.Exists(root)) PortableMigrationStorage.DeleteOwnedDirectory(root); }
    }
}
