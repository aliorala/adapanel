using AdaPanel.Core.Dtos.Databases;
using MySqlConnector;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// MySQL/MariaDB tablo bakımı: denetleme ve onarım.
/// <para>
/// AYRI BİR BAĞLANTI KURAR, sağlayıcınınkini kullanmaz. Sebep ölçülebilir: sağlayıcının bağlantısı
/// <c>DefaultCommandTimeout = 5</c> ile açılıyor ve bu, listeleme sorguları için doğru bir seçim.
/// Ama bir tablonun denetimi dakikalarca sürebilir; aynı bağlantıyla çalıştırmak, işlemi tam
/// ortasında kesip kullanıcıya "veritabanı yanıt vermiyor" gibi yanıltıcı bir hata gösterirdi.
/// </para>
/// </summary>
internal static class MySqlMaintenanceRunner
{
    /// <summary>Envanter sorgusu; çok tablolu sunucularda information_schema yavaşlar.</summary>
    private const int InventoryTimeoutSeconds = 60;

    /// <summary>
    /// Bakım ifadeleri. Büyük bir tablonun denetimi/onarımı uzun sürer; buraya kısa bir değer
    /// koymak, işi bitmek üzereyken kesip tabloyu belirsiz bir durumda bırakırdı.
    /// </summary>
    private const int MaintenanceTimeoutSeconds = 1800;

    /// <summary>
    /// Veritabanını denetler; <paramref name="repairTables"/> içinde adı geçen tabloları onarır.
    /// <para>
    /// Akış bilinçli olarak iki fazlıdır: önce yalnızca OKUYAN bir denetim, sonra KULLANICININ
    /// SEÇTİĞİ tablolarda onarım. Tek tıkla kör onarım yapılmaz; onarım yıkıcı olabilir ve
    /// kullanıcının neyin, neden onarılacağını görmesi gerekir.
    /// </para>
    /// <para>
    /// CHECK her zaman QUICK ile çalışır. İki gerekçe: (1) InnoDB'de MEDIUM/EXTENDED/FAST/CHANGED
    /// seçenekleri belgelere göre SESSİZCE YOK SAYILIR — "kapsamlı tarama" diye sunmak,
    /// kullanıcıya aynı işi farklı isimle satmak olurdu; (2) belgeler büyük InnoDB tablolarında
    /// QUICK dışındaki denetimlerin diğer iş parçacıklarını kilitleyebileceğini söyleyip doğrudan
    /// QUICK öneriyor.
    /// </para>
    /// </summary>
    internal static async Task<DatabaseCheckResultDto> RunAsync(
        DatabaseConnectionProfile profile,
        string engineKey,
        string databaseName,
        IReadOnlyCollection<string> repairTables,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(profile, cancellationToken);

        var envanter = await ReadInventoryAsync(connection, databaseName, cancellationToken);
        var onarilacak = new HashSet<string>(repairTables ?? [], StringComparer.OrdinalIgnoreCase);
        var sonuclar = new List<DatabaseTableCheckDto>();

        foreach (var (ad, motor, boyut) in envanter)
        {
            var tamAd = $"{TableMaintenance.Quote(databaseName)}.{TableMaintenance.Quote(ad)}";
            var mesajlar = new List<string>();

            if (onarilacak.Contains(ad))
            {
                // MOTOR KONTROLÜ BURADA DA YAPILIR, arayüze güvenilmez.
                //
                // Seçim listesi arayüzden gelir; arayüz eski bir sürüm olabilir ya da istek
                // doğrudan API'ye gönderilmiş olabilir. InnoDB'ye REPAIR göndermek her durumda
                // yanlıştır: hata vermez, hiçbir şey yapmaz ve "OK" döner — yani panel
                // kullanıcıya onardığını söylerken aslında hiçbir şey yapmamış olur.
                if (TableMaintenance.CanRepair(motor))
                {
                    mesajlar.AddRange(Format(await ExecuteAsync(connection, $"REPAIR TABLE {tamAd}", cancellationToken)));
                }
                else if (TableMaintenance.CanRebuild(motor))
                {
                    // InnoDB: OPTIMIZE, ALTER TABLE ... FORCE'a eşlenir ve tabloyu gerçekten
                    // yeniden yazar. Bu bir ONARIM DEĞİLDİR; arayüz de öyle adlandırmaz.
                    mesajlar.AddRange(Format(await ExecuteAsync(connection, $"OPTIMIZE TABLE {tamAd}", cancellationToken)));
                }
            }

            // DOĞRULAMA: onarımdan SONRA yeniden denetlenir. Bu adım atlanırsa başarısız bir
            // onarım "tamam" diye raporlanırdı.
            var denetim = await ExecuteAsync(connection, $"CHECK TABLE {tamAd} QUICK", cancellationToken);
            mesajlar.AddRange(Format(denetim));

            sonuclar.Add(new DatabaseTableCheckDto
            {
                Table = ad,
                Engine = motor,
                SizeBytes = boyut,
                Status = TableMaintenance.Classify(denetim),
                CanRepair = TableMaintenance.CanRepair(motor),
                CanRebuild = TableMaintenance.CanRebuild(motor),
                Messages = mesajlar
            });
        }

        return new DatabaseCheckResultDto
        {
            Engine = engineKey,
            Database = databaseName,
            Tables = sonuclar,
            RepairAttempted = onarilacak.Count > 0
        };
    }

