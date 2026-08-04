using AdaPanel.Core.Dtos.Databases;
using MySqlConnector;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// Tek bir veritabanının detayını okur: boyut, tablo sayısı ve yetkili kullanıcılar.
/// </summary>
internal static class MySqlDetailReader
{
    /// <summary>
    /// Boyut sorgusu için ayrı ve UZUN zaman aşımı.
    /// <para>
    /// Ölçülmüş gerçek: <c>information_schema</c>'nın boyut sütunları "dinamik"tir ve sorgu,
    /// çok tablolu sunucularda saniyeler sürebilir; <c>innodb_stats_on_metadata</c> açıksa
    /// dakikalara çıkabilir. Sağlayıcının 5 saniyelik varsayılanı burada işi ortasında keser ve
    /// kullanıcıya "veritabanı yanıt vermiyor" gibi yanlış bir izlenim verirdi.
    /// </para>
    /// </summary>
    private const int SizeTimeoutSeconds = 120;

    internal static async Task<DatabaseDetailDto> ReadAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string siteName,
        CancellationToken cancellationToken)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = profile.Host,
            Port = Convert.ToUInt32(profile.Port),
            UserID = profile.AdminUserName,
            Password = profile.AdminPassword,
            Database = "mysql",
            SslMode = MySqlSslMode.None,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = SizeTimeoutSeconds
        };

        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        long? boyut = null;
        var tabloSayisi = 0;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(DATA_LENGTH + INDEX_LENGTH), 0)
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE';
                """;
            command.Parameters.AddWithValue("@db", databaseName);
            command.CommandTimeout = SizeTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                tabloSayisi = reader.GetInt32(0);
                boyut = reader.GetInt64(1);
            }
        }
        catch (MySqlException)
        {
            // Boyut ÖLÇÜLEMEDİ. null bırakılır -- sıfır yazmak "boş veritabanı" demek olurdu ve
            // kullanıcı ölçüm hatasını veri yokluğu sanardı.
            boyut = null;
        }

        return new DatabaseDetailDto
        {
            Engine = profile.EngineKey,
            Name = databaseName,
            Host = "localhost",
            Port = profile.Port,
            EngineName = profile.DisplayName,
            Users = await ReadUsersAsync(connection, databaseName, cancellationToken),
            SizeBytes = boyut,
            TableCount = tabloSayisi,
            SiteName = siteName
        };
    }

    /// <summary>
    /// Bu veritabanına yetkili kullanıcıları okur.
    /// <para>
    /// DESEN EŞLEŞMESİ kullanılır (<c>@db LIKE Db</c>), eşitlik DEĞİL. <c>mysql.db</c> tablosunun
    /// <c>Db</c> sütunu bir LIKE desenidir: <c>_</c> ve <c>%</c> joker anlamı taşır. Eşitlikle
    /// sorgulamak, desenle verilmiş yetkileri GÖRMEZ ve panel "kullanıcısı yok" derken aslında
    /// erişebilen kullanıcılar olurdu.
    /// </para>
    /// <para>
    /// Tablo seviyesi yetkiler de eklenir: yalnızca <c>mysql.db</c>'ye bakmak, sadece belirli
    /// tablolara yetkisi olan bir kullanıcıyı görünmez kılar.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<DatabaseUserRefDto>> ReadUsersAsync(
        MySqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var kullanicilar = new List<DatabaseUserRefDto>();
        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT User, Host FROM mysql.db
            WHERE User <> '' AND @db LIKE Db
            UNION
            SELECT DISTINCT User, Host FROM mysql.tables_priv
            WHERE User <> '' AND Db = @db;
            """;
        command.Parameters.AddWithValue("@db", databaseName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ad = reader.GetString(0);
            var konak = reader.GetString(1);

            // Sistem hesapları listelenmez: kullanıcıya ait olmayan hesapları "veritabanı
            // kullanıcısı" diye göstermek, kullanıcısız bir veritabanını kullanıcılı sanmaya
            // yol açardı.
            if (SystemAccounts.Contains(ad))
            {
                continue;
            }

            if (gorulen.Add($"{ad}@{konak}"))
            {
                kullanicilar.Add(new DatabaseUserRefDto { UserName = ad, Host = konak });
            }
        }

        return kullanicilar;
    }

    private static readonly HashSet<string> SystemAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "root", "mariadb.sys", "mysql.sys", "mysql.session", "mysql.infoschema", "debian-sys-maint"
    };
}
