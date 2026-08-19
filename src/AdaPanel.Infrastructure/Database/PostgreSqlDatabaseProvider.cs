using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Enums;
using Npgsql;

namespace AdaPanel.Infrastructure.Database;

internal sealed class PostgreSqlDatabaseProvider : IDatabaseEngineProvider
{
    public DbEngine Engine => DbEngine.PostgreSql;

    public async Task<DatabaseWorkspaceDto> GetWorkspaceAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);
        var databases = await GetDatabasesAsync(connection, profile.EngineKey, cancellationToken);
        var users = await GetUsersAsync(connection, profile.EngineKey, cancellationToken);

        return new DatabaseWorkspaceDto
        {
            Engine = profile.EngineKey,
            DisplayName = profile.DisplayName,
            ComponentId = profile.ComponentId,
            ServiceName = profile.ServiceName,
            Host = profile.Host,
            Port = profile.Port,
            AdminUserName = profile.AdminUserName,
            AdminPassword = profile.AdminPassword,
            SupportsHostScopedUsers = profile.SupportsHostScopedUsers,
            Databases = databases,
            Users = users
        };
    }

    public async Task CreateDatabaseAsync(DatabaseConnectionProfile profile, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            throw new AppException("database.database_already_exists", 409, 409);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using (var terminateCommand = connection.CreateCommand())
            {
                terminateCommand.CommandText = """
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE datname = @databaseName
                      AND pid <> pg_backend_pid();
                    """;
                terminateCommand.Parameters.AddWithValue("@databaseName", databaseName);
                await terminateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var dropCommand = connection.CreateCommand();
            dropCommand.CommandText = $"DROP DATABASE {QuoteIdentifier(databaseName)};";
            await dropCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task CreateUserAsync(
        DatabaseConnectionProfile profile,
        string userName,
        string password,
        string allowedHost,
        CancellationToken cancellationToken)
    {
        _ = allowedHost;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE ROLE {QuoteIdentifier(userName)} WITH LOGIN PASSWORD @password;";
            command.Parameters.AddWithValue("@password", password);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateObject)
        {
            throw new AppException("database.user_already_exists", 409, 409);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string userName, string password, string allowedHost, CancellationToken cancellationToken)
    {
        _ = allowedHost;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER ROLE {QuoteIdentifier(userName)} WITH LOGIN PASSWORD @password;";
            command.Parameters.AddWithValue("@password", password);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedObject)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task DeleteUserAsync(DatabaseConnectionProfile profile, string userName, string host, CancellationToken cancellationToken)
    {
        _ = host;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP ROLE {QuoteIdentifier(userName)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedObject)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public async Task GrantAccessAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string userName,
        string allowedHost,
        IReadOnlyCollection<string> privileges,
        CancellationToken cancellationToken)
    {
        _ = allowedHost;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"GRANT {BuildPrivilegeClause(privileges)} ON DATABASE {QuoteIdentifier(databaseName)} TO {QuoteIdentifier(userName)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedObject)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    public string RemoteAccessMechanism => "pgHba";

    public async Task<IReadOnlyDictionary<string, RemoteAccessFacts>> ReadRemoteAccessAsync(
        DatabaseConnectionProfile profile,
        IReadOnlyCollection<string> userNames,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            // Dosya ve listen_addresses kullanıcıdan bağımsızdır: bir kez okunur.
            var hbaPath = await ShowAsync(connection, "hba_file", cancellationToken);
            var entries = PostgreSqlHbaEditor.Read(ReadHbaFile(hbaPath));
            var warning = await DetectListenWarningAsync(connection, cancellationToken);
            var databases = await ReadAllGrantedDatabasesAsync(connection, cancellationToken);

            var result = new Dictionary<string, RemoteAccessFacts>(StringComparer.OrdinalIgnoreCase);
            foreach (var userName in userNames)
            {
                result[userName] = new RemoteAccessFacts(
                    entries
                        .Where(entry => string.Equals(entry.User, userName, StringComparison.Ordinal))
                        .Select(entry => entry.Address)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    databases.TryGetValue(userName, out var owned) ? owned : [],
                    warning);
            }

            return result;
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
    }

    /// <summary>
    /// <c>pg_hba.conf</c>'taki panel bloğunu bu kullanıcı için yeniden yazar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PostgreSQL'de erişim sınırı rolün kendisinde değil, <c>pg_hba.conf</c>'tadır: "hangi
    /// veritabanına, hangi rolle, hangi ağdan". Bu yüzden satırlar (veritabanı × adres) çarpımıdır
    /// ve kullanıcının yetkili OLMADIĞI bir veritabanı için satır yazılmaz — yazılsaydı erişim
    /// listesi, yetkiden daha geniş bir kapı açardı.
    /// </para>
    /// <para>
    /// Değişiklik <c>pg_reload_conf()</c> ile YÜKLENİR. Yüklenmezse dosya doğru ama sunucu eski
    /// kuralları uyguluyor olurdu; panel "açıldı" derken erişim açılmamış olurdu. Yeniden başlatma
    /// GEREKMEZ (<c>pg_hba.conf</c> reload ile okunur), yani çalışan bağlantılar kesilmez.
    /// </para>
    /// </remarks>
    public async Task ApplyRemoteAccessAsync(
        DatabaseConnectionProfile profile,
        string userName,
        IReadOnlyCollection<string> databaseNames,
        IReadOnlyCollection<RemoteAccessOrigin> origins,
        string password,
        CancellationToken cancellationToken)
    {
        _ = password; // PostgreSQL'de yeni bir rol oluşturulmaz; yalnız erişim listesi değişir.
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        var hbaPath = await ShowAsync(connection, "hba_file", cancellationToken);
        var content = ReadHbaFile(hbaPath);

        var entries = new List<HbaEntry>();
        foreach (var databaseName in databaseNames)
        {
            foreach (var origin in origins)
            {
                entries.Add(new HbaEntry(databaseName, userName, origin.ToPostgresAddress()));

                // "Her yer" hem IPv4 hem IPv6'yı kapsamalı; tek satır yazmak, IPv6 üzerinden gelen
                // istemcinin sessizce reddedilmesi demek olurdu (kullanıcı için açıklanamaz bir hata).
                if (origin.IsAnywhere)
                {
                    entries.Add(new HbaEntry(databaseName, userName, "::/0"));
                }
            }
        }

        var updated = PostgreSqlHbaEditor.Apply(content, entries, PostgreSqlHbaEditor.DetectAuthMethod(content));

        try
        {
            // İlk değişiklikten önce bir kereye mahsus yedek: yönetici elle yazdığı kuralları
            // panelin bir hatasından sonra geri alabilsin.
            var backupPath = hbaPath + ".adapanel.bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(hbaPath, backupPath);
            }

            File.WriteAllText(hbaPath, updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AppException("database.remote_hba_failed", 500, 500, exception.Message);
        }

        await using var reload = connection.CreateCommand();
        reload.CommandText = "SELECT pg_reload_conf();";
        await reload.ExecuteScalarAsync(cancellationToken);
    }

    private static string ReadHbaFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AppException("database.remote_hba_failed", 500, 500, exception.Message);
        }
    }

    private static async Task<string> ShowAsync(NpgsqlConnection connection, string setting, CancellationToken cancellationToken)
    {
        // Yol TAHMİN EDİLMEZ, motora sorulur. Kurulum dizininden yol türetmek, sunucuda zaten
        // kurulu (panelin kurmadığı) bir PostgreSQL'de yanlış dosyayı düzenlemek demekti.
        await using var command = connection.CreateCommand();
        command.CommandText = $"SHOW {setting};";
        var value = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppException("database.remote_hba_failed", 500, 500);
        }

        return value;
    }

    /// <summary>
    /// Sunucu yalnız yerel arayüzü dinliyorsa uyarı anahtarı döner.
    /// </summary>
    /// <remarks>
    /// <c>listen_addresses</c> panel tarafından DEĞİŞTİRİLMEZ: bu ayar yalnız yeniden başlatmayla
    /// etkinleşir ve canlı bir veritabanını uyarısız yeniden başlatmak, açılan bir porttan daha
    /// büyük bir sürprizdir. Bunun yerine durum bildirilir; kararı yönetici verir.
    /// </remarks>
    private static async Task<string> DetectListenWarningAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var value = await ShowAsync(connection, "listen_addresses", cancellationToken);
        var entries = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        var yalnizYerel = entries.Length > 0 && entries.All(entry =>
            entry.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            entry.Equals("127.0.0.1", StringComparison.Ordinal) ||
            entry.Equals("::1", StringComparison.Ordinal));

        return yalnizYerel ? "database.remote_pg_listen_local" : string.Empty;
    }

    private static async Task<Dictionary<string, List<string>>> ReadAllGrantedDatabasesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        // has_database_privilege KULLANILMAZ: PUBLIC'e verilmiş CONNECT yetkisi yüzünden HER rol
        // HER veritabanı için doğru döner ve kapsam denetimi anlamsızlaşırdı. Burada yalnız role
        // AÇIKÇA verilmiş ACL girdileri (ya da sahiplik) sayılır.
        const string sql = """
            SELECT r.rolname, d.datname
            FROM pg_database d
            JOIN pg_roles r
              ON r.oid = d.datdba
              OR r.oid IN (SELECT a.grantee FROM aclexplode(d.datacl) a)
            WHERE d.datistemplate = FALSE
              AND d.datname <> 'postgres'
              AND r.rolname NOT LIKE 'pg_%'
            ORDER BY r.rolname, d.datname;
            """;

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var role = reader.GetString(0);
            if (!result.TryGetValue(role, out var names))
            {
                names = [];
                result[role] = names;
            }

            var name = reader.GetString(1);
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return result;
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.AdminUserName,
                Password = profile.AdminPassword,
                Database = "postgres",
                SslMode = SslMode.Disable,
                Timeout = 5,
                CommandTimeout = 5
            };

            var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (PostgresException exception)
        {
            throw MapUnexpectedPostgresException(exception);
        }
        catch (Exception)
        {
            throw new AppException("database.connection_failed", 503, 503);
        }
    }

    private static async Task<IReadOnlyCollection<DatabaseRecordDto>> GetDatabasesAsync(
        NpgsqlConnection connection,
        string engineKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                d.datname,
                pg_catalog.pg_get_userbyid(d.datdba) AS owner_name,
                pg_encoding_to_char(d.encoding) AS encoding_name,
                d.datcollate
            FROM pg_database d
            WHERE d.datistemplate = FALSE
              AND d.datname <> 'postgres'
            ORDER BY d.datname;
            """;

        var results = new List<DatabaseRecordDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseRecordDto
            {
                Engine = engineKey,
                Name = reader.GetString(0),
                Owner = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Encoding = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Collation = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
            });
        }

        return results;
    }

    private static async Task<IReadOnlyCollection<DatabaseUserDto>> GetUsersAsync(
        NpgsqlConnection connection,
        string engineKey,
        CancellationToken cancellationToken)
    {
        // Sistem rollerini (pg_*) ve süper-kullanıcı 'postgres'u GİZLE (motor admin — silinirse panel DB erişimini kaybeder).
        const string sql = """
            SELECT rolname, rolcanlogin
            FROM pg_roles
            WHERE rolname NOT LIKE 'pg_%'
              AND rolname <> 'postgres'
            ORDER BY rolname;
            """;

        var results = new List<DatabaseUserDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseUserDto
            {
                Engine = engineKey,
                UserName = reader.GetString(0),
                Host = "*",
                CanLogin = !reader.IsDBNull(1) && reader.GetBoolean(1)
            });
        }

        return results;
    }

    private static string QuoteIdentifier(string value)
    {
        return $"\"{value}\"";
    }

    private static string BuildPrivilegeClause(IReadOnlyCollection<string> privileges)
    {
        if (privileges.Count == 0 || privileges.Contains("ALL PRIVILEGES", StringComparer.OrdinalIgnoreCase))
        {
            return "ALL PRIVILEGES";
        }

        return string.Join(", ", privileges.Select(value =>
            string.Equals(value, "TEMP", StringComparison.OrdinalIgnoreCase) ? "TEMPORARY" : value));
    }

    private static AppException MapUnexpectedPostgresException(PostgresException exception)
    {
        return exception.SqlState == PostgresErrorCodes.InvalidPassword
            ? new AppException("database.connection_failed", 503, 503)
            : new AppException("database.configuration_failed", 500, 500);
    }
}
