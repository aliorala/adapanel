using AdaPanel.Data.Extensions;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DataRootConfigurationTests
{
    [Fact]
    public void AddAdaPanelData_UsesPanelDataRootForTheDefaultDatabase()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"adapanel-data-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Panel:DataRoot"] = dataRoot,
                    ["ConnectionStrings:AdaPanel"] = "Data Source=%ProgramData%\\AdaPanel\\data\\adapanel.db"
                })
                .Build();
            var services = new ServiceCollection();

            services.AddAdaPanelData(configuration);

            using var provider = services.BuildServiceProvider();
            using var context = provider.GetRequiredService<AdaPanelDbContext>();
            var expected = Path.GetFullPath(Path.Combine(dataRoot, "data", "adapanel.db"));
            Assert.Equal(expected, context.Database.GetDbConnection().DataSource);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }
}
