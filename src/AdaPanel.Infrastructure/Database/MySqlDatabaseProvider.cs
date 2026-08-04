using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Enums;
using MySqlConnector;

namespace AdaPanel.Infrastructure.Database;

internal sealed class MySqlDatabaseProvider : IDatabaseEngineProvider
{
    public DbEngine Engine => DbEngine.MySql;

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
        catch (MySqlException exception) when (exception.Number == 1007)
        {
            throw new AppException("database.database_already_exists", 409, 409);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    public async Task DeleteDatabaseAsync(DatabaseConnectionProfile profile, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE {QuoteIdentifier(databaseName)};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number is 1008 or 1049)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    public async Task CreateUserAsync(
        DatabaseConnectionProfile profile,
        string userName,
        string password,
        string allowedHost,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE USER '{userName.Replace("'", "''")}'@'{allowedHost.Replace("'", "''")}' IDENTIFIED BY @password;";
            command.Parameters.AddWithValue("@password", password);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1396)
        {
            throw new AppException("database.user_already_exists", 409, 409);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    public async Task SetUserPasswordAsync(DatabaseConnectionProfile profile, string userName, string password, string allowedHost, CancellationToken cancellationToken)
    {
        var host = string.IsNullOrWhiteSpace(allowedHost) ? "%" : allowedHost;
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER USER '{userName.Replace("'", "''")}'@'{host.Replace("'", "''")}' IDENTIFIED BY @password;";
            command.Parameters.AddWithValue("@password", password);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number is 1396 or 1133)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    // K2 (atomik akışın "uygula" adımı): yerel root hesaplarına (localhost/127.0.0.1/::1) güçlü parola koyar.
    // Her host için YOKSA oluştur + parolayı set et + tam yetki ver → uzak root@'%' kaldırılınca panelin
    // 127.0.0.1 bağlantısı kopmaz. Sonra yeni parolayla YENİ bağlantı açıp DOĞRULAR; başarısızsa servis
    // katmanı saklı parolayı geri alır (panel eski parolayla çalışmaya devam eder).
    public async Task SetRootPasswordAsync(DatabaseConnectionProfile profile, string newPassword, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(profile, cancellationToken);

            foreach (var host in new[] { "localhost", "127.0.0.1", "::1" })
            {
                await using (var ensure = connection.CreateCommand())
                {
                    ensure.CommandText = $"CREATE USER IF NOT EXISTS 'root'@'{host}' IDENTIFIED BY @pw;";
                    ensure.Parameters.AddWithValue("@pw", newPassword);
                    await ensure.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var alter = connection.CreateCommand())
                {
                    alter.CommandText = $"ALTER USER 'root'@'{host}' IDENTIFIED BY @pw;";
                    alter.Parameters.AddWithValue("@pw", newPassword);
                    await alter.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var grant = connection.CreateCommand())
                {
                    grant.CommandText = $"GRANT ALL PRIVILEGES ON *.* TO 'root'@'{host}' WITH GRANT OPTION;";
                    await grant.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var flush = connection.CreateCommand())
            {
                flush.CommandText = "FLUSH PRIVILEGES;";
                await flush.ExecuteNonQueryAsync(cancellationToken);
            }

            // DOĞRULA: panelin kullanacağı yeni parolayla YENİ bağlantı (mevcut bağlantı zaten kimlik doğruluydu).
            var verifyProfile = profile with { AdminPassword = newPassword };
            await using var verifyConnection = await OpenConnectionAsync(verifyProfile, cancellationToken);
            await using var ping = verifyConnection.CreateCommand();
            ping.CommandText = "SELECT 1;";
            await ping.ExecuteScalarAsync(cancellationToken);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    public async Task DeleteUserAsync(DatabaseConnectionProfile profile, string userName, string host, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            var allowedHost = string.IsNullOrWhiteSpace(host) ? "%" : host;
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP USER '{userName.Replace("'", "''")}'@'{allowedHost.Replace("'", "''")}'; FLUSH PRIVILEGES;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number is 1396)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
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
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT {BuildPrivilegeClause(privileges)} ON {EscapeGrantDatabaseName(databaseName)}.* TO '{userName.Replace("'", "''")}'@'{allowedHost.Replace("'", "''")}';
                FLUSH PRIVILEGES;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number is 1049)
        {
            throw new AppException("database.database_not_found", 404, 404);
        }
        catch (MySqlException exception) when (exception.Number is 1133 or 1141 or 1396)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
    }

    /// <summary>
    /// root hesaplarının kimlik doğrulama ZİNCİRİNİ sadeleştirir: yalnızca parola kalsın.
    /// <para>
    /// MariaDB'nin Windows kurucusu (<c>mysql_install_db.exe</c>) root'u "parola VEYA gssapi" olarak
    /// kurar. Sunucu, parolayla gelen istemciye zincirdeki gssapi yöntemini önerebiliyor; PHP'nin
    /// mysqli'si bunu "auth_gssapi_client bilinmiyor" diye reddediyor ve BAĞLANTI KURULAMIYOR.
    /// Panelin kendi sürücüsü ilk yöntemi seçtiği için etkilenmiyor — bu yüzden sorun görünmüyordu.
    /// </para>
    /// <para>
    /// Somut etkisi: phpMyAdmin'i belirli bir veritabanı seçmeden açmak root ile giriş yapar ve
    /// çalışmaz; yöneticinin panelde gördüğü root parolası harici araçlarda da reddedilir. Canlı
    /// olarak ölçüldü: zincir sadeleştirildikten sonra aynı parola hem PHP'den hem mysql.exe'den
    /// çalıştı ve panel etkilenmedi.
    /// </para>
    /// <para>Parola DEĞİŞTİRİLMEZ; yalnız alternatif yöntem kaldırılır. İdempotenttir.</para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> NormalizeRootAuthenticationAsync(
        MySqlConnection connection,
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var fixedAccounts = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.AdminPassword))
        {
            // Parolayı bilmeden zinciri yeniden yazmak, hesabı erişilemez bırakırdı.
            return fixedAccounts;
        }

