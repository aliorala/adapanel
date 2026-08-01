using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Monitoring;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

namespace AdaPanel.Infrastructure.Monitoring;

/// <summary>
/// "Bu paylaşılan süreci KİM kullanıyor?" — süreç listesinin bittiği yerden devam eder.
///
/// TEMEL SORUN: MySQL/MariaDB/PostgreSQL tek bir işletim sistemi sürecidir ve TÜM siteler onu paylaşır.
/// Süreç listesinde görünen "%42 CPU" hangi sitenin sorgusundan geldiğini söyleyemez, çünkü işletim
/// sistemi bunu bilmez. Bilen tek yer motorun KENDİ oturum listesidir. Bu servis o listeyi okur ve
/// her aktif işlemi -mümkünse- bir SİTEYE bağlar (panelin site↔veritabanı kaydı üzerinden).
///
/// TASARIM KURALLARI:
/// - Her kaynak KENDİ try/catch'inde: MySQL yoksa PostgreSQL yine çalışsın (uç kaynak başına çağrılır).
/// - Kurulu olmayan motor HATA DEĞİLDİR → Installed=false ile sakin bir durum döner.
/// - Uç 2-3 saniyede bir çağrılabilir: yalnız hazır sistem görünümleri okunur (information_schema.PROCESSLIST,
///   pg_stat_activity), 5 sn bağlantı/komut zaman aşımı vardır, ağır sorgu ÇALIŞTIRILMAZ.
/// - GİZLİLİK: sorgu metni başkalarının verisidir; kısaltılır ve parola içerenler maskelenir (bkz. SanitizeStatement).
/// </summary>
internal sealed partial class ResourceAttributionService(
    AdaPanelDbContext dbContext,
    ISecretProtector secretProtector,
    ISiteDatabaseLinkService siteDatabaseLinks,
    IDatabaseOwnershipService databaseOwnership,
    ILogger<ResourceAttributionService> logger) : IResourceAttributionService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // DatabaseService'in kullandığı yapılandırma anahtarlarının AYNISI — kimlik bilgisi tek kaynaktan
    // (kurulu bileşenin korumalı yapılandırması) okunur, burada yeni bir bağlantı yöntemi İCAT EDİLMEZ.
    private const string GeneratedPasswordKey = "generatedPassword";
    private const string AdminUserKey = "adminUser";
    private const string AdminPortKey = "adminPort";

    /// <summary>
    /// Sorgu metni bu uzunlukta kırpılır. Yavaş bir sorguyu TEŞHİS etmek için tamamını görmek gerekir
    /// (WHERE koşulu, JOIN'ler, alt sorgu genelde 200 karakterden uzundur), o yüzden cömert bir sınır.
    /// Yine de sınırsız değil: tek bir satır megabaytlarca metin taşıyabilir ve bu liste saniyede
    /// bir çekiliyor. Arayüz satırı katlı gösterir, tıklayınca tamamı açılır.
    /// </summary>
    private const int StatementMaxLength = 4000;

    public async Task<ResourceConsumerSnapshotDto> GetConsumersAsync(
        string source,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var key = (source ?? string.Empty).Trim().ToLowerInvariant();
        var take = Math.Clamp(limit, 1, 200);

        return key switch
        {
            "iis" or "web" => await GetIisConsumersAsync(take, cancellationToken),
            "mysql" or "mariadb" or "postgresql" or "postgres" => await GetDatabaseConsumersAsync(
                key == "postgres" ? "postgresql" : key, take, cancellationToken),
            _ => throw new AppException("monitoring.source_not_supported", 400, 400)
        };
    }

    // -------------------------------------------------------------------------------------------------
    // Veritabanı motorları (MySQL / MariaDB / PostgreSQL)
    // -------------------------------------------------------------------------------------------------

    private async Task<ResourceConsumerSnapshotDto> GetDatabaseConsumersAsync(
        string engineKey,
        int take,
        CancellationToken cancellationToken)
    {
        var descriptor = ResolveDescriptor(engineKey);

        // 1) Motor kurulu mu? Kurulu değilse bu bir HATA DEĞİL, bilgidir.
        var component = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ComponentId == descriptor.ComponentId && item.Status == ComponentInstallStatus.Installed,
                cancellationToken);

        if (component is null)
        {
            return Snapshot(engineKey, descriptor.DisplayName, installed: false, statusCode: "not_installed");
        }

        // 2) Panelin kendi sakladığı yönetici kimliği (DatabaseService.ResolveConnectionProfileAsync ile aynı yol).
        var configuration = ReadProtectedConfiguration(component);
        var password = configuration.TryGetValue(GeneratedPasswordKey, out var storedPassword) ? storedPassword : string.Empty;
        if (string.IsNullOrWhiteSpace(password))
        {
            // Motor var ama panel ona bağlanamaz (kimlik kaydedilmemiş) — kullanıcıya ayrı mesaj gösterilir.
            return Snapshot(engineKey, descriptor.DisplayName, installed: true, statusCode: "credentials_missing");
        }

        var adminUser = configuration.TryGetValue(AdminUserKey, out var storedUser) && !string.IsNullOrWhiteSpace(storedUser)
            ? storedUser
            : descriptor.AdminUserName;
        var port = configuration.TryGetValue(AdminPortKey, out var storedPort) &&
                   int.TryParse(storedPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) &&
                   parsedPort > 0
            ? parsedPort
            : descriptor.DefaultPort;

        // 3) Veritabanı → site eşlemesi: panelin site↔DB bağı (config/site-databases.json). Bu ekranın
        //    ASIL DEĞERİ bu sütun; teknik bilmeyen biri "alioral.xyz 12 saniyedir sorgu çalıştırıyor"
        //    cümlesini ancak buradan okuyabilir.
        var siteMap = await BuildSiteMapAsync(cancellationToken);
        // Sitesi baska sunucuda barindirilan veritabanlari icin: yerel site yok, sahip var.
        var ownerMap = await BuildOwnerMapAsync(cancellationToken);

        try
        {
            var rows = descriptor.IsMySqlFamily
                ? await ReadMySqlProcessListAsync(adminUser, password, port, engineKey, siteMap, ownerMap, cancellationToken)
                : await ReadPostgreSqlActivityAsync(adminUser, password, port, engineKey, siteMap, ownerMap, cancellationToken);

            var ordered = rows
                .OrderByDescending(row => row.DurationSeconds)
                .ThenBy(row => row.Target, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ResourceConsumerSnapshotDto
            {
                Source = engineKey,
                DisplayName = descriptor.DisplayName,
                Installed = true,
                Available = true,
                StatusCode = string.Empty,
                CapturedAtUtc = DateTime.UtcNow,
                TotalCount = ordered.Length,
                Consumers = ordered.Take(take).ToArray()
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Servis durmuş / parola değişmiş / port kapalı olabilir. Bu ekran salt bilgilendirmedir;
            // ölçüm başarısızlığı 500 döndürmemeli, "şu an okunamadı" olarak gösterilmeli.
            logger.LogDebug(exception, "{Engine} oturum listesi okunamadı; kaynak 'unavailable' olarak raporlanıyor.", engineKey);
            return Snapshot(engineKey, descriptor.DisplayName, installed: true, statusCode: "unavailable");
        }
    }

    /// <summary>
    /// MySQL/MariaDB: <c>information_schema.PROCESSLIST</c> (SHOW FULL PROCESSLIST'in tablo hâli — aynı veri,
    /// ama WHERE ile SUNUCUDA süzülebilir). UYKUDAKİ (COMMAND='Sleep') bağlantılar AYIKLANIR: onlar açık ama
    /// boşta duran havuz bağlantılarıdır, CPU yemezler; listeyi doldurup asıl suçluyu gizlerler.
    /// </summary>
    private static async Task<List<ResourceConsumerDto>> ReadMySqlProcessListAsync(
        string adminUser,
        string password,
        int port,
        string engineKey,
        IReadOnlyDictionary<string, string> siteMap,
        IReadOnlyDictionary<string, string> ownerMap,
        CancellationToken cancellationToken)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = "127.0.0.1",
            Port = Convert.ToUInt32(port),
            UserID = adminUser,
            Password = password,
            Database = "information_schema",
            SslMode = MySqlSslMode.None,
            // Uç saniyede bir çağrılabilir: asla uzun beklemeyelim.
            ConnectionTimeout = 5,
            DefaultCommandTimeout = 5
        };

        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // ID <> CONNECTION_ID(): panelin kendi ölçüm bağlantısı listede görünmesin (kendini sayan bir izleyici
        // her seferinde bir satır fazla gösterir ve kafa karıştırır).
        const string sql = """
            SELECT ID, USER, HOST, DB, COMMAND, TIME, STATE, INFO
            FROM information_schema.PROCESSLIST
            WHERE ID <> CONNECTION_ID()
              AND COMMAND NOT IN ('Sleep', 'Daemon', 'Binlog Dump', 'Binlog Dump GTID')
              AND USER NOT IN ('system user', 'event_scheduler')
            ORDER BY TIME DESC;
            """;

        var results = new List<ResourceConsumerDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var databaseName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var statement = SanitizeStatement(reader.IsDBNull(7) ? null : reader.GetString(7), StatementMaxLength);

            results.Add(new ResourceConsumerDto
            {
                ResourceType = "database",
                Source = engineKey,
                SiteName = ResolveSiteForDatabase(siteMap, engineKey, databaseName),
                SiteLinked = ResolveSiteForDatabase(siteMap, engineKey, databaseName) is not null,
                OwnerName = ResolveOwnerForDatabase(ownerMap, engineKey, databaseName),
                Target = databaseName,
                User = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                ClientHost = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                RemoteClient = IsRemoteClientHost(reader.IsDBNull(2) ? null : reader.GetString(2)),
                // TIME nadiren negatif gelebilir (saat sıçraması / replikasyon) → 0'a kenetle.
                DurationSeconds = reader.IsDBNull(5) ? 0 : Math.Max(0, Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture)),
                State = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                Command = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Statement = statement.Text,
                Redacted = statement.Redacted,
                ConnectionId = reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty
            });
        }

        return results;
    }

    /// <summary>
    /// PostgreSQL: <c>pg_stat_activity</c>. 'idle' oturumlar (boşta bekleyen havuz bağlantıları) AYIKLANIR;
    /// datname IS NOT NULL koşulu arka plan işçilerini (autovacuum launcher, walwriter) eler — onlar bir
    /// siteye ait değildir. Süre, sorgunun başlangıcından (yoksa işlem/oturum başlangıcından) itibaren ölçülür.
    /// </summary>
    private static async Task<List<ResourceConsumerDto>> ReadPostgreSqlActivityAsync(
        string adminUser,
        string password,
        int port,
        string engineKey,
        IReadOnlyDictionary<string, string> siteMap,
        IReadOnlyDictionary<string, string> ownerMap,
        CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Username = adminUser,
            Password = password,
            Database = "postgres",
            SslMode = SslMode.Disable,
            Timeout = 5,
            CommandTimeout = 5
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // EXTRACT(EPOCH ...) PG 14+'ta numeric döner; ::float8 ile sürüm farkı kapatılır.
        const string sql = """
            SELECT
                pid,
                COALESCE(usename, ''),
                COALESCE(datname, ''),
                COALESCE(state, ''),
                COALESCE(query, ''),
                EXTRACT(EPOCH FROM (now() - COALESCE(query_start, xact_start, backend_start)))::float8,
                COALESCE(host(client_addr), '')
            FROM pg_stat_activity
            WHERE datname IS NOT NULL
              AND state IS DISTINCT FROM 'idle'
              AND pid <> pg_backend_pid()
            ORDER BY 6 DESC;
            """;

        var results = new List<ResourceConsumerDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var databaseName = reader.GetString(2);
            var statement = SanitizeStatement(reader.GetString(4), StatementMaxLength);
            var state = reader.GetString(3);

            results.Add(new ResourceConsumerDto
            {
                ResourceType = "database",
                Source = engineKey,
                SiteName = ResolveSiteForDatabase(siteMap, engineKey, databaseName),
                SiteLinked = ResolveSiteForDatabase(siteMap, engineKey, databaseName) is not null,
                OwnerName = ResolveOwnerForDatabase(ownerMap, engineKey, databaseName),
                Target = databaseName,
                User = reader.GetString(1),
                ClientHost = reader.GetString(6),
                RemoteClient = IsRemoteClientHost(reader.GetString(6)),
                DurationSeconds = reader.IsDBNull(5) ? 0 : Math.Max(0, reader.GetDouble(5)),
                State = state,
                // PostgreSQL'de MySQL'deki COMMAND sütununun karşılığı yoktur; durum aynı zamanda işlem türüdür.
                Command = state,
                Statement = statement.Text,
                Redacted = statement.Redacted,
                ConnectionId = reader.GetInt32(0).ToString(CultureInfo.InvariantCulture)
            });
        }

        return results;
    }

    // -------------------------------------------------------------------------------------------------
    // IIS: o an ÇALIŞAN HTTP istekleri
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// IIS'te w3wp zaten app pool başına ayrıdır (site ataması süreç listesinde çözülü). Eksik olan
    /// "şu an hangi İSTEK asılı kalmış" bilgisidir; onu yalnız <c>appcmd list requests</c> verir.
    /// appcmd yoksa (IIS kurulu değil) BOŞ liste + Installed=false döner, HATA ATILMAZ.
    /// </summary>
    private async Task<ResourceConsumerSnapshotDto> GetIisConsumersAsync(int take, CancellationToken cancellationToken)
    {
        var appcmd = ResolveAppcmdPath();
        if (appcmd is null)
        {
            return Snapshot("iis", "IIS", installed: false, statusCode: "not_installed");
        }

        try
        {
            // XML biçimi SİTE ADINI da taşır (metin biçimi taşımaz) — bu ekranın asıl değeri site sütunu
            // olduğu için önce XML denenir. appcmd asılırsa istek kilitlenmesin diye kısa zaman aşımı.
            var result = await ProcessRunner.RunAsync(
                appcmd,
                "list requests /xml",
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(10));

            var output = result.StandardOutput;
            var rows = ParseAppcmdRequests(output);

            // XML denemesi başarısızsa (eski appcmd, /xml desteklenmiyor) düz metin biçimine düş.
            // Site adı bu biçimde yoktur; yine de URL + süre gösterilir, hiç göstermemekten iyidir.
            if (rows.Count == 0 && !LooksLikeXml(output))
            {
                var fallback = await ProcessRunner.RunAsync(
                    appcmd,
                    "list requests",
                    cancellationToken: cancellationToken,
                    timeout: TimeSpan.FromSeconds(10));
                rows = ParseAppcmdRequests(fallback.StandardOutput);
            }

            var consumers = rows
                .Select(ToConsumer)
                .OrderByDescending(row => row.DurationSeconds)
                .ToArray();

            return new ResourceConsumerSnapshotDto
            {
                Source = "iis",
                DisplayName = "IIS",
                Installed = true,
                Available = true,
                StatusCode = string.Empty,
                CapturedAtUtc = DateTime.UtcNow,
                TotalCount = consumers.Length,
                Consumers = consumers.Take(take).ToArray()
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "appcmd list requests çalıştırılamadı; IIS kaynağı 'unavailable' olarak raporlanıyor.");
            return Snapshot("iis", "IIS", installed: true, statusCode: "unavailable");
        }
    }

    private static string? ResolveAppcmdPath()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "inetsrv",
            "appcmd.exe");

        return File.Exists(path) ? path : null;
    }

    private static ResourceConsumerDto ToConsumer(IisRequestRow row)
    {
        // Panel kendi app pool'larını "AdaPanel.<site>" kalıbıyla kurar; IIS site adı zaten alan adıdır.
        var siteName = string.IsNullOrWhiteSpace(row.SiteName)
            ? ResolveSiteNameFromPool(row.AppPoolName)
            : row.SiteName;

        var target = !string.IsNullOrWhiteSpace(siteName)
            ? siteName
            : (!string.IsNullOrWhiteSpace(row.AppPoolName) ? row.AppPoolName : row.ClientHost);

        // URL de kullanıcı verisi taşıyabilir (querystring'de token/parola); aynı maskeleme uygulanır.
        var statement = SanitizeStatement(row.Url, StatementMaxLength);

        return new ResourceConsumerDto
        {
            ResourceType = "web",
            Source = "iis",
            SiteName = siteName,
            Target = target ?? string.Empty,
            User = string.Empty,
            ClientHost = row.ClientHost,
            DurationSeconds = row.DurationSeconds,
            State = row.Stage,
            Command = row.Verb,
            Statement = statement.Text,
            Redacted = statement.Redacted,
            ConnectionId = row.RequestId
        };
    }

    // -------------------------------------------------------------------------------------------------
    // Saf mantık (birim testi kapsamında): appcmd ayrıştırma, db → site eşlemesi, sorgu maskeleme.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Tek bir IIS isteği (ayrıştırıcının ara biçimi).</summary>
    internal sealed record IisRequestRow(
        string RequestId,
        string? SiteName,
        string AppPoolName,
        string Verb,
        string Url,
        double DurationSeconds,
        string ClientHost,
        string Stage,
        string Module);

    internal static bool LooksLikeXml(string? output)
    {
        var trimmed = (output ?? string.Empty).TrimStart();
        return trimmed.StartsWith('<');
    }

    /// <summary>
    /// <c>appcmd list requests</c> çıktısını ayrıştırır. İKİ biçim de desteklenir çünkü appcmd sürüm/
    /// kapsam farkına göre ikisini de üretebilir:
    ///
    /// XML (<c>/xml</c>): &lt;REQUEST REQUEST.NAME="..." SITE.NAME="Default Web Site" verb="GET" url="/x" timeElapsed="1250" ... /&gt;
    /// Metin: REQUEST "1300000080000045" (url:GET /default.aspx, time:1250 msec, client:localhost, stage:..., module:...)
    ///
    /// Öznitelik adları sürümler arasında değişebildiği için ADAY LİSTESİYLE ve büyük/küçük harf duyarsız
    /// aranır; bulunamayan alan boş kalır ve satır yine de listelenir (eksik bilgi &gt; hiç bilgi).
    /// </summary>
    internal static IReadOnlyList<IisRequestRow> ParseAppcmdRequests(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return LooksLikeXml(output) ? ParseXmlRequests(output) : ParseTextRequests(output);
    }

    private static IReadOnlyList<IisRequestRow> ParseXmlRequests(string output)
    {
        try
        {
            var document = XDocument.Parse(output.Trim());
            var rows = new List<IisRequestRow>();

            foreach (var element in document.Descendants()
                         .Where(node => string.Equals(node.Name.LocalName, "REQUEST", StringComparison.OrdinalIgnoreCase)))
            {
                var verb = Attribute(element, "verb", "method");
                var url = Attribute(element, "url", "requestUrl");

                // Bazı sürümler fiil ve yolu tek alanda ("GET /x") verir; ayrıştırıp iki sütuna böl.
                if (string.IsNullOrEmpty(verb))
                {
                    (verb, url) = SplitVerb(url);
                }

                rows.Add(new IisRequestRow(
                    RequestId: Attribute(element, "REQUEST.NAME", "REQUEST.ID", "requestId"),
                    SiteName: NullIfEmpty(Attribute(element, "SITE.NAME", "siteName")),
                    AppPoolName: Attribute(element, "APPPOOL.NAME", "appPoolName", "applicationPool"),
                    Verb: verb,
                    Url: url,
                    DurationSeconds: ParseMilliseconds(Attribute(element, "timeElapsed", "time", "elapsed")),
                    ClientHost: Attribute(element, "clientIp", "clientIP", "client", "hostName"),
                    Stage: Attribute(element, "pipelineState", "stage"),
                    Module: Attribute(element, "currentModule", "module")));
            }

            return rows;
        }
        catch (System.Xml.XmlException)
        {
            // Bozuk/kesik XML → boş liste. Ölçüm ekranı asla hata yüzünden çökmemeli.
            return [];
        }
    }

    private static IReadOnlyList<IisRequestRow> ParseTextRequests(string output)
    {
        var rows = new List<IisRequestRow>();

        foreach (var line in output.Split('\n'))
        {
            var match = TextRequestPattern().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var body = match.Groups["body"].Value;
            var (verb, url) = SplitVerb(FieldFromBody(body, "url"));

            rows.Add(new IisRequestRow(
                RequestId: match.Groups["id"].Value,
                // Metin biçiminde site adı YOKTUR — dürüst davranıp null bırakıyoruz; UI "app pool / istemci"ye düşer.
                SiteName: null,
                AppPoolName: string.Empty,
                Verb: verb,
                Url: url,
                DurationSeconds: ParseMilliseconds(FieldFromBody(body, "time").Replace("msec", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()),
                ClientHost: FieldFromBody(body, "client"),
                Stage: FieldFromBody(body, "stage"),
                Module: FieldFromBody(body, "module")));
        }

        return rows;
    }

    private static string FieldFromBody(string body, string field)
    {
        // "url:GET /a?b=1, time:12 msec, ..." → istenen alanın değeri (bir sonraki "<ad>:" başlangıcına kadar).
        var match = Regex.Match(
            body,
            $@"\b{Regex.Escape(field)}\s*:\s*(?<value>.*?)(?=,\s*[A-Za-z][A-Za-z0-9_.]*\s*:|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(250));

        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static (string Verb, string Url) SplitVerb(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        var space = trimmed.IndexOf(' ');
        if (space <= 0)
        {
            return (string.Empty, trimmed);
        }

        var candidate = trimmed[..space];
        // Yalnız büyük harfli HTTP fiili gibi görünüyorsa ayır; "/a b" gibi yollarda bölmeyelim.
        return candidate.All(char.IsAsciiLetterUpper)
            ? (candidate, trimmed[(space + 1)..].Trim())
            : (string.Empty, trimmed);
    }

    private static double ParseMilliseconds(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0
            ? Math.Round(milliseconds / 1000d, 3)
            : 0;
    }

    private static string Attribute(XElement element, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var attribute = element.Attributes().FirstOrDefault(item =>
                string.Equals(item.Name.LocalName, candidate, StringComparison.OrdinalIgnoreCase));

            if (attribute is not null && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value.Trim();
            }
        }

        return string.Empty;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Panel app pool'larını <c>AdaPanel.&lt;site&gt;</c> kalıbıyla kurar; öneki atmak site adını verir.
    /// (ProcessMonitorService ile aynı kural — orası DEĞİŞTİRİLMEDEN burada yeniden uygulanır.)
    /// </summary>
    internal static string? ResolveSiteNameFromPool(string? appPoolName)
    {
        const string managedPoolPrefix = "AdaPanel.";
        if (string.IsNullOrWhiteSpace(appPoolName) ||
            !appPoolName.StartsWith(managedPoolPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var siteName = appPoolName[managedPoolPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(siteName) ? null : siteName;
    }

    /// <summary>Site eşleme sözlüğünün anahtarı: "motor|veritabanı" (küçük harf).</summary>
    internal static string SiteMapKey(string engine, string databaseName) =>
        $"{(engine ?? string.Empty).Trim().ToLowerInvariant()}|{(databaseName ?? string.Empty).Trim().ToLowerInvariant()}";

    /// <summary>
    /// Veritabanı adını SİTEYE çevirir. İsim tahminine (ör. "site_db" kalıbı) ASLA güvenilmez; yalnız
    /// panelin kendi kaydı kullanılır. Tek esneklik: MySQL ve MariaDB aynı protokolü konuşur ve bir bağ
    /// diğerinin motor anahtarıyla kaydedilmiş olabilir → kardeş anahtar da denenir.
    /// </summary>
    internal static string? ResolveSiteForDatabase(
        IReadOnlyDictionary<string, string> siteMap,
        string engineKey,
        string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return null;
        }

        if (siteMap.TryGetValue(SiteMapKey(engineKey, databaseName), out var siteName) && !string.IsNullOrWhiteSpace(siteName))
        {
            return siteName;
        }

        var sibling = engineKey.Equals("mysql", StringComparison.OrdinalIgnoreCase)
            ? "mariadb"
            : engineKey.Equals("mariadb", StringComparison.OrdinalIgnoreCase) ? "mysql" : null;

        if (sibling is not null &&
            siteMap.TryGetValue(SiteMapKey(sibling, databaseName), out var siblingSite) &&
            !string.IsNullOrWhiteSpace(siblingSite))
        {
            return siblingSite;
        }

        return null;
    }

    /// <summary>
    /// Sorgu metnini yayına HAZIR hâle getirir.
    ///
    /// GİZLİLİK GEREKÇESİ: burada başka müşterilerin çalışan sorguları görünür. Bir sorgu düz metin parola
    /// taşıyabilir (CREATE USER ... IDENTIFIED BY 'x', ALTER USER ... PASSWORD 'x', SET PASSWORD = PASSWORD('x')).
    /// Böyle bir sorgu tespit edilirse metindeki TÜM değişmez dizeler maskelenir — sorgunun şekli görünür
    /// kalır (yönetici hâlâ "parola değiştiriliyor" diyebilir) ama sır ekrana düşmez. Kırpma ayrıca hem
    /// gizliliği hem dar ekranda okunabilirliği korur.
    /// </summary>
    internal static (string Text, bool Redacted) SanitizeStatement(string? statement, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(statement))
        {
            return (string.Empty, false);
        }

        // Çok satırlı sorgular tek satıra indirgenir; tablo satırı bozulmasın.
        var normalized = WhitespacePattern().Replace(statement, " ").Trim();
        var redacted = false;

        if (SensitivePattern().IsMatch(normalized))
        {
            redacted = true;

            // Önce TIRNAKSIZ bir yer tutucuya çevir; böylece "geriye kalan tırnak" kontrolü kendi
            // koyduğumuz maskelere takılmaz (aksi halde ilk maskeyi kapanmamış tırnak sanardık).
            const string placeholder = "\u0001";
            normalized = QuotedLiteralPattern().Replace(normalized, placeholder);

            // Motor sorguyu zaten kırpmış olabilir → kapanmamış tırnak kalırsa oradan itibarısını at:
            // yarım kalmış bir parola değişmezi ekrana DÜŞMEMELİ.
            var dangling = normalized.IndexOfAny(['\'', '"']);
            if (dangling >= 0)
            {
                normalized = string.Concat(normalized.AsSpan(0, dangling), placeholder);
            }

            normalized = normalized.Replace(placeholder, "'***'");
        }

        var limit = Math.Max(16, maxLength);
        return normalized.Length > limit
            ? (string.Concat(normalized.AsSpan(0, limit), "…"), redacted)
            : (normalized, redacted);
    }

    // -------------------------------------------------------------------------------------------------
    // Yardımcılar
    // -------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyDictionary<string, string>> BuildSiteMapAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var link in await siteDatabaseLinks.GetAllAsync(cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(link.DatabaseName) || string.IsNullOrWhiteSpace(link.SiteName))
                {
                    continue;
                }

                map[SiteMapKey(link.Engine, link.DatabaseName)] = link.SiteName.Trim();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Eşleme dosyası okunamazsa site sütunu boş kalır; liste yine de gösterilir.
            logger.LogDebug(exception, "Site↔veritabanı eşlemesi okunamadı; site sütunu boş kalacak.");
        }

        return map;
    }

    /// <summary>
    /// Veritabanı → sahip müşteri adı eşlemesi. Sitesi BAŞKA BİR SUNUCUDA barındırılan ama
    /// veritabanı burada duran kurulumlarda yerel site yoktur, dolayısıyla site sütunu boş kalır;
    /// o satırın kime ait olduğunu söyleyebilecek tek kayıt panelin sahiplik defteridir.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> BuildOwnerMapAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var owners = await databaseOwnership.GetAllAsync(cancellationToken);
            if (owners.Count == 0)
            {
                return map;
            }

            var userNames = await dbContext.Users
                .Select(user => new { user.Id, user.UserName })
                .ToDictionaryAsync(user => user.Id, user => user.UserName, cancellationToken);

            foreach (var owner in owners)
            {
                if (string.IsNullOrWhiteSpace(owner.DatabaseName) ||
                    !userNames.TryGetValue(owner.OwnerUserId, out var userName))
                {
                    continue;
                }

                map[SiteMapKey(owner.Engine, owner.DatabaseName)] = userName;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Veritabanı sahiplik defteri okunamadı; sahip sütunu boş kalacak.");
        }

        return map;
    }

    /// <summary>
    /// İstemci adresi bu sunucunun dışından mı geliyor? MySQL "host:port", PostgreSQL ise ham adres
    /// (yerel soket bağlantısında BOŞ) döndürür. Boş/localhost/127.x/::1 → yerel; onun dışındaki her
    /// gerçek adres dış bir uygulamadır. "localhost" adı da kabul edilir çünkü MySQL kendi çözer.
    /// </summary>
    internal static bool IsRemoteClientHost(string? clientHost)
    {
        var value = (clientHost ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        // "host:port" veya IPv6 "[::1]:port" → adres kısmını ayıkla.
        if (value.StartsWith('['))
        {
            var closing = value.IndexOf(']');
            value = closing > 0 ? value[1..closing] : value.Trim('[', ']');
        }
        else
        {
            var lastColon = value.LastIndexOf(':');
            // Tek ':' varsa port ayracıdır; birden çoksa tırnaksız IPv6'dır ve port yoktur.
            if (lastColon > 0 && value.IndexOf(':') == lastColon)
            {
                value = value[..lastColon];
            }
        }

        value = value.Trim();
        if (value.Length == 0 ||
            value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("::1", StringComparison.Ordinal) ||
            value.StartsWith("127.", StringComparison.Ordinal))
        {
            return false;
        }

        // Adres olarak ayrıştırılamayan bir şey (ör. makine adı) geldiyse "uzak" varsaymak yanıltıcı
        // olur; yalnız GERÇEK bir IP ise ve loopback değilse dış bağlantı deriz.
        return System.Net.IPAddress.TryParse(value, out var address) && !System.Net.IPAddress.IsLoopback(address);
    }

    /// <summary>Sahip adını, kardeş motor (mysql↔mariadb) yedeğiyle birlikte çözer.</summary>
    internal static string ResolveOwnerForDatabase(
        IReadOnlyDictionary<string, string> ownerMap,
        string engineKey,
        string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return string.Empty;
        }

        if (ownerMap.TryGetValue(SiteMapKey(engineKey, databaseName), out var owner) && !string.IsNullOrWhiteSpace(owner))
        {
            return owner;
        }

        var sibling = engineKey.Equals("mysql", StringComparison.OrdinalIgnoreCase)
            ? "mariadb"
            : engineKey.Equals("mariadb", StringComparison.OrdinalIgnoreCase) ? "mysql" : null;

        return sibling is not null &&
               ownerMap.TryGetValue(SiteMapKey(sibling, databaseName), out var siblingOwner) &&
               !string.IsNullOrWhiteSpace(siblingOwner)
            ? siblingOwner
            : string.Empty;
    }

    private static ResourceConsumerSnapshotDto Snapshot(string source, string displayName, bool installed, string statusCode) =>
        new()
        {
            Source = source,
            DisplayName = displayName,
            Installed = installed,
            Available = false,
            StatusCode = statusCode,
            CapturedAtUtc = DateTime.UtcNow,
            TotalCount = 0,
            Consumers = []
        };

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
            // Bozuk/okunamayan yapılandırma boş kabul edilir (DatabaseService ile aynı davranış).
        }

        return result;
    }

    private static EngineDescriptor ResolveDescriptor(string engineKey) => engineKey switch
    {
        // DatabaseService.ResolveDescriptor ile AYNI bileşen kimlikleri/varsayılanları.
        "mariadb" => new EngineDescriptor("MariaDB", "mariadb-11", "root", 3306, true),
        "mysql" => new EngineDescriptor("MySQL", "mysql-8", "root", 3307, true),
        "postgresql" => new EngineDescriptor("PostgreSQL", "postgresql-16", "postgres", 5432, false),
        _ => throw new AppException("monitoring.source_not_supported", 400, 400)
    };

    private sealed record EngineDescriptor(
        string DisplayName,
        string ComponentId,
        string AdminUserName,
        int DefaultPort,
        bool IsMySqlFamily);

    [GeneratedRegex(@"^REQUEST\s+""(?<id>[^""]*)""\s*\((?<body>.*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TextRequestPattern();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();

    // IDENTIFIED BY / IDENTIFIED WITH ... BY / SET PASSWORD / PASSWORD( / PASSWORD = / PASSWORD '...'
    [GeneratedRegex(@"identified\s+(by|with)|set\s+password|\bpassword\s*(\(|=|'|"")", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SensitivePattern();

    // Tek veya çift tırnaklı SQL değişmezleri (kaçışlı tırnak dahil).
    [GeneratedRegex(@"'(?:[^']|'')*'|""(?:[^""]|"""")*""", RegexOptions.Compiled)]
    private static partial Regex QuotedLiteralPattern();
}
