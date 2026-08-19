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
            throw ClassifyFileError(exception);
        }
    }

    // Ham FTP/SFTP istisnasını anlaşılır bir başlığa (yetkilendirme / uzak yol / bağlantı) eşler ve gerçek teknik
    // mesajı Details'te taşır. Tür adı + mesaj üzerinden anahtar-kelime sınıflandırması (kütüphaneye sıkı bağlı
    // değil; FluentFTP ve SSH.NET'in tipik metinlerini kapsar). Eşleşme yoksa generic "migration.failed".
    private static AppException ClassifyFileError(Exception exception)
    {
        var detail = exception.Message;
        var probe = $"{exception.GetType().Name} {detail}".ToLowerInvariant();

        if (probe.Contains("auth") || probe.Contains("login") || probe.Contains("permission denied") ||
            probe.Contains("password") || probe.Contains("530"))
        {
            return new AppException("migration.files.auth_failed", 502, 502, detail);
        }

        if (probe.Contains("no such file") || probe.Contains("not found") || probe.Contains("pathnotfound") ||
            probe.Contains("could not find") || probe.Contains("no such directory") || probe.Contains("550"))
        {
            return new AppException("migration.files.path_not_found", 502, 502, detail);
        }

        if (probe.Contains("socket") || probe.Contains("connect") || probe.Contains("timeout") ||
            probe.Contains("timed out") || probe.Contains("resolve") || probe.Contains("refused") ||
            probe.Contains("unreachable") || probe.Contains("host"))
        {
            return new AppException("migration.files.connect_failed", 502, 502, detail);
        }

        return new AppException("migration.failed", 502, 500, detail);
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
        var failed = results.Count(item => !item.IsSuccess);

        var result = new RemoteFileMigrationResult
        {
            SiteName = request.SiteName,
            Protocol = secure ? "ftps" : "ftp",
            FilesDownloaded = files.Count,
            BytesDownloaded = files.Sum(item => Math.Max(0, item.Size)),
            TargetPath = target
        };

        EnsureFilesDownloaded(result, remotePath, failed);
        return result;
    }

    /// <summary>
    /// SIFIR DOSYA BAŞARI DEĞİLDİR.
    /// <para>
    /// Eski hâlde bu kontrol hiç yoktu: yanlış "Uzak yol" (en sık göç hatası — /httpdocs yerine /,
    /// ya da kullanıcının ev dizini boş) verildiğinde FluentFTP/SSH.NET istisna atmaz, sadece boş
    /// bir liste döner. Panel de "Dosyalar çekildi" diye YEŞİL bir adım gösterirdi; kullanıcı
    /// göçün bittiğini sanıp DNS'i çevirir ve karşısına boş site çıkardı.
    /// </para>
    /// <para>
    /// Mesaj anahtarı bilerek <c>migration.files.path_not_found</c>: kullanıcıya yapması gereken
    /// şeyi söyler ("Uzak yol'u kontrol et: /httpdocs, /public_html, /").
    /// </para>
    /// </summary>
    internal static void EnsureFilesDownloaded(RemoteFileMigrationResult result, string remotePath, int failedCount)
    {
        if (result.FilesDownloaded > 0)
        {
            return;
        }

        var detay = $"'{remotePath}' yolundan hicbir dosya indirilmedi.";
        if (failedCount > 0)
        {
            detay += $" {failedCount} ogenin aktarimi basarisiz oldu (yetki ya da baglanti).";
        }
        else
        {
            detay += " Uzak dizin bos olabilir ya da yol yanlis olabilir.";
        }

        throw new AppException("migration.files.path_not_found", 502, 502, detay);
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

            var result = new RemoteFileMigrationResult
            {
                SiteName = request.SiteName,
                Protocol = "sftp",
                FilesDownloaded = counter.Files,
                BytesDownloaded = counter.Bytes,
                TargetPath = target
            };

            // FTP ile aynı gerçek: SFTP'de de boş/yanlış uzak yol istisna atmaz, sessizce 0 dosya döner.
            EnsureFilesDownloaded(result, remotePath, failedCount: 0);
            return result;
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
