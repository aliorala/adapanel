using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// Uzak veritabanı erişimi: hangi veritabanı kullanıcısının, nereden bağlanabileceği.
/// </summary>
/// <remarks>
/// <para>
/// ESKİ HÂLİ VE NEDEN DEĞİŞTİ. Bu servis önce sunucu geneli TEK bir anahtardı: "3306'yı şu IP'ye
/// aç". Üç ayrı sorunu vardı ve üçü de ölçüldü:
/// </para>
/// <list type="number">
/// <item>Tek IP alıyordu; birden çok ofis/CIDR yazılamıyordu.</item>
/// <item>Yalnız 3306 biliyordu; PostgreSQL (5432) kurulu olsa bile kapsam dışıydı.</item>
/// <item>
/// Sunucu geneliydi. Çok kiracılı bir panelde A müşterisinin isteğiyle açılan port, B
/// müşterisinin veritabanı hesaplarını da aynı anda dışarıdan denenebilir yapıyordu.
/// </item>
/// </list>
/// <para>
/// Ayar artık KULLANICI bazındadır ve iki katman birlikte yönetilir: motor tarafı (MySQL'de hesabın
/// host kümesi, PostgreSQL'de <c>pg_hba.conf</c>) ve ağ tarafı (Windows Güvenlik Duvarı kuralı).
/// Güvenlik duvarı kuralının izin verdiği adresler, kullanıcı listelerinin BİRLEŞİMİDİR: panel hiçbir
/// zaman kimsenin istemediği bir adrese port açmaz.
/// </para>
/// <para>
/// Motor listesi elle yazılmaz; <see cref="IDatabaseService.GetEnginesAsync"/> ile KURULU motorlar
/// okunur. Hangi motorun uzak erişimi nasıl uyguladığı da motorun kendi sağlayıcısındadır.
/// </para>
/// </remarks>
internal sealed partial class RemoteDbAccessService(
    IPanelPreferencesService preferencesService,
    IDatabaseService databaseService,
    IDatabaseOwnershipService ownershipService,
    DatabaseCredentialStore credentialStore,
    IEnumerable<IDatabaseEngineProvider> providers,
    AdaPanel.Data.Persistence.AdaPanelDbContext dbContext,
    ISecretProtector secretProtector,
    JsonFileStore fileStore) : IRemoteDbAccessService
{
    private const string StatePath = "config/remote-db.json";
    private const int LegacyMariaDbPort = 3306;

    /// <summary>
    /// Kural adı port içerir; böylece her motorun kendi kuralı olur.
    /// </summary>
    /// <remarks>
    /// 3306 için üretilen ad, servisin eski sürümünün kullandığı adla AYNIDIR. Bilerek: aksi hâlde
    /// eski kural sunucuda öksüz kalır ve panel "kapalı" derken port açık kalırdı.
    /// </remarks>
    private static string RuleName(int port) => $"AdaPanel RemoteDB {port}";

    // ---------------------------------------------------------------------------------------
    // Kullanıcı bazlı uzak erişim
    // ---------------------------------------------------------------------------------------

    public async Task<DatabaseRemoteAccessOverviewDto> GetUserAccessAsync(CancellationToken cancellationToken = default)
    {
        var state = await ReadStateAsync(cancellationToken);
        var serverHost = (await preferencesService.GetAsync(cancellationToken)).ServerAddress;
        var engines = new List<DatabaseRemoteAccessEngineDto>();

        foreach (var engineStatus in await databaseService.GetEnginesAsync(cancellationToken))
        {
            engines.Add(await BuildEngineAsync(engineStatus.Engine, engineStatus.DisplayName, engineStatus.Installed, serverHost, state, cancellationToken));
        }

        return new DatabaseRemoteAccessOverviewDto { Engines = engines };
    }

    public async Task<DatabaseRemoteAccessEngineDto> SetUserAccessAsync(
        UpdateDatabaseUserRemoteAccessRequest request,
        Guid actorSubscriptionId,
        bool actorIsAdministrator,
        CancellationToken cancellationToken = default)
    {
        var engineKey = (request.Engine ?? string.Empty).Trim();
        var dbEngine = DatabaseProfiles.ParseEngine(engineKey);
        var userName = (request.UserName ?? string.Empty).Trim();
        MySqlAccountHosts.EnsureManageable(userName);

        var provider = ResolveProvider(dbEngine);
        if (provider.RemoteAccessMechanism == "none")
        {
            throw new AppException("database.remote_not_supported", 400, 400);
        }

        var mode = ParseMode(request.Mode);
        var origins = ResolveOrigins(mode, request);

        // PostgreSQL users share one HBA file; firewall updates also publish an engine-wide
        // snapshot. Serialize the complete operation with all account/resource mutations.
        using var mutation = await DatabaseLifecycle.EnterEngineAsync(engineKey, cancellationToken);
        var profile = await DatabaseProfiles.ResolveAsync(dbContext, secretProtector, dbEngine, cancellationToken);
        var facts = await ReadFactsAsync(provider, profile, [userName], cancellationToken);
        if (!facts.TryGetValue(userName, out var userFacts) || userFacts.Hosts.Count + userFacts.Databases.Count == 0)
        {
            throw new AppException("database.user_not_found", 404, 404);
        }

        await EnsureOwnershipAsync(profile.EngineKey, userFacts.Databases, actorSubscriptionId, actorIsAdministrator, cancellationToken);

        var password = await credentialStore.FindPasswordAsync(profile.EngineKey, userName, cancellationToken);
        await provider.ApplyRemoteAccessAsync(profile, userName, userFacts.Databases, origins, password, cancellationToken);

        // Motor tarafı BAŞARILI olmadan panel kaydı değişmez: kayıt, uygulanmış gerçeği yansıtmalıdır.
        var state = await WriteUserStateAsync(profile.EngineKey, userName, mode, origins, cancellationToken);

        if (request.ApplyFirewall)
        {
            await SyncFirewallAsync(profile.EngineKey, profile.Port, state, cancellationToken);
        }

        var serverHost = (await preferencesService.GetAsync(cancellationToken)).ServerAddress;
        return await BuildEngineAsync(profile.EngineKey, profile.DisplayName, true, serverHost, state, cancellationToken);
    }

    private async Task<DatabaseRemoteAccessEngineDto> BuildEngineAsync(
        string engineKey,
        string displayName,
        bool installed,
        string serverHost,
        RemoteDbState state,
        CancellationToken cancellationToken)
    {
        var dbEngine = DatabaseProfiles.ParseEngine(engineKey);
        var provider = TryResolveProvider(dbEngine);
        var mechanism = provider?.RemoteAccessMechanism ?? "none";

        var supported = installed && mechanism != "none";
        if (!supported)
        {
            return new DatabaseRemoteAccessEngineDto
            {
                Engine = engineKey,
                DisplayName = displayName,
                Installed = installed,
                Supported = false,
                Mechanism = mechanism,
                ServerHost = serverHost,
                Port = DatabaseProfiles.DefaultExternalPort(dbEngine)
            };
        }

        // Motora ulaşılamıyorsa (servis durmuş, parola kayıtlı değil) BOŞ liste dönmek "kimse
        // uzaktan bağlanamıyor" yalanı olurdu. Kurulu ama okunamayan motor uyarıyla işaretlenir.
        try
        {
            var profile = await DatabaseProfiles.ResolveAsync(dbContext, secretProtector, dbEngine, cancellationToken);
            var workspace = await databaseService.GetWorkspaceAsync(engineKey, cancellationToken);
            var userNames = workspace.Users
                .Select(item => item.UserName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();

            var facts = await ReadFactsAsync(provider!, profile, userNames, cancellationToken);
            var firewall = await ReadFirewallAsync(profile.Port, cancellationToken);

            return new DatabaseRemoteAccessEngineDto
            {
                Engine = engineKey,
                DisplayName = displayName,
                Installed = true,
                Supported = true,
                Mechanism = mechanism,
                ServerHost = serverHost,
                Port = profile.Port,
                FirewallOpen = firewall.Open,
                FirewallAllowedAddresses = firewall.Addresses,
                WarningKey = firewall.WarningKey.Length > 0
                    ? firewall.WarningKey
                    : facts.Values.Select(item => item.WarningKey).FirstOrDefault(key => key.Length > 0) ?? string.Empty,
                Users = [.. userNames.Select(userName => BuildUser(engineKey, userName, facts, state))]
            };
        }
        catch (AppException exception)
        {
            return new DatabaseRemoteAccessEngineDto
            {
                Engine = engineKey,
                DisplayName = displayName,
                Installed = installed,
                Supported = false,
                Mechanism = mechanism,
                ServerHost = serverHost,
                Port = DatabaseProfiles.DefaultExternalPort(dbEngine),
                WarningKey = exception.MessageKey
            };
        }
    }

    private static DatabaseUserRemoteAccessDto BuildUser(
        string engineKey,
        string userName,
        IReadOnlyDictionary<string, RemoteAccessFacts> facts,
        RemoteDbState state)
    {
        var userFacts = facts.TryGetValue(userName, out var found) ? found : RemoteAccessFacts.Empty;
        var stored = state.Users.TryGetValue(UserKey(engineKey, userName), out var entry) ? entry : null;

        // GÖSTERİLEN KİP MOTORDAN TÜRETİLİR, panelin kaydından değil. Panel kaydı ile motor
        // ayrışabilir (elle çalıştırılmış SQL, panel öncesinden kalan hesap, eski sürümün '%'
        // varsayılanı). Kullanıcının görmesi gereken, gerçekte kimin bağlanabildiğidir.
        var remoteHosts = userFacts.Hosts.Where(host => !MySqlAccountHosts.IsLocal(host)).ToArray();
        var anywhere = remoteHosts.Any(host => host is "%" or "0.0.0.0/0" or "::/0");
        var mode = anywhere ? "anywhere" : remoteHosts.Length > 0 ? "allowlist" : "closed";

        return new DatabaseUserRemoteAccessDto
        {
            Engine = engineKey,
            UserName = userName,
            Mode = mode,
            // Kaynak listesi panelin kaydından gelir (kullanıcının yazdığı biçim korunsun diye),
            // ama yalnız motor da uzak erişimi doğruluyorsa gösterilir.
            AllowedOrigins = mode == "closed"
                ? []
                : stored is { Origins.Count: > 0 } ? stored.Origins : remoteHosts,
            EngineHosts = userFacts.Hosts,
            RemoteOpen = remoteHosts.Length > 0,
            Databases = userFacts.Databases
        };
    }

    /// <summary>
    /// Kiracı kapsamı: yönetici değilse, kullanıcının yetkili olduğu HER veritabanı isteyene ait olmalı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sahiplik <see cref="IDatabaseOwnershipService"/>ten okunur; ad benzerliğine
    /// (<c>musteri1_</c> öneki gibi) BAKILMAZ — ad tahmini, adını uygun seçen bir kiracıya
    /// başkasının veritabanını verirdi.
    /// </para>
    /// <para>
    /// Tek bir yabancı veritabanı bile isteği reddeder: birden çok kiracının veritabanına yetkili
    /// ORTAK bir hesabın erişim sınırını kiracılardan biri tek başına değiştirememelidir.
    /// </para>
    /// <para>
    /// Hiç veritabanı yoksa da reddedilir. Kapsamı bilinmeyen bir hesap için "sahibi benim"
    /// varsayılamaz; kapalıya düşmek burada güvenli yöndür.
    /// </para>
    /// </remarks>
    private async Task EnsureOwnershipAsync(
        string engineKey,
        IReadOnlyCollection<string> databases,
        Guid actorSubscriptionId,
        bool actorIsAdministrator,
        CancellationToken cancellationToken)
    {
        if (actorIsAdministrator)
        {
            return;
        }

        if (databases.Count == 0)
        {
            throw new AppException("database.not_owned", 403, 403);
        }

        foreach (var databaseName in databases)
        {
            var owner = await ownershipService.GetOwnerAsync(engineKey, databaseName, cancellationToken);
            if (owner is null || owner.Value != actorSubscriptionId)
            {
                throw new AppException("database.not_owned", 403, 403);
            }
        }
    }

    /// <summary>İstenen kipe göre kaynak listesini kurar ve "her yer" onayını dayatır.</summary>
    internal static IReadOnlyList<RemoteAccessOrigin> ResolveOrigins(RemoteAccessMode mode, UpdateDatabaseUserRemoteAccessRequest request)
    {
        switch (mode)
        {
            case RemoteAccessMode.Closed:
                return [];

            case RemoteAccessMode.Anywhere:
                if (!request.ConfirmAnywhere)
                {
                    throw new AppException("database.remote_anywhere_not_confirmed", 400, 400);
                }

                return [RemoteAccessOrigin.Parse("%")];

            default:
                var origins = RemoteAccessOrigin.ParseAll(request.AllowedOrigins);
                if (origins.Count == 0)
                {
                    throw new AppException("database.remote_origin_required", 400, 400);
                }

                // "Her yer" LİSTE İÇİNDE de kaçak yapamaz: kullanıcı 0.0.0.0/0 yazarak onay
                // ekranını atlayamamalı, çünkü sonuç birebir "herkese açık"tır.
                if (origins.Any(origin => origin.IsAnywhere) && !request.ConfirmAnywhere)
                {
                    throw new AppException("database.remote_anywhere_not_confirmed", 400, 400);
                }

                return origins;
        }
    }

    internal static RemoteAccessMode ParseMode(string? value)
    {
        // Tanınmayan değer SESSİZCE varsayılana düşmez: "kapalı" sanıp "her yer" uygulamak ya da
        // tersi, kullanıcının ekranda gördüğüyle sunucudaki gerçeği ayırırdı.
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "closed" => RemoteAccessMode.Closed,
            "allowlist" => RemoteAccessMode.Allowlist,
            "anywhere" => RemoteAccessMode.Anywhere,
            _ => throw new AppException("database.remote_mode_invalid", 400, 400)
        };
    }

    // ---------------------------------------------------------------------------------------
    // Güvenlik duvarı
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Kuralı, KULLANICI listelerinin birleşimine göre yeniden yazar.
    /// </summary>
    /// <remarks>
    /// Hiç kimsenin uzak erişimi yoksa kural KALDIRILIR. Böylece "son uzak kullanıcıyı kapattım
    /// ama port hâlâ açık" durumu oluşamaz.
    /// </remarks>
    private async Task SyncFirewallAsync(string engineKey, int port, RemoteDbState state, CancellationToken cancellationToken)
    {
        var addresses = CollectFirewallAddresses(engineKey, state);
        if (addresses.Count == 0)
        {
            await RemoveRuleAsync(port, cancellationToken);
            return;
        }

        var script =
            $"$ErrorActionPreference = 'Stop'; " +
            $"Remove-NetFirewallRule -DisplayName '{RuleName(port)}' -ErrorAction SilentlyContinue | Out-Null; " +
            $"New-NetFirewallRule -DisplayName '{RuleName(port)}' -Direction Inbound -Action Allow -Protocol TCP " +
            $"-LocalPort {port} -RemoteAddress {string.Join(",", addresses)} -Profile Any | Out-Null";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("database.remote_firewall_failed", 500, 500, result.StandardError);
        }
    }

    internal static IReadOnlyList<string> CollectFirewallAddresses(string engineKey, RemoteDbState state)
    {
        var prefix = engineKey.Trim().ToLowerInvariant() + "|";
        var addresses = new List<string>();

        foreach (var entry in state.Users
                     .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Value))
        {
            if (string.Equals(entry.Mode, "closed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var origin in entry.Origins)
            {
                // Kayıtta bozuk bir değer varsa kuralı hiç yazmamak yerine o satırı atla: tek bozuk
                // kayıt yüzünden diğer kullanıcıların erişimi kapanmasın.
                RemoteAccessOrigin parsed;
                try
                {
                    parsed = RemoteAccessOrigin.Parse(origin);
                }
                catch (AppException)
                {
                    continue;
                }

                // Tek bir "her yer" varsa liste anlamsızdır: kural zaten her yeri kapsar.
                if (parsed.IsAnywhere)
                {
                    return ["Any"];
                }

                var address = parsed.ToFirewallRemoteAddress();
                if (!addresses.Contains(address, StringComparer.OrdinalIgnoreCase))
                {
                    addresses.Add(address);
                }
            }
        }

        return addresses;
    }

    private static async Task RemoveRuleAsync(int port, CancellationToken cancellationToken)
    {
        // KAPATMA da açma kadar sıkı denetlenir. Kural silinemediğinde (grup ilkesiyle yönetilen
        // firewall, başka profilde oluşmuş kural, yetki yok) panel "kapalı" yazarken veritabanı
        // portu açık kalırdı. Kullanıcının kapalı sandığı bir port, açık bildiğinden tehlikelidir.
        // Kural YOKSA hata değildir: kapatma isteği idempotent olmalı.
        var script =
            $"$ErrorActionPreference = 'Stop'; " +
            $"$rule = Get-NetFirewallRule -DisplayName '{RuleName(port)}' -ErrorAction SilentlyContinue; " +
            $"if ($rule) {{ Remove-NetFirewallRule -DisplayName '{RuleName(port)}' | Out-Null }}; " +
            $"if (Get-NetFirewallRule -DisplayName '{RuleName(port)}' -ErrorAction SilentlyContinue) {{ throw 'kural silinemedi' }}";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("database.remote_firewall_failed", 500, 500, result.StandardError);
        }
    }

    private static async Task<(bool Open, IReadOnlyList<string> Addresses, string WarningKey)> ReadFirewallAsync(
        int port,
        CancellationToken cancellationToken)
    {
        var script =
            $"$rule = Get-NetFirewallRule -DisplayName '{RuleName(port)}' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
            $"if ($rule) {{ " +
            $"$adres = (Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $rule).RemoteAddress; " +
            $"[Console]::Out.WriteLine('ADAPANEL-KURAL=' + ($adres -join ';')) }} " +
            $"else {{ [Console]::Out.WriteLine('ADAPANEL-KURAL-YOK') }}";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        var output = result.StandardOutput ?? string.Empty;

        if (output.Contains("ADAPANEL-KURAL-YOK", StringComparison.Ordinal))
        {
            return (false, [], string.Empty);
        }

        var match = FirewallOutputPattern().Match(output);
        if (!match.Success)
        {
            // BİLİNMİYOR ile KAPALI ayrı şeylerdir. PowerShell çalışmadıysa "kapalı" demek,
            // kullanıcıya doğrulanmamış bir güvence vermek olurdu.
            return (false, [], "database.remote_firewall_unknown");
        }

        var addresses = match.Groups[1].Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return (true, addresses, string.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Eski (sunucu geneli) uç — geriye uyum
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// MariaDB portunun güvenlik duvarı durumunu döner (eski sözleşme).
    /// </summary>
    /// <remarks>
    /// Artık panelin kaydını değil, GÜVENLİK DUVARINDAKİ gerçek kuralı raporlar. Eski hâli
    /// dosyadaki bayrağı okuyordu; kural elle silinmişse panel "açık" demeye devam ederdi.
    /// </remarks>
    public async Task<RemoteDbAccessDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var preferences = await preferencesService.GetAsync(cancellationToken);
        var firewall = await ReadFirewallAsync(LegacyMariaDbPort, cancellationToken);

        return new RemoteDbAccessDto
        {
            Enabled = firewall.Open,
            AllowedIp = string.Join(", ", firewall.Addresses.Where(item => !item.Equals("Any", StringComparison.OrdinalIgnoreCase))),
            ServerHost = preferences.ServerAddress,
            Port = LegacyMariaDbPort
        };
    }

    /// <summary>
    /// MariaDB portunu açar/kapatır (eski sözleşme), ama artık kullanıcı ayarlarına BAĞLI.
    /// </summary>
    /// <remarks>
    /// Eskiden bu uç, hiçbir veritabanı kullanıcısının uzak erişimi olmasa bile portu açabiliyordu —
    /// yani asıl açığın kendisiydi. Artık açılacak adresler kullanıcı listelerinin birleşiminden
    /// gelir; kimsenin uzak erişimi yoksa açacak bir şey yoktur ve istek reddedilir. Kapatma her
    /// zaman çalışır (güvenli yön hiçbir koşulda engellenmez).
    /// </remarks>
    public async Task<RemoteDbAccessDto> SetAsync(UpdateRemoteDbAccessRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await DatabaseLifecycle.EnterEngineAsync("mariadb", cancellationToken);
        if (!request.Enabled)
        {
            await RemoveRuleAsync(LegacyMariaDbPort, cancellationToken);
            return await GetAsync(cancellationToken);
        }

        var state = await ReadStateAsync(cancellationToken);
        if (CollectFirewallAddresses("mariadb", state).Count == 0)
        {
            throw new AppException("database.remote_no_user_access", 409, 409);
        }

        await SyncFirewallAsync("mariadb", LegacyMariaDbPort, state, cancellationToken);
        return await GetAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------------------------
    // Yardımcılar
    // ---------------------------------------------------------------------------------------

    private static Task<IReadOnlyDictionary<string, RemoteAccessFacts>> ReadFactsAsync(
        IDatabaseEngineProvider provider,
        DatabaseConnectionProfile profile,
        IReadOnlyCollection<string> userNames,
        CancellationToken cancellationToken)
        => provider.ReadRemoteAccessAsync(profile, userNames, cancellationToken);

    private IDatabaseEngineProvider ResolveProvider(AdaPanel.Core.Enums.DbEngine engine)
        => TryResolveProvider(engine) ?? throw new AppException("database.engine_not_supported", 400, 400);

    private IDatabaseEngineProvider? TryResolveProvider(AdaPanel.Core.Enums.DbEngine engine)
    {
        // MariaDB, MySQL sağlayıcısıyla yönetilir (DatabaseService.ResolveProvider ile aynı kural).
        var target = engine == AdaPanel.Core.Enums.DbEngine.MariaDb ? AdaPanel.Core.Enums.DbEngine.MySql : engine;
        return providers.FirstOrDefault(candidate => candidate.Engine == target);
    }

    private static string UserKey(string engineKey, string userName) =>
        DatabaseAccountIdentity.FamilyKey(engineKey, userName);

    private Task<RemoteDbState> ReadStateAsync(CancellationToken cancellationToken)
        => fileStore.ReadAsync(StatePath, new RemoteDbState(), cancellationToken);

    private async Task<RemoteDbState> WriteUserStateAsync(
        string engineKey,
        string userName,
        RemoteAccessMode mode,
        IReadOnlyList<RemoteAccessOrigin> origins,
        CancellationToken cancellationToken)
    {
        // Atomik oku-değiştir-yaz: iki kullanıcı aynı anda ayar değiştirdiğinde biri kaybolmasın.
        return await fileStore.UpdateAsync(StatePath, new RemoteDbState(), existing =>
        {
            existing.Users[UserKey(engineKey, userName)] = new RemoteUserAccessState
            {
                Mode = mode.ToString().ToLowerInvariant(),
                Origins = [.. origins.Select(origin => origin.Canonical)]
            };

            return existing;
        }, cancellationToken);
    }

    internal sealed class RemoteDbState
    {
        /// <summary>Eski sürümden kalan sunucu geneli bayrak; okunur ama artık karar vermez.</summary>
        public bool Enabled { get; set; }

        /// <summary>Eski sürümden kalan tek IP alanı.</summary>
        public string AllowedIp { get; set; } = string.Empty;

        /// <summary>Anahtar: "motor|kullanici".</summary>
        public Dictionary<string, RemoteUserAccessState> Users { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class RemoteUserAccessState
    {
        public string Mode { get; set; } = "closed";

        public List<string> Origins { get; set; } = [];
    }

    [GeneratedRegex(@"ADAPANEL-KURAL=(.*)", RegexOptions.Compiled)]
    private static partial Regex FirewallOutputPattern();
}
