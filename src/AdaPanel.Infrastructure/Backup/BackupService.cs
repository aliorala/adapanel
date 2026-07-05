using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Backup;

public sealed class BackupService(
    IIisSiteService iisSiteService,
    IDatabaseService databaseService,
    IMailService mailService,
    IPanelPreferencesService preferencesService,
    IFtpService ftpService,
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory) : IBackupService
{
    private const string IndexPath = "config/backups.json";

    private static readonly System.Text.Json.JsonSerializerOptions BackupJsonOptions =
        new() { WriteIndented = true };

    public async Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        var records = await fileStore.ReadAsync(IndexPath, Array.Empty<BackupRecordDto>(), cancellationToken);
        return records
            .OrderByDescending(record => record.CreatedAtUtc)
            .ToArray();
    }

    public async Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        var preferences = await preferencesService.GetAsync(cancellationToken);
        var backupsDirectory = EnsureBackupDirectory(preferences.DefaultBackupRoot, "sites");
        var fileName = $"{site.Name}-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        var applicationRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
            ? site.PhysicalPath
            : site.ApplicationRootPath;

        // profesyonel panel benzeri tam-site yedeği: tek .zip içinde files/ + config/ + databases/ + mail/.
        var includedParts = new List<string>();
        await using (var zipStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            if (request.IncludeFiles && Directory.Exists(applicationRoot))
            {
                AddDirectoryToArchive(archive, applicationRoot, "files");
                includedParts.Add("files");
            }

            if (request.IncludeConfig)
            {
                var configJson = System.Text.Json.JsonSerializer.Serialize(site, BackupJsonOptions);
                AddTextEntry(archive, "config/site.json", configJson);
                includedParts.Add("config");
            }

            foreach (var db in request.Databases ?? [])
            {
                if (string.IsNullOrWhiteSpace(db.Engine) || string.IsNullOrWhiteSpace(db.DatabaseName))
                {
                    continue;
                }

                var ext = db.Engine.Equals("mssql", StringComparison.OrdinalIgnoreCase) ||
                          db.Engine.Equals("sqlserver", StringComparison.OrdinalIgnoreCase) ? "bak" : "sql";
                var tempDump = Path.Combine(Path.GetTempPath(), $"adabk-{Guid.NewGuid():N}.{ext}");
                try
                {
                    await databaseService.ExportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
                    {
                        Engine = db.Engine,
                        DatabaseName = db.DatabaseName,
                        FilePath = tempDump
                    }, cancellationToken);

                    if (File.Exists(tempDump))
                    {
                        archive.CreateEntryFromFile(tempDump, $"databases/{db.Engine}-{db.DatabaseName}.{ext}", CompressionLevel.Optimal);
                        includedParts.Add($"db:{db.DatabaseName}");
                    }
                }
                finally
                {
                    if (File.Exists(tempDump))
                    {
                        File.Delete(tempDump);
                    }
                }
            }

            if (request.IncludeMail)
            {
                try
                {
                    var mailDomain = site.Name.ToLowerInvariant();
                    var mailWorkspace = await mailService.GetWorkspaceAsync(mailDomain, cancellationToken);
                    var mailJson = System.Text.Json.JsonSerializer.Serialize(mailWorkspace, BackupJsonOptions);
                    AddTextEntry(archive, $"mail/{mailDomain}.json", mailJson);
                    includedParts.Add("mail");
                }
                catch
                {
                    // Mail kurulu değilse/alan adı yoksa sessizce atla — yedek yine de tamam.
                }
            }

            var manifest = System.Text.Json.JsonSerializer.Serialize(new
            {
                site = site.Name,
                createdAtUtc = DateTime.UtcNow,
                parts = includedParts
            }, BackupJsonOptions);
            AddTextEntry(archive, "manifest.json", manifest);
        }

        var target = ResolveTarget(request.TargetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);
        var remoteUri = await UploadToRemoteAsync(targetPath, fileName, target, cancellationToken);

        var record = new BackupRecordDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = fileName,
            Type = "site",
            Source = site.Name,
            Path = targetPath,
            Format = "zip",
            CreatedAtUtc = DateTime.UtcNow,
            Size = new FileInfo(targetPath).Length,
            Status = string.IsNullOrWhiteSpace(remoteUri) ? "created" : "created_remote_synced",
            TargetType = target.Type,
            RemoteUri = remoteUri
        };

        await AppendRecordAsync(record, cancellationToken);
        return record;
    }

    public async Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request, CancellationToken cancellationToken = default)
    {
        var preferences = await preferencesService.GetAsync(cancellationToken);
        var backupsDirectory = EnsureBackupDirectory(preferences.DefaultBackupRoot, "databases");
        var safeDatabaseName = string.IsNullOrWhiteSpace(request.DatabaseName) ? "all" : request.DatabaseName.Trim();
        var extension = request.Engine.Equals("mssql", StringComparison.OrdinalIgnoreCase) ||
                        request.Engine.Equals("sqlserver", StringComparison.OrdinalIgnoreCase)
            ? "bak"
            : "sql";
        var fileName = $"{request.Engine}-{safeDatabaseName}-{DateTime.UtcNow:yyyyMMddHHmmss}.{extension}";
        var targetPath = Path.Combine(backupsDirectory, fileName);
        var dump = await databaseService.ExportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
        {
            Engine = request.Engine,
            DatabaseName = request.DatabaseName,
            FilePath = targetPath
        }, cancellationToken);

        var target = ResolveTarget(request.TargetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);
        var remoteUri = await UploadToRemoteAsync(targetPath, fileName, target, cancellationToken);

        var record = new BackupRecordDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = fileName,
            Type = "database",
            Source = $"{dump.Engine}:{dump.DatabaseName}",
            Path = targetPath,
            Format = extension,
            CreatedAtUtc = DateTime.UtcNow,
            Size = dump.Size,
            Status = string.IsNullOrWhiteSpace(remoteUri) ? "created" : "created_remote_synced",
            TargetType = target.Type,
            RemoteUri = remoteUri
        };

        await AppendRecordAsync(record, cancellationToken);
        return record;
    }

    public async Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request, CancellationToken cancellationToken = default)
    {
        var preferences = await preferencesService.GetAsync(cancellationToken);
        var backupsDirectory = EnsureBackupDirectory(preferences.DefaultBackupRoot, "panel");
        var fileName = $"panel-config-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        // Panelin KENDİ durumu: adapanel.db (kullanıcı/2FA) + config/*.json (planlar, scope'lar, otomasyon...).
        // DataProtection ANAHTARLARI (keys/) ve sertifikalar BİLEREK DIŞARIDA — makineye bağlı/hassas, taşınamaz.
        var configDirectory = fileStore.ResolvePath("config");
        var dbPath = fileStore.ResolvePath("data/adapanel.db");
        var includedParts = new List<string>();

        await using (var zipStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            // adapanel.db (+ WAL/SHM) — çalışan panel kilitleyebilir → paylaşımlı okuma ile ekle.
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file))
                {
                    AddSharedFileToArchive(archive, file, $"data/{Path.GetFileName(file)}");
                    includedParts.Add(Path.GetFileName(file));
                }
            }

            // config/ tüm JSON durum dosyaları (backups.json dahil — zararsız).
            if (Directory.Exists(configDirectory))
            {
                AddDirectoryToArchive(archive, configDirectory, "config");
                includedParts.Add("config");
            }

            // Planlı görevler (\AdaPanel\ klasörü): Windows Görev Zamanlayıcı'da yaşarlar, panel dosyalarında DEĞİL —
            // ham görev XML'i (principal/tetikleyici/eylem birebir) yedeğe girer; panel geri yüklemesinde otomatik kaydedilir.
            var exportedTasks = await TryExportScheduledTasksAsync(cancellationToken);
            if (exportedTasks.Count > 0)
            {
                var index = new List<object>();
                for (var i = 0; i < exportedTasks.Count; i++)
                {
                    var entryName = $"tasks/{i:000}.xml";
                    AddTextEntry(archive, entryName, exportedTasks[i].Xml);
                    index.Add(new { name = exportedTasks[i].Name, entry = entryName });
                }

                AddTextEntry(archive, "tasks/index.json", System.Text.Json.JsonSerializer.Serialize(index, BackupJsonOptions));
                includedParts.Add($"tasks({exportedTasks.Count})");
            }

            // Tam-sunucu DR: IIS site envanteri (ad + fiziksel yol + havuz + bağlamalar). Geri yüklemede
            // eksik siteler bu envanterden yeniden kurulur; içerik site yedeklerinden döner. Best-effort.
            try
            {
                var sites = await iisSiteService.GetSitesAsync(cancellationToken);
                if (sites.Count > 0)
                {
                    AddTextEntry(archive, "iis/sites.json", System.Text.Json.JsonSerializer.Serialize(sites, BackupJsonOptions));
                    includedParts.Add($"iis-sites({sites.Count})");
                }
            }
            catch
            {
                // IIS okunamıyorsa (rol yok vb.) panel yedeği yine de oluşur.
            }

            var manifest = System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "panel-config",
                createdAtUtc = DateTime.UtcNow,
                parts = includedParts,
                note = "DataProtection keys and certificates are intentionally excluded (machine-bound)."
            }, BackupJsonOptions);
            AddTextEntry(archive, "manifest.json", manifest);
        }

        var target = ResolveTarget(request.TargetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);
        var remoteUri = await UploadToRemoteAsync(targetPath, fileName, target, cancellationToken);

        var record = new BackupRecordDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = fileName,
            Type = "panel",
            Source = "panel",
            Path = targetPath,
            Format = "zip",
            CreatedAtUtc = DateTime.UtcNow,
            Size = new FileInfo(targetPath).Length,
            Status = string.IsNullOrWhiteSpace(remoteUri) ? "created" : "created_remote_synced",
            TargetType = target.Type,
            RemoteUri = remoteUri
        };

        await AppendRecordAsync(record, cancellationToken);
        return record;
    }

    // Çalışan süreç kilitlese bile (ör. SQLite adapanel.db) paylaşımlı okuma ile zip'e ekler.
    private static void AddSharedFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        try
        {
            using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            source.CopyTo(entryStream);
        }
        catch (IOException)
        {
            // Kilitli/erişilemeyen dosyayı atla; yedek geri kalanıyla devam eder.
        }
    }

    private sealed record ScheduledTaskExport(string Name, string Xml);

    private sealed record ScheduledTaskIndexEntry(string Name, string Entry);

    // Planlı görevlerin ham XML dökümü (best-effort: dışa aktarılamazsa panel yedeği yine de oluşur).
    private static async Task<List<ScheduledTaskExport>> TryExportScheduledTasksAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string script = @"$list = Get-ScheduledTask -TaskPath '\AdaPanel\' -ErrorAction SilentlyContinue | ForEach-Object {
  [pscustomobject]@{ Name = $_.TaskName; Xml = [string](Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath) }
}
$list | ConvertTo-Json -Depth 3";
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return [];
            }

            using var document = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            ScheduledTaskExport[] items = root.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Array => System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskExport[]>(root) ?? [],
                System.Text.Json.JsonValueKind.Object => [System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskExport>(root)!],
                _ => []
            };

            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Xml))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // Panel yedeğindeki görevleri Görev Zamanlayıcı'ya geri kaydeder (XML: principal/tetikleyici birebir).
    private static async Task TryRestoreScheduledTasksAsync(string extractedRoot, CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(extractedRoot, "tasks", "index.json");
            if (!File.Exists(indexPath))
            {
                return;
            }

            var index = System.Text.Json.JsonSerializer.Deserialize<List<ScheduledTaskIndexEntry>>(
                await File.ReadAllTextAsync(indexPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

            foreach (var item in index)
            {
                var xmlPath = Path.Combine(extractedRoot, item.Entry.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(item.Name) || !File.Exists(xmlPath))
                {
                    continue;
                }

                // XML'i base64 taşı: tırnak/özel karakter kaçış derdi tamamen ortadan kalkar.
                var xml = await File.ReadAllTextAsync(xmlPath, cancellationToken);
                var xmlBase64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(xml));
                var script = $@"$xml = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{xmlBase64}'))
Register-ScheduledTask -TaskName '{item.Name.Replace("'", "''")}' -TaskPath '\AdaPanel\' -Xml $xml -Force | Out-Null";
                await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            }
        }
        catch
        {
            // Best-effort: bir görev geri yüklenemese de yedek çıkarma işlemi tamamlanır.
        }
    }

    /// <summary>
    /// Tam-sunucu DR: panel yedeğindeki IIS envanterinden (iis/sites.json) eksik siteleri yeniden kurar.
    /// Best-effort — site başına hata diğerlerini durdurmaz; sonuç, çıkarma klasörüne
    /// iis-restore-report.txt olarak yazılır (kullanıcı ne kurulup ne atlandığını görür).
    /// HTTPS bağlamaları bilerek kurulmaz (sertifika makineye bağlıdır; SSL ekranından yeniden verilir).
    /// </summary>
    private async Task TryRestoreIisSitesAsync(string extractedRoot, List<string> report, CancellationToken cancellationToken)
    {
        try
        {
            var snapshotPath = Path.Combine(extractedRoot, "iis", "sites.json");
            if (!File.Exists(snapshotPath))
            {
                return;
            }

            var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<AdaPanel.Core.Dtos.Sites.SiteDto>>(
                await File.ReadAllTextAsync(snapshotPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (snapshot.Count == 0)
            {
                return;
            }

            var existing = (await iisSiteService.GetSitesAsync(cancellationToken)).Select(site => site.Name);
            var missing = IisSnapshotRestore.GetMissingSites(snapshot, existing);
            report.Add($"Envanter: {snapshot.Count} site; eksik: {missing.Count}.");

            foreach (var site in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (create, extraBindings) = IisSnapshotRestore.ToProvisioningPlan(site);
                    if (!string.IsNullOrWhiteSpace(create.PhysicalPath))
                    {
                        Directory.CreateDirectory(create.PhysicalPath);
                    }

                    await iisSiteService.CreateSiteAsync(create, cancellationToken);
                    foreach (var binding in extraBindings)
                    {
                        try
                        {
                            await iisSiteService.AddBindingAsync(site.Name, binding, cancellationToken);
                        }
                        catch (Exception bindingException)
                        {
                            report.Add($"  {site.Name}: ek bağlama {binding.HostName}:{binding.Port} eklenemedi — {bindingException.Message}");
                        }
                    }

                    report.Add($"  {site.Name}: yeniden kuruldu ({1 + extraBindings.Count} http bağlama). İçerik için site yedeğini geri yükleyin; HTTPS'i SSL ekranından yeniden verin.");
                }
                catch (Exception siteException)
                {
                    report.Add($"  {site.Name}: KURULAMADI — {siteException.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            report.Add($"IIS envanteri geri yüklenemedi: {exception.Message}");
        }
    }

    private sealed class FtpUserSnapshot
    {
        public string UserName { get; set; } = string.Empty;
        public string HomePath { get; set; } = string.Empty;
        public string SiteName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Tam-sunucu DR: yedekteki config/ftp-users.json kayıtlarından FTP hesaplarını yeniden kurar.
    /// Windows yerel hesap PAROLALARI yedeğe giremez (dışa aktarılamaz) → hesap RASTGELE parolayla
    /// açılır ve rapora "parolayı FTP ekranından sıfırlayın" düşülür. Hesap zaten varsa parolasına
    /// dokunulmaz (CreateFtpUserAsync mevcut hesapta yalnız ACL/ana dizini tazeler — idempotent).
    /// </summary>
    private async Task TryRestoreFtpUsersAsync(string extractedRoot, List<string> report, CancellationToken cancellationToken)
    {
        try
        {
            var snapshotPath = Path.Combine(extractedRoot, "config", "ftp-users.json");
            if (!File.Exists(snapshotPath))
            {
                return;
            }

            var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<FtpUserSnapshot>>(
                await File.ReadAllTextAsync(snapshotPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (snapshot.Count == 0)
            {
                return;
            }

            report.Add($"FTP envanteri: {snapshot.Count} hesap.");
            foreach (var user in snapshot.Where(item => !string.IsNullOrWhiteSpace(item.UserName)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ftpService.CreateFtpUserAsync(new AdaPanel.Core.Dtos.Ftp.CreateFtpUserRequest
                    {
                        UserName = user.UserName,
                        Password = $"Dr{Guid.NewGuid():N}aA1",
                        SiteName = user.SiteName,
                        HomePath = user.HomePath
                    }, cancellationToken);
                    report.Add($"  FTP {user.UserName}: hazır — parolayı FTP ekranından sıfırlayın (parolalar yedeğe giremez).");
                }
                catch (Exception exception)
                {
                    report.Add($"  FTP {user.UserName}: KURULAMADI — {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            report.Add($"FTP envanteri geri yüklenemedi: {exception.Message}");
        }
    }

    private static async Task TryWriteRestoreReportAsync(string extractedRoot, List<string> report, CancellationToken cancellationToken)
    {
        if (report.Count == 0)
        {
            return;
        }

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(extractedRoot, "iis-restore-report.txt"), report, cancellationToken);
        }
        catch
        {
            // rapor yazılamazsa akış bozulmaz
        }
    }

    private const string StalwartTaskName = "AdaStalwart";

    /// <summary>
    /// Stalwart mail verisinin yedeği. RocksDB canlı kopyalanamayacağından servis
    /// (zamanlanmış görev) kısa süre durdurulur; zip alınır; her durumda yeniden başlatılır.
    /// </summary>
    public async Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request, CancellationToken cancellationToken = default)
    {
        var installDirectory = ResolveStalwartInstallDirectory();
        var dataDirectory = Path.Combine(installDirectory, "data");
        if (!Directory.Exists(dataDirectory))
        {
            throw new AppException("backup.mail_not_installed", 404, 404);
        }

        var preferences = await preferencesService.GetAsync(cancellationToken);
        var backupsDirectory = EnsureBackupDirectory(preferences.DefaultBackupRoot, "mail");
        var fileName = $"stalwart-mail-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);

        await StopStalwartAsync(installDirectory, cancellationToken);
        try
        {
            ZipFile.CreateFromDirectory(dataDirectory, targetPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        finally
        {
            await StartStalwartAsync(cancellationToken);
        }

        var target = ResolveTarget(request.TargetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);
        var remoteUri = await UploadToRemoteAsync(targetPath, fileName, target, cancellationToken);

        var record = new BackupRecordDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = fileName,
            Type = "mail",
            Source = "stalwart",
            Path = targetPath,
            Format = "zip",
            CreatedAtUtc = DateTime.UtcNow,
            Size = new FileInfo(targetPath).Length,
            Status = string.IsNullOrWhiteSpace(remoteUri) ? "created" : "created_remote_synced",
            TargetType = target.Type,
            RemoteUri = remoteUri
        };

        await AppendRecordAsync(record, cancellationToken);
        return record;
    }

    private async Task RestoreMailAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        var installDirectory = ResolveStalwartInstallDirectory();
        var dataDirectory = Path.Combine(installDirectory, "data");

        await StopStalwartAsync(installDirectory, cancellationToken);
        try
        {
            // Mevcut veri yana alınır; geri yükleme başarısız olursa elle dönülebilir.
            if (Directory.Exists(dataDirectory))
            {
                var sideline = dataDirectory + $".old-{DateTime.UtcNow:yyyyMMddHHmmss}";
                Directory.Move(dataDirectory, sideline);
            }

            // Yedek "data/..." girdileri içerir; kurulum köküne çıkarılır.
            ZipFile.ExtractToDirectory(record.Path, installDirectory, overwriteFiles: true);
        }
        finally
        {
            await StartStalwartAsync(cancellationToken);
        }
    }

    private static string ResolveStalwartInstallDirectory()
    {
        var root = Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\stalwart");
        if (!Directory.Exists(root))
        {
            throw new AppException("backup.mail_not_installed", 404, 404);
        }

        return root;
    }

    private static async Task StopStalwartAsync(string installDirectory, CancellationToken cancellationToken)
    {
        await ProcessRunner.RunAsync("schtasks.exe", $"/End /TN \"{StalwartTaskName}\"", cancellationToken: cancellationToken);

        // RocksDB kilitlerinin bırakıldığından emin olmak için dizinden çalışan süreçleri sonlandır.
        var prefix = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("stalwart"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
            }
            catch
            {
                // erişilemeyen/zaten çıkmış süreçler atlanır
            }
            finally
            {
                process.Dispose();
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }

    private static Task StartStalwartAsync(CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync("schtasks.exe", $"/Run /TN \"{StalwartTaskName}\"", cancellationToken: cancellationToken);

    public async Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default)
    {
        var backups = (await GetBackupsAsync(cancellationToken)).ToArray();
        var record = backups.FirstOrDefault(item => item.Id == request.BackupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        if (!File.Exists(record.Path))
        {
            throw new AppException("backup.not_found", 404, 404);
        }

        if (string.Equals(record.Type, "mail", StringComparison.OrdinalIgnoreCase))
        {
            await RestoreMailAsync(record, cancellationToken);
            record.Status = "restored";
            await UpsertRecordAsync(record, cancellationToken);
            return record;
        }

        // profesyonel panel benzeri tam-site yedeği (files/ + databases/ + config/ içeren birleşik .zip):
        // dosyaları sitenin köküne, veritabanlarını içe aktararak gerçek geri yükleme yap.
        if (string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase) &&
            IsCombinedSiteArchive(record.Path))
        {
            await RestoreCombinedSiteAsync(record, request.TargetPath, cancellationToken);
            record.Status = "restored";
            await UpsertRecordAsync(record, cancellationToken);
            return record;
        }

        if (string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            var destination = string.IsNullOrWhiteSpace(request.TargetPath)
                ? fileStore.EnsureDirectory($"restore/{record.Source}-{DateTime.UtcNow:yyyyMMddHHmmss}")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.TargetPath.Trim()));

            Directory.CreateDirectory(destination);
            ZipFile.ExtractToDirectory(record.Path, destination, overwriteFiles: true);

            // Panel yedeği: içindeki planlı görevleri (tasks/*.xml) Görev Zamanlayıcı'ya otomatik geri kaydet
            // + tam-sunucu DR: eksik IIS siteleri + FTP hesapları yeniden kurulur (rapor: iis-restore-report.txt).
            if (string.Equals(record.Type, "panel", StringComparison.OrdinalIgnoreCase))
            {
                await TryRestoreScheduledTasksAsync(destination, cancellationToken);
                var report = new List<string>();
                await TryRestoreIisSitesAsync(destination, report, cancellationToken);
                await TryRestoreFtpUsersAsync(destination, report, cancellationToken);
                await TryWriteRestoreReportAsync(destination, report, cancellationToken);
            }

            record.Status = "restored";
            record.Path = destination;
        }
        else if (string.Equals(record.Format, "sql", StringComparison.OrdinalIgnoreCase))
        {
            var sourceParts = record.Source.Split(':', 2);
            await databaseService.ImportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
            {
                Engine = sourceParts[0],
                DatabaseName = sourceParts.Length > 1 ? sourceParts[1] : string.Empty,
                FilePath = record.Path
            }, cancellationToken);
            record.Status = "restored";
        }

        await UpsertRecordAsync(record, cancellationToken);
        return record;
    }

    private const int MaxEntriesPerListing = 500;

    /// <summary>
    /// Zip yedeğinin içinde dizin bazlı gezinme: verilen önekin hemen altındaki
    /// klasörler ve dosyalar döner. Girdi adları '\\' veya '/' ayırıcılı olabilir.
    /// </summary>
    public async Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default)
    {
        var record = await FindZipRecordAsync(backupId, cancellationToken);
        var normalizedPrefix = NormalizeArchivePath(prefix ?? string.Empty);
        if (normalizedPrefix.Length > 0 && !normalizedPrefix.EndsWith('/'))
        {
            normalizedPrefix += "/";
        }

        var directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<BackupEntryDto>();
        var truncated = false;

        using var archive = ZipFile.OpenRead(record.Path);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeArchivePath(entry.FullName);
            if (!name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) || name.Length == normalizedPrefix.Length)
            {
                continue;
            }

            var rest = name[normalizedPrefix.Length..];
            var slashIndex = rest.IndexOf('/');
            if (slashIndex >= 0)
            {
                directories.Add(rest[..slashIndex]);
            }
            else if (files.Count < MaxEntriesPerListing)
            {
                files.Add(new BackupEntryDto
                {
                    Name = rest,
                    Path = name,
                    IsDirectory = false,
                    Size = entry.Length
                });
            }
            else
            {
                truncated = true;
            }
        }

        var entries = directories
            .Select(directory => new BackupEntryDto
            {
                Name = directory,
                Path = normalizedPrefix + directory + "/",
                IsDirectory = true
            })
            .Concat(files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            .Take(MaxEntriesPerListing)
            .ToArray();

        return new BackupEntriesDto
        {
            BackupId = record.Id,
            Prefix = normalizedPrefix,
            Entries = entries,
            Truncated = truncated || entries.Length >= MaxEntriesPerListing
        };
    }

    /// <summary>
    /// Seçilen arşiv öğelerini geri yükler. Hedef verilmemişse sitenin orijinal kökü
    /// kullanılır: yedek includeBaseDirectory ile alındığından girdiler kök klasör adını
    /// içerir; bu yüzden çıkarma, kökün ÜST dizinine göre yapılır.
    /// </summary>
    public async Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default)
    {
        var record = await FindZipRecordAsync(request.BackupId, cancellationToken);
        var items = request.Items
            .Select(NormalizeArchivePath)
            .Where(item => item.Length > 0)
            .ToArray();
        if (items.Length == 0)
        {
            throw new AppException("backup.items_required", 400, 400);
        }

        string targetRoot;
        if (!string.IsNullOrWhiteSpace(request.TargetPath))
        {
            targetRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.TargetPath.Trim()));
        }
        else
        {
            if (!string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException("backup.target_path_required", 400, 400);
            }

            AdaPanel.Core.Dtos.Sites.SiteDto site;
            try
            {
                site = await iisSiteService.GetSiteAsync(record.Source, cancellationToken);
            }
            catch (AppException)
            {
                // Site artık yoksa kullanıcı açık bir hedef yol vermelidir.
                throw new AppException("backup.target_path_required", 404, 404);
            }

            var applicationRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                ? site.PhysicalPath
                : site.ApplicationRootPath;
            targetRoot = Path.GetDirectoryName(Path.GetFullPath(applicationRoot))
                ?? throw new AppException("backup.target_path_required", 400, 400);
        }

        Directory.CreateDirectory(targetRoot);
        var rootWithSeparator = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var restored = 0;

        using var archive = ZipFile.OpenRead(record.Path);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeArchivePath(entry.FullName);
            if (name.Length == 0 || name.EndsWith('/'))
            {
                continue;
            }

            var matched = items.Any(item => item.EndsWith('/')
                ? name.StartsWith(item, StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, item, StringComparison.OrdinalIgnoreCase));
            if (!matched)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(targetRoot, name.Replace('/', Path.DirectorySeparatorChar)));
            // Zip-slip koruması: hedef her zaman kök altında kalmalı.
            if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException("backup.invalid_entry_path", 400, 400, name);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            restored++;
        }

        if (restored == 0)
        {
            throw new AppException("backup.items_not_found", 404, 404);
        }

        return new RestoreBackupItemsResultDto
        {
            RestoredCount = restored,
            TargetRoot = targetRoot
        };
    }

    private async Task<BackupRecordDto> FindZipRecordAsync(string backupId, CancellationToken cancellationToken)
    {
        var record = (await GetBackupsAsync(cancellationToken)).FirstOrDefault(item => item.Id == backupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        if (!string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("backup.not_browsable", 400, 400);
        }

        if (!File.Exists(record.Path))
        {
            throw new AppException("backup.not_found", 404, 404);
        }

        return record;
    }

    private static string NormalizeArchivePath(string value) =>
        value.Replace('\\', '/').TrimStart('/');

    public async Task<int> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var records = (await GetBackupsAsync(cancellationToken)).ToList();
        var removedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            // Süresi dolmuş yedekleri buda (yerel + off-site uzak kopya).
            if (record.CreatedAtUtc >= cutoff)
            {
                continue;
            }

            try
            {
                // Uzak rotasyon: off-site (S3/FTP/WebDAV) kopyayı kaydı silmeden ÖNCE sil (best-effort).
                if (remote is not null
                    && !string.IsNullOrWhiteSpace(record.RemoteUri)
                    && record.TargetType is "s3" or "ftp" or "webdav" or "http")
                {
                    await TryDeleteRemoteBackupAsync(record, remote, cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(record.Path) && File.Exists(record.Path))
                {
                    File.Delete(record.Path);
                }
                else if (!string.IsNullOrWhiteSpace(record.Path) && Directory.Exists(record.Path))
                {
                    Directory.Delete(record.Path, recursive: true);
                }

                removedIds.Add(record.Id);
            }
            catch (IOException)
            {
                // Dosya kilitliyse kaydı koru, bir sonraki budamada tekrar denenir.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (removedIds.Count > 0)
        {
            // ATOMİK: güncel listeden YALNIZ silinen ID'leri çıkar → eşzamanlı eklenen yeni kayıt (otomatik+elle
            // yedek aynı anda) KORUNUR. Dosya silme kilidin DIŞINDA yapıldı; burada yalnız kısa liste güncellemesi var.
            await fileStore.UpdateAsync(
                IndexPath,
                Array.Empty<BackupRecordDto>(),
                existing => existing.Where(item => !removedIds.Contains(item.Id)).ToArray(),
                cancellationToken);
        }

        return removedIds.Count;
    }

    public async Task DeleteBackupAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var record = (await GetBackupsAsync(cancellationToken)).FirstOrDefault(item => item.Id == backupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        // Yerel dosya kilitliyse kayıt KORUNUR (kör kayıt kaybı olmasın); kullanıcı gerçek nedeni görür.
        try
        {
            if (!string.IsNullOrWhiteSpace(record.Path) && File.Exists(record.Path))
            {
                File.Delete(record.Path);
            }
            else if (!string.IsNullOrWhiteSpace(record.Path) && Directory.Exists(record.Path))
            {
                Directory.Delete(record.Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AppException("backup.delete_failed", 409, 409, exception.Message);
        }

        // ATOMİK: yalnız bu ID çıkarılır → eşzamanlı eklenen yeni kayıtlar (otomatik yedek) korunur.
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing => existing.Where(item => item.Id != backupId).ToArray(),
            cancellationToken);
    }

    // Off-site yedeğin UZAK kopyasını siler (uzak rotasyon). Best-effort: hata/yoksa sessizce geçer, budamayı BOZMAZ.
    private async Task TryDeleteRemoteBackupAsync(BackupRecordDto record, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(record.RemoteUri, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            switch (record.TargetType)
            {
                case "ftp":
                    await DeleteFtpAsync(uri, remote, cancellationToken);
                    break;
                case "s3" when !string.IsNullOrWhiteSpace(remote.UserName):
                    await DeleteS3SignedAsync(uri, remote, cancellationToken);
                    break;
                case "webdav":
                case "http":
                    await DeleteHttpAsync(uri, remote, cancellationToken);
                    break;
                // "s3" kimliksiz (presigned URL) → imza atılamaz, silinemez; atlanır.
            }
        }
        catch
        {
            // Best-effort: uzak silme başarısızsa yerel budama yine sürer (kayıt yine kaldırılır; kalıcı uzak
            // yetim yalnız erişim/kimlik sorununda olur ve nadirdir).
        }
    }

    // S3 (SigV4 imzalı) DELETE. Boş gövde → EmptyPayloadSha256. Olmayan nesnede de 204 (idempotent).
    private async Task DeleteS3SignedAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var region = string.IsNullOrWhiteSpace(remote.Region) ? "us-east-1" : remote.Region;
        var (authorization, amzDate, contentSha) = S3RequestSigner.Sign(
            "DELETE", uri, remote.UserName, remote.Password, region, DateTimeOffset.UtcNow, S3RequestSigner.EmptyPayloadSha256);

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha);
        using var response = await client.SendAsync(request, cancellationToken);
    }

    // WebDAV/HTTP DELETE (basic auth). 404/410 = zaten yok → sorun değil.
    private async Task DeleteHttpAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        if (!string.IsNullOrWhiteSpace(remote.UserName))
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{remote.UserName}:{remote.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        using var response = await client.SendAsync(request, cancellationToken);
    }

#pragma warning disable SYSLIB0014
    // FTP DELETE (DELE). Kimlik uzak seçeneklerden; RemoteUri temizlenmiş (kimliksiz) olduğundan buradan verilir.
    private static async Task DeleteFtpAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = WebRequestMethods.Ftp.DeleteFile;
        request.Credentials = new NetworkCredential(remote.UserName, remote.Password);
        request.UsePassive = true;
        request.KeepAlive = false;
        using var registration = cancellationToken.Register(() => request.Abort());
        using var response = (FtpWebResponse)await request.GetResponseAsync();
    }
