using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Php;

public sealed class PhpService(
    AdaPanelDbContext dbContext,
    IComponentBootstrapper componentBootstrapper,
    IConcurrencyLock concurrencyLock,
    IIisSiteService iisSiteService,
    ILogger<PhpService> logger) : IPhpService
{
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";
    private const string PhpHandlerPrefix = "AdaPanel.Php.";

    public async Task<IReadOnlyCollection<PhpVersionDto>> GetInstalledVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = await dbContext.InstalledComponents
            .Where(component => component.ComponentId.StartsWith("php-"))
            .OrderBy(component => component.DisplayName)
            .ToArrayAsync(cancellationToken);

        using var serverManager = TryCreateServerManager();

        return versions.Select(component =>
        {
            var executablePath = ResolvePhpExecutablePath(component.InstallDir);
            var isFastCgiRegistered = false;
            if (serverManager is not null && File.Exists(executablePath))
            {
                try
                {
                    isFastCgiRegistered = IsFastCgiRegistered(serverManager, executablePath);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "FastCGI registration could not be read for PHP component {ComponentId}.",
                        component.ComponentId);
                }
            }

            return new PhpVersionDto
            {
                ComponentId = component.ComponentId,
                DisplayName = component.DisplayName,
                Version = component.Version,
                InstallDir = component.InstallDir,
                ExecutablePath = executablePath,
                Status = component.Status.ToString(),
                IsFastCgiRegistered = isFastCgiRegistered,
                InstalledAtUtc = component.InstalledAtUtc
            };
        }).ToArray();
    }

    public async Task<ComponentInstallResultDto> InstallVersionAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        EnsurePhpComponentId(componentId);

        var result = await componentBootstrapper.InstallAsync(componentId, actorUserId, ipAddress, cancellationToken);
        try
        {
            await RegisterFastCgiAsync(componentId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkPhpConfigurationFailedAsync(componentId, exception, cancellationToken);
            throw;
        }

        return new ComponentInstallResultDto
        {
            ComponentId = result.ComponentId,
            DisplayName = result.DisplayName,
            Version = result.Version,
            Status = result.Status,
            InstallDir = result.InstallDir,
            ServiceName = result.ServiceName,
            GeneratedPassword = result.GeneratedPassword,
            Message = "php.install.success"
        };
    }

    public async Task EnsureExtensionsAsync(string componentId, IEnumerable<string> extensions, CancellationToken cancellationToken = default)
    {
        var component = await GetInstalledPhpComponentAsync(componentId, cancellationToken);
        var phpIniPath = Path.Combine(component.InstallDir, "php.ini");
        if (!File.Exists(phpIniPath))
        {
            return; // php.ini yoksa (beklenmedik) sessizce geç; FastCGI kaydı yine yapılır.
        }

        var extensionDirectory = Path.Combine(component.InstallDir, "ext");
        var lines = (await File.ReadAllLinesAsync(phpIniPath, cancellationToken)).ToList();
        var changed = false;
        foreach (var extension in extensions)
        {
            // DLL yoksa atla (her PHP derlemesi her eklentiyi getirmez); varsa etkinleştir.
            if (!File.Exists(Path.Combine(extensionDirectory, $"php_{extension}.dll")))
            {
                continue;
            }

            if (EnablePhpIniExtension(lines, extension))
            {
                changed = true;
            }
        }

        if (changed)
        {
            await File.WriteAllLinesAsync(phpIniPath, lines, cancellationToken);
        }
    }

    // php.ini'de bir eklentiyi etkinleştirir: zaten aktifse false; yorumluysa açar / yoksa ekler → true.
    private static bool EnablePhpIniExtension(List<string> lines, string extension)
    {
        static string Value(string normalized) => normalized["extension=".Length..].Trim().Trim('"');
        bool Matches(string text)
        {
            var normalized = text.Replace(" ", string.Empty);
            if (!normalized.StartsWith("extension=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var value = Value(normalized);
            return string.Equals(value, extension, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, $"php_{extension}.dll", StringComparison.OrdinalIgnoreCase);
        }

        // Zaten aktif (yorumsuz) bir satır varsa dokunma.
        if (lines.Any(line => !line.TrimStart().StartsWith(';') && Matches(line.Trim())))
        {
            return false;
        }

        // Yorumlu bir satır varsa onu aç.
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(';') && Matches(trimmed.TrimStart(';').TrimStart()))
            {
                lines[i] = $"extension={extension}";
                return true;
            }
        }

        lines.Add($"extension={extension}");
        return true;
    }

    public async Task RegisterFastCgiAsync(string componentId, CancellationToken cancellationToken = default)
    {
        var component = await GetInstalledPhpComponentAsync(componentId, cancellationToken);
        var executablePath = ResolvePhpExecutablePath(component.InstallDir);
        if (!File.Exists(executablePath))
        {
            throw new AppException("php.executable_not_found", 404, 404);
        }

        try
        {
            await concurrencyLock.RunExclusiveAsync(() =>
            {
                using var serverManager = CreateServerManager();
                EnsureFastCgiApplication(serverManager, executablePath);
                InvokeMethod(serverManager, "CommitChanges");
                return Task.CompletedTask;
            }, cancellationToken);

            using var verificationManager = CreateServerManager();
            if (!IsFastCgiRegistered(verificationManager, executablePath))
            {
                throw new AppException(
                    "php.configuration_failed",
                    500,
                    500,
                    $"FastCGI registration was not persisted for '{executablePath}'.");
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var mappedException = MapPhpConfigurationException(exception);
            logger.LogError(
                exception,
                "PHP FastCGI registration failed for {ComponentId}. MappedMessageKey={MessageKey}",
                componentId,
                mappedException.MessageKey);
            throw mappedException;
        }
    }

    private async Task MarkPhpConfigurationFailedAsync(
        string componentId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken);
        if (component is null)
        {
            return;
        }

        component.Status = ComponentInstallStatus.Failed;
        component.LastError = exception is AppException appException &&
                              !string.IsNullOrWhiteSpace(appException.Details)
            ? $"{appException.MessageKey}\r\n{appException.Details}"
            : exception.Message;
        component.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SitePhpAssignmentDto> SetSitePhpVersionAsync(
        string siteName,
        SetSitePhpVersionRequest request,
        CancellationToken cancellationToken = default)
    {
        var component = await GetInstalledPhpComponentAsync(request.ComponentId, cancellationToken);
        var executablePath = ResolvePhpExecutablePath(component.InstallDir);
        if (!File.Exists(executablePath))
        {
            throw new AppException("php.executable_not_found", 404, 404);
        }

        var handlerName = BuildHandlerName(component.ComponentId);

        try
        {
            var assignment = await concurrencyLock.RunExclusiveAsync(() =>
            {
                using var serverManager = CreateServerManager();
                EnsureSiteExists(serverManager, siteName);
                EnsureFastCgiApplication(serverManager, executablePath);
                SetSitePhpHandler(serverManager, siteName, executablePath, handlerName);
                InvokeMethod(serverManager, "CommitChanges");

                return Task.FromResult(new SitePhpAssignmentDto
                {
                    SiteName = siteName,
                    ComponentId = component.ComponentId,
                    DisplayName = component.DisplayName,
                    Version = component.Version,
                    ExecutablePath = executablePath,
                    HandlerName = handlerName,
                    IsFastCgiRegistered = true
                });
            }, cancellationToken);

            // B4 (Plesk gibi): PHP atanan siteye .user.ini YOKsa izolasyonlu (open_basedir AÇIK) varsayılan
            // yaz. Mevcut .user.ini'ye dokunulmaz → mevcut siteler etkilenmez. Best-effort.
            await EnsureDefaultUserIniAsync(siteName, cancellationToken);
            return assignment;
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var mappedException = MapPhpConfigurationException(exception);
            logger.LogError(
                exception,
                "PHP assignment failed for site {SiteName} and component {ComponentId}. MappedMessageKey={MessageKey}",
                siteName,
                request.ComponentId,
                mappedException.MessageKey);
            throw mappedException;
        }
    }

    public async Task<SitePhpSettingsDto> GetSitePhpSettingsAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var dto = new SitePhpSettingsDto { SiteName = site.Name };
        var iniPath = Path.Combine(site.PhysicalPath, ".user.ini");

        if (File.Exists(iniPath))
        {
            dto.Configured = true;
            var values = ParseUserIni(await File.ReadAllLinesAsync(iniPath, cancellationToken));
            if (values.TryGetValue("memory_limit", out var ml)) dto.MemoryLimit = ml;
            if (values.TryGetValue("max_execution_time", out var met) && int.TryParse(met, out var metVal)) dto.MaxExecutionTime = metVal;
            if (values.TryGetValue("upload_max_filesize", out var umf)) dto.UploadMaxFilesize = umf;
            if (values.TryGetValue("post_max_size", out var pms)) dto.PostMaxSize = pms;
            if (values.TryGetValue("display_errors", out var de))
            {
                dto.DisplayErrors = de.Equals("On", StringComparison.OrdinalIgnoreCase) || de == "1";
            }

            // open_basedir satırı varsa izolasyon açık demektir.
            dto.OpenBasedirEnabled = values.ContainsKey("open_basedir");
        }

        return dto;
    }

    // IIS app pool kimliği (IIS_IUSRS) bir dizine YAZABİLSİN (open_basedir açıkken PHP oturum dosyaları
    // site-içi tmp'e yazılır). Best-effort; icacls yoksa/başarısızsa sessiz geçer (ayar yine yazılır).
    private static async Task GrantAppPoolWriteAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync(
                "icacls.exe",
                $"\"{directory}\" /grant \"IIS_IUSRS:(OI)(CI)M\" /T /C /Q",
                cancellationToken: cancellationToken);
        }
        catch
        {
            // best-effort
        }
    }

    // open_basedir + session.save_path satırlarını .user.ini içeriğine ekler. KÖK = ApplicationRootPath
    // (Laravel'de çerçeve kodu public'in ÜSTÜndedir; panel gerçek proje kökünü bilir), boşsa PhysicalPath.
    // Oturumlar site-içi tmp'e; YÜKLEMELER OS temp'inde kalır (upload_tmp_dir INI_SYSTEM) → OS temp'i de
    // izinli yola dahil ederiz, böylece açınca yükleme/oturum KIRILMAZ.
    private async Task AppendOpenBasedirAsync(StringBuilder content, AdaPanel.Core.Dtos.Sites.SiteDto site, CancellationToken cancellationToken)
    {
        var openBaseRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(site.ApplicationRootPath)
            ? site.PhysicalPath
            : site.ApplicationRootPath);
        var sessionDir = Path.Combine(openBaseRoot, "tmp");
        Directory.CreateDirectory(sessionDir);
        await GrantAppPoolWriteAsync(sessionDir, cancellationToken);

        var osTemp = Path.GetFullPath(Environment.ExpandEnvironmentVariables("%SystemRoot%\\Temp"));
        var allowed = new[] { openBaseRoot, sessionDir, osTemp }
            .Select(path => path.Replace('\\', '/').TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        content.AppendLine($"open_basedir = \"{string.Join(';', allowed)}\"");
        content.AppendLine($"session.save_path = \"{sessionDir.Replace('\\', '/')}\"");
    }

    // B4 (Plesk gibi): PHP atanan siteye .user.ini YOKsa, izolasyonlu (open_basedir AÇIK) varsayılan yazar.
    // Mevcut .user.ini'ye DOKUNMAZ (kullanıcı/önceki ayar korunur). Best-effort: hata atama akışını bozmaz.
    private async Task EnsureDefaultUserIniAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            if (string.IsNullOrWhiteSpace(site.PhysicalPath) || !Directory.Exists(site.PhysicalPath))
            {
                return;
            }

            var iniPath = Path.Combine(site.PhysicalPath, ".user.ini");
            if (File.Exists(iniPath))
            {
                return;
            }

            var content = new StringBuilder();
            content.AppendLine("; AdaPanel - site PHP ayarları (.user.ini, per-dizin)");
            content.AppendLine("memory_limit = 256M");
            content.AppendLine("max_execution_time = 120");
            content.AppendLine("upload_max_filesize = 64M");
            content.AppendLine("post_max_size = 64M");
            content.AppendLine("display_errors = Off");
            await AppendOpenBasedirAsync(content, site, cancellationToken);
            await File.WriteAllTextAsync(iniPath, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Varsayılan .user.ini (open_basedir) yazılamadı: {Site}", siteName);
        }
    }

    public async Task<SitePhpSettingsDto> UpdateSitePhpSettingsAsync(
        string siteName,
        UpdateSitePhpSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        if (string.IsNullOrWhiteSpace(site.PhysicalPath) || !Directory.Exists(site.PhysicalPath))
        {
            throw new AppException("php.site_root_missing", 404, 404);
        }

        var content = new StringBuilder();
        content.AppendLine("; AdaPanel - site PHP ayarları (.user.ini, per-dizin)");
        content.AppendLine($"memory_limit = {SanitizeIniValue(request.MemoryLimit)}");
        content.AppendLine($"max_execution_time = {Math.Clamp(request.MaxExecutionTime, 0, 86400)}");
        content.AppendLine($"upload_max_filesize = {SanitizeIniValue(request.UploadMaxFilesize)}");
        content.AppendLine($"post_max_size = {SanitizeIniValue(request.PostMaxSize)}");
        content.AppendLine($"display_errors = {(request.DisplayErrors ? "On" : "Off")}");

        if (request.OpenBasedirEnabled)
        {
            await AppendOpenBasedirAsync(content, site, cancellationToken);
        }

        var iniPath = Path.Combine(site.PhysicalPath, ".user.ini");
        await File.WriteAllTextAsync(iniPath, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        // KRİTİK: PHP upload limitini yükseltmek tek başına yetmez — IIS maxAllowedContentLength varsayılanı
        // ~30MB'de "413 Request Entity Too Large" verir. post_max_size'ı baz alıp IIS limitini de yükselt
        // ki panel ayarı uçtan uca çalışsın (phpMyAdmin SQL import dahil).
        var uploadBytes = ParsePhpSize(request.PostMaxSize);
        if (uploadBytes > 0)
        {
            AdaPanel.Infrastructure.Iis.WebAppPublisher.SetMaxAllowedContentLength(site.PhysicalPath, uploadBytes);
        }

        // max_execution_time panelden değiştirilebilir; FastCGI requestTimeout/activityTimeout'u da en az o kadar
        // yükselt ki uzun işlemler (büyük import vb.) "500 timeout (0x80070102)" vermesin. Raise-only, global FastCGI.
        await RaiseFastCgiTimeoutAsync(FastCgiSecondsFor(request.MaxExecutionTime), cancellationToken);

        return await GetSitePhpSettingsAsync(siteName, cancellationToken);
    }

    public async Task<SitePhpSettingsDto> GetGlobalPhpSettingsAsync(CancellationToken cancellationToken = default)
    {
        var component = await GetLatestInstalledPhpAsync(cancellationToken);
        var dto = new SitePhpSettingsDto { SiteName = "global" };
        if (component is null)
        {
            return dto;
        }

        var iniPath = Path.Combine(component.InstallDir, "php.ini");
        if (File.Exists(iniPath))
        {
            dto.Configured = true;
            var values = ParseUserIni(await File.ReadAllLinesAsync(iniPath, cancellationToken));
            if (values.TryGetValue("memory_limit", out var ml)) dto.MemoryLimit = ml;
            if (values.TryGetValue("max_execution_time", out var met) && int.TryParse(met, out var metVal)) dto.MaxExecutionTime = metVal;
            if (values.TryGetValue("upload_max_filesize", out var umf)) dto.UploadMaxFilesize = umf;
            if (values.TryGetValue("post_max_size", out var pms)) dto.PostMaxSize = pms;
        }

        return dto;
    }

    public async Task<SitePhpSettingsDto> UpdateGlobalPhpSettingsAsync(
        UpdateSitePhpSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var component = await GetLatestInstalledPhpAsync(cancellationToken)
            ?? throw new AppException("php.version_not_installed", 404, 404);
        var iniPath = Path.Combine(component.InstallDir, "php.ini");
        if (!File.Exists(iniPath))
        {
            throw new AppException("php.version_not_installed", 404, 404);
        }

        var lines = (await File.ReadAllLinesAsync(iniPath, cancellationToken)).ToList();
        SetIniDirective(lines, "memory_limit", SanitizeIniValue(request.MemoryLimit));
        SetIniDirective(lines, "max_execution_time", Math.Clamp(request.MaxExecutionTime, 0, 86400).ToString());
        SetIniDirective(lines, "upload_max_filesize", SanitizeIniValue(request.UploadMaxFilesize));
        SetIniDirective(lines, "post_max_size", SanitizeIniValue(request.PostMaxSize));
        await File.WriteAllLinesAsync(iniPath, lines, cancellationToken);

        // Süre (max_execution_time) değişince FastCGI requestTimeout'u da yükselt (yoksa büyük import 500 timeout verir).
        await RaiseFastCgiTimeoutAsync(FastCgiSecondsFor(request.MaxExecutionTime), cancellationToken);

        // php-cgi süreçlerini yenile ki yeni php.ini HEMEN uygulansın (FastCGI php.ini'yi süreç başında okur). Best-effort.
        try
        {
            await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync("taskkill.exe", "/f /im php-cgi.exe", cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "php-cgi yeniden başlatılamadı; global PHP ayarı bir sonraki havuz dönüşünde uygulanır.");
        }

        return await GetGlobalPhpSettingsAsync(cancellationToken);
    }

    public async Task<RedisExtensionResultDto> EnableRedisExtensionAsync(CancellationToken cancellationToken = default)
    {
        var component = await GetLatestInstalledPhpAsync(cancellationToken)
            ?? throw new AppException("php.not_installed", 404, 404);

        var phpCli = Path.Combine(component.InstallDir, "php.exe");
        if (!File.Exists(phpCli))
        {
            phpCli = ResolvePhpExecutablePath(component.InstallDir); // php-cgi.exe yedeği
        }
        if (!File.Exists(phpCli))
        {
            throw new AppException("php.executable_not_found", 404, 404);
        }

        // Zaten yüklüyse tekrar indirme.
        if (await PhpHasModuleAsync(phpCli, "redis", cancellationToken))
        {
            return new RedisExtensionResultDto { Enabled = true, Message = "already" };
        }

        var (minor, ts, arch) = await DetectPhpBuildAsync(phpCli, cancellationToken);
        var vs = Version.TryParse(minor + ".0", out var parsed) && parsed >= new Version(8, 4) ? "vs17" : "vs16";
        const string redisVersion = "6.1.0";
        var fileName = $"php_redis-{redisVersion}-{minor}-{ts}-{vs}-{arch}.zip";
        var url = $"https://downloads.php.net/~windows/pecl/releases/redis/{redisVersion}/{fileName}";

        var extDir = Path.Combine(component.InstallDir, "ext");
        Directory.CreateDirectory(extDir);
        var dllPath = Path.Combine(extDir, "php_redis.dll");
        var tempZip = Path.Combine(Path.GetTempPath(), $"adaredis-{Guid.NewGuid():N}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"adaredis-{Guid.NewGuid():N}");

        var script =
            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; " +
            $"Invoke-WebRequest -Uri '{url}' -OutFile '{tempZip.Replace("'", "''")}' -UseBasicParsing; " +
            $"Expand-Archive -Path '{tempZip.Replace("'", "''")}' -DestinationPath '{tempDir.Replace("'", "''")}' -Force";
        var download = await AdaPanel.Infrastructure.Support.ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        var sourceDll = Directory.Exists(tempDir)
            ? Directory.EnumerateFiles(tempDir, "php_redis.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (!download.Success || sourceDll is null)
        {
            SafeDeleteFile(tempZip);
            SafeDeleteDirectory(tempDir);
            throw new AppException("php.redis_download_failed", 502, 502, $"{fileName}: {TruncateError(download.StandardError)}");
        }

        File.Copy(sourceDll, dllPath, overwrite: true);
        SafeDeleteFile(tempZip);
        SafeDeleteDirectory(tempDir);

        var iniPath = Path.Combine(component.InstallDir, "php.ini");
        var lines = (await File.ReadAllLinesAsync(iniPath, cancellationToken)).ToList();
        var alreadyDeclared = lines.Any(line =>
        {
            var normalized = line.Trim().TrimStart(';').Replace(" ", string.Empty);
            return normalized.StartsWith("extension=redis", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("extension=php_redis", StringComparison.OrdinalIgnoreCase);
        });
        if (!alreadyDeclared)
        {
            lines.Add("extension=redis");
            await File.WriteAllLinesAsync(iniPath, lines, cancellationToken);
        }

        // DOĞRULA: redis gerçekten yüklendi mi? Hayırsa GERİ AL (DLL sil + extension satırı çıkar) → PHP kırılmaz.
        if (!await PhpHasModuleAsync(phpCli, "redis", cancellationToken))
        {
            var reverted = lines
                .Where(line => !line.Trim().Replace(" ", string.Empty).Equals("extension=redis", StringComparison.OrdinalIgnoreCase))
                .ToList();
            await File.WriteAllLinesAsync(iniPath, reverted, cancellationToken);
            SafeDeleteFile(dllPath);
            throw new AppException("php.redis_verify_failed", 500, 500, $"{fileName} yüklenemedi (PHP build uyumsuz); geri alındı.");
        }

        // Başarılı → php-cgi yenile (FastCGI php.ini'yi süreç başında okur).
        try
        {
            await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync("taskkill.exe", "/f /im php-cgi.exe", cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "php-cgi yeniden başlatılamadı; redis uzantısı bir sonraki havuz dönüşünde aktifleşir.");
        }

        return new RedisExtensionResultDto { Enabled = true, Message = "installed", FileName = fileName };
    }

    private static async Task<bool> PhpHasModuleAsync(string phpExecutable, string moduleName, CancellationToken cancellationToken)
    {
        var result = await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync(
            phpExecutable, "-m", cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(30));
        return result.Success && result.StandardOutput
            .Split('\n')
            .Any(line => line.Trim().Equals(moduleName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(string Minor, string Ts, string Arch)> DetectPhpBuildAsync(string phpExecutable, CancellationToken cancellationToken)
    {
        const string code = "echo PHP_MAJOR_VERSION.'.'.PHP_MINOR_VERSION.'|'.(PHP_ZTS?'ts':'nts').'|'.(PHP_INT_SIZE===8?'x64':'x86');";
        var result = await AdaPanel.Infrastructure.Support.ProcessRunner.RunAsync(
            phpExecutable, $"-r \"{code}\"", cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(30));
        var parts = (result.StandardOutput ?? string.Empty).Trim().Split('|');
        if (parts.Length == 3 && parts[0].Contains('.') && (parts[1] is "ts" or "nts"))
        {
            return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
        }

        // Çözümlenemezse en yaygın IIS/FastCGI build'ine düş (PHP 8.3 NTS x64).
        return ("8.3", "nts", "x64");
    }

    private static string TruncateError(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var trimmed = value.Trim();
        return trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }

    private static void SafeDeleteFile(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* en iyi çaba */ }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, true); } } catch { /* en iyi çaba */ }
    }

    private async Task<InstalledComponent?> GetLatestInstalledPhpAsync(CancellationToken cancellationToken)
    {
        return await dbContext.InstalledComponents
            .Where(item => item.ComponentId.StartsWith("php-") && item.Status == ComponentInstallStatus.Installed)
            .OrderByDescending(item => item.ComponentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // php.ini'de bir direktifi set eder: aktif varsa değiştirir, yorumluysa açar, yoksa ekler.
    private static void SetIniDirective(List<string> lines, string name, string value)
    {
        var activeIndex = -1;
        var commentedIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            var isComment = trimmed.StartsWith(';');
            var normalized = trimmed.TrimStart(';').TrimStart();
            if (normalized.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                normalized.Length > name.Length &&
                normalized[name.Length..].TrimStart().StartsWith('='))
            {
                if (isComment)
                {
                    if (commentedIndex < 0) { commentedIndex = i; }
                }
                else
                {
                    activeIndex = i;
                    break;
                }
            }
        }

        var formatted = $"{name} = {value}";
        if (activeIndex >= 0) { lines[activeIndex] = formatted; }
        else if (commentedIndex >= 0) { lines[commentedIndex] = formatted; }
        else { lines.Add(formatted); }
    }

    // "256M" / "1G" / "512K" / "12345" → byte. IIS maxAllowedContentLength için PHP boyut değerini çevirir.
    private static long ParsePhpSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        value = value.Trim();
        var unit = char.ToUpperInvariant(value[^1]);
        var hasUnit = unit is 'K' or 'M' or 'G';
        var numberPart = hasUnit ? value[..^1].Trim() : value;
        if (!long.TryParse(numberPart, out var number) || number <= 0)
        {
            return 0;
        }

        return unit switch
        {
            'K' => number * 1024L,
            'M' => number * 1024L * 1024,
            'G' => number * 1024L * 1024 * 1024,
            _ => number
        };
    }

    private static Dictionary<string, string> ParseUserIni(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('['))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            result[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
        }

        return result;
    }

    private static string SanitizeIniValue(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^-?\\d+[KMG]?$") ? trimmed : "0";
    }

    private async Task<InstalledComponent> GetInstalledPhpComponentAsync(string componentId, CancellationToken cancellationToken)
    {
        EnsurePhpComponentId(componentId);

        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken);

        if (component is null || !string.Equals(component.Status.ToString(), "Installed", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("php.version_not_installed", 404, 404);
        }

        return component;
    }

    private static void EnsurePhpComponentId(string componentId)
    {
        if (!componentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("php.invalid_component", 400, 400);
        }
    }

    private static string ResolvePhpExecutablePath(string installDir)
    {
        return Path.Combine(installDir, "php-cgi.exe");
    }

    private static string BuildHandlerName(string componentId)
    {
        return PhpHandlerPrefix + componentId.Replace(' ', '-');
    }

    private static IDisposable CreateServerManager()
    {
        var assemblyPath = Environment.ExpandEnvironmentVariables(WebAdministrationAssemblyPath);
        if (!File.Exists(assemblyPath))
        {
            throw new AppException("sites.iis_not_available", 503, 503);
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var serverManagerType = assembly.GetType(ServerManagerTypeName)
            ?? throw new AppException("sites.iis_not_available", 503, 503);

        return Activator.CreateInstance(serverManagerType) as IDisposable
            ?? throw new AppException("sites.iis_not_available", 503, 503);
    }

    private static IDisposable? TryCreateServerManager()
    {
        try
        {
            return CreateServerManager();
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureSiteExists(IDisposable serverManager, string siteName)
    {
        var sites = GetPropertyObject(serverManager, "Sites");
        var site = Enumerate(sites).FirstOrDefault(item =>
            string.Equals(GetStringProperty(item, "Name"), siteName, StringComparison.OrdinalIgnoreCase));

        if (site is null)
        {
            throw new AppException("sites.site_not_found", 404, 404);
        }
    }

    private static bool IsFastCgiRegistered(IDisposable serverManager, string executablePath)
    {
        var fastCgiCollection = GetFastCgiCollection(serverManager);
        return Enumerate(fastCgiCollection).Any(item =>
            string.Equals(GetAttributeString(item, "fullPath"), executablePath, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureFastCgiApplication(IDisposable serverManager, string executablePath)
    {
        var fastCgiCollection = GetFastCgiCollection(serverManager);
        var existing = Enumerate(fastCgiCollection).FirstOrDefault(item =>
            string.Equals(GetAttributeString(item, "fullPath"), executablePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // KRİTİK: FastCGI requestTimeout varsayılanı 90sn, activityTimeout 30sn — PHP max_execution_time'dan
            // (300sn) KISA. Büyük SQL import / uzun işlemler "500 0x80070102 (timeout)" verir. Mevcut kayıtta da yükselt.
            SetAttributeValue(existing, "activityTimeout", 600);
            SetAttributeValue(existing, "requestTimeout", 600);
            // B4 (sertleştirme): bir php-cgi süreci 10000 istekten sonra geri dönüştürülür. Kaçak bellek
            // biriktiren / uzun ömürlü süreçlerin "blast radius"unu sınırlar; PHP'nin kendi PHP_FCGI_MAX_REQUESTS
            // varsayılanıyla (10000) hizalı olduğundan PHP süreci önce kendini sonlandırıp 500 vermez.
            SetAttributeValue(existing, "instanceMaxRequests", 10000);
            return;
        }

        var application = InvokeMethod(fastCgiCollection, "CreateElement", "application");
        SetAttributeValue(application, "fullPath", executablePath);
        SetAttributeValue(application, "arguments", string.Empty);
        SetAttributeValue(application, "activityTimeout", 600);
        SetAttributeValue(application, "requestTimeout", 600);
        // B4: süreç geri-dönüşüm sınırı (yukarıdaki açıklama). maxInstances'a DOKUNULMAZ -> IIS otomatik
        // (0) bırakılır; sabit düşük bir tavan meşru eşzamanlılığı boğardı.
        SetAttributeValue(application, "instanceMaxRequests", 10000);
        InvokeMethod(fastCgiCollection, "Add", application);
    }

    // Tüm FastCGI uygulamalarının requestTimeout/activityTimeout'unu en az `seconds` yapar (düşürmez).
    // Panelden max_execution_time yükseltilince çağrılır → FastCGI önce kesip 500 vermez. Best-effort.
    private async Task RaiseFastCgiTimeoutAsync(int seconds, CancellationToken cancellationToken)
    {
        try
        {
            await concurrencyLock.RunExclusiveAsync(() =>
            {
                using var serverManager = CreateServerManager();
                var collection = GetFastCgiCollection(serverManager);
                var changed = false;
                foreach (var app in Enumerate(collection))
                {
                    changed |= RaiseTimeoutAttribute(app, "requestTimeout", seconds);
                    changed |= RaiseTimeoutAttribute(app, "activityTimeout", seconds);
                }

                if (changed)
                {
                    InvokeMethod(serverManager, "CommitChanges");
                }

                return Task.CompletedTask;
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "FastCGI timeout yükseltilemedi (best-effort).");
        }
    }

    private static bool RaiseTimeoutAttribute(object application, string name, int seconds)
    {
        int.TryParse(GetAttributeString(application, name), out var current);
        if (current >= seconds)
        {
            return false;
        }

        SetAttributeValue(application, name, seconds);
        return true;
    }

    // max_execution_time 0 ise PHP'de SÜRESİZ demektir; ama IIS FastCGI sınırsız olamaz → 24 saat (86400) tavan.
    // Aksi halde "süresiz PHP + kısa FastCGI" durumunda uzun işlem yine 500 timeout verirdi.
    private static int FastCgiSecondsFor(int maxExecutionTime)
        => maxExecutionTime <= 0 ? 86400 : Math.Clamp(maxExecutionTime, 30, 86400) + 60;

    private static void SetSitePhpHandler(IDisposable serverManager, string siteName, string executablePath, string handlerName)
    {
        var configuration = InvokeMethod(serverManager, "GetWebConfiguration", siteName);
        var handlersSection = InvokeMethod(configuration, "GetSection", "system.webServer/handlers");
        var handlers = GetConfigurationCollection(handlersSection);

        foreach (var existing in Enumerate(handlers).Where(item =>
                     GetAttributeString(item, "name").StartsWith(PhpHandlerPrefix, StringComparison.OrdinalIgnoreCase))
                 .ToArray())
        {
            InvokeMethod(handlers, "Remove", existing);
        }

        var handler = InvokeMethod(handlers, "CreateElement", "add");
        SetAttributeValue(handler, "name", handlerName);
        SetAttributeValue(handler, "path", "*.php");
        SetAttributeValue(handler, "verb", "GET,HEAD,POST");
        SetAttributeValue(handler, "modules", "FastCgiModule");
        SetAttributeValue(handler, "scriptProcessor", executablePath);
        SetAttributeValue(handler, "resourceType", "Either");
        SetAttributeValue(handler, "requireAccess", "Script");

        // KRİTİK: handler'ı listenin BAŞINA ekle. IIS handler eşleştirmesini yukarıdan
        // aşağıya yapar ve ilk eşleşeni kullanır. Devralınan StaticFile (path="*") tüm
        // istekleri yakaladığı için *.php handler sona eklenirse StaticFile önce eşleşir ve
        // .php dosyaları "404.3 - extension not configured" döner (phpMyAdmin/Adminer açılmaz).
        // AddAt(0, ...) ile *.php FastCGI handler StaticFile'dan ÖNCE değerlendirilir.
        try
        {
            InvokeMethod(handlers, "AddAt", 0, handler);
        }
        catch (Exception)
        {
            // Bazı IIS yapılandırmalarında 0. indeks kilitli olabilir; en azından ekleyip
            // çalışmasını garantiye al (eklenememe sessizce 404.3'e dönmesin diye fallback).
            InvokeMethod(handlers, "Add", handler);
        }
    }

    private static object GetFastCgiCollection(IDisposable serverManager)
    {
        var configuration = InvokeMethod(serverManager, "GetApplicationHostConfiguration");
        var section = InvokeMethod(configuration, "GetSection", "system.webServer/fastCgi");
        return GetConfigurationCollection(section);
    }

    private static IEnumerable<object> Enumerate(object collection)
    {
        return collection is IEnumerable enumerable
            ? enumerable.Cast<object>()
            : [];
    }

    private static object GetPropertyObject(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            return property.GetValue(target)
                ?? throw new AppException("php.configuration_failed", 500, 500);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static object GetConfigurationCollection(object section)
    {
        var property = section.GetType().GetProperty("Collection");
        if (property is not null)
        {
            try
            {
                var collection = property.GetValue(section);
                if (collection is not null)
                {
                    return collection;
                }
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        return InvokeMethod(section, "GetCollection");
    }

    private static string GetStringProperty(object target, string propertyName)
    {
        return GetPropertyObject(target, propertyName).ToString() ?? string.Empty;
    }

    private static string GetAttributeString(object target, string attributeName)
    {
        return InvokeMethod(target, "GetAttributeValue", attributeName).ToString() ?? string.Empty;
    }

    private static void SetAttributeValue(object target, string attributeName, object? value)
    {
        InvokeMethod(target, "SetAttributeValue", attributeName, value);
    }

    private static object InvokeMethod(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length &&
                AreParametersCompatible(candidate.GetParameters(), arguments));

        if (method is null)
        {
            throw new MissingMethodException(target.GetType().FullName, methodName);
        }

        try
        {
            return method.Invoke(target, arguments)
                ?? new object();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static AppException MapPhpConfigurationException(Exception exception)
    {
        // Dile bağımsız (HRESULT + İngilizce) sınıflandırma — Türkçe Windows'ta da doğru çalışır.
        var message = Support.Win32ErrorHelper.Flatten(exception);
        if (Support.Win32ErrorHelper.IsAccessDenied(message))
        {
            return CreateDiagnosticException("sites.iis_permission_denied", 403, exception);
        }

        if (message.Contains("section", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("FastCgiModule", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("fastCgi", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_install_required", 503, exception);
        }

        return CreateDiagnosticException("php.configuration_failed", 500, exception);
    }

    private static AppException CreateDiagnosticException(string messageKey, int statusCode, Exception exception)
    {
        return new AppException(messageKey, statusCode, statusCode, BuildDiagnosticDetails(exception));
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        var details = exception.ToString();
        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private static string FlattenExceptionMessage(Exception exception)
    {
        var builder = new StringBuilder(exception.Message);
        var current = exception.InnerException;
        while (current is not null)
        {
            builder.Append(' ').Append(current.Message);
            current = current.InnerException;
        }

        return builder.ToString();
    }

    private static bool AreParametersCompatible(ParameterInfo[] parameters, object?[] arguments)
    {
        for (var index = 0; index < parameters.Length; index += 1)
        {
            var parameterType = parameters[index].ParameterType;
            var argument = arguments[index];

            if (argument is null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                {
                    return false;
                }

                continue;
            }

            if (parameterType.IsInstanceOfType(argument))
            {
                continue;
            }

            if (parameterType.IsEnum && argument is IConvertible)
            {
                continue;
            }

            if (typeof(IConvertible).IsAssignableFrom(parameterType) && argument is IConvertible)
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
