using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace AdaPanel.Infrastructure.Database;

internal sealed partial class DatabaseService(
    AdaPanelDbContext dbContext,
    ISecretProtector secretProtector,
    DatabaseCredentialStore credentialStore,
    IEnumerable<IDatabaseEngineProvider> providers) : IDatabaseService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string GeneratedPasswordKey = "generatedPassword";
    private const string AdminUserKey = "adminUser";
    private const string AdminPortKey = "adminPort";
    private const string SystemDetectedKey = "systemDetected";

    public async Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<DatabaseEngineStatusDto>();

        foreach (var engine in new[] { DbEngine.MariaDb, DbEngine.MySql, DbEngine.PostgreSql })
        {
            var descriptor = ResolveDescriptor(engine);
            var component = await dbContext.InstalledComponents.FirstOrDefaultAsync(
                item => item.ComponentId == descriptor.ComponentId && item.Status == ComponentInstallStatus.Installed,
                cancellationToken);

            if (component is null)
            {
                result.Add(new DatabaseEngineStatusDto
                {
                    Engine = descriptor.EngineKey,
                    DisplayName = descriptor.DisplayName,
                    Installed = false
                });
                continue;
            }

            var configuration = ReadProtectedConfiguration(component);
            result.Add(BuildEngineStatus(descriptor, component, configuration));
        }

        // Microsoft SQL Server entegre kimlik doğrulamayla yönetildiği için parola gerekmez.
        try
        {
            var mssql = ResolveMsSqlProfile();
            result.Add(new DatabaseEngineStatusDto
            {
                Engine = mssql.EngineKey,
                DisplayName = mssql.DisplayName,
                Installed = true,
                Ready = true,
                SystemDetected = true,
                Host = mssql.Host,
                Port = mssql.Port,
                AdminUserName = mssql.AdminUserName
            });
        }
        catch (AppException)
        {
            // MSSQL kurulu değil; listeye eklemiyoruz.
        }

        return result;
    }

    public async Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(
        RegisterDatabaseCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        var engine = ParseEngine(request.Engine);
        if (engine == DbEngine.MsSql)
        {
            // MSSQL entegre güvenlik kullanır; ayrı kimlik bilgisi gerekmez.
            throw new AppException("database.engine_not_supported", 400, 400);
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            throw new AppException("database.admin_password_missing", 400, 400);
        }

        var descriptor = ResolveDescriptor(engine);
        var component = await dbContext.InstalledComponents.FirstOrDefaultAsync(
            item => item.ComponentId == descriptor.ComponentId && item.Status == ComponentInstallStatus.Installed,
            cancellationToken)
            ?? throw new AppException("database.engine_not_installed", 404, 404);

        var adminUser = string.IsNullOrWhiteSpace(request.AdminUserName)
            ? descriptor.AdminUserName
            : NormalizeUserName(request.AdminUserName);
        var port = request.Port > 0 ? request.Port : DefaultExternalPort(engine);

        var profile = new DatabaseConnectionProfile(
            engine,
            descriptor.EngineKey,
            descriptor.DisplayName,
            descriptor.ComponentId,
            component.ServiceName,
            "127.0.0.1",
            port,
            adminUser,
            request.Password,
            descriptor.SupportsHostScopedUsers,
            component.InstallDir);

        // Kimlik bilgilerini GERÇEKTEN bağlanarak doğrula; yanlışsa kaydetme.
        try
        {
            await ResolveProvider(engine).GetWorkspaceAsync(profile, cancellationToken);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppException("database.credentials_invalid", 400, 400, exception.Message);
        }

        // Mevcut yapılandırmayı koru (systemDetected vb.), yalnız kimlik bilgilerini ekle/güncelle.
        var configuration = ReadProtectedConfiguration(component);
        configuration[GeneratedPasswordKey] = request.Password;
        configuration[AdminUserKey] = adminUser;
        configuration[AdminPortKey] = port.ToString();
        component.ProtectedConfigurationJson = secretProtector.Protect(
            JsonSerializer.Serialize(configuration, SerializerOptions));
        component.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return BuildEngineStatus(descriptor, component, configuration);
    }

    public async Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(engine);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        return await ResolveProvider(dbEngine).GetWorkspaceAsync(profile, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(engine);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        return await ResolveProvider(dbEngine).HardenAdminAccountsAsync(profile, cancellationToken);
    }

    public async Task<DatabaseWorkspaceDto> CreateDatabaseAsync(
        CreateDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(request.Engine);
        var databaseName = NormalizeDatabaseName(request.Name);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        await provider.CreateDatabaseAsync(profile, databaseName, cancellationToken);

        // Plesk gibi: kullanıcı adı verildiyse aynı anda kullanıcı oluştur ve bu veritabanına
        // otomatik TAM yetki ver (host '%' = her yerden; ayrı host/yetki seçimi yok).
        if (!string.IsNullOrWhiteSpace(request.UserName))
        {
            var newUser = NormalizeUserName(request.UserName);
            const string anyHost = "%";
            var fullPrivileges = NormalizePrivileges(["ALL PRIVILEGES"], dbEngine);
            await provider.CreateUserAsync(profile, newUser, request.Password, anyHost, cancellationToken);
            await provider.GrantAccessAsync(profile, databaseName, newUser, anyHost, fullPrivileges, cancellationToken);
            // phpMyAdmin'i bu kullanıcı olarak açıp yalnız kendi DB'sini göstermek için parolayı şifreli sakla.
            await credentialStore.UpsertAsync(profile.EngineKey, newUser, anyHost, databaseName, request.Password, cancellationToken);
        }

        return await provider.GetWorkspaceAsync(profile, cancellationToken);
    }

    public async Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(engine);
        var databaseName = NormalizeDatabaseName(name);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        // Plesk gibi: DB'ye bağlı (panel oluşturduğu) kullanıcıyı da temizle ki öksüz kullanıcı kalmasın.
        var owner = await credentialStore.FindForDatabaseAsync(profile.EngineKey, databaseName, cancellationToken);

        await provider.DeleteDatabaseAsync(profile, databaseName, cancellationToken);

        // Önce bu DB'nin kimlik kaydını sil, sonra kullanıcı başka DB'de kullanılmıyorsa kullanıcıyı düşür.
        await credentialStore.RemoveDatabaseAsync(profile.EngineKey, databaseName, cancellationToken);
        if (owner is not null)
        {
            var stillUsed = await credentialStore.IsUserReferencedAsync(profile.EngineKey, owner.Value.UserName, owner.Value.Host, cancellationToken);
            if (!stillUsed)
            {
                try
                {
                    await provider.DeleteUserAsync(profile, owner.Value.UserName, owner.Value.Host, cancellationToken);
                }
                catch
                {
                    // Kullanıcı zaten yoksa / silinemezse DB silme işlemini başarısız sayma.
                }
            }
        }
    }

    public async Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(engine);
        var normalizedUser = NormalizeUserName(userName);
        var allowedHost = NormalizeAllowedHost(host);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        await ResolveProvider(dbEngine).DeleteUserAsync(profile, normalizedUser, allowedHost, cancellationToken);
        // Öksüz kimlik kaydı kalmasın (kullanıcı silinince phpMyAdmin parolası da gitmeli).
        await credentialStore.RemoveUserAsync(profile.EngineKey, normalizedUser, allowedHost, cancellationToken);
    }

    public async Task<DatabaseWorkspaceDto> SetUserPasswordAsync(
        SetDatabaseUserPasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(request.Engine);
        var userName = NormalizeUserName(request.UserName);
        var allowedHost = NormalizeAllowedHost(request.AllowedHost);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        await provider.SetUserPasswordAsync(profile, userName, request.Password, allowedHost, cancellationToken);
        await credentialStore.UpsertAsync(profile.EngineKey, userName, allowedHost, string.Empty, request.Password, cancellationToken);
        return await provider.GetWorkspaceAsync(profile, cancellationToken);
    }

    public async Task<DatabaseWorkspaceDto> CreateUserAsync(
        CreateDatabaseUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(request.Engine);
        var userName = NormalizeUserName(request.UserName);
        var allowedHost = NormalizeAllowedHost(request.AllowedHost);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        await provider.CreateUserAsync(profile, userName, request.Password, allowedHost, cancellationToken);
        await credentialStore.UpsertAsync(profile.EngineKey, userName, allowedHost, string.Empty, request.Password, cancellationToken);
        return await provider.GetWorkspaceAsync(profile, cancellationToken);
    }

    public async Task<DatabaseWorkspaceDto> GrantAccessAsync(
        GrantDatabaseAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(request.Engine);
        var databaseName = NormalizeDatabaseName(request.DatabaseName);
        var userName = NormalizeUserName(request.UserName);
        var allowedHost = NormalizeAllowedHost(request.AllowedHost);
        var privileges = NormalizePrivileges(request.Privileges, dbEngine);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        await provider.GrantAccessAsync(profile, databaseName, userName, allowedHost, privileges, cancellationToken);
        return await provider.GetWorkspaceAsync(profile, cancellationToken);
    }

    public async Task<DatabaseDumpResultDto> ExportDumpAsync(
        DatabaseDumpRequest request,
        CancellationToken cancellationToken = default)
    {
        var engine = ParseEngine(request.Engine);
        var profile = await ResolveConnectionProfileAsync(engine, cancellationToken);
        var databaseName = string.IsNullOrWhiteSpace(request.DatabaseName)
            ? string.Empty
            : NormalizeDatabaseName(request.DatabaseName);
        var targetPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.FilePath.Trim()));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var result = engine switch
        {
            DbEngine.MariaDb or DbEngine.MySql => await ExportMySqlAsync(profile, databaseName, targetPath, cancellationToken),
            DbEngine.PostgreSql => await ExportPostgreSqlAsync(profile, targetPath, cancellationToken),
            DbEngine.MsSql => await ExportMsSqlAsync(profile, databaseName, targetPath, cancellationToken),
            _ => throw new AppException("database.engine_not_supported", 400, 400)
        };

        if (!result.Success || !File.Exists(targetPath))
        {
            throw new AppException("database.dump_failed", 500, 500);
        }

        return new DatabaseDumpResultDto
        {
            Engine = profile.EngineKey,
            DatabaseName = databaseName,
            FilePath = targetPath,
            Size = new FileInfo(targetPath).Length
        };
    }

    public async Task<DatabaseDumpResultDto> ImportDumpAsync(
        DatabaseDumpRequest request,
        CancellationToken cancellationToken = default)
    {
        var engine = ParseEngine(request.Engine);
        var profile = await ResolveConnectionProfileAsync(engine, cancellationToken);
        var sourcePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.FilePath.Trim()));
        if (!File.Exists(sourcePath))
        {
            throw new AppException("backup.not_found", 404, 404);
        }

        var databaseName = string.IsNullOrWhiteSpace(request.DatabaseName)
            ? string.Empty
            : NormalizeDatabaseName(request.DatabaseName);
        var result = engine switch
        {
            DbEngine.MariaDb or DbEngine.MySql => await ImportMySqlAsync(profile, databaseName, sourcePath, cancellationToken),
            DbEngine.PostgreSql => await ImportPostgreSqlAsync(profile, sourcePath, cancellationToken),
            DbEngine.MsSql => await ImportMsSqlAsync(profile, databaseName, sourcePath, cancellationToken),
            _ => throw new AppException("database.engine_not_supported", 400, 400)
        };

        if (!result.Success)
        {
            throw new AppException("database.restore_failed", 500, 500);
        }

        return new DatabaseDumpResultDto
        {
            Engine = profile.EngineKey,
            DatabaseName = databaseName,
            FilePath = sourcePath,
            Size = new FileInfo(sourcePath).Length
        };
    }

    public async Task<DatabaseTransferResultDto> TransferDatabaseAsync(
        DatabaseTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        var engine = ParseEngine(request.Engine);
        if (engine is not (DbEngine.MariaDb or DbEngine.MySql))
        {
            // Sunucu taraflı taşıma yalnız MySQL/MariaDB için (mysqldump→mysql).
            throw new AppException("database.engine_not_supported", 400, 400);
        }

        var sourceHost = (request.SourceHost ?? string.Empty).Trim();
        var sourceUser = (request.SourceUserName ?? string.Empty).Trim();
        if (sourceHost.Length == 0 || sourceUser.Length == 0)
        {
            throw new AppException("database.transfer_source_required", 400, 400);
        }

        var sourcePort = request.SourcePort > 0 ? request.SourcePort : 3306;
        var sourceDatabase = NormalizeDatabaseName(request.SourceDatabase);
        var targetDatabase = string.IsNullOrWhiteSpace(request.TargetDatabase)
            ? sourceDatabase
            : NormalizeDatabaseName(request.TargetDatabase);

        var targetProfile = await ResolveConnectionProfileAsync(engine, cancellationToken);
        var provider = ResolveProvider(engine);

        // Hedef DB yoksa oluştur; varsa içine aktarmaya devam et.
        try
        {
            await provider.CreateDatabaseAsync(targetProfile, targetDatabase, cancellationToken);
        }
        catch (AppException existing) when (existing.MessageKey == "database.database_already_exists")
        {
            // Hedef zaten var; üstüne aktarılır.
        }

        var mysqldump = FindExecutable(targetProfile.InstallDir, "mysqldump.exe");
        var mysql = FindExecutable(targetProfile.InstallDir, "mysql.exe");
        var tempFile = Path.Combine(Path.GetTempPath(), $"adapanel-transfer-{Guid.NewGuid():N}.sql");
        // Büyük veritabanları için geniş tavan (kullanıcının yaşadığı timeout sorununu çözer).
        var transferTimeout = TimeSpan.FromHours(2);

        try
        {
            // 1) Kaynaktan (uzak olabilir) tek DB dump → temp .sql.
            //    --databases KULLANMA: CREATE DATABASE/USE eklemesin ki hedef ad farklı olsa bile içine aktarılsın.
            var dumpArguments =
                $"--host={sourceHost} --port={sourcePort} --user=\"{sourceUser}\" " +
                "--single-transaction --routines --events --triggers --skip-lock-tables --default-character-set=utf8mb4 " +
                $"\"{sourceDatabase}\" --result-file=\"{tempFile}\"";
            var dump = await ProcessRunner.RunAsync(
                mysqldump,
                dumpArguments,
                cancellationToken: cancellationToken,
                environmentVariables: new Dictionary<string, string> { ["MYSQL_PWD"] = request.SourcePassword ?? string.Empty },
                timeout: transferTimeout);

            if (dump.ExitCode != 0 || !File.Exists(tempFile))
            {
                throw new AppException("database.transfer_source_failed", 502, 502, TrimTransferError(dump.StandardError));
            }

            // 2) Yerel hedef veritabanına import.
            var normalizedSource = tempFile.Replace('\\', '/').Replace("\"", "\\\"");
            var importArguments =
                $"--host={targetProfile.Host} --port={targetProfile.Port} --user=\"{targetProfile.AdminUserName}\" " +
                $"--default-character-set=utf8mb4 \"{targetDatabase}\" --execute=\"source {normalizedSource}\"";
            var import = await ProcessRunner.RunAsync(
                mysql,
                importArguments,
                cancellationToken: cancellationToken,
                environmentVariables: new Dictionary<string, string> { ["MYSQL_PWD"] = targetProfile.AdminPassword },
                timeout: transferTimeout);

            if (import.ExitCode != 0)
            {
                throw new AppException("database.transfer_import_failed", 500, 500, TrimTransferError(import.StandardError));
            }

            return new DatabaseTransferResultDto
            {
                Engine = targetProfile.EngineKey,
                SourceDatabase = sourceDatabase,
                TargetDatabase = targetDatabase,
                Size = File.Exists(tempFile) ? new FileInfo(tempFile).Length : 0
            };
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // En iyi çaba: geçici dosya silinemezse işlemi başarısız sayma.
            }
        }
    }

    private static string TrimTransferError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 600 ? trimmed[..600] : trimmed;
    }

    private async Task<DatabaseConnectionProfile> ResolveConnectionProfileAsync(DbEngine engine, CancellationToken cancellationToken)
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

        var configuration = ReadProtectedConfiguration(installedComponent);
        var adminPassword = configuration.TryGetValue(GeneratedPasswordKey, out var storedPassword) ? storedPassword : string.Empty;
        if (string.IsNullOrWhiteSpace(adminPassword))
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

    private static int DefaultExternalPort(DbEngine engine)
        => engine switch
        {
            DbEngine.PostgreSql => 5432,
            // Sistemde kurulu MySQL/MariaDB geleneksel olarak 3306 dinler
            // (AdaPanel'in kendi MySQL kurulumu 3307 kullanır, çakışmayı önlemek için).
            _ => 3306
        };

    private DatabaseEngineStatusDto BuildEngineStatus(
        EngineDescriptor descriptor,
        InstalledComponent component,
        IReadOnlyDictionary<string, string> configuration)
    {
        var hasPassword = configuration.TryGetValue(GeneratedPasswordKey, out var password) && !string.IsNullOrWhiteSpace(password);
        var adminUser = configuration.TryGetValue(AdminUserKey, out var storedUser) && !string.IsNullOrWhiteSpace(storedUser)
            ? storedUser
            : descriptor.AdminUserName;
        var port = configuration.TryGetValue(AdminPortKey, out var storedPort) && int.TryParse(storedPort, out var parsedPort) && parsedPort > 0
            ? parsedPort
            : descriptor.DefaultPort;
        var systemDetected = configuration.TryGetValue(SystemDetectedKey, out var detected) &&
            bool.TryParse(detected, out var detectedFlag) && detectedFlag;

        return new DatabaseEngineStatusDto
        {
            Engine = descriptor.EngineKey,
            DisplayName = descriptor.DisplayName,
            Installed = true,
            Ready = hasPassword,
            SystemDetected = systemDetected,
            Host = "127.0.0.1",
            Port = port,
            AdminUserName = adminUser
        };
    }

    private Dictionary<string, string> ReadProtectedConfiguration(InstalledComponent component)
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

    private static Task<CommandResult> ExportMySqlAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable(profile.InstallDir, "mysqldump.exe");
        var databaseArgument = string.IsNullOrWhiteSpace(databaseName)
            ? "--all-databases"
            : $"--databases \"{databaseName}\"";
        var arguments = $"--host={profile.Host} --port={profile.Port} --user=\"{profile.AdminUserName}\" --single-transaction --routines --events --triggers {databaseArgument} --result-file=\"{targetPath}\"";
        return ProcessRunner.RunAsync(
            executable,
            arguments,
            cancellationToken: cancellationToken,
            environmentVariables: new Dictionary<string, string> { ["MYSQL_PWD"] = profile.AdminPassword });
    }

    private static Task<CommandResult> ImportMySqlAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable(profile.InstallDir, "mysql.exe");
        var databaseArgument = string.IsNullOrWhiteSpace(databaseName) ? string.Empty : $" \"{databaseName}\"";
        var normalizedSource = sourcePath.Replace('\\', '/').Replace("\"", "\\\"");
        var arguments = $"--host={profile.Host} --port={profile.Port} --user=\"{profile.AdminUserName}\"{databaseArgument} --execute=\"source {normalizedSource}\"";
        return ProcessRunner.RunAsync(
            executable,
            arguments,
            cancellationToken: cancellationToken,
            environmentVariables: new Dictionary<string, string> { ["MYSQL_PWD"] = profile.AdminPassword });
    }

    private static Task<CommandResult> ExportPostgreSqlAsync(
        DatabaseConnectionProfile profile,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable(profile.InstallDir, "pg_dumpall.exe");
        var arguments = $"--host={profile.Host} --port={profile.Port} --username=\"{profile.AdminUserName}\" --file=\"{targetPath}\"";
        return ProcessRunner.RunAsync(
            executable,
            arguments,
            cancellationToken: cancellationToken,
            environmentVariables: new Dictionary<string, string> { ["PGPASSWORD"] = profile.AdminPassword });
    }

    private static Task<CommandResult> ImportPostgreSqlAsync(
        DatabaseConnectionProfile profile,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable(profile.InstallDir, "psql.exe");
        var arguments = $"--host={profile.Host} --port={profile.Port} --username=\"{profile.AdminUserName}\" --dbname=postgres --file=\"{sourcePath}\"";
        return ProcessRunner.RunAsync(
            executable,
            arguments,
            cancellationToken: cancellationToken,
            environmentVariables: new Dictionary<string, string> { ["PGPASSWORD"] = profile.AdminPassword });
    }

    private static async Task<CommandResult> ExportMsSqlAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string targetPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new AppException("database.name_required", 400, 400);
        }

        try
        {
            await using var connection = new SqlConnection(BuildMsSqlConnectionString(profile, "master"));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = $"BACKUP DATABASE [{databaseName}] TO DISK = @path WITH INIT, COMPRESSION, CHECKSUM;";
            command.Parameters.AddWithValue("@path", targetPath);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new CommandResult { ExitCode = 0 };
        }
        catch (SqlException exception)
        {
            return new CommandResult { ExitCode = exception.Number, StandardError = exception.Message };
        }
    }

    private static async Task<CommandResult> ImportMsSqlAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new AppException("database.name_required", 400, 400);
        }

        try
        {
            await using var connection = new SqlConnection(BuildMsSqlConnectionString(profile, "master"));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = $"""
                IF DB_ID(N'{databaseName.Replace("'", "''")}') IS NOT NULL
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                RESTORE DATABASE [{databaseName}] FROM DISK = @path WITH REPLACE, RECOVERY;
                ALTER DATABASE [{databaseName}] SET MULTI_USER;
                """;
            command.Parameters.AddWithValue("@path", sourcePath);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new CommandResult { ExitCode = 0 };
        }
        catch (SqlException exception)
        {
            return new CommandResult { ExitCode = exception.Number, StandardError = exception.Message };
        }
    }

    private static string BuildMsSqlConnectionString(DatabaseConnectionProfile profile, string databaseName)
    {
        return new SqlConnectionStringBuilder
        {
            DataSource = profile.Host,
            InitialCatalog = databaseName,
            IntegratedSecurity = string.IsNullOrWhiteSpace(profile.AdminPassword),
            UserID = string.IsNullOrWhiteSpace(profile.AdminPassword) ? string.Empty : profile.AdminUserName,
            Password = profile.AdminPassword,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 5
        }.ConnectionString;
    }

    private static DatabaseConnectionProfile ResolveMsSqlProfile()
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

    private static string FindExecutable(string installDir, string fileName)
    {
        var candidates = new[]
        {
            installDir,
            Path.Combine(installDir, "bin"),
            Environment.ExpandEnvironmentVariables("%ProgramFiles%\\MariaDB 11.4\\bin"),
            Environment.ExpandEnvironmentVariables("%ProgramFiles%\\MySQL\\MySQL Server 8.4\\bin"),
            Environment.ExpandEnvironmentVariables("%ProgramFiles%\\PostgreSQL\\16\\bin")
        };

        foreach (var directory in candidates.Where(Directory.Exists))
        {
            var direct = Path.Combine(directory, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            var nested = Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (nested is not null)
            {
                return nested;
            }
        }

        throw new AppException("database.dump_tool_missing", 404, 404);
    }

    private static EngineDescriptor ResolveDescriptor(DbEngine engine)
    {
        return engine switch
        {
            DbEngine.MariaDb => new EngineDescriptor(engine, "mariadb", "MariaDB", "mariadb-11", "root", 3306, true),
            DbEngine.MySql => new EngineDescriptor(engine, "mysql", "MySQL", "mysql-8", "root", 3307, true),
            DbEngine.PostgreSql => new EngineDescriptor(engine, "postgresql", "PostgreSQL", "postgresql-16", "postgres", 5432, false),
            _ => throw new AppException("database.engine_not_supported", 400, 400)
        };
    }

    private IDatabaseEngineProvider ResolveProvider(DbEngine engine)
    {
        if (engine == DbEngine.MariaDb)
        {
            return providers.First(provider => provider.Engine == DbEngine.MySql);
        }

        var provider = providers.FirstOrDefault(candidate => candidate.Engine == engine);
        return provider ?? throw new AppException("database.engine_not_supported", 400, 400);
    }

    private static DbEngine ParseEngine(string engine)
    {
        return engine.Trim().ToLowerInvariant() switch
        {
            "mariadb" => DbEngine.MariaDb,
            "mysql" => DbEngine.MySql,
            "postgresql" or "postgres" => DbEngine.PostgreSql,
            "mssql" or "sqlserver" => DbEngine.MsSql,
            _ => throw new AppException("database.engine_not_supported", 400, 400)
        };
    }

    private static string NormalizeDatabaseName(string value)
    {
        var normalized = value.Trim();
        if (!DatabaseNamePattern().IsMatch(normalized))
        {
            throw new AppException("database.invalid_name", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeUserName(string value)
    {
        var normalized = value.Trim();
        if (!UserNamePattern().IsMatch(normalized))
        {
            throw new AppException("database.invalid_user_name", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeAllowedHost(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "%" : value.Trim();
        if (!HostPattern().IsMatch(normalized))
        {
            throw new AppException("database.invalid_host", 400, 400);
        }

        return normalized;
    }

    private static IReadOnlyCollection<string> NormalizePrivileges(string[] privileges, DbEngine engine)
    {
        if (privileges.Length == 0)
        {
            return ["ALL PRIVILEGES"];
        }

        var allowed = engine switch
        {
            DbEngine.PostgreSql => new HashSet<string>(
                ["ALL PRIVILEGES", "CONNECT", "CREATE", "TEMP", "TEMPORARY"],
                StringComparer.OrdinalIgnoreCase),
            DbEngine.MsSql => new HashSet<string>(
                ["ALL PRIVILEGES", "CONNECT", "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER"],
                StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(
                ["ALL PRIVILEGES", "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "INDEX"],
                StringComparer.OrdinalIgnoreCase)
        };

        var normalized = privileges
            .Select(item => item.Trim().ToUpperInvariant())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            return ["ALL PRIVILEGES"];
        }

        if (normalized.Any(item => !allowed.Contains(item)))
        {
            throw new AppException("database.invalid_privileges", 400, 400);
        }

        return normalized.Contains("ALL PRIVILEGES", StringComparer.OrdinalIgnoreCase)
            ? ["ALL PRIVILEGES"]
            : normalized;
    }

    private sealed record EngineDescriptor(
        DbEngine Engine,
        string EngineKey,
        string DisplayName,
        string ComponentId,
        string AdminUserName,
        int DefaultPort,
        bool SupportsHostScopedUsers);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,63}$", RegexOptions.Compiled)]
    private static partial Regex DatabaseNamePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,63}$", RegexOptions.Compiled)]
    private static partial Regex UserNamePattern();

    [GeneratedRegex("^[A-Za-z0-9.%:_-]{1,255}$", RegexOptions.Compiled)]
    private static partial Regex HostPattern();
}