        // Bu sağlayıcı HEM MariaDB HEM MySQL için kullanılıyor (DatabaseService.ResolveProvider),
        // ama aşağıdaki her şey MariaDB'ye ÖZGÜ: `mysql.global_priv` tablosu MySQL'de YOKTUR
        // (hata 1146) ve `IDENTIFIED VIA ... USING PASSWORD()` MariaDB sözdizimidir. Motor kontrolü
        // yapılmazsa MySQL 8 sunucusunda sertleştirmenin TAMAMI bu adımda patlar — anonim hesap
        // temizliği yapılmış olsa bile uç 500 döner ve kullanıcı güvenlik sıkılaştırmasını hiç
        // tamamlayamaz. gssapi zinciri zaten yalnızca MariaDB'nin Windows kurucusunun ürettiği
        // bir durumdur; MySQL'de böyle bir sorun yoktur.
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "SELECT VERSION();";
            var version = (await versionCommand.ExecuteScalarAsync(cancellationToken))?.ToString() ?? string.Empty;
            if (!version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
            {
                return fixedAccounts;
            }
        }

        var hosts = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            // Zincirde gssapi geçen root satırları. global_priv, yöntem listesini JSON olarak tutar.
            query.CommandText =
                "SELECT Host FROM mysql.global_priv " +
                "WHERE User = 'root' AND JSON_SEARCH(Priv, 'one', 'gssapi') IS NOT NULL;";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hosts.Add(reader.GetString(0));
            }
        }

        foreach (var host in hosts)
        {
            try
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText =
                    $"ALTER USER 'root'@'{host.Replace("'", "''")}' " +
                    "IDENTIFIED VIA mysql_native_password USING PASSWORD(@pwd);";
                alter.Parameters.AddWithValue("@pwd", profile.AdminPassword);
                await alter.ExecuteNonQueryAsync(cancellationToken);
                fixedAccounts.Add($"root@{host} (gssapi kaldırıldı)");
            }
            catch (MySqlException)
            {
                // en iyi çaba: tek satırın hatası diğerlerini ve sıkılaştırmanın kalanını engellemesin
            }
        }

        return fixedAccounts;
    }

    // K2 (mysql_secure_installation eşdeğeri, GÜVENLİ): anonim kullanıcıları ve uzak root@'%' hesabını
    // kaldır. root@localhost ve loopback'e DOKUNMA — panel onlarla bağlanıyor, kilitlenme olmaz. İdempotent.
    public async Task<IReadOnlyList<string>> HardenAdminAccountsAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        var targets = new List<(string User, string Host)>();
        await using (var query = connection.CreateCommand())
        {
            // Güvensiz hesaplar: anonim (User='') veya uzak root (root@'%'). localhost/loopback root KORUNUR.
            query.CommandText = "SELECT User, Host FROM mysql.user WHERE User = '' OR (User = 'root' AND Host = '%');";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                targets.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (user, host) in targets)
        {
            try
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP USER IF EXISTS '{user.Replace("'", "''")}'@'{host.Replace("'", "''")}';";
                await drop.ExecuteNonQueryAsync(cancellationToken);
                removed.Add(string.IsNullOrEmpty(user) ? $"(anonim)@{host}" : $"{user}@{host}");
            }
            catch (MySqlException)
            {
                // en iyi çaba — tek hesabın hatası diğerlerini engellemesin
            }
        }

        // JOKER YETKİ TEMİZLİĞİ.
        //
        // Panel yetkileri uzun süre kaçırmadan verdi: `musteri1_db` için verilen yetki, MySQL'in
        // joker semantiği yüzünden `musteri1Xdb` gibi desene uyan BAŞKA müşterilerin
        // veritabanlarını da kapsıyordu. Üretim tarafı düzeltildi ama ESKİ yetkiler sunucuda
        // olduğu gibi durur; kod düzeltmesi onlara dokunmaz. Burada daraltılırlar.
        try
        {
            var daraltilan = await GrantWildcardRepair.RepairAsync(connection, cancellationToken);
            removed.AddRange(daraltilan.Select(kayit => $"yetki daraltıldı: {kayit}"));
        }
        catch (MySqlException)
        {
            // Temizlik yapılamaması diğer sertleştirme adımlarını geçersiz kılmamalı.
        }

        // Kimlik zinciri sadeleştirmesi sertleştirmenin SON adımıdır ve başarısızlığı ondan önceki
        // (anonim hesap, uzak root, test veritabanı) temizliği geçersiz kılmamalı.
        try
        {
            removed.AddRange(await NormalizeRootAuthenticationAsync(connection, profile, cancellationToken));
        }
        catch (MySqlException)
        {
            // Motor bu şemayı desteklemiyor ya da yetki yok; diğer sertleştirme adımları geçerli kalır.
        }

        if (removed.Count > 0)
        {
            await using var flush = connection.CreateCommand();
            flush.CommandText = "FLUSH PRIVILEGES;";
            await flush.ExecuteNonQueryAsync(cancellationToken);
        }

        // mysql_secure_installation eşi: varsayılan 'test' veritabanını düşür (anonim erişime açık güvensiz
        // varsayılan). YALNIZ BOŞSA (tablosuz) düşürülür → gerçek veri kazara silinmez. Best-effort.
        try
        {
            long testExists;
            await using (var existsCmd = connection.CreateCommand())
            {
                existsCmd.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'test';";
                testExists = Convert.ToInt64(await existsCmd.ExecuteScalarAsync(cancellationToken));
            }

            if (testExists > 0)
            {
                long testTables;
                await using (var tablesCmd = connection.CreateCommand())
                {
                    tablesCmd.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'test';";
                    testTables = Convert.ToInt64(await tablesCmd.ExecuteScalarAsync(cancellationToken));
                }

                if (testTables == 0)
                {
                    await using var dropTest = connection.CreateCommand();
                    dropTest.CommandText = "DROP DATABASE IF EXISTS `test`;";
                    await dropTest.ExecuteNonQueryAsync(cancellationToken);
                    removed.Add("test (bos guvensiz varsayilan veritabani)");
                }
            }
        }
        catch (MySqlException)
        {
            // best-effort — test db düşürülemese de hesap sertleştirmesi geçerli.
        }

        return removed;
    }

    private static async Task<MySqlConnection> OpenConnectionAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = profile.Host,
                Port = Convert.ToUInt32(profile.Port),
                UserID = profile.AdminUserName,
                Password = profile.AdminPassword,
                Database = "mysql",
                SslMode = MySqlSslMode.None,
                ConnectionTimeout = 5,
                DefaultCommandTimeout = 5
            };

            var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (MySqlException exception)
        {
            throw MapUnexpectedMySqlException(exception);
        }
        catch (Exception)
        {
            throw new AppException("database.connection_failed", 503, 503);
        }
    }

    private static async Task<IReadOnlyCollection<DatabaseRecordDto>> GetDatabasesAsync(
        MySqlConnection connection,
        string engineKey,
        CancellationToken cancellationToken)
    {
        // DB → yetkili kullanıcı(lar) eşlemesi (mysql.db per-DB grant tablosu; sistem hesapları hariç).
        // Böylece listede her DB'nin kullanıcısı görünür; kullanıcısı olmayan (yetim) DB'ler ayırt edilir.
        // Ham (Db deseni, kullanici) ciftleri; eslestirme asagida DESEN olarak yapilir.
        var grants = new List<(string Pattern, string User)>();
        await using (var mapCommand = connection.CreateCommand())
        {
            mapCommand.CommandText = """
                SELECT DISTINCT Db, User
                FROM mysql.db
                WHERE User <> ''
                  AND LOWER(User) NOT IN ('root', 'mariadb.sys', 'mysql.sys', 'mysql.session', 'mysql.infoschema', 'debian-sys-maint')
                ORDER BY Db, User;
                """;
            await using var mapReader = await mapCommand.ExecuteReaderAsync(cancellationToken);
            while (await mapReader.ReadAsync(cancellationToken))
            {
                grants.Add((mapReader.GetString(0), mapReader.GetString(1)));
            }
        }

        const string sql = """
            SELECT
                SCHEMA_NAME,
                DEFAULT_CHARACTER_SET_NAME,
                DEFAULT_COLLATION_NAME
            FROM INFORMATION_SCHEMA.SCHEMATA
            WHERE SCHEMA_NAME NOT IN
                ('information_schema', 'performance_schema', 'mysql', 'sys', 'phpmyadmin')
            ORDER BY SCHEMA_NAME;
            """;

        var results = new List<DatabaseRecordDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            results.Add(new DatabaseRecordDto
            {
                Engine = engineKey,
                Name = name,
                Owner = string.Empty,
                Encoding = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Collation = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                // KULLANICI ESLESTIRMESI DESENLE yapilir, esitlikle DEGIL.
                //
                // mysql.db'nin Db sutunu bir LIKE desenidir. Panel artik yetkileri kacirarak
                // veriyor (guvenlik: kacirilmamis "_" komsu veritabanlarini da kapsiyordu), yani
                // kayit "musteri\_db" olarak saklaniyor. Birebir esitlik arayan eski kod bunu
                // BULAMIYOR ve panel, yetkisi VERILMIS bir veritabani icin "kullanicisi yok"
                // diyordu -- kullanici yetkiyi verip sonucun degismedigini goruyordu.
                Users = grants
                    .Where(grant => GrantWildcardRepair.MatchesPattern(name, grant.Pattern))
                    .Select(grant => grant.User)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        return results;
    }

    private static async Task<IReadOnlyCollection<DatabaseUserDto>> GetUsersAsync(
        MySqlConnection connection,
        string engineKey,
        CancellationToken cancellationToken)
    {
        // Sistem/iç hesapları ve rolleri GİZLE: root (motor admin — silinirse panel DB erişimini kaybeder),
        // mariadb.sys / mysql.sys / mysql.session / mysql.infoschema (kilitli iç hesaplar) ve roller (Host='').
        // Kullanıcıya yalnız gerçek, yönetilebilir DB hesapları gösterilir (Plesk gibi).
        const string sql = """
            SELECT User, Host
            FROM mysql.user
            WHERE User <> ''
              AND Host <> ''
              AND LOWER(User) NOT IN ('root', 'mariadb.sys', 'mysql.sys', 'mysql.session', 'mysql.infoschema', 'debian-sys-maint')
            ORDER BY User, Host;
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
                Host = reader.GetString(1),
                CanLogin = true
            });
        }

        return results;
    }

    private static string QuoteIdentifier(string value)
    {
        return $"`{value}`";
    }

    /// <summary>
    /// GRANT ifadesindeki VERİTABANI ADI için tanımlayıcıyı kaçırır.
    /// <para>
    /// GÜVENLİK — burada <see cref="QuoteIdentifier"/> YETMEZ. GRANT ifadesinin veritabanı adı
    /// bölümünde <c>_</c> ve <c>%</c> karakterleri ters tırnak içinde bile JOKER olarak
    /// yorumlanır. Yani:
    /// </para>
    /// <code>
    /// GRANT ALL ON `musteri1_db`.* TO 'u'@'localhost';
    /// </code>
    /// <para>
    /// yalnızca <c>musteri1_db</c>'ye değil, <c>musteri1Xdb</c>, <c>musteri1-db</c> gibi desene
    /// uyan HER veritabanına yetki verir. Panelin veritabanı adı kuralı alt çizgiye izin veriyor
    /// ve alt çizgi hosting adlarında neredeyse kuraldır (<c>site_wp</c>, <c>musteri_db</c>).
    /// </para>
    /// <para>
    /// Çok kiracılı bir panelde sonucu şudur: bir müşterinin veritabanı kullanıcısı, adı desene
    /// uyan BAŞKA bir müşterinin veritabanını okuyup yazabilir. Yani yatay yetki yükseltmesi.
    /// Kaçırma bu yüzden GRANT'a özgüdür ve atlanamaz.
    /// </para>
    /// </summary>
    internal static string EscapeGrantDatabaseName(string value)
    {
        var kacirilmis = (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("_", "\\_")
            .Replace("%", "\\%");

        return $"`{kacirilmis}`";
    }

    private static string BuildPrivilegeClause(IReadOnlyCollection<string> privileges)
    {
        return privileges.Count == 0 || privileges.Contains("ALL PRIVILEGES", StringComparer.OrdinalIgnoreCase)
            ? "ALL PRIVILEGES"
            : string.Join(", ", privileges);
    }

    private static AppException MapUnexpectedMySqlException(MySqlException exception)
    {
        return exception.Number == 1045
            ? new AppException("database.connection_failed", 503, 503)
            : new AppException("database.configuration_failed", 500, 500);
    }
}
