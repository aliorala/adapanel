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
    ISiteDatabaseLinkService siteDatabaseLinks,
    IEnumerable<IDatabaseEngineProvider> providers) : IDatabaseService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string GeneratedPasswordKey = DatabaseProfiles.GeneratedPasswordKey;
    private const string AdminUserKey = DatabaseProfiles.AdminUserKey;
    private const string AdminPortKey = DatabaseProfiles.AdminPortKey;
    private const string SystemDetectedKey = DatabaseProfiles.SystemDetectedKey;

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
            : NormalizeUserName(request.AdminUserName, engine);
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


    /// <summary>
    /// Tek veritabanının detay kartı.
    /// <para>
    /// Liste ekranı bunu VERİTABANI BAŞINA, kart açıldığında çeker. Boyutu liste sorgusuna
    /// katmak, on veritabanlı bir sunucuda sayfa açılışını information_schema'nın en yavaş
    /// sütunlarına bağlardı — oysa kullanıcı çoğu zaman tek bir veritabanının boyutunu merak eder.
    /// </para>
    /// </summary>
    public async Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(engine);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var name = NormalizeDatabaseName(databaseName);

        if (dbEngine is not (DbEngine.MariaDb or DbEngine.MySql))
        {
            // Diğer motorlarda ayrıntı okuyucu yok. Uydurma bir kart göstermek yerine bağlantı
            // ucunu verip boyutu BİLİNMİYOR bırakırız — yanlış bilgi, bilgi yokluğundan kötüdür.
            return new DatabaseDetailDto
            {
                Engine = profile.EngineKey,
                Name = name,
                Host = "localhost",
                Port = profile.Port,
                EngineName = profile.DisplayName,
                SiteName = await ResolveLinkedSiteAsync(profile.EngineKey, name, cancellationToken)
            };
        }

        return await MySqlDetailReader.ReadAsync(
            profile,
            name,
            await ResolveLinkedSiteAsync(profile.EngineKey, name, cancellationToken),
            cancellationToken);
    }

    public async Task<DatabaseCheckResultDto> CheckAndRepairAsync(
        string engine,
        string databaseName,
        IReadOnlyCollection<string> repairTables,
        CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(engine);
        if (dbEngine is not (DbEngine.MariaDb or DbEngine.MySql))
        {
            throw new AppException("database.check_not_supported", 400, 400);
        }

        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);

        return await MySqlMaintenanceRunner.RunAsync(
            profile,
            profile.EngineKey,
            NormalizeDatabaseName(databaseName),
            repairTables ?? [],
            cancellationToken);
    }

    /// <summary>Veritabanının bağlı olduğu site (panelin kendi kaydı); yoksa boş.</summary>
    private async Task<string> ResolveLinkedSiteAsync(string engineKey, string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            var baglar = await siteDatabaseLinks.GetAllAsync(cancellationToken);
            return baglar.FirstOrDefault(bag =>
                string.Equals(bag.Engine, engineKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(bag.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase))?.SiteName ?? string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Site bağı okunamadıysa kart yine de açılmalı; bu alan bilgilendirici, zorunlu değil.
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<string>> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(engine);

        // DESTEKLENMEYEN MOTORDA "BASARILI" DONMEZ.
        //
        // Sertleştirme adımlarının hepsi MySQL/MariaDB'ye özgü (anonim kullanıcılar, uzak root@'%',
        // parolasız root, boş 'test' veritabanı). Diğer motorlarda aşağıdaki blok atlanıyor ve
        // metot BOŞ LİSTE dönüyordu; uç bunu "sertleştirildi" diye raporluyor, denetim günlüğü de
        // "güvensiz yönetici hesabı bulunamadı" yazıyordu. Yani hiçbir şey bakılmamışken kullanıcıya
        // "baktık, temiz" deniyordu — sunucusunu güvende sanmasına yol açan bir yanlış bilgi.
        //
        // Aynı kararın karşılığı CheckAndRepairAsync'te zaten var (database.check_not_supported).
        if (dbEngine is not (DbEngine.MariaDb or DbEngine.MySql))
        {
            throw new AppException("database.harden_not_supported", 400, 400);
        }

        var provider = ResolveProvider(dbEngine);
        var results = new List<string>();

        // K2 (parolasız-root açığı) — yalnız MySQL/MariaDB. Boş parolaya İZİN VEREREK profil kur (parolasız
        // root'a bağlanabilmek için); root parolasızsa Plesk gibi güçlü parola ATA (atomik). Sonra güvensiz
        // hesapları (anonim + uzak root) ve boş 'test' db'sini temizle (mysql_secure_installation eşi).
        if (dbEngine is DbEngine.MariaDb or DbEngine.MySql)
        {
            var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken, allowEmptyPassword: true);
            if (string.IsNullOrWhiteSpace(profile.AdminPassword))
            {
                profile = await SetRootPasswordAtomicAsync(dbEngine, profile, results, cancellationToken);
            }

            results.AddRange(await provider.HardenAdminAccountsAsync(profile, cancellationToken));
            return results;
        }

        var otherProfile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        results.AddRange(await provider.HardenAdminAccountsAsync(otherProfile, cancellationToken));
        return results;
    }

    // ATOMİK root parolası (Plesk gibi): MariaDB root'u parolasızsa güçlü parola atar + saklar. "MariaDB yeni /
    // panel eski" TEHLİKELİ durumu OLUŞMAZ: önce SAKLAR, sonra UYGULAR+DOĞRULAR, hata olursa saklamayı GERİ ALIR.
    // Saklanan parola tek kaynaktır → panelin DB bağlantıları + phpMyAdmin (WebDbClient) aynı anda tutarlı kalır.
    private async Task<DatabaseConnectionProfile> SetRootPasswordAtomicAsync(
        DbEngine dbEngine,
        DatabaseConnectionProfile oldProfile,
        List<string> results,
        CancellationToken cancellationToken)
    {
        var descriptor = ResolveDescriptor(dbEngine);
        var component = await dbContext.InstalledComponents.FirstOrDefaultAsync(
            item => item.ComponentId == descriptor.ComponentId && item.Status == ComponentInstallStatus.Installed,
            cancellationToken) ?? throw new AppException("database.engine_not_installed", 404, 404);

        var configuration = ReadProtectedConfiguration(component);
        var oldPassword = oldProfile.AdminPassword; // boş
        var newPassword = GenerateStrongDbPassword();

        // 1) Yeni parolayı önce SAKLA (panel + phpMyAdmin bunu kullanır). [sakla=yeni, MariaDB=eski → kurtarılabilir]
        configuration[GeneratedPasswordKey] = newPassword;
        component.ProtectedConfigurationJson = secretProtector.Protect(JsonSerializer.Serialize(configuration, SerializerOptions));
        component.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            // 2) MariaDB'ye UYGULA (eski/boş parolayla bağlan) + yeni parolayla DOĞRULA.
            await ResolveProvider(dbEngine).SetRootPasswordAsync(oldProfile, newPassword, cancellationToken);
            results.Add("root@localhost: guclu parola ayarlandi (panel uretti; phpMyAdmin/araclar otomatik kullanir)");
            return oldProfile with { AdminPassword = newPassword };
        }
        catch
        {
            // 3) Hata → saklamayı ESKİYE geri al (panel eski parolayla çalışmaya devam eder, kilitlenmez).
            configuration[GeneratedPasswordKey] = oldPassword;
            component.ProtectedConfigurationJson = secretProtector.Protect(JsonSerializer.Serialize(configuration, SerializerOptions));
            component.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    // K2 root parolası: güçlü, belirsiz karakter içermeyen, bağlantı-dizesi güvenli (özel karakter yok) 28 hane.
    private static string GenerateStrongDbPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(28);
        var chars = new char[28];
        for (var index = 0; index < bytes.Length; index++)
        {
            chars[index] = alphabet[bytes[index] % alphabet.Length];
        }

        return new string(chars);
    }

    public async Task<DatabaseWorkspaceDto> CreateDatabaseAsync(
        CreateDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(request.Engine);
        var databaseName = NormalizeDatabaseName(request.Name);

        // KULLANICI ADI, VERİTABANI OLUŞTURULMADAN ÖNCE DOĞRULANIR.
        //
        // ÖLÇÜLEN HATA: doğrulama CREATE DATABASE'den SONRA yapılıyordu. MySQL'in 32 karakter
        // sınırını aşan bir adda veritabanı oluşuyor, CREATE USER 1470 ile düşüyor ve geri alma
        // olmadığı için sunucuda sahipsiz bir veritabanı kalıyordu. Doğrulamayı öne almak, o
        // durumun DOĞMASINI engeller — telafi etmekten iyidir.
        var newUser = string.IsNullOrWhiteSpace(request.UserName)
            ? string.Empty
            : NormalizeUserName(request.UserName, dbEngine);

        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        await provider.CreateDatabaseAsync(profile, databaseName, cancellationToken);

        // profesyonel paneller gibi: kullanıcı adı verildiyse aynı anda kullanıcı oluştur ve bu veritabanına
        // otomatik TAM yetki ver. Host YEREL'dir (bkz. NewUserHost) — uzak erişim ayrı ve açık bir karardır.
        if (newUser.Length > 0)
        {
            var newUserHost = NewUserHost(dbEngine);
            var fullPrivileges = NormalizePrivileges(["ALL PRIVILEGES"], dbEngine);
            try
            {
                await provider.CreateUserAsync(profile, newUser, request.Password, newUserHost, cancellationToken);
                await provider.GrantAccessAsync(profile, databaseName, newUser, newUserHost, fullPrivileges, cancellationToken);
                // phpMyAdmin'i bu kullanıcı olarak açıp yalnız kendi DB'sini göstermek için parolayı şifreli sakla.
                await credentialStore.UpsertAsync(profile.EngineKey, newUser, newUserHost, databaseName, request.Password, cancellationToken);
            }
            catch
            {
                // GERİ AL: kullanıcı kurulamadıysa BU ÇAĞRIDA oluşturduğumuz veritabanını da kaldır.
                //
                // Silmek güvenli çünkü veritabanı bir kaç satır önce BU çağrıda doğdu:
                // CreateDatabaseAsync `CREATE DATABASE` (IF NOT EXISTS DEĞİL) çalıştırır ve zaten
                // varsa 1007'yi `database.database_already_exists` ile yukarı verir — yani buraya
                // gelinmişse veritabanı bize aittir ve içinde veri yoktur.
                //
                // Geri almanın kendisi patlarsa YUTULUR ve asıl hata yukarı çıkar: kullanıcıya
                // "kullanıcı oluşturulamadı" demek, "temizlik başarısız" demekten daha yararlıdır.
                // (Zaten parola politikası/1470 gibi asıl sebep de o mesajdadır.)
                try
                {
                    await provider.DeleteDatabaseAsync(profile, databaseName, cancellationToken);
                }
                catch (Exception)
                {
                    // Yutuldu: asıl istisna aşağıda yeniden fırlatılıyor, onu gölgelemeyiz.
                }

                throw;
            }
        }

        // Site bağı (Plesk gibi): site verildiyse bu DB'yi siteye bağla → site silinince cascade ile gider.
        if (!string.IsNullOrWhiteSpace(request.SiteName))
        {
            await siteDatabaseLinks.LinkAsync(request.Engine, databaseName, request.SiteName, cancellationToken);
        }

        return await provider.GetWorkspaceAsync(profile, cancellationToken);
    }

    // Var olan bir veritabanı+kullanıcıyı panele TANIT (import): erişimi garanti et (eksikse GRANT ALL) ve
    // kullanıcının parolasını sakla → phpMyAdmin "bu kullanıcı olarak aç" + panel yönetimi (yedek/şifre/yetki).
    // Harici (uygulama/elle oluşturulmuş) DB'ler panelde LİSTELENİR ama sahibi/credential'ı izlenmez; bu onu bağlar.
    // Kullanıcı zaten var olmalı (yoksa "Kullanıcı ekle" + veritabanı seçimini kullan).
    public async Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(request.Engine);
        var databaseName = NormalizeDatabaseName(request.DatabaseName);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);
        var userName = NormalizeUserName(request.UserName, dbEngine);

        // HOST TAHMİN EDİLMEZ, MOTORDAN OKUNUR.
        //
        // Eskiden burada `NormalizeAllowedHost(request.Host)` vardı ve boş gelen host `%` oluyordu;
        // arayüz de zaten `host: "%"` SABİTİNİ gönderiyordu. Panelin kendi kullanıcıları ise
        // `localhost`'ta duruyor (MySqlAccountHosts.SafeCreationHost) → yetki var olmayan bir
        // hesaba yazılıyor, kimlik kaydına `%` düşüyor ve silme yanlış hesabı hedefliyordu.
        // GrantAccessAsync bu dersi zaten almıştı; içe aktarma yolu atlanmıştı.
        var targetHosts = string.IsNullOrWhiteSpace(request.Host)
            ? await ResolveExistingHostsAsync(provider, profile, userName, cancellationToken)
            : [NormalizeAllowedHost(request.Host)];

        var fullPrivileges = NormalizePrivileges(["ALL PRIVILEGES"], dbEngine);
        foreach (var host in targetHosts)
        {
            await provider.GrantAccessAsync(profile, databaseName, userName, host, fullPrivileges, cancellationToken);
        }

        // Kimlik kaydına YEREL hesap yazılır (varsa). Kayıt phpMyAdmin'in "bu kullanıcı olarak aç"
        // akışı ve DB silinirken sahibi bulmak için kullanılıyor; ikisi de panelin MOTORA YEREL
        // bağlandığı hesabı ister. Yerel hesap yoksa okunan ilk hesap yazılır — uydurulmuş bir
        // değer değil, ÖLÇÜLMÜŞ bir hesap.
        var credentialHost = targetHosts.FirstOrDefault(MySqlAccountHosts.IsLocal) ?? targetHosts[0];
        await credentialStore.UpsertAsync(profile.EngineKey, userName, credentialHost, databaseName, request.Password, cancellationToken);

        // Site bağı: içe aktarılan DB de bir siteye bağlanabilir → site silinince cascade ile gider.
        if (!string.IsNullOrWhiteSpace(request.SiteName))
        {
            await siteDatabaseLinks.LinkAsync(request.Engine, databaseName, request.SiteName, cancellationToken);
        }

        return await provider.GetWorkspaceAsync(profile, cancellationToken);
    }

    public async Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(engine);
        var databaseName = NormalizeDatabaseName(name);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        // profesyonel paneller gibi: DB'ye bağlı (panel oluşturduğu) kullanıcıyı da temizle ki öksüz kullanıcı kalmasın.
        var owner = await credentialStore.FindForDatabaseAsync(profile.EngineKey, databaseName, cancellationToken);

        await provider.DeleteDatabaseAsync(profile, databaseName, cancellationToken);

        // Önce bu DB'nin kimlik kaydını sil, sonra kullanıcı başka DB'de kullanılmıyorsa kullanıcıyı düşür.
        await credentialStore.RemoveDatabaseAsync(profile.EngineKey, databaseName, cancellationToken);
        // DB bağımsız silindiyse site bağını da temizle (öksüz bağ kalmasın).
        await siteDatabaseLinks.UnlinkAsync(engine, databaseName, cancellationToken);
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
        var normalizedUser = NormalizeUserName(userName, dbEngine);
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
        var userName = NormalizeUserName(request.UserName, dbEngine);
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
        var userName = NormalizeUserName(request.UserName, dbEngine);
        // OLUŞTURMA yolunda varsayılan YEREL'dir; NormalizeAllowedHost'un '%' varsayılanı ise var
        // olan hesabı adresleyen yollar (parola, silme, yetki) içindir ve orada değiştirilemez.
        var allowedHost = string.IsNullOrWhiteSpace(request.AllowedHost)
            ? NewUserHost(dbEngine)
            : NormalizeAllowedHost(request.AllowedHost);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        await provider.CreateUserAsync(profile, userName, request.Password, allowedHost, cancellationToken);

        // Plesk gibi: kullanıcı bir veritabanı bağlamında eklenirse o DB'ye OTOMATİK tam yetki (GRANT ALL) ver.
        // Aksi halde kullanıcı giriş yapar ama "Access denied for user ... to database ..." alır (en sık şikâyet).
        // Veritabanı seçilmemişse yalnız kullanıcı oluşur (ileride GrantAccess ile bağlanabilir).
        var grantedDatabase = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.DatabaseName))
        {
            grantedDatabase = NormalizeDatabaseName(request.DatabaseName);
            var fullPrivileges = NormalizePrivileges(["ALL PRIVILEGES"], dbEngine);
            await provider.GrantAccessAsync(profile, grantedDatabase, userName, allowedHost, fullPrivileges, cancellationToken);
        }

        await credentialStore.UpsertAsync(profile.EngineKey, userName, allowedHost, grantedDatabase, request.Password, cancellationToken);
        return await provider.GetWorkspaceAsync(profile, cancellationToken);
    }

    public async Task<DatabaseWorkspaceDto> GrantAccessAsync(
        GrantDatabaseAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var dbEngine = ParseEngine(request.Engine);
        var databaseName = NormalizeDatabaseName(request.DatabaseName);
        var userName = NormalizeUserName(request.UserName, dbEngine);
        var privileges = NormalizePrivileges(request.Privileges, dbEngine);
        var profile = await ResolveConnectionProfileAsync(dbEngine, cancellationToken);
        var provider = ResolveProvider(dbEngine);

        // Host verilmediyse TAHMİN EDİLMEZ, motordan okunur. Eski varsayılan '%' idi; hesap
        // yerelle sınırlıyken var olmayan bir hesaba yetki yazmaya çalışıyor ya da (GRANT'ın
        // hesap oluşturabildiği yapılandırmalarda) her yerden geçerli yeni bir hesap doğuruyordu.
        // Kullanıcının birden çok hesabı varsa (yerel + uzak) yetki HEPSİNE verilir: panel
        // kullanıcısı için bunlar tek bir kimliktir, birinde olup diğerinde olmayan yetki
        // "bazen çalışan bağlantı" demek olurdu.
        var targetHosts = string.IsNullOrWhiteSpace(request.AllowedHost)
            ? await ResolveExistingHostsAsync(provider, profile, userName, cancellationToken)
            : [NormalizeAllowedHost(request.AllowedHost)];

        foreach (var host in targetHosts)
        {
            await provider.GrantAccessAsync(profile, databaseName, userName, host, privileges, cancellationToken);
        }

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
            DbEngine.PostgreSql => await ExportPostgreSqlAsync(profile, databaseName, targetPath, cancellationToken),
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
            DbEngine.PostgreSql => await ImportPostgreSqlAsync(profile, databaseName, sourcePath, cancellationToken),
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

            // 3) Hedef DB KULLANICISINI oluştur + bu DB'ye tam yetki (taşınan uygulama bağlanabilsin). Eskiden yalnız
            //    DB + veri taşınıyordu, kullanıcı oluşturulmuyordu → uygulama "Access denied for user" alıyordu.
            //    Kullanıcı/parola verilmezse DB adından türetilir / güçlü üretilir. Best-effort (transfer'i bozmaz).
            var createdUser = string.Empty;
            var createdPassword = string.Empty;
            try
            {
                var transferUser = string.IsNullOrWhiteSpace(request.TargetUserName)
                    ? DeriveTransferUser(targetDatabase)
                    : NormalizeUserName(request.TargetUserName, engine);
                var passwordGenerated = string.IsNullOrWhiteSpace(request.TargetPassword);
                var transferPassword = passwordGenerated ? GenerateDbPassword() : request.TargetPassword;
                var transferHost = NewUserHost(engine);
                var fullPrivileges = NormalizePrivileges(["ALL PRIVILEGES"], engine);

                try
                {
                    await provider.CreateUserAsync(targetProfile, transferUser, transferPassword, transferHost, cancellationToken);
                }
                catch
                {
                    // kullanıcı zaten var olabilir → parolayı değiştirmeden yetkilendirmeye devam et
                }

                createdUser = transferUser;
                createdPassword = passwordGenerated ? transferPassword : string.Empty;
                await provider.GrantAccessAsync(targetProfile, targetDatabase, transferUser, transferHost, fullPrivileges, cancellationToken);
                await credentialStore.UpsertAsync(targetProfile.EngineKey, transferUser, transferHost, targetDatabase, transferPassword, cancellationToken);
            }
            catch
            {
                // best-effort: kullanıcı kurulamasa da transfer başarılı (DB + veri taşındı)
            }

            return new DatabaseTransferResultDto
            {
                Engine = targetProfile.EngineKey,
                SourceDatabase = sourceDatabase,
                TargetDatabase = targetDatabase,
                Size = File.Exists(tempFile) ? new FileInfo(tempFile).Length : 0,
                UserName = createdUser,
                Password = createdPassword
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

    // Hedef DB kullanıcı adı türetimi (kullanıcı vermediyse): DB adından harf-rakam süzülür, harfle
    // başlatılır, kırpılır, "_usr" eklenir. Kırpma sınırı ELLE yazılmaz, MySQL'in sınırından
    // hesaplanır (32 - "_usr") — taşıma hedefi her zaman yerel MySQL/MariaDB'dir ve ikisinde de
    // geçerli olan dar sınır MySQL'inkidir. Eskiden "27" sabiti duruyordu; sınır tek kaynağa
    // taşınınca burada bayat bir kopya kalmasın diye türetildi.
    private const int TransferUserSuffixLength = 4; // "_usr"

    private static string DeriveTransferUser(string databaseName)
    {
        var cleaned = new string((databaseName ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (cleaned.Length == 0 || !char.IsLetter(cleaned[0]))
        {
            cleaned = "db" + cleaned;
        }

        var maxBase = DatabaseIdentifierLimits.MySqlUserNameLength - TransferUserSuffixLength;
        if (cleaned.Length > maxBase)
        {
            cleaned = cleaned[..maxBase];
        }

        return cleaned + "_usr";
    }

    // Politika dostu (büyük + küçük + rakam + simge) ve SQL string-literal güvenli (tırnak/ters bölü yok) parola.
    private static string GenerateDbPassword()
        => "Ap1!" + System.Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    private static string TrimTransferError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 600 ? trimmed[..600] : trimmed;
    }

    private Task<DatabaseConnectionProfile> ResolveConnectionProfileAsync(DbEngine engine, CancellationToken cancellationToken, bool allowEmptyPassword = false)
        => DatabaseProfiles.ResolveAsync(dbContext, secretProtector, engine, cancellationToken, allowEmptyPassword);

    private static int DefaultExternalPort(DbEngine engine) => DatabaseProfiles.DefaultExternalPort(engine);

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
        => DatabaseProfiles.ReadProtectedConfiguration(secretProtector, component);

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

    /// <summary>
    /// TEK veritabanının dökümünü alır.
    /// <para>
    /// Burada eskiden <c>pg_dumpall</c> kullanılıyordu ve <c>databaseName</c> hiç okunmuyordu: kullanıcı
    /// tek bir veritabanının yedeğini istediğinde SUNUCUNUN TAMAMI (tüm roller ve tüm veritabanları)
    /// dökülüyor, dosya adı ve arayüz ise tek veritabanı gösteriyordu. Geri yükleme de aynı dosyayı
    /// çalıştırdığı için "şu veritabanını geri yükle" demek bütün kümenin üzerine yazmak anlamına
    /// geliyordu — istenmeyen veritabanları ve roller sessizce eziliyordu.
    /// </para>
    /// <para><c>--no-password</c>: parola yanlışsa istem beklemek yerine hemen hata versin.</para>
    /// </summary>
    private static Task<CommandResult> ExportPostgreSqlAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable(profile.InstallDir, "pg_dump.exe");
        var arguments =
            $"--host={profile.Host} --port={profile.Port} --username=\"{profile.AdminUserName}\" " +
            $"--no-password --dbname=\"{databaseName}\" --file=\"{targetPath}\"";
        return ProcessRunner.RunAsync(
            executable,
            arguments,
            cancellationToken: cancellationToken,
            environmentVariables: new Dictionary<string, string> { ["PGPASSWORD"] = profile.AdminPassword });
    }

    /// <summary>
    /// Dökümü HEDEF veritabanına geri yükler.
    /// <para>
    /// <c>-v ON_ERROR_STOP=1</c> ZORUNLU: psql, tek tek SQL ifadelerinin hataları yüzünden çıkış kodunu
    /// DEĞİŞTİRMEZ — bağlantı kurulabildiği sürece 0 döner. Bu bayrak olmadan bozuk ya da uyumsuz bir
    /// döküm geri yüklenirken her ifade patlasa bile panel "geri yükleme tamam" der; yönetici de buna
    /// güvenip elindeki yedeği siler. (PostgreSQL psql belgeleri, "Exit Status".)
    /// </para>
    /// <para>
    /// <c>--dbname</c> artık <c>postgres</c> değil HEDEF veritabanı: döküm tek veritabanına ait.
    /// </para>
    /// </summary>
    private static Task<CommandResult> ImportPostgreSqlAsync(
        DatabaseConnectionProfile profile,
        string databaseName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable(profile.InstallDir, "psql.exe");
        var arguments =
            $"--host={profile.Host} --port={profile.Port} --username=\"{profile.AdminUserName}\" " +
            $"--no-password -v ON_ERROR_STOP=1 --dbname=\"{databaseName}\" --file=\"{sourcePath}\"";
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

    private static DatabaseConnectionProfile ResolveMsSqlProfile() => DatabaseProfiles.ResolveMsSqlProfile();

    /// <summary>
    /// Motorun komut satırı aracını (mysqldump, pg_dump vb.) bulur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adaylar SABİT SÜRÜM klasörleriyle yazılıydı: "MariaDB 11.4", "MySQL Server 8.4",
    /// "PostgreSQL". Sunucuda zaten kurulu (panelin kurmadığı) bir MariaDB 10.11 ya da
    /// PostgreSQL 15 varsa hiçbiri eşleşmiyordu ve yedekleme, dışa/içe aktarma, veritabanı taşıma
    /// yollarının hepsi "dump aracı bulunamadı" ile düşüyordu.
    /// </para>
    /// <para>
    /// Artık sürüm klasörleri TARANIYOR (en yeni önce) ve son çare olarak PATH'e bakılıyor.
    /// </para>
    /// </remarks>
    private static string FindExecutable(string installDir, string fileName)
    {
        var candidates = new List<string> { installDir, Path.Combine(installDir, "bin") };

        foreach (var kok in new[] { "%ProgramFiles%", "%ProgramFiles(x86)%" })
        {
            var taban = Environment.ExpandEnvironmentVariables(kok);
            if (!Directory.Exists(taban))
            {
                continue;
            }

            // MariaDB doğrudan "MariaDB <sürüm>" klasörüne, MySQL/PostgreSQL bir ara klasörün
            // altına kurulur; ikisi de kapsanıyor.
            candidates.AddRange(SurumKlasorleri(taban, "MariaDB*"));
            candidates.AddRange(SurumKlasorleri(Path.Combine(taban, "MySQL"), "*"));
            candidates.AddRange(SurumKlasorleri(Path.Combine(taban, "PostgreSQL"), "*"));
        }

        // PATH: araç ortamda tanımlıysa sürüm klasörü aramaya gerek yok.
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var directory in candidates.Where(item => !string.IsNullOrWhiteSpace(item) && Directory.Exists(item)))
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

    /// <summary>
    /// Bir tabanın altındaki sürüm klasörlerini (ve bin alt klasörlerini) EN YENİDEN eskiye döndürür.
    /// </summary>
    private static IEnumerable<string> SurumKlasorleri(string taban, string desen)
    {
        if (!Directory.Exists(taban))
        {
            yield break;
        }

        string[] klasorler;
        try
        {
            klasorler = Directory.GetDirectories(taban, desen, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var klasor in klasorler.OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(klasor, "bin");
            yield return klasor;
        }
    }

    private static EngineDescriptor ResolveDescriptor(DbEngine engine) => DatabaseProfiles.ResolveDescriptor(engine);

    private IDatabaseEngineProvider ResolveProvider(DbEngine engine)
    {
        if (engine == DbEngine.MariaDb)
        {
            return providers.First(provider => provider.Engine == DbEngine.MySql);
        }

        var provider = providers.FirstOrDefault(candidate => candidate.Engine == engine);
        return provider ?? throw new AppException("database.engine_not_supported", 400, 400);
    }

    private static DbEngine ParseEngine(string engine) => DatabaseProfiles.ParseEngine(engine);

    private static string NormalizeDatabaseName(string value)
    {
        var normalized = value.Trim();
        if (!DatabaseNamePattern().IsMatch(normalized))
        {
            throw new AppException("database.invalid_name", 400, 400);
        }

        return normalized;
    }

    /// <summary>
    /// Kullanıcı adını biçim VE motorun uzunluk sınırına göre doğrular.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uzunluk MOTORA göre ölçülür (<see cref="DatabaseIdentifierLimits"/>): MySQL 32'de durur,
    /// MariaDB 80, PostgreSQL 63, SQL Server 128. Eskiden tek bir regex 64 dayatıyordu — hem
    /// MySQL için ÇOK GENİŞ (33 karakterlik ad kabul edilip motorda 1470 ile düşüyordu) hem
    /// SQL Server için gereksiz DAR.
    /// </para>
    /// <para>
    /// Sınırı aşan ad, motora HİÇ GİTMEDEN 400 ile reddedilir. Bu yalnızca daha iyi bir mesaj
    /// değil, bir BÜTÜNLÜK meselesidir: <see cref="CreateDatabaseAsync"/> veritabanını
    /// kullanıcıdan ÖNCE oluşturuyor; adı burada yakalamak, "veritabanı oluştu ama kullanıcı
    /// oluşmadı" durumunun hiç doğmamasını sağlar.
    /// </para>
    /// </remarks>
    private static string NormalizeUserName(string value, DbEngine engine)
    {
        var normalized = value.Trim();
        if (!UserNamePattern().IsMatch(normalized))
        {
            throw new AppException("database.invalid_user_name", 400, 400);
        }

        var limit = DatabaseIdentifierLimits.ForEngine(engine);
        if (normalized.Length > limit)
        {
            throw new AppException(
                "database.user_name_too_long",
                400,
                400,
                $"engine={engine}; length={normalized.Length}; max={limit}");
        }

        return normalized;
    }

    /// <summary>
    /// Kullanıcının motorda VAR OLAN hesap host'ları; hiç yoksa hata.
    /// </summary>
    /// <remarks>
    /// Rol tabanlı motorlarda (PostgreSQL) host kavramı yoktur; orada tek elemanlı bir liste
    /// döner ve sağlayıcı bu değeri zaten yok sayar.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ResolveExistingHostsAsync(
        IDatabaseEngineProvider provider,
        DatabaseConnectionProfile profile,
        string userName,
        CancellationToken cancellationToken)
    {
        if (!profile.SupportsHostScopedUsers)
        {
            return ["%"];
        }

        var facts = await provider.ReadRemoteAccessAsync(profile, [userName], cancellationToken);
        var hosts = facts.TryGetValue(userName, out var found) ? found.Hosts : [];

        return hosts.Count > 0
            ? [.. hosts]
            : throw new AppException("database.user_not_found", 404, 404);
    }

    /// <summary>
    /// YENİ oluşturulan bir veritabanı kullanıcısının host değeri.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ÖLÇÜLEN DURUM: panel her kullanıcıyı <c>kullanici@'%'</c> olarak açıyordu — veritabanını
    /// oluştururken de (<see cref="CreateDatabaseAsync"/>), kullanıcı eklerken de
    /// (<c>CreateDatabaseUserRequest.AllowedHost</c> varsayılanı), taşımada da. Yani her müşteri
    /// hesabı kimlik doğrulama açısından dünyaya açıktı ve tek engel güvenlik duvarıydı. Bir
    /// güvenlik sınırını tek bir güvenlik duvarı kuralına bağlamak, o kural bir kez açıldığında
    /// (yönetici eli, başka bir bileşen, VPN/NAT) sunucudaki BÜTÜN müşteri hesaplarının aynı anda
    /// dışarıdan denenebilir olması demektir.
    /// </para>
    /// <para>
    /// KARAR: yalnız OLUŞTURMA yolu değişir; var olan hesaplara DOKUNULMAZ. Çalışan kurulumlarda
    /// <c>%</c> ile açılmış hesaplar olduğu gibi kalır — onları toplu değiştirmek, uzaktan bağlanan
    /// bir uygulamayı habersiz koparırdı. Geçiş yolu uzak erişim ekranıdır: orada hesabın MOTORDAN
    /// OKUNAN gerçek host'ları görünür, "her yerden" olanlar işaretlenir ve yönetici tek tek,
    /// bilerek daraltır.
    /// </para>
    /// <para>
    /// <c>localhost</c> seçilir çünkü Windows'ta uygulamalar motora TCP ile 127.0.0.1 üzerinden
    /// bağlanır ve ad çözümü açıkken (varsayılan) sunucu bunu <c>localhost</c> olarak eşler.
    /// Motorda <c>skip-name-resolve</c> açıksa yalnız sayısal karşılık eşleşir; bu durumda uzak
    /// erişim akışının yerel küme tamamlaması (<see cref="MySqlAccountHosts.LocalHosts"/>) devreye
    /// girer ve eksik yerel hesaplar oluşturulur.
    /// </para>
    /// </remarks>
    private static string NewUserHost(DbEngine engine)
        => engine is DbEngine.MariaDb or DbEngine.MySql ? MySqlAccountHosts.SafeCreationHost : "%";

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

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,63}$", RegexOptions.Compiled)]
    private static partial Regex DatabaseNamePattern();

    // Yalnız BİÇİM: harfle başlar, harf/rakam/alt çizgi devam eder. UZUNLUK burada DEĞİL,
    // NormalizeUserName içinde motorun sınırına göre ölçülür — tek bir sayı dört motorun hiçbirini
    // doğru temsil etmiyordu. Üst sınır olarak DatabaseIdentifierLimits.RequestCeiling kullanılır
    // ki desen sınırsız bir girdiyle uğraşmasın.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,127}$", RegexOptions.Compiled)]
    private static partial Regex UserNamePattern();

    [GeneratedRegex("^[A-Za-z0-9.%:_-]{1,255}$", RegexOptions.Compiled)]
    private static partial Regex HostPattern();
}
