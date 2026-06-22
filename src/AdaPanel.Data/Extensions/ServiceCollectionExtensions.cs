using AdaPanel.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdaPanel.Data.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAdaPanelData(this IServiceCollection services, IConfiguration configuration)
    {
        const string defaultDataRoot = "%ProgramData%\\AdaPanel";
        const string defaultConnectionString = "Data Source=%ProgramData%\\AdaPanel\\data\\adapanel.db";
        var configuredConnectionString = configuration.GetConnectionString("AdaPanel");
        var configuredDataRoot = configuration["Panel:DataRoot"];
        var useDataRoot = string.IsNullOrWhiteSpace(configuredConnectionString) ||
                          string.Equals(configuredConnectionString, defaultConnectionString, StringComparison.OrdinalIgnoreCase);
        var rawConnectionString = useDataRoot
            ? new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(
                    Environment.ExpandEnvironmentVariables(
                        string.IsNullOrWhiteSpace(configuredDataRoot) ? defaultDataRoot : configuredDataRoot),
                    "data",
                    "adapanel.db")
            }.ToString()
            : configuredConnectionString!;

        var expandedConnectionString = Environment.ExpandEnvironmentVariables(rawConnectionString);
        var builder = new SqliteConnectionStringBuilder(expandedConnectionString);

        if (!string.IsNullOrWhiteSpace(builder.DataSource))
        {
            var dataSourcePath = builder.DataSource;
            if (!Path.IsPathRooted(dataSourcePath))
            {
                dataSourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataSourcePath));
            }

            var directory = Path.GetDirectoryName(dataSourcePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            builder.DataSource = dataSourcePath;
        }

        services.AddDbContext<AdaPanelDbContext>(options =>
        {
            options.UseSqlite(builder.ToString());
        });

        return services;
    }
}
