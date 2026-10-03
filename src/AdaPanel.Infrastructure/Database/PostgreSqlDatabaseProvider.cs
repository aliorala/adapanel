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
        catch (PostgresException exception)
        {
            throw MapDeleteDatabaseException(exception);
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
        catch (PostgresException exception)
        {
            throw MapDeleteUserException(exception);
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
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"GRANT {BuildPrivilegeClause(privileges)} ON DATABASE {QuoteIdentifier(databaseName)} TO {QuoteIdentifier(userName)};";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await IsolateDatabaseAsync(profile, connection, databaseName, userName, cancellationToken);
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

    /// <summary>
    /// Bir veritabanını KİRACISINA kapatır: ortak (PUBLIC) erişimi kaldırır, o kiracının kendi
    /// veritabanını gerçekten KULLANABİLMESİNİ sağlar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>1) ÖLÇÜLEN AÇIK — <c>REVOKE ALL ... FROM PUBLIC</c>.</b> PostgreSQL her veritabanına
    /// <c>CONNECT</c> yetkisini <c>PUBLIC</c> rolüne VERİLMİŞ olarak doğar. <c>pg_hba.conf</c>
    /// kurulumda <c>host all all 127.0.0.1/32 scram-sha-256</c> yazar — yani "hangi veritabanı"
    /// sorusuna hiç karışmaz. Sonuç yalıtılmış PostgreSQL 16.14'te SÜRÜLDÜ: A kiracısının rolü
    /// B kiracısının veritabanına bağlandı ve <c>pg_class</c>/<c>pg_attribute</c> üzerinden
    /// B'nin TÜM tablo ve SÜTUN adlarını okudu (satır verisi tablo yetkisi olmadığı için
    /// gelmiyordu; <c>information_schema</c> de yetkiye göre süzülüyordu). Yani veri değil ama
    /// kurbanın şeması sızıyordu — ve bu, sonraki adımların haritasıdır.
    /// </para>
    /// <para>
    /// <b>2) ÖLÇÜLEN KIRIK ÖZELLİK — <c>GRANT ALL ON SCHEMA public</c>.</b> Aynı sürümde şu da
    /// ölçüldü: <c>GRANT ALL PRIVILEGES ON DATABASE x TO u</c> almış bir kiracı KENDİ
    /// veritabanında tablo OLUŞTURAMIYOR ("public şemasına erişim engellendi"). PostgreSQL 15
    /// <c>PUBLIC</c>'in <c>public</c> şeması üzerindeki <c>CREATE</c> yetkisini kaldırdı; panelin
    /// verdiği veritabanı düzeyi yetki şema düzeyine inmiyor. Yani panelin PostgreSQL sağlama
    /// yolu, sevk edilen sürümde (components.json: postgresql-16.14) kullanılamaz durumdaydı.
    /// Bu satır onu onarır ve aynı zamanda 1. maddenin FAZLA KAPATMA nöbetçisidir: yalıtım
    /// kiracıyı kendi verisinden koparmaz.
    /// </para>
    /// <para>
    /// Panelin kendi bağlantısı <c>postgres</c> süper-kullanıcısıdır ve iki kural da onu
    /// etkilemez (ölçüldü) — panel kilitlenmez.
    /// </para>
    /// </remarks>
    private static async Task IsolateDatabaseAsync(
        DatabaseConnectionProfile profile,
        NpgsqlConnection connection,
        string databaseName,
        string userName,
        CancellationToken cancellationToken)
    {
        await using (var revoke = connection.CreateCommand())
        {
            revoke.CommandText = RevokePublicAccessSql(databaseName);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        // Şema yetkisi VERİTABANININ İÇİNDE verilir; yönetim bağlantısı 'postgres' veritabanına
        // bağlıdır ve oradan başka bir veritabanının şemasına GRANT yazılamaz.
        await using var owned = await OpenConnectionAsync(profile, cancellationToken, databaseName);
        await using var grant = owned.CreateCommand();
        grant.CommandText = GrantPublicSchemaSql(userName);
        await grant.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Ortak (<c>PUBLIC</c>) veritabanı erişimini kaldıran ifade — çapraz kiracı bağlantısını kapatır.
    /// </summary>
    /// <remarks>
    /// Yalıtılmış PostgreSQL 16.14 üzerinde SÜRÜLDÜ: bu ifadeden ÖNCE A kiracısının rolü B'nin
    /// veritabanına bağlanıp <c>pg_class</c>/<c>pg_attribute</c> üzerinden B'nin tablo ve sütun
    /// adlarını okudu; SONRA bağlantı <c>FATAL: ... veritabanına erişim engellendi</c> ile düştü.
    /// </remarks>
    internal static string RevokePublicAccessSql(string databaseName) =>
        $"REVOKE ALL ON DATABASE {QuoteIdentifier(databaseName)} FROM PUBLIC;";

    /// <summary>
    /// Kiracının KENDİ veritabanını kullanabilmesi için şema yetkisi — yalıtımın FAZLA KAPATMA nöbetçisi.
    /// </summary>
    /// <remarks>
    /// PostgreSQL 15, <c>PUBLIC</c>'in <c>public</c> şeması üzerindeki <c>CREATE</c> hakkını
    /// kaldırdı. Aynı yalıtılmış sunucuda ölçüldü: <c>GRANT ALL PRIVILEGES ON DATABASE</c> almış
    /// bir kiracı KENDİ veritabanında <c>CREATE TABLE</c> yapamıyordu ("public şemasına erişim
    /// engellendi"). Yani panelin PostgreSQL sağlama yolu, sevk edilen sürümde zaten kırıktı; bu
    /// ifade hem onu onarır hem de <see cref="RevokePublicAccessSql"/>'in kiracıyı kendi
    /// verisinden koparmamasını garanti eder.
    /// </remarks>
    internal static string GrantPublicSchemaSql(string userName) =>
        $"GRANT ALL ON SCHEMA public TO {QuoteIdentifier(userName)};";

    /// <summary>
    /// MEVCUT kurulumlarda ortak (PUBLIC) veritabanı erişimini kaldırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kod düzeltmesi yalnız BUNDAN SONRA verilecek yetkileri kapsar; sunucuda hâlihazırda duran
    /// veritabanları <c>PUBLIC</c>'e açık kalır. MySQL tarafındaki joker yetki temizliğiyle
    /// (<see cref="GrantWildcardRepair"/>) aynı desen: onarım, yöneticinin bilerek çalıştırdığı
    /// "sertleştir" eyleminin içinde durur ve ne yaptığını satır satır döner.
    /// </para>
    /// <para>
    /// <b>KİMSEYİ KAPI DIŞINDA BIRAKMAZ.</b> Yalnızca AÇIK bir yetkilisi olan (ACL girdisi ya da
    /// süper-kullanıcı olmayan sahibi bulunan) veritabanlarına dokunulur. Erişimi yalnız
    /// <c>PUBLIC</c> üzerinden olan bir veritabanında <c>REVOKE</c> yazmak, o veritabanını
    /// kullanan uygulamayı SESSİZCE koparırdı — açığın kendisinden pahalı bir sonuç. Böyle
    /// veritabanları atlanır ve raporda "atlandı" olarak görünür.
    /// </para>
    /// <para>
    /// Yetkililere ayrıca <c>public</c> şema hakkı verilir: aksi hâlde PostgreSQL 15+ üzerinde
    /// erişim "var ama kullanılamaz" durumda kalırdı (bkz. <see cref="IsolateDatabaseAsync"/>).
    /// </para>
    /// </remarks>
    public async Task<DatabaseHardeningReport> HardenAdminAccountsAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var sonuc = new List<string>();

        // BAŞARISIZLAR ADIYLA TAŞINIR. Aşağıdaki catch tek veritabanının onarılamamasının
        // diğerlerini engellememesi için var (bu doğru), ama sonuç hiçbir yere yazılmıyordu:
        // ortak (PUBLIC) erişimi açık kalan veritabanı panelde görünmüyordu.
        var basarisizlar = new List<string>();
        await using var connection = await OpenConnectionAsync(profile, cancellationToken);

        // (veritabanı → açık yetkili roller). aclexplode + sahiplik; sistem rolleri hariç.
        var hedefler = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var acikErisimliler = new HashSet<string>(StringComparer.Ordinal);

        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT d.datname, r.rolname
                FROM pg_database d
                LEFT JOIN pg_roles r
                  ON (r.oid = d.datdba AND NOT r.rolsuper)
                  OR (r.oid IN (SELECT a.grantee FROM aclexplode(d.datacl) a) AND NOT r.rolsuper)
                WHERE d.datistemplate = FALSE
                  AND d.datallowconn = TRUE
                  AND d.datname <> 'postgres'
                  AND has_database_privilege('public', d.datname, 'CONNECT');
                """;
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var veritabani = reader.GetString(0);
                acikErisimliler.Add(veritabani);
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                if (!hedefler.TryGetValue(veritabani, out var roller))
                {
                    roller = [];
                    hedefler[veritabani] = roller;
                }

                var rol = reader.GetString(1);
                if (!roller.Contains(rol, StringComparer.Ordinal))
                {
                    roller.Add(rol);
                }
            }
        }

        foreach (var veritabani in acikErisimliler)
        {
            if (!hedefler.TryGetValue(veritabani, out var roller) || roller.Count == 0)
            {
                sonuc.Add($"{veritabani}: ortak erisim ACIK birakildi (acik yetkilisi yok — kapatmak baglantiyi koparirdi)");
                continue;
            }

            try
            {
                await using (var revoke = connection.CreateCommand())
                {
                    revoke.CommandText = RevokePublicAccessSql(veritabani);
                    await revoke.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var owned = await OpenConnectionAsync(profile, cancellationToken, veritabani);
                foreach (var rol in roller)
                {
                    await using var grant = owned.CreateCommand();
                    grant.CommandText = GrantPublicSchemaSql(rol);
                    await grant.ExecuteNonQueryAsync(cancellationToken);
                }

                sonuc.Add($"ortak (PUBLIC) erisim kaldirildi: {veritabani} → {string.Join(", ", roller)}");
            }
            catch (PostgresException exception)
            {
                // Tek veritabanının onarılamaması diğerlerini engellemez; başarısız olan AÇIK
                // hâlinde kalır (erişim kaybolmaz) ve bir sonraki çalıştırmada yeniden denenir.
                // Ama SESSİZ kalmaz: ortak erişimi açık kalan veritabanı ADIYLA raporlanır.
                basarisizlar.Add($"{veritabani}: ortak (PUBLIC) erisim KALDIRILAMADI ({exception.MessageText})");
            }
        }

        return new DatabaseHardeningReport
        {
            Applied = sonuc,
            Failed = basarisizlar,

            // Hedefler bu çağrının kendi sorgusuyla ölçüldü ve her hedefin sonucu (uygulandı /
            // uygulanamadı / bilerek açık bırakıldı) rapora yazıldı.
            Measured = true
        };
    }

    public bool SupportsHardening => true;

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

            var result = new Dictionary<string, RemoteAccessFacts>(StringComparer.Ordinal);
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

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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

    /// <param name="database">
    /// Bağlanılacak veritabanı; boşsa yönetim veritabanı (<c>postgres</c>). Şema düzeyi yetkiler
    /// yalnız hedef veritabanının İÇİNDEN yazılabildiği için gerekir.
    /// </param>
    private static async Task<NpgsqlConnection> OpenConnectionAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken,
        string? database = null)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.AdminUserName,
                Password = profile.AdminPassword,
                Database = string.IsNullOrWhiteSpace(database) ? "postgres" : database,
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

    /// <summary>
    /// PostgreSQL tanımlayıcısını tırnaklar. Çift tırnak İKİLENİR — üst katman adları katı bir
    /// desenle doğruluyor (<c>DatabaseService.NormalizeDatabaseName</c>/<c>NormalizeUserName</c>),
    /// ama kaçırma o doğrulamadan BAĞIMSIZ olarak burada durmalı: panel dışından (taşıma, betik,
    /// ileride eklenecek bir yol) gelen bir ad bu metoda düşerse tırnak kapatıp SQL yazamasın.
    /// </summary>
    private static string QuoteIdentifier(string value)
    {
        return $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
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

    internal static AppException MapDeleteDatabaseException(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.InvalidCatalogName
            ? AppException.Gone("database.database_not_found")
            : MapUnexpectedPostgresException(exception);

    internal static AppException MapDeleteUserException(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UndefinedObject
            ? AppException.Gone("database.user_not_found")
            : MapUnexpectedPostgresException(exception);

    private static AppException MapUnexpectedPostgresException(PostgresException exception)
    {
        return exception.SqlState == PostgresErrorCodes.InvalidPassword
            ? new AppException("database.connection_failed", 503, 503)
            : new AppException("database.configuration_failed", 500, 500);
    }
}