#pragma warning restore SYSLIB0014

    // ATOMİK ekleme: arka plan otomatik yedek + kullanıcı elle yedek AYNI ANDA çalışırsa (UpdateAsync dosya-başına
    // kilit tutar) kayıt kaybolmaz. Eski Read+Write deseni "son yazan kazanır" → kayıp yedek kaydı veriyordu.
    private async Task AppendRecordAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing => existing.Append(record).ToArray(),
            cancellationToken);
    }

    private async Task UpsertRecordAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing =>
            {
                var list = existing.ToList();
                var index = list.FindIndex(item => item.Id == record.Id);
                if (index >= 0)
                {
                    list[index] = record;
                }
                else
                {
                    list.Add(record);
                }

                return list.ToArray();
            },
            cancellationToken);
    }

    public async Task<BackupRemoteTestResultDto> TestRemoteTargetAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default)
    {
        var targetType = (request.TargetType ?? "local").Trim().ToLowerInvariant();
        if (targetType is "local" or "")
        {
            // Yerel hedef için ağ testi yok; her zaman "tamam".
            return new BackupRemoteTestResultDto { Success = true, Message = "backup.remote_test.local" };
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"adapanel-conn-test-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(
                tempFile,
                $"AdaPanel off-site backup connection test {DateTime.UtcNow:O}",
                cancellationToken);

            var target = ResolveTarget(targetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);
            // Sabit ad: her test aynı dosyayı üzerine yazar → hedefte birikme olmaz.
            await UploadToRemoteAsync(tempFile, "adapanel-connection-test.txt", target, cancellationToken);
            return new BackupRemoteTestResultDto { Success = true, Message = "backup.remote_test.ok" };
        }
        catch (AppException exception)
        {
            return new BackupRemoteTestResultDto { Success = false, Message = exception.MessageKey, Details = exception.Details };
        }
        catch (Exception exception)
        {
            return new BackupRemoteTestResultDto { Success = false, Message = "backup.remote_test.failed", Details = exception.Message };
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
                // geçici dosya temizliği best-effort
            }
        }
    }

    private async Task<string> UploadToRemoteAsync(
        string localPath,
        string fileName,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Type == "local")
        {
            return string.Empty;
        }

        if (target.Uri is null)
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        try
        {
            if (target.Type == "ftp")
            {
                var ftpUri = AppendFileNameIfDirectory(target.Uri, fileName);
                await UploadFtpAsync(localPath, ftpUri, target, cancellationToken);
                return SanitizeUri(ftpUri);
            }

            // Erişim anahtarı verilmiş S3: SigV4 imzalı PUT (gerçek S3 / MinIO / R2 / Wasabi).
            // Anahtarsız S3: presigned URL'e düz PUT (geri uyumluluk).
            if (target.Type == "s3" && !string.IsNullOrWhiteSpace(target.UserName))
            {
                var s3Uri = AppendFileNameIfDirectory(target.Uri, fileName);
                await UploadS3SignedAsync(localPath, s3Uri, target, cancellationToken);
                return SanitizeUri(s3Uri);
            }

            if (target.Type is "s3" or "webdav" or "http")
            {
                var httpUri = target.Type == "webdav"
                    ? AppendFileNameIfDirectory(target.Uri, fileName)
                    : target.Uri;
                await UploadHttpPutAsync(localPath, httpUri, target, cancellationToken);
                return SanitizeUri(httpUri);
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppException("backup.remote_upload_failed", 502, 500, BuildDiagnosticDetails(exception));
        }

        throw new AppException("backup.remote_target_invalid", 400, 400);
    }

    private async Task UploadS3SignedAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var region = string.IsNullOrWhiteSpace(target.Region) ? "us-east-1" : target.Region;
        var (authorization, amzDate, contentSha) = S3RequestSigner.Sign(
            "PUT",
            uri,
            target.UserName,
            target.Password,
            region,
            DateTimeOffset.UtcNow,
            S3RequestSigner.UnsignedPayload);

        var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AppException(
                "backup.remote_upload_failed",
                (int)response.StatusCode,
                502,
                $"Url={SanitizeUri(uri)}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 2000)}");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private async Task UploadHttpPutAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = content
        };

        var credential = ResolveCredential(target, uri);
        if (credential is not null)
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException(
                "backup.remote_upload_failed",
                (int)response.StatusCode,
                502,
                $"Url={SanitizeUri(uri)}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

#pragma warning disable SYSLIB0014
    private static async Task UploadFtpAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != "ftp")
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var credential = ResolveCredential(target, uri)
            ?? throw new AppException("backup.remote_target_invalid", 400, 400);

        await EnsureFtpDirectoriesAsync(uri, credential);

        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = WebRequestMethods.Ftp.UploadFile;
        request.UseBinary = true;
        request.Credentials = credential;
        request.ContentLength = new FileInfo(localPath).Length;

        await using (var source = File.OpenRead(localPath))
        await using (var destination = await request.GetRequestStreamAsync())
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        using var response = (FtpWebResponse)await request.GetResponseAsync();
        if (response.StatusCode is not (FtpStatusCode.ClosingData or FtpStatusCode.CommandOK))
        {
            throw new AppException("backup.remote_upload_failed", (int)response.StatusCode, 502);
        }
    }

    private static async Task EnsureFtpDirectoriesAsync(Uri uri, NetworkCredential credential)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length <= 1)
        {
            return;
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };

        for (var index = 0; index < segments.Length - 1; index++)
        {
            builder.Path = CombineUriPath(builder.Path, segments[index]);
            var request = (FtpWebRequest)WebRequest.Create(builder.Uri);
            request.Method = WebRequestMethods.Ftp.MakeDirectory;
            request.Credentials = credential;

            try
            {
                using var response = (FtpWebResponse)await request.GetResponseAsync();
            }
            catch (WebException exception) when ((exception.Response as FtpWebResponse)?.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable)
            {
                // Existing directories are reported as 550 by many FTP servers.
            }
        }
    }
