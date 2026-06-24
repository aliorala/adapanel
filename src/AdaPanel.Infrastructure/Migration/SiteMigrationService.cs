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

    private static MigrationStepDto Step(string name, bool success, string message)
        => new() { Step = name, Success = success, Message = message };

    private static string ToMessage(Exception exception)
        => exception is AppException appException ? appException.MessageKey : exception.Message;
}
