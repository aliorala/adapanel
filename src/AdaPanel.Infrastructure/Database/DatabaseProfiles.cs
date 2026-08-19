using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// Bir veritabanı motoruna nasıl bağlanılacağının TEK kaynağı.
/// </summary>
/// <remarks>
/// Bu mantık daha önce yalnız <see cref="DatabaseService"/> içinde, private olarak duruyordu.
/// Uzak erişim de aynı bağlantı profillerine ihtiyaç duyunca iki seçenek vardı: kopyalamak ya da
/// ortak kaynağa çıkarmak. Kopya olsaydı ikisi zamanla ayrışırdı — depoda bunun bedeli daha önce
/// ödendi: elle yazılmış bir motor listesi yüzünden veritabanları yedeklenmeden kalmıştı. Motorun
/// hangi bileşenle kurulduğu, hangi portu ve yönetici hesabını kullandığı artık tek yerde tanımlıdır.
/// </remarks>
internal static class DatabaseProfiles
{
    public const string GeneratedPasswordKey = "generatedPassword";
    public const string AdminUserKey = "adminUser";
    public const string AdminPortKey = "adminPort";
    public const string SystemDetectedKey = "systemDetected";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Panelin tanıdığı motorlar. Yeni motor yalnız buraya eklenir.</summary>
    public static readonly IReadOnlyList<DbEngine> ManagedEngines = [DbEngine.MariaDb, DbEngine.MySql, DbEngine.PostgreSql];

    public static EngineDescriptor ResolveDescriptor(DbEngine engine)
    {
        return engine switch
        {
            DbEngine.MariaDb => new EngineDescriptor(engine, "mariadb", "MariaDB", "mariadb-11", "root", 3306, true),
            DbEngine.MySql => new EngineDescriptor(engine, "mysql", "MySQL", "mysql-8", "root", 3307, true),
            DbEngine.PostgreSql => new EngineDescriptor(engine, "postgresql", "PostgreSQL", "postgresql-16", "postgres", 5432, false),
            _ => throw new AppException("database.engine_not_supported", 400, 400)
        };
    }

    public static DbEngine ParseEngine(string engine)
    {
        return (engine ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "mariadb" => DbEngine.MariaDb,
            "mysql" => DbEngine.MySql,
            "postgresql" or "postgres" => DbEngine.PostgreSql,
            "mssql" or "sqlserver" => DbEngine.MsSql,
            _ => throw new AppException("database.engine_not_supported", 400, 400)
        };
    }

    public static int DefaultExternalPort(DbEngine engine)
        => engine switch
        {
            DbEngine.PostgreSql => 5432,
            // Sistemde kurulu MySQL/MariaDB geleneksel olarak 3306 dinler
            // (AdaPanel'in kendi MySQL kurulumu 3307 kullanır, çakışmayı önlemek için).
            _ => 3306
        };

    public static async Task<DatabaseConnectionProfile> ResolveAsync(
        AdaPanelDbContext dbContext,
        ISecretProtector secretProtector,
        DbEngine engine,
        CancellationToken cancellationToken,
        bool allowEmptyPassword = false)
    {
        if (engine == DbEngine.MsSql)
        {
            return ResolveMsSqlProfile();
        }

        var descriptor = ResolveDescriptor(engine);
        var installedComponent = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(
                item => item.ComponentId == descriptor.ComponentId && item.Status == ComponentInstallStatus.Installed,
                cancellationToken);

        if (installedComponent is null)
        {
            throw new AppException("database.engine_not_installed", 404, 404);
        }

        var configuration = ReadProtectedConfiguration(secretProtector, installedComponent);
        var adminPassword = configuration.TryGetValue(GeneratedPasswordKey, out var storedPassword) ? storedPassword : string.Empty;
        if (string.IsNullOrWhiteSpace(adminPassword) && !allowEmptyPassword)
        {
            throw new AppException("database.admin_password_missing", 409, 409);
        }

        // Harici (sistemde zaten kurulu) motorlar için kayıtlı kullanıcı/portu kullan;
        // yoksa AdaPanel'in kendi kurulumundaki varsayılanlara düş.
        var adminUser = configuration.TryGetValue(AdminUserKey, out var storedUser) && !string.IsNullOrWhiteSpace(storedUser)
            ? storedUser
            : descriptor.AdminUserName;
        var port = configuration.TryGetValue(AdminPortKey, out var storedPort) && int.TryParse(storedPort, out var parsedPort) && parsedPort > 0
            ? parsedPort
            : descriptor.DefaultPort;

        return new DatabaseConnectionProfile(
            engine,
            descriptor.EngineKey,
            descriptor.DisplayName,
            descriptor.ComponentId,
            installedComponent.ServiceName,
            "127.0.0.1",
            port,
            adminUser,
            adminPassword,
            descriptor.SupportsHostScopedUsers,
            installedComponent.InstallDir);
    }

    public static DatabaseConnectionProfile ResolveMsSqlProfile()
    {
        foreach (var registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView);
            using var instancesKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
            var instanceName = instancesKey?.GetValueNames().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                continue;
            }

            var isDefault = string.Equals(instanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase);
            return new DatabaseConnectionProfile(
                DbEngine.MsSql,
                "mssql",
                "Microsoft SQL Server",
                "mssql-existing",
                isDefault ? "MSSQLSERVER" : $"MSSQL${instanceName}",
                isDefault ? "localhost" : $@"localhost\{instanceName}",
                1433,
                "Windows",
                string.Empty,
                false,
                string.Empty);
        }

        throw new AppException("database.engine_not_installed", 404, 404);
    }

    public static Dictionary<string, string> ReadProtectedConfiguration(ISecretProtector secretProtector, InstalledComponent component)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(component.ProtectedConfigurationJson))
        {
            return result;
        }

        try
        {
            var payload = secretProtector.Unprotect(component.ProtectedConfigurationJson);
            var configuration = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, SerializerOptions);
            if (configuration is not null)
            {
                foreach (var pair in configuration)
                {
                    result[pair.Key] = pair.Value;
                }
            }
        }
        catch
        {
            // Bozuk/okunamayan yapılandırma boş kabul edilir.
        }

        return result;
    }
}

internal sealed record EngineDescriptor(
    DbEngine Engine,
    string EngineKey,
    string DisplayName,
    string ComponentId,
    string AdminUserName,
    int DefaultPort,
    bool SupportsHostScopedUsers);