    /// <summary>
    /// Tablo envanteri: ad, MOTOR ve boyut.
    /// <para>
    /// Motor ÖNCEDEN okunur; onarımın hangi tabloda anlamlı olduğu buna göre belirlenir.
    /// <c>TABLE_TYPE = 'BASE TABLE'</c> ile görünümler elenir — REPAIR görünümlerde çalışmaz.
    /// </para>
    /// </summary>
    private static async Task<List<(string Ad, string Motor, long Boyut)>> ReadInventoryAsync(
        MySqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var envanter = new List<(string, string, long)>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TABLE_NAME, IFNULL(ENGINE, ''), IFNULL(DATA_LENGTH, 0) + IFNULL(INDEX_LENGTH, 0)
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME;
            """;
        command.Parameters.AddWithValue("@db", databaseName);
        command.CommandTimeout = InventoryTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            envanter.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return envanter;
    }

    /// <summary>
    /// Bakım ifadesini çalıştırır ve sonuç kümesini okur (Table, Op, Msg_type, Msg_text).
    /// <para>
    /// TÜM satırlar toplanır, yalnızca sonuncusu değil: bir tablo için birden çok satır döner ve
    /// hata satırı ortada olup son satır <c>status: OK</c> olabilir.
    /// </para>
    /// </summary>
    private static async Task<List<(string Tip, string Metin)>> ExecuteAsync(
        MySqlConnection connection,
        string statement,
        CancellationToken cancellationToken)
    {
        var satirlar = new List<(string, string)>();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.CommandTimeout = MaintenanceTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // Sütun sırası sabit: 0=Table, 1=Op, 2=Msg_type, 3=Msg_text.
                var tip = reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetString(2) : string.Empty;
                var metin = reader.FieldCount > 3 && !reader.IsDBNull(3) ? reader.GetString(3) : string.Empty;
                satirlar.Add((tip, metin));
            }
        }
        catch (MySqlException exception)
        {
            // Tek bir tablodaki hata TÜM denetimi düşürmemeli; kalan tablolar da denetlenmeli.
            // Hata, o tablonun sonucu olarak raporlanır — yutulmaz.
            satirlar.Add(("error", exception.Message));
        }

        return satirlar;
    }

    private static IEnumerable<string> Format(IEnumerable<(string Tip, string Metin)> rows) =>
        rows.Where(row => !string.IsNullOrWhiteSpace(row.Metin))
            .Select(row => string.IsNullOrWhiteSpace(row.Tip) ? row.Metin : $"{row.Tip}: {row.Metin}");

    private static async Task<MySqlConnection> OpenAsync(
        DatabaseConnectionProfile profile,
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
            DefaultCommandTimeout = MaintenanceTimeoutSeconds
        };

        var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
