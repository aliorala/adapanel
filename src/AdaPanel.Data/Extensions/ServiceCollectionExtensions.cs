using AdaPanel.Data.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdaPanel.Data.Extensions;

public static class ServiceCollectionExtensions
{
    public const int DefaultBusyTimeoutSeconds = 30;

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
        // 8.0.27's pool can lend one native handle to concurrent openers during
        // activation (dotnet/efcore#39008; fixed in 10.0.13). Keep .NET 8 opens
        // independent, including when configuration explicitly enables pooling.
        builder.Pooling = false;

        // Eşzamanlı yazarlar "database is locked" ile düşmesin: Microsoft.Data.Sqlite, "Default Timeout"u
        // komut zaman aşımı olarak uygular; busy_timeout'a ise SqliteBusyTimeoutInterceptor yansıtır. Açıkça yazılmamışsa
        // 30 sn'ye SABİTLENİR (kütüphane varsayılanına örtük bağlı kalınmaz). Bilinçle KISA (5 sn) değil:
        // aynı değer komut zaman aşımıdır; uzun migration/VACUUM komutlarını 5 sn'de keserdi.
        // Açıkça verilen değer (yapılandırma) olduğu gibi korunur.
        if (!builder.ContainsKey("Default Timeout"))
        {
            builder.DefaultTimeout = DefaultBusyTimeoutSeconds;
        }

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

        var busyTimeoutMs = checked(builder.DefaultTimeout * 1000);

        services.AddDbContext<AdaPanelDbContext>(options =>
        {
            options.UseSqlite(builder.ToString());
            // Microsoft.Data.Sqlite SQLite'ın busy_timeout'unu ayarlamaz; bağlantı başına açıkça ayarla.
            options.AddInterceptors(new SqliteBusyTimeoutInterceptor(busyTimeoutMs));
        });

        return services;
    }
}
