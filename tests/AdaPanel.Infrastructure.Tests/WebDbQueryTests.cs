using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebDbQueryTests
{
    [Fact]
    public async Task InstalledPhpQuery_IsTranslatedBySqlite()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"adapanel-webdb-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        try
        {
            await using (var dbContext = new AdaPanelDbContext(options))
            {
                await dbContext.Database.EnsureCreatedAsync();
                dbContext.InstalledComponents.AddRange(
                    new InstalledComponent
                    {
                        ComponentId = "php-8.3",
                        DisplayName = "PHP 8.3",
                        Status = ComponentInstallStatus.Installed
                    },
                    new InstalledComponent
                    {
                        ComponentId = "adminer",
                        DisplayName = "Adminer",
                        Status = ComponentInstallStatus.Installed
                    });
                await dbContext.SaveChangesAsync();

                var installedPhp = await dbContext.InstalledComponents
                    .Where(item =>
                        item.Status == ComponentInstallStatus.Installed &&
                        item.ComponentId.StartsWith("php-"))
                    .OrderByDescending(item => item.ComponentId)
                    .Select(item => item.ComponentId)
                    .FirstOrDefaultAsync();

                Assert.Equal("php-8.3", installedPhp);
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