#pragma warning restore SYSLIB0014

    private static BackupTarget ResolveTarget(string? targetType, string? remoteUrl, string? remoteUserName, string? remotePassword, string? remoteRegion = null)
    {
        var normalizedType = string.IsNullOrWhiteSpace(targetType)
            ? "local"
            : targetType.Trim().ToLowerInvariant();

        if (normalizedType is "none" or "")
        {
            normalizedType = "local";
        }

        if (normalizedType == "local")
        {
            return new BackupTarget("local", null, string.Empty, string.Empty, string.Empty);
        }

        if (normalizedType is "s3-compatible")
        {
            normalizedType = "s3";
        }

        if (normalizedType is "drive")
        {
            normalizedType = "webdav";
        }

        if (normalizedType is not ("ftp" or "s3" or "webdav" or "http"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var remoteUrlText = remoteUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(remoteUrlText, UriKind.Absolute, out var uri))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        if (normalizedType == "ftp" && uri.Scheme != "ftp")
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        if ((normalizedType is "s3" or "webdav" or "http") && uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        return new BackupTarget(
            normalizedType,
            uri,
            remoteUserName?.Trim() ?? string.Empty,
            remotePassword ?? string.Empty,
            remoteRegion?.Trim() ?? string.Empty);
    }

    private static NetworkCredential? ResolveCredential(BackupTarget target, Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(target.UserName))
        {
            return new NetworkCredential(target.UserName, target.Password);
        }

        if (string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return null;
        }

        var parts = uri.UserInfo.Split(':', 2);
        return new NetworkCredential(
            Uri.UnescapeDataString(parts[0]),
            parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }

    private static Uri AppendFileNameIfDirectory(Uri uri, string fileName)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var lastSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lastSegment) && Path.HasExtension(lastSegment))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Path = CombineUriPath(path, fileName),
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri;
    }

    private static string CombineUriPath(string left, string right)
    {
        var normalizedLeft = string.IsNullOrWhiteSpace(left) ? "/" : left;
        if (!normalizedLeft.EndsWith('/'))
        {
            normalizedLeft += "/";
        }

        return normalizedLeft + Uri.EscapeDataString(right);
    }

    private static string SanitizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.ToString();
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        var details = exception.ToString();
        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private static string EnsureBackupDirectory(string root, string category)
    {
        var path = Path.Combine(Environment.ExpandEnvironmentVariables(root), category);
        Directory.CreateDirectory(path);
        return path;
    }

    // Bir dizini tüm alt içeriğiyle arşive verilen önek altında ekler (örn. "files/...").
    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string entryPrefix)
    {
        var root = Path.GetFullPath(sourceDirectory);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                archive.CreateEntryFromFile(file, $"{entryPrefix}/{relative}", CompressionLevel.Optimal);
            }
            catch (IOException)
            {
                // Kilitli/erişilemeyen dosyayı atla; yedek geri kalanıyla devam eder.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
        writer.Write(content);
    }

    // Birleşik tam-site arşivi mi? (manifest.json + files/ öneki olan yeni format).
    private static bool IsCombinedSiteArchive(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return archive.Entries.Any(entry =>
                entry.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("databases/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private async Task RestoreCombinedSiteAsync(BackupRecordDto record, string? targetPath, CancellationToken cancellationToken)
    {
        // Hedef: belirtilmişse o yol, değilse sitenin gerçek fiziksel kökü (canlı siteyi geri yükle).
        string siteRoot;
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            siteRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(targetPath.Trim()));
        }
        else
        {
            var site = await iisSiteService.GetSiteAsync(record.Source, cancellationToken);
            siteRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        }

        Directory.CreateDirectory(siteRoot);

        using var archive = ZipFile.OpenRead(record.Path);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1) files/* -> site köküne ("files/" öneki sıyrılarak).
            if (entry.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // dizin girdisi
                }

                var relative = entry.FullName["files/".Length..].Replace('/', Path.DirectorySeparatorChar);
                var destinationPath = Path.GetFullPath(Path.Combine(siteRoot, relative));

                // Zip-slip koruması: hedef site kökünün dışına çıkamaz.
                if (!destinationPath.StartsWith(Path.GetFullPath(siteRoot), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
            // 2) databases/*.sql -> ilgili motora içe aktar.
            else if (entry.FullName.StartsWith("databases/", StringComparison.OrdinalIgnoreCase) &&
                     entry.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(entry.Name))
            {
                var fileName = Path.GetFileNameWithoutExtension(entry.Name); // "engine-dbname"
                var split = fileName.Split('-', 2);
                if (split.Length != 2)
                {
                    continue;
                }

                var tempDump = Path.Combine(Path.GetTempPath(), $"adabk-restore-{Guid.NewGuid():N}.sql");
                try
                {
                    entry.ExtractToFile(tempDump, overwrite: true);
                    await databaseService.ImportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
                    {
                        Engine = split[0],
                        DatabaseName = split[1],
                        FilePath = tempDump
                    }, cancellationToken);
                }
                catch
                {
                    // Tek bir DB içe aktarımı başarısızsa diğerleriyle/dosyalarla devam et.
                }
                finally
                {
                    if (File.Exists(tempDump))
                    {
                        File.Delete(tempDump);
                    }
                }
            }
        }
    }

    private sealed record BackupTarget(string Type, Uri? Uri, string UserName, string Password, string Region);
}
