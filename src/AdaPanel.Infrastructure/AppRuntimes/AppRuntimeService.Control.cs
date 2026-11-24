using System.Text;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Iis;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.AppRuntimes;

/// <summary>
/// Barındırılan Node/Python uygulamasının yaşam döngüsü: durdur / başlat / yeniden başlat / kaldır / günlük.
/// </summary>
/// <remarks>
/// <b>Süreç modeli:</b> uygulama ayrı bir Windows servisi DEĞİL, IIS HttpPlatformHandler'ın uygulama
/// havuzu altında başlattığı çocuk süreçtir. Bu yüzden "durdur/başlat/yeniden başlat" sitenin KENDİ
/// uygulama havuzunu (panel her siteye ayrı havuz verir) durdurur/başlatır/geri dönüştürür; sonuç
/// havuz durumu yeniden okunarak DOĞRULANIR — beklenen duruma gelmediyse başarı dönülmez.
/// </remarks>
public sealed partial class AppRuntimeService
{
    private const int MaxLogReadBytes = 256 * 1024;
    private const string StdoutLogPrefix = "httpplatform-stdout";

    public Task<AppRuntimeSiteConfigurationDto> StopAppAsync(string siteName,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        ControlAsync(siteName, "stop", beforeMutation, cancellationToken);

    public Task<AppRuntimeSiteConfigurationDto> StartAppAsync(string siteName,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        ControlAsync(siteName, "start", beforeMutation, cancellationToken);

    public Task<AppRuntimeSiteConfigurationDto> RestartAppAsync(string siteName,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        ControlAsync(siteName, "restart", beforeMutation, cancellationToken);

    private async Task<AppRuntimeSiteConfigurationDto> ControlAsync(string siteName, string action,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken)
    {
        using var target = await SiteMutationGuard.CaptureAsync(iisSiteService, concurrencyLock, siteName, cancellationToken);
        AppRuntimeSiteConfigurationDto? result = null;
        await target.RunAsync(async current =>
        {
            await beforeMutation(current, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = ReadConfiguration(current);
            if (!configuration.IsEnabled || configuration.RuntimeType is not ("node" or "python"))
                throw new AppException("appruntime.not_hosted", 409, 409);
            try
            {
                ControlPool(current.ApplicationPoolName, action);
            }
            catch (Exception exception) when (exception is not (AppException or OperationCanceledException))
            {
                logger.LogError(exception, "Application pool {Action} failed for site {SiteName}.", action, current.Name);
                throw MapAppRuntimeException(exception, "appruntime.control_failed");
            }
            result = ReadConfiguration(current);
            var expected = action == "stop" ? new[] { "Stopped", "Stopping" } : ["Started", "Starting"];
            if (result.PoolState is null || !expected.Contains(result.PoolState, StringComparer.OrdinalIgnoreCase))
                throw new AppException("appruntime.control_failed", 500, 500,
                    $"Pool state after '{action}' is '{result.PoolState ?? "unknown"}'.");
        }, cancellationToken);
        return result!;
    }

    private void ControlPool(string poolName, string action)
    {
        if (string.IsNullOrWhiteSpace(poolName)) throw new AppException("appruntime.control_failed", 500, 500);
        using var manager = ManagerFactory();
        var pool = Enumerate(GetPropertyObject(manager, "ApplicationPools"))
            .FirstOrDefault(item => string.Equals(GetStringProperty(item, "Name"), poolName, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException("appruntime.control_failed", 500, 500, $"Application pool '{poolName}' was not found.");
        var state = TryGetPoolState(pool);
        var stopped = string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase);
        switch (action)
        {
            case "stop":
                if (!stopped) InvokeMethod(pool, "Stop");
                break;
            case "start":
                if (stopped) InvokeMethod(pool, "Start");
                break;
            default:
                // Duran havuz geri dönüştürülemez; başlatmak yeniden başlatmanın karşılığıdır.
                InvokeMethod(pool, stopped ? "Start" : "Recycle");
                break;
        }
    }

    private static string? TryGetPoolState(object pool)
    {
        try { return GetStringProperty(pool, "State"); }
        catch { return null; }
    }

    private string? ReadPoolState(string poolName)
    {
        if (string.IsNullOrWhiteSpace(poolName)) return null;
        try
        {
            using var manager = ManagerFactory();
            var pool = Enumerate(GetPropertyObject(manager, "ApplicationPools"))
                .FirstOrDefault(item => string.Equals(GetStringProperty(item, "Name"), poolName, StringComparison.OrdinalIgnoreCase));
            return pool is null ? null : TryGetPoolState(pool);
        }
        catch
        {
            return null;
        }
    }

    public async Task<AppRuntimeRemovalResultDto> RemoveAppAsync(string siteName, bool deleteLogs,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default)
    {
        using var target = await SiteMutationGuard.CaptureAsync(iisSiteService, concurrencyLock, siteName, cancellationToken);
        AppRuntimeRemovalResultDto? outcome = null;
        await target.RunAsync(async current =>
        {
            await beforeMutation(current, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            outcome = RemoveCore(current, deleteLogs, requireHosted: true, cancellationToken);
        }, cancellationToken);
        return outcome!;
    }

    /// <summary>
    /// Site silme akışı için: çağıran silme işaretini ZATEN tutar (<c>SiteMutationGuard.BeginDeletionAsync</c>), bu yüzden
    /// yakalama (Capture) yapılmaz — yapılsaydı "site siliniyor" diye reddederdi. Barındırma yoksa no-op.
    /// </summary>
    public Task<AppRuntimeRemovalResultDto> RemoveAppForSiteDeletionAsync(SiteDto site, bool deleteLogs, CancellationToken cancellationToken = default) =>
        Task.FromResult(RemoveCore(site, deleteLogs, requireHosted: false, cancellationToken));

    private AppRuntimeRemovalResultDto RemoveCore(SiteDto current, bool deleteLogs, bool requireHosted, CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        var leftovers = new List<string>();
        var siteRoot = ResolveSiteRoot(current.PhysicalPath);
        var webConfigPath = ResolveSiteManagedPath(siteRoot, "web.config", allowCurrentDirectory: false);
        var before = ReadConfiguration(current);
        var hosted = before.RuntimeType != "none" || before.HasHttpPlatformHandler;
        if (!hosted)
        {
            if (requireHosted) throw new AppException("appruntime.not_hosted", 409, 409);
            return new AppRuntimeRemovalResultDto { SiteName = current.Name, Configuration = before };
        }

        // 1) Önce yapılandırmayı kaldır: havuz durdurulsa bile handler kalırsa site kırık kalır.
        try
        {
            if (RemovePanelEntries(webConfigPath, cancellationToken)) removed.Add("web.config");
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or AppException))
        {
            logger.LogError(exception, "Unable to remove application runtime configuration for site {SiteName}.", current.Name);
            throw MapAppRuntimeException(exception, "appruntime.configuration_failed");
        }

        // 2) Süreç: havuzu geri dönüştür (web.config değişikliği çocuk süreci zaten düşürür; bu, ölçülür kılar).
        try
        {
            ControlPool(current.ApplicationPoolName, "restart");
            removed.Add("process");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Application pool recycle after runtime removal failed for site {SiteName}.", current.Name);
            leftovers.Add("process: the hosted process may still be running until the application pool is recycled");
        }

        // 3) Panelin günlük klasörü — yalnız panelin yazdığı stdout dosyaları; özyineleme YOK.
        if (deleteLogs) DeletePanelLogs(siteRoot, removed, leftovers);

        var after = ReadConfiguration(current);
        if (after.IsEnabled || after.HasHttpPlatformHandler)
            leftovers.Add("web.config: the hosting configuration is still present");
        return new AppRuntimeRemovalResultDto
        {
            SiteName = current.Name,
            Removed = removed.ToArray(),
            Leftovers = leftovers.ToArray(),
            Configuration = after
        };
    }

    /// <summary>Panelin yazdığı handler + httpPlatform öğelerini siler. Başka hiçbir ayara dokunmaz.</summary>
    private bool RemovePanelEntries(string webConfigPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(webConfigPath)) return false;
        var document = XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace);
        var systemWebServer = document.Element("configuration")?.Element("system.webServer");
        if (systemWebServer is null) return false;
        var handlers = systemWebServer.Element("handlers");
        // YALNIZ panelin kendi handler adı: kullanıcının elle eklediği (başka adlı) handler'a dokunulmaz.
        var owned = (handlers?.Elements("add") ?? []).Where(element =>
            string.Equals((string?)element.Attribute("name"), HandlerName, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Panel girdisi yoksa httpPlatform da kullanıcıya aittir; silinmez (kalıntı olarak bildirilir).
        if (owned.Length == 0) return false;
        var changed = false;
        foreach (var handler in owned)
        {
            handler.Remove();
            changed = true;
        }
        foreach (var platform in systemWebServer.Elements("httpPlatform").ToArray())
        {
            platform.Remove();
            changed = true;
        }
        if (!changed) return false;
        if (handlers is not null && !handlers.HasElements) handlers.Remove();
        if (!systemWebServer.HasElements) systemWebServer.Remove();
        WebConfigurationFile.Save(webConfigPath, document, ConfigurationStream, cancellationToken);
        return true;
    }

    private void DeletePanelLogs(string siteRoot, List<string> removed, List<string> leftovers)
    {
        try
        {
            var logs = ResolveSiteManagedPath(siteRoot, LogsDirectoryName, allowCurrentDirectory: true);
            if (!Directory.Exists(logs)) return;
            var failed = 0;
            foreach (var file in Directory.EnumerateFiles(logs, StdoutLogPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { failed++; continue; }
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failed++;
                }
            }
            if (failed > 0) { leftovers.Add($"logs: {failed} log file(s) could not be deleted"); return; }
            // Klasör yalnız BOŞSA silinir: içinde kullanıcının başka dosyası varsa dokunulmaz.
            if (!Directory.EnumerateFileSystemEntries(logs).Any()) Directory.Delete(logs);
            removed.Add("logs");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to delete application runtime logs under {SiteRoot}.", siteRoot);
            leftovers.Add("logs: " + (exception is AppException app ? app.MessageKey : "the log folder could not be cleaned"));
        }
    }

    public async Task<AppRuntimeLogsDto> GetLogsAsync(string siteName, int tail, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var siteRoot = ResolveSiteRoot(site.PhysicalPath);
        var logs = ResolveSiteManagedPath(siteRoot, LogsDirectoryName, allowCurrentDirectory: true);
        var lines = Math.Clamp(tail, 10, 2000);
        if (!Directory.Exists(logs)) return new AppRuntimeLogsDto { SiteName = site.Name };
        var newest = new DirectoryInfo(logs).EnumerateFiles(StdoutLogPrefix + "*", SearchOption.TopDirectoryOnly)
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return new AppRuntimeLogsDto { SiteName = site.Name };
        try
        {
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var truncated = stream.Length > MaxLogReadBytes;
            if (truncated) stream.Seek(-MaxLogReadBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(cancellationToken);
            var all = text.Split('\n');
            var selected = all.Length > lines ? all[^lines..] : all;
            return new AppRuntimeLogsDto
            {
                SiteName = site.Name,
                FileName = newest.Name,
                Output = SecretRedactor.Redact(string.Join('\n', selected).TrimEnd('\r', '\n')),
                Truncated = truncated || all.Length > lines
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Unable to read application runtime log for site {SiteName}.", siteName);
            throw new AppException("appruntime.logs_read_failed", 500, 500);
        }
    }
}
