using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using FluentFTP;
using Renci.SshNet;

namespace AdaPanel.Infrastructure.Migration;

/// <summary>
/// Uzak sunucudan site dosyalarını çeker: FTP/FTPS için FluentFTP, SFTP için SSH.NET (her ikisi de
/// listeleme/özyinelemeyi güvenli yapar — elle FTP listesi ayrıştırmaya gerek yok). Hedef = sitenin
/// IIS fiziksel kök dizini. Sertifika/host-key doğrulaması göç senaryosu için gevşektir (kullanıcı kendi
/// dosyalarını çekiyor).
/// </summary>
internal sealed class FileMigrationService(IIisSiteService iisSiteService) : IFileMigrationService
{
    public async Task<RemoteFileMigrationResult> PullAsync(RemoteFileMigrationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw new AppException("migration.host_required", 400, 400);
        }

        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        var target = site.PhysicalPath;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            throw new AppException("migration.target_missing", 400, 400);
        }

        var protocol = (request.Protocol ?? "ftp").Trim().ToLowerInvariant();
        var remotePath = string.IsNullOrWhiteSpace(request.RemotePath) ? "/" : request.RemotePath.Trim();

        try
        {
            return protocol switch
            {
                "sftp" => await PullSftpAsync(request, target, remotePath, cancellationToken),
                _ => await PullFtpAsync(request, target, remotePath, protocol == "ftps", cancellationToken)
            };
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppException("migration.failed", 502, 500, exception.Message);
        }
    }

    private async Task<RemoteFileMigrationResult> PullFtpAsync(
        RemoteFileMigrationRequest request,
        string target,
        string remotePath,
        bool secure,
        CancellationToken cancellationToken)
    {
        var config = new FtpConfig
        {
            EncryptionMode = secure ? FtpEncryptionMode.Auto : FtpEncryptionMode.None,
            ValidateAnyCertificate = true
        };

        await using var client = new AsyncFtpClient(
            request.Host,
            request.UserName,
            request.Password,
            request.Port > 0 ? request.Port : 21,
            config);

        await client.Connect(cancellationToken);
        var results = await client.DownloadDirectory(
            target,
            remotePath,
            FtpFolderSyncMode.Update,
            FtpLocalExists.Overwrite,
            FtpVerify.None,
            token: cancellationToken);
        await client.Disconnect(cancellationToken);

        var files = results.Where(item => item.IsSuccess && item.Type == FtpObjectType.File).ToList();
        return new RemoteFileMigrationResult
        {
            SiteName = request.SiteName,
            Protocol = secure ? "ftps" : "ftp",
            FilesDownloaded = files.Count,
            BytesDownloaded = files.Sum(item => Math.Max(0, item.Size)),
            TargetPath = target
        };
    }

    private async Task<RemoteFileMigrationResult> PullSftpAsync(
        RemoteFileMigrationRequest request,
        string target,
        string remotePath,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var client = new SftpClient(
                request.Host,
                request.Port > 0 ? request.Port : 22,
                request.UserName,
                request.Password);

            client.Connect();
            var counter = new MigrationCounter();
            DownloadSftpDirectory(client, remotePath, target, counter, cancellationToken);
            client.Disconnect();

            return new RemoteFileMigrationResult
            {
                SiteName = request.SiteName,
                Protocol = "sftp",
                FilesDownloaded = counter.Files,
                BytesDownloaded = counter.Bytes,
                TargetPath = target
            };
        }, cancellationToken);
    }

    private static void DownloadSftpDirectory(
        SftpClient client,
        string remoteDir,
        string localDir,
        MigrationCounter counter,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localDir);
        foreach (var entry in client.ListDirectory(remoteDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var localPath = Path.Combine(localDir, entry.Name);
            if (entry.IsDirectory)
            {
                DownloadSftpDirectory(client, entry.FullName, localPath, counter, cancellationToken);
            }
            else if (entry.IsRegularFile)
            {
                using var stream = File.Create(localPath);
                client.DownloadFile(entry.FullName, stream);
                counter.Files++;
                counter.Bytes += entry.Length;
            }
        }
    }

    private sealed class MigrationCounter
    {
        public int Files;
        public long Bytes;
    }
}
