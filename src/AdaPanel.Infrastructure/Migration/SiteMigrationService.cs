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
    ILogger<SiteMigrationService> logger) : ISiteMigrationService
{
    public async Task<MigrateSiteResultDto> MigrateSiteAsync(
        MigrateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<MigrationStepDto>();

        if (request.CreateSite)
        {
            try
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
                steps.Add(Step("site", true, "migration.step.site_created"));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Göç: site oluşturma başarısız {Site}", request.SiteName);
                steps.Add(Step("site", false, ToMessage(exception)));
            }
        }

        if (request.PullFiles)
        {
            try
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
                steps.Add(Step("files", true, "migration.step.files_pulled"));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Göç: dosya çekme başarısız {Site}", request.SiteName);
                steps.Add(Step("files", false, ToMessage(exception)));
            }
        }

        if (request.TransferDatabase)
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(request.TargetDatabase) ? request.SourceDatabase : request.TargetDatabase;

                // Hedef DB'yi oluştur (zaten varsa yok say) → transfer geri yüklesin.
                try
                {
                    await databaseService.CreateDatabaseAsync(
                        new CreateDatabaseRequest { Engine = request.DatabaseEngine, Name = target },
                        cancellationToken);
                }
                catch (Exception createException)
                {
                    logger.LogInformation(createException, "Göç: hedef DB zaten var olabilir ({Db}); transfer devam ediyor.", target);
                }

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
                steps.Add(Step("database", true, "migration.step.db_transferred"));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Göç: veritabanı transferi başarısız {Site}", request.SiteName);
                steps.Add(Step("database", false, ToMessage(exception)));
            }
        }

        return new MigrateSiteResultDto
        {
            SiteName = request.SiteName,
            Steps = steps,
            OverallSuccess = steps.Count > 0 && steps.TrueForAll(step => step.Success)
        };
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
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", request.ApiKey);
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
            throw new AppException("migration.plesk.api_error", 502, 502, $"HTTP {(int)response.StatusCode}");
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

    private static MigrationStepDto Step(string name, bool success, string message)
        => new() { Step = name, Success = success, Message = message };

    private static string ToMessage(Exception exception)
        => exception is AppException appException ? appException.MessageKey : exception.Message;
}
