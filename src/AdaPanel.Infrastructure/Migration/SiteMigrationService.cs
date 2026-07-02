using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Migration;

/// <summary>
/// Site göçü orkestratörü (Plesk/cPanel/ham sunucu → AdaPanel): mevcut, test edilmiş parçaları zincirler —
/// site sağlama (ISiteProvisioningService), dosya çekme (IFileMigrationService), DB transfer (IDatabaseService).
/// Her adım bağımsız try/catch → biri patlasa migration durmaz; kısmi sonuç adım-adım raporlanır.
/// </summary>
public sealed class SiteMigrationService(
    ISiteProvisioningService siteProvisioningService,
    IFileMigrationService fileMigrationService,
    IDatabaseService databaseService,
    IIisSiteService iisSiteService,
    ILogger<SiteMigrationService> logger) : ISiteMigrationService
{
    public async Task<MigrateSiteResultDto> MigrateSiteAsync(
        MigrateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        Func<MigrationProgressEvent, CancellationToken, Task>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<MigrationStepDto>();

        async Task ReportAsync(MigrationProgressEvent progressEvent)
        {
            if (onProgress is not null)
            {
                await onProgress(progressEvent, cancellationToken);
            }
        }

        // Tek bir adımı çalıştırır: önce "start" raporlar → dener → sonucu hem listeye ekler hem "result" raporlar.
        // Adımlar bağımsız: istisna yutulur (göç durmaz, kısmi başarı olabilir). Başarı/başarısızlık değerini döner
        // ki çağıran zincirleme kararı verebilsin (ör. userini yalnızca dosya çekme başarılıysa).
        async Task<bool> ExecuteStepAsync(string stepId, string successMessage, Func<Task<string?>> action)
        {
            await ReportAsync(new MigrationProgressEvent { Phase = "start", Step = stepId });
            try
            {
                var successDetail = await action();
                steps.Add(new MigrationStepDto { Step = stepId, Success = true, Message = successMessage, Detail = successDetail });
                await ReportAsync(new MigrationProgressEvent { Phase = "result", Step = stepId, Success = true, Message = successMessage, Detail = successDetail });
                return true;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Göç: {Step} adımı başarısız {Site}", stepId, request.SiteName);
                var (message, detail) = DescribeError(exception);
                steps.Add(new MigrationStepDto { Step = stepId, Success = false, Message = message, Detail = detail });
                await ReportAsync(new MigrationProgressEvent { Phase = "result", Step = stepId, Success = false, Message = message, Detail = detail });
                return false;
            }
        }

        // Önce çalışacak adımları bildir (istemci "sırada" satırlarını çizsin). userini dinamiktir (yalnızca dosya
        // adımı başarılıysa) → plana koymuyoruz; istemci o satırı canlı ekler.
        var plan = new List<string>();
        if (request.CreateSite) plan.Add("site");
        if (request.PullFiles) plan.Add("files");
        if (request.TransferDatabase) plan.Add("database");
        await ReportAsync(new MigrationProgressEvent { Phase = "plan", Steps = plan });

        if (request.CreateSite)
        {
            await ExecuteStepAsync("site", "migration.step.site_created", async () =>
            {
                await siteProvisioningService.CreateAndProvisionAsync(
                    new CreateSiteRequest
                    {
                        Name = request.SiteName,
                        PhysicalPath = request.PhysicalPath,
                        HostName = request.SiteName,
                        IpAddress = "*",
                        Port = 80,
                        Protocol = "http",
                        RuntimeProfile = "php",
                        AutoStart = true,
                        ProvisionDefaultServices = false
                    },
                    actorUserId,
                    ipAddress,
                    cancellationToken);
                return null;
            });
        }

        if (request.PullFiles)
        {
            var filesPulled = await ExecuteStepAsync("files", "migration.step.files_pulled", async () =>
            {
                await fileMigrationService.PullAsync(
                    new RemoteFileMigrationRequest
                    {
                        SiteName = request.SiteName,
                        Protocol = request.FileProtocol,
                        Host = request.FileHost,
                        Port = request.FilePort,
                        UserName = request.FileUserName,
                        Password = request.FilePassword,
                        RemotePath = request.RemotePath
                    },
                    cancellationToken);
                return null;
            });

            // Dosyalar çekildiyse göçen .user.ini'leri temizle: eski Plesk/vhost yolları + bozuk open_basedir
            // (kaynaktan gelen) site'yi çalışmaz hale getirir → düzelt/temizle. Yalnızca dosya çekme başarılıysa.
            if (filesPulled)
            {
                await ExecuteStepAsync("userini", "migration.step.userini_sanitized", async () =>
                {
                    await SanitizeUserIniFilesAsync(request.SiteName, cancellationToken);
                    return null;
                });
            }
        }

        if (request.TransferDatabase)
        {
            await ExecuteStepAsync("database", "migration.step.db_transferred", async () =>
            {
                var target = string.IsNullOrWhiteSpace(request.TargetDatabase) ? request.SourceDatabase : request.TargetDatabase;

                // Taşınan uygulamanın bağlanacağı hedef DB kullanıcısı: verilmişse aynen (uygulamanın config'iyle
                // eşleşsin → bağlantı değişmeden çalışır), yoksa site adından türetilir. Parola yoksa güçlü üretilir.
                var dbUser = string.IsNullOrWhiteSpace(request.TargetDatabaseUser)
                    ? DeriveDbUser(request.SiteName, target)
                    : request.TargetDatabaseUser.Trim();
                var passwordGenerated = string.IsNullOrWhiteSpace(request.TargetDatabasePassword);
                var dbPassword = passwordGenerated ? GenerateDbPassword() : request.TargetDatabasePassword;

                // 1) Hedef DB'yi oluştur + siteye bağla (zaten varsa yok say → transfer yine de geri yükler).
                try
                {
                    await databaseService.CreateDatabaseAsync(
                        new CreateDatabaseRequest { Engine = request.DatabaseEngine, Name = target, SiteName = request.SiteName },
                        cancellationToken);
                }
                catch (Exception createException)
                {
                    logger.LogInformation(createException, "Göç: hedef DB zaten var olabilir ({Db}); devam ediyor.", target);
                }

                // 2) DB KULLANICISINI oluştur + bu DB'ye tam yetki (Plesk göçü gibi). Eskiden yalnız DB taşınıyordu →
                //    kullanıcı olmayınca uygulama "Access denied for user" alıyordu. Kullanıcı zaten varsa yok say.
                try
                {
                    await databaseService.CreateUserAsync(
                        new CreateDatabaseUserRequest
                        {
                            Engine = request.DatabaseEngine,
                            UserName = dbUser,
                            Password = dbPassword,
                            DatabaseName = target
                        },
                        cancellationToken);
                }
                catch (Exception userException)
                {
                    logger.LogInformation(userException, "Göç: DB kullanıcısı zaten var olabilir ({User}); devam ediyor.", dbUser);
                }

                // 3) Veriyi aktar (mysqldump → mysql).
                await databaseService.TransferDatabaseAsync(
                    new DatabaseTransferRequest
                    {
                        Engine = request.DatabaseEngine,
                        SourceHost = request.DbHost,
                        SourcePort = request.DbPort,
                        SourceUserName = request.DbUserName,
                        SourcePassword = request.DbPassword,
                        SourceDatabase = request.SourceDatabase,
                        TargetDatabase = target
                    },
                    cancellationToken);

                // Oluşturulan erişim bilgisini adım detayında göster → kullanıcı uygulama config'ini (wp-config.php/.env)
                // güncelleyebilsin. Parola kullanıcı tarafından verildiyse ekranda tekrar göstermeyiz (zaten biliyor).
                return passwordGenerated
                    ? $"DB user: {dbUser}  ·  password: {dbPassword}"
                    : $"DB user: {dbUser}";
            });
        }

        var result = new MigrateSiteResultDto
        {
            SiteName = request.SiteName,
            Steps = steps,
            OverallSuccess = steps.Count > 0 && steps.TrueForAll(step => step.Success)
        };

        await ReportAsync(new MigrationProgressEvent
        {
            Phase = "done",
            OverallSuccess = result.OverallSuccess,
            Message = result.OverallSuccess ? "migration.site.success" : "migration.site.partial"
        });

        return result;
    }

    // Plesk REST keşfi: GET https://host:8443/api/v2/domains (X-API-Key) → domain listesi. Plesk genelde
    // self-signed sertifika kullanır → bu TEK seferlik göç bağlantısı için sertifika doğrulaması atlanır (admin
    // kendi sunucusuna bağlanıyor). Best-effort: bağlantı/şekil hatası → net AppException; manuel form çalışmaya devam eder.
    public async Task<PleskDiscoverResultDto> DiscoverPleskAsync(PleskDiscoverRequest request, CancellationToken cancellationToken = default)
    {
        var host = request.Host.Trim().TrimEnd('/');
        var port = request.Port > 0 ? request.Port : 8443;

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        // Kimlik: gizli API anahtarı (X-API-Key) ya da yönetici kullanıcı adı+parola (Basic auth). İkisi de yoksa net hata.
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", request.ApiKey.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(request.UserName) && !string.IsNullOrWhiteSpace(request.Password))
        {
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{request.UserName.Trim()}:{request.Password}"));
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        }
        else
        {
            throw new AppException("migration.plesk.auth_required", 400, 400);
        }

        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"https://{host}:{port}/api/v2/domains", cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plesk keşif bağlantısı başarısız {Host}:{Port}", host, port);
            throw new AppException("migration.plesk.connect_failed", 502, 502, exception.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Gerçek nedeni yüzeye çıkar: HTTP kodu + Plesk'in JSON hata gövdesi (Details'te). Yaygın kodları sınıflandır:
            // 401/403 = kimlik reddi (kullanıcı/parola yanlış ya da IP/erişim kısıtı), 404 = bu sürümde REST API v2 yolu yok.
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var snippet = errorBody.Length > 300 ? errorBody[..300] : errorBody;
            var status = (int)response.StatusCode;
            var key = status switch
            {
                401 or 403 => "migration.plesk.auth_failed",
                404 => "migration.plesk.not_found",
                _ => "migration.plesk.api_error"
            };
            throw new AppException(key, 502, 502, $"HTTP {status} {response.ReasonPhrase}\r\n{snippet}".Trim());
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var domains = new List<PleskDomainDto>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object ||
                        !element.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    domains.Add(new PleskDomainDto
                    {
                        Name = nameElement.GetString() ?? string.Empty,
                        HostingType = element.TryGetProperty("hosting_type", out var hostingType) && hostingType.ValueKind == JsonValueKind.String
                            ? hostingType.GetString() ?? string.Empty
                            : string.Empty
                    });
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plesk keşif yanıtı ayrıştırılamadı.");
            throw new AppException("migration.plesk.parse_failed", 502, 502, exception.Message);
        }

        return new PleskDiscoverResultDto { Domains = domains };
    }

    // cPanel/WHM keşfi: GET https://host:2087/json-api/listaccts?api.version=1 (Authorization: whm user:TOKEN) →
    // { data: { acct: [ { domain, user } ] } }. WHM genelde self-signed → bu tek-seferlik göç bağlantısında
    // sertifika doğrulaması atlanır (admin kendi sunucusuna bağlanıyor). Plesk keşfiyle aynı sonuç şekli döner.
    public async Task<PleskDiscoverResultDto> DiscoverCpanelAsync(CpanelDiscoverRequest request, CancellationToken cancellationToken = default)
    {
        var host = request.Host.Trim().TrimEnd('/');
        var port = request.Port > 0 ? request.Port : 2087;
        var user = string.IsNullOrWhiteSpace(request.UserName) ? "root" : request.UserName.Trim();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"whm {user}:{request.ApiToken}");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"https://{host}:{port}/json-api/listaccts?api.version=1", cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "cPanel/WHM keşif bağlantısı başarısız {Host}:{Port}", host, port);
            throw new AppException("migration.cpanel.connect_failed", 502, 502, exception.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AppException("migration.cpanel.api_error", 502, 502, $"HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var domains = new List<PleskDomainDto>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("acct", out var accounts) &&
                accounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var account in accounts.EnumerateArray())
                {
                    if (account.ValueKind != JsonValueKind.Object ||
                        !account.TryGetProperty("domain", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    domains.Add(new PleskDomainDto
                    {
                        Name = name,
                        // HostingType alanını cPanel kullanıcı adı için kullan (sihirbazda yol şablonu üretmeye yarar).
                        HostingType = account.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.String
                            ? userElement.GetString() ?? string.Empty
                            : string.Empty
                    });
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "cPanel/WHM keşif yanıtı ayrıştırılamadı.");
            throw new AppException("migration.cpanel.parse_failed", 502, 502, exception.Message);
        }

        return new PleskDiscoverResultDto { Domains = domains };
    }

    // Göç ÖNCESİ ucuz ön-kontroller: site adı uygunluğu (oluşturulacaksa çakışma yok / hedefse var) + dosya/DB
    // kaynağı TCP erişilebilirliği (kimlik doğrulamaz; "host:port'a bağlanılıyor mu" = en sık göç tıkanması).
    public async Task<MigrationValidationResultDto> ValidateMigrationAsync(MigrateSiteRequest request, CancellationToken cancellationToken = default)
    {
        var checks = new List<MigrationCheckDto>();

        if (string.IsNullOrWhiteSpace(request.SiteName))
        {
            checks.Add(Check("site_name", false, "migration.validate.site_name_empty"));
        }
        else
        {
            var exists = await SiteExistsAsync(request.SiteName, cancellationToken);
            checks.Add(request.CreateSite
                ? Check("site_name", !exists, exists ? "migration.validate.site_exists" : "migration.validate.site_name_ok")
                : Check("site_name", exists, exists ? "migration.validate.site_found" : "migration.validate.site_missing"));
        }

        if (request.PullFiles)
        {
            var filePort = request.FilePort > 0 ? request.FilePort : DefaultFilePort(request.FileProtocol);
            var reachable = await CanConnectAsync(request.FileHost, filePort, cancellationToken);
            checks.Add(Check("file_source", reachable, reachable ? "migration.validate.file_reachable" : "migration.validate.file_unreachable"));
        }

        if (request.TransferDatabase)
        {
            var dbPort = request.DbPort > 0 ? request.DbPort : 3306;
            var reachable = await CanConnectAsync(request.DbHost, dbPort, cancellationToken);
            checks.Add(Check("db_source", reachable, reachable ? "migration.validate.db_reachable" : "migration.validate.db_unreachable"));
        }

        if (checks.Count == 0)
        {
            checks.Add(Check("nothing", false, "migration.validate.nothing_selected"));
        }

        return new MigrationValidationResultDto
        {
            Checks = checks,
            AllPassed = checks.TrueForAll(check => check.Ok)
        };
    }

    private async Task<bool> SiteExistsAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            _ = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int DefaultFilePort(string protocol)
        => string.Equals(protocol?.Trim(), "sftp", StringComparison.OrdinalIgnoreCase) ? 22 : 21;

    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(host.Trim(), port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static MigrationCheckDto Check(string name, bool ok, string message)
        => new() { Name = name, Ok = ok, Message = message };

    // Göçen .user.ini dosyalarını temizler: open_basedir'i yeni site köküne sıfırlar; mutlak ama bu makinede
    // OLMAYAN yolları (eski Plesk/vhost: session.save_path, upload_tmp_dir, error_log ...) yorum satırına alır →
    // PHP varsayılanlarına döner. Değişiklik yoksa dosyaya dokunmaz. Dosya başına best-effort.
    private async Task<int> SanitizeUserIniFilesAsync(string siteName, CancellationToken cancellationToken)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = site.PhysicalPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return 0;
        }

        var tempDir = Path.GetTempPath().TrimEnd('\\', '/');
        var sanitized = 0;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, ".user.ini", SearchOption.AllDirectories).Take(200).ToList();
        }
        catch
        {
            files = Directory.EnumerateFiles(root, ".user.ini", SearchOption.TopDirectoryOnly).ToList();
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = await File.ReadAllLinesAsync(file, cancellationToken);
            var changed = false;
            for (var index = 0; index < lines.Length; index++)
            {
                var rewritten = SanitizeUserIniLine(lines[index], root, tempDir);
                if (rewritten != lines[index])
                {
                    lines[index] = rewritten;
                    changed = true;
                }
            }

            if (changed)
            {
                await File.WriteAllLinesAsync(file, lines, cancellationToken);
                sanitized++;
            }
        }

        return sanitized;
    }

    private static readonly string[] StaleUserIniPathMarkers =
    {
        "\\vhosts\\", "/vhosts/", "inetpub\\vhosts", "/var/www/vhosts", "/var/www/httpd"
    };

    private static readonly HashSet<string> PathBearingUserIniKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "session.save_path", "upload_tmp_dir", "sys_temp_dir", "error_log",
        "sendmail_path", "curl.cainfo", "openssl.cafile", "soap.wsdl_cache_dir",
        "opcache.file_cache", "extension_dir"
    };

    private static string SanitizeUserIniLine(string line, string root, string tempDir)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '[' or '#')
        {
            return line;
        }

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return line;
        }

        var key = line[..equalsIndex].Trim().ToLowerInvariant();
        var value = line[(equalsIndex + 1)..].Trim().Trim('"').Trim();

        // open_basedir göçten gelirse ESKİ sunucu yollarını gösterir → yeni site kökü + temp olarak yeniden yaz.
        if (key == "open_basedir")
        {
            return $"open_basedir = \"{root};{tempDir}\"";
        }

        if (value.Length == 0 || !PathBearingUserIniKeys.Contains(key))
        {
            return line;
        }

        var stale = StaleUserIniPathMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var rooted = Path.IsPathRooted(value) || value.StartsWith('/') || value.Contains(":\\", StringComparison.Ordinal);
        var directory = TryGetDirectoryName(value);
        var missing = rooted && !Directory.Exists(value) && !File.Exists(value) &&
                      (directory is null || directory.Length == 0 || !Directory.Exists(directory));

        return stale || missing
            ? "; " + line + "    ; AdaPanel goc: gecersiz yol devre disi birakildi"
            : line;
    }

    private static string? TryGetDirectoryName(string value)
    {
        try
        {
            return Path.GetDirectoryName(value);
        }
        catch
        {
            return null;
        }
    }

    // Hedef DB kullanıcı adı türetimi (kullanıcı vermediyse): site adı/DB adından harf-rakam süzülür, 24'e kırpılır,
    // "_usr" eklenir. DatabaseService.NormalizeUserName ayrıca son geçerliliği sağlar (MySQL kullanıcı sınırı 32).
    private static string DeriveDbUser(string siteName, string databaseName)
    {
        var basis = !string.IsNullOrWhiteSpace(siteName) ? siteName : databaseName;
        var cleaned = new string(basis.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (cleaned.Length == 0)
        {
            cleaned = "site";
        }
        if (cleaned.Length > 24)
        {
            cleaned = cleaned[..24];
        }
        return cleaned + "_usr";
    }

    // Politika dostu (büyük + küçük + rakam + simge) ve SQL string-literal güvenli (tırnak/ters bölü yok) parola.
    // Entropi 12 rastgele bayttan (24 hex). MySQL/MariaDB validate_password politikalarını da karşılar.
    private static string GenerateDbPassword()
        => "Ap1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    // Hatayı kullanıcıya iki parçaya ayırır: başlık (i18n anahtarı = "ne oldu") + detay (ham teknik neden = "neden").
    // AppException → MessageKey + Details; diğerleri → ham .Message tek başına (başlık olarak). Böylece "Dosya göçü
    // başarısız oldu" gibi sağır mesajlar yerine gerçek sebep ("530 Login incorrect", "/httpdocs yok …) görünür.
    private static (string Message, string? Detail) DescribeError(Exception exception)
        => exception is AppException appException
            ? (appException.MessageKey, string.IsNullOrWhiteSpace(appException.Details) ? null : appException.Details)
            : (exception.Message, null);
}
