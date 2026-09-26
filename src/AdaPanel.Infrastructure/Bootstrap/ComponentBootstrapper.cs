using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Nginx;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace AdaPanel.Infrastructure.Bootstrap;

public sealed class ComponentBootstrapper(
    AdaPanelDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    ISecretProtector secretProtector,
    IAuditService auditService,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ComponentDiscoveryService componentDiscoveryService,
    IOperationProgressService operationProgress,
    ILogger<ComponentBootstrapper> logger) : IComponentBootstrapper
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private const string GeneratedPasswordKey = "generatedPassword";
    private const string SystemDetectedKey = "systemDetected";
    private const string DetectionSourceKey = "detectionSource";
    private const string DetectionExecutableKey = "detectionExecutable";
    private const string UninstallCommandKey = "uninstallCommand";
    private const string DeleteInstallDirectoryKey = "deleteInstallDirectory";
    private const int MariaDbPort = 3306;
    private const int MySqlPort = 3307;
    private static readonly HashSet<string> KnownPostInstallSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        "writePhpIni",
        "initMySqlZip",
        "initMariaDbZip",
        "installRedisService",
        "installMemcachedService",
        "installDockerEngine",
        "installStalwartService",
        "prepareNginx",
        "registerFastCgi"
    };

    /// <summary>
    /// Bileşeni Windows SERVİSİ olarak değil, ONSTART ZAMANLANMIŞ GÖREV olarak kaydeden
    /// <c>postInstall</c> kancaları.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Buradaki iki kanca <see cref="RegisterLongRunningTaskAsync"/> çağırır; kalanlar
    /// <c>sc</c>/<c>--service-install</c> ile gerçek servis kaydeder. Ayrım GÖRÜNÜR olmalı,
    /// çünkü <c>components.json</c>'daki <c>serviceName</c> alanı bu iki bileşende bir SERVİS
    /// adı değil GÖREV adıdır (kurulum kodu görevi tam o adla kaydeder).
    /// </para>
    /// <para>
    /// ÖLÇÜLDÜ — bu ayrımın olmaması pahalıya patlıyordu: tepsi programı <c>serviceName</c>
    /// dolu olan her bileşeni Windows servisi sanıp SCM'de arıyor, bulamayınca Stalwart'ı
    /// KALICI olarak "kurulu, servisi kayıtlı değil" gösteriyor ve "panelden yeniden kurun"
    /// diyordu — oysa bileşen kurulu ve doğru kaydedilmişti. Bu makinede <c>Get-Service</c>
    /// yalnız <c>AdaPanel</c> döndü, <c>AdaStalwart</c> ise Task Scheduler'da <c>State=3</c>
    /// (Ready) olarak duruyordu.
    /// </para>
    /// <para>
    /// Tepsi bu kümeyi okur; iki yerde iki liste tutulmaz.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> ZamanlanmisGorevKancalari =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "installStalwartService",
            "installMemcachedService"
        };
    private readonly PanelRuntimeOptions runtimeOptions = runtimeOptions.Value;

    public async Task<SoftwareCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        await SynchronizeDetectedComponentsAsync(manifest, cancellationToken);
        var installedEntities = await dbContext.InstalledComponents
            .OrderBy(component => component.DisplayName)
            .ToArrayAsync(cancellationToken);

        var updateCache = ReadUpdateCache();

        return new SoftwareCatalogDto
        {
            Manifest = manifest,
            Defaults = manifest.Where(component => component.Default).ToArray(),
            Installed = installedEntities.Select(component => MapInstalledComponent(component, updateCache)).ToArray()
        };
    }

    /// <summary>
    /// Bileşenin üst sürümü CANLI olarak çözülebiliyor mu (ResolveDynamicComponentAsync'te karşılığı
    /// var mı). Çözülemeyenlerin sürümü manifeste SABİT yazılıdır; bunlar için "güncelleme yok"
    /// demek yanıltıcıdır — panel bunu "kontrol edilemiyor" olarak bildirir, sessizce güncelmiş
    /// gibi göstermez. (Örn. PHP, MariaDB, MySQL, PostgreSQL, nginx yeni sürüm çıkardığında
    /// manifest elle güncellenene kadar panel fark etmez.)
    /// </summary>
    internal static bool SupportsVersionCheck(string componentId)
    {
        return componentId.Trim().ToLowerInvariant() is
            "adminer" or "clamav" or "composer" or "docker-engine" or "memcached" or
            "nodejs-lts" or "phpmyadmin" or "roundcube" or "stalwart-cli" or "stalwart-mail" or "win-acme" or
            "php-8.3" or "mariadb-11" or "mysql-8" or "postgresql-16" or "pgadmin4" or
            "nginx" or "python-3" or "redis" or
            // Sabit "evergreen" adresler: URL her zaman en gunceli verir (aka.ms/vs/17/release/...,
            // Microsoft IIS eklenti MSI'lari). Surum sorgusu gerekmez; "kontrol edilemiyor" DEGIL.
            "vc-redist-x64" or "url-rewrite" or "iis-arr" or "http-platform-handler";
    }

    public async Task<int> GetPendingUpdateCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cache = ComponentUpdateCache.Read(UpdateCacheFilePath());
        var installed = await dbContext.InstalledComponents
            .AsNoTracking()
            .Where(item => item.Status == ComponentInstallStatus.Installed)
            .ToListAsync(cancellationToken);
        var managed = installed.Where(item => !ReadBoolean(ReadProtectedConfiguration(item.ProtectedConfigurationJson), SystemDetectedKey)).ToArray();
        if (managed.Any(item => SupportsVersionCheck(item.ComponentId) && !cache.ContainsKey(item.ComponentId)))
            throw new InvalidDataException("Component update results are not available for all installed components.");

        // A persisted flag may predate an installation; compare against the current DB version.
        return managed.Count(item => cache.TryGetValue(item.ComponentId, out var info)
            && IsNewerVersion(info.AvailableVersion, item.Version));
    }

    public async Task<SoftwareCatalogDto> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        await SynchronizeDetectedComponentsAsync(manifest, cancellationToken);
        var installedEntities = await dbContext.InstalledComponents
            .OrderBy(component => component.DisplayName)
            .ToArrayAsync(cancellationToken);

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AdaPanel-UpdateCheck/1.0");
        }

        var cache = new Dictionary<string, ComponentUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var installed in installedEntities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Sistemde algılanan (AdaPanel kurmadığı) bileşenlerin güncellemesini panel yönetmez.
            var configuration = ReadProtectedConfiguration(installed.ProtectedConfigurationJson);
            if (ReadBoolean(configuration, SystemDetectedKey))
            {
                continue;
            }

            var manifestItem = manifest.FirstOrDefault(item =>
                string.Equals(item.Id, installed.ComponentId, StringComparison.OrdinalIgnoreCase));
            if (manifestItem is null)
            {
                continue;
            }

            var availableVersion = manifestItem.Version;
            try
            {
                var resolved = await ResolveDynamicComponentAsync(manifestItem, client, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved.Version))
                {
                    availableVersion = resolved.Version;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogInformation(
                    exception,
                    "Update check could not resolve the latest version for {ComponentId}; using manifest version.",
                    installed.ComponentId);
            }

            cache[installed.ComponentId] = new ComponentUpdateInfo
            {
                AvailableVersion = availableVersion,
                UpdateAvailable = IsNewerVersion(availableVersion, installed.Version),
                VersionCheckSupported = SupportsVersionCheck(installed.ComponentId),
                CheckedAtUtc = DateTime.UtcNow
            };
        }

        WriteUpdateCache(cache);

        return new SoftwareCatalogDto
        {
            Manifest = manifest,
            Defaults = manifest.Where(component => component.Default).ToArray(),
            Installed = installedEntities.Select(component => MapInstalledComponent(component, cache)).ToArray()
        };
    }

    private async Task SynchronizeDetectedComponentsAsync(
        IReadOnlyCollection<ComponentManifestItemDto> manifest,
        CancellationToken cancellationToken)
    {
        var discovery = await componentDiscoveryService.DiscoverAsync(manifest, cancellationToken);
        var detectedById = discovery.Detections.ToDictionary(
            item => item.ComponentId,
            StringComparer.OrdinalIgnoreCase);
        var installedComponents = await dbContext.InstalledComponents
            .ToArrayAsync(cancellationToken);

        foreach (var installedComponent in installedComponents)
        {
            var configuration = ReadProtectedConfiguration(installedComponent.ProtectedConfigurationJson);
            if (!ReadBoolean(configuration, SystemDetectedKey))
            {
                continue;
            }

            if (!detectedById.TryGetValue(installedComponent.ComponentId, out var detection))
            {
                // Y2: "ÖLÇEMEDİM" ile "KURULU DEĞİL" aynı şey değildir. Keşif kaynaklarından biri bile
                // okunamadıysa kaydı SİLMEYİZ: kayıttaki ProtectedConfigurationJson veritabanı root
                // parolasını + admin kullanıcıyı + portu taşır ve yeniden keşifte GERİ GELMEZ. Bu yol
                // GetCatalogAsync (Yazılım ekranı her açılışta) ve CheckForUpdatesAsync (günlük) içinde
                // çalıştığından tek bir geçici registry hatası kimlik bilgisini kalıcı yok ederdi.
                if (!discovery.CanTrustAbsence(installedComponent.ComponentId))
                {
                    logger.LogWarning(
                        "Component {ComponentId} could not be measured; keeping the installed record (credentials would be lost).",
                        installedComponent.ComponentId);
                    continue;
                }

                dbContext.InstalledComponents.Remove(installedComponent);
                continue;
            }

            ApplyDetection(installedComponent, detection);
            detectedById.Remove(installedComponent.ComponentId);
        }

        foreach (var detection in detectedById.Values)
        {
            var existing = installedComponents.FirstOrDefault(item =>
                string.Equals(item.ComponentId, detection.ComponentId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // AdaPanel-owned records retain their generated credentials and lifecycle ownership.
                continue;
            }

            var manifestComponent = manifest.First(item =>
                string.Equals(item.Id, detection.ComponentId, StringComparison.OrdinalIgnoreCase));
            var installedComponent = new InstalledComponent
            {
                ComponentId = manifestComponent.Id,
                DisplayName = manifestComponent.DisplayName,
                Type = ParseComponentType(manifestComponent.Type),
                Status = ComponentInstallStatus.Installed,
                InstalledAtUtc = DateTime.UtcNow
            };
            ApplyDetection(installedComponent, detection);
            dbContext.InstalledComponents.Add(installedComponent);
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private void ApplyDetection(InstalledComponent component, DetectedComponent detection)
    {
        var manifestVersion = string.IsNullOrWhiteSpace(detection.Version) ? component.Version : detection.Version;
        component.Version = manifestVersion;
        component.InstallDir = detection.InstallDirectory;
        component.ServiceName = detection.ServiceName;
        component.Status = ComponentInstallStatus.Installed;
        component.LastError = string.Empty;
        component.UpdatedAtUtc = DateTime.UtcNow;

        // Mevcut yapılandırmadan korunması gereken anahtarları (yöneticinin panele tanıttığı
        // veritabanı kimlik bilgileri gibi) taşı; aksi halde her katalog yenilemesinde silinir
        // ve keşfedilen MySQL/MariaDB/PostgreSQL tekrar "yönetilemez" duruma düşer.
        var configuration = ReadProtectedConfiguration(component.ProtectedConfigurationJson);
        configuration[SystemDetectedKey] = bool.TrueString;
        configuration[DetectionSourceKey] = detection.DetectionSource;
        configuration[DetectionExecutableKey] = detection.ExecutablePath;
        configuration[UninstallCommandKey] = detection.UninstallCommand;
        configuration[DeleteInstallDirectoryKey] = detection.DeleteInstallDirectory.ToString();
        component.ProtectedConfigurationJson = secretProtector.Protect(
            JsonSerializer.Serialize(configuration, SerializerOptions));
    }

    public async Task<ComponentInstallResultDto> InstallAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        var component = manifest.FirstOrDefault(item => string.Equals(item.Id, componentId, StringComparison.OrdinalIgnoreCase));
        if (component is null)
        {
            throw new AppException("software.component_not_found", 404, 404);
        }

        // Tekil kurulum için de canlı ilerleme yuvası aç; bağımlılıklar dahil her bileşen
        // adım adım raporlanır (InstallComponentInternalAsync). Yazılım Mağazası bunu yoklar.
        operationProgress.Begin(component.DisplayName);
        try
        {
            var result = await InstallWithDependenciesAsync(
                component,
                manifest,
                actorUserId,
                ipAddress,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                forceInstall: force,
                cancellationToken);
            operationProgress.Complete();
            return result;
        }
        catch (Exception exception)
        {
            operationProgress.Fail(exception.Message);
            throw;
        }
    }

    public async Task<IReadOnlyCollection<ComponentInstallResultDto>> InstallDefaultsAsync(
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        var defaults = manifest.Where(component => component.Default).ToArray();
        var results = new List<ComponentInstallResultDto>(defaults.Length);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in defaults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await InstallWithDependenciesAsync(
                    component,
                    manifest,
                    actorUserId,
                    ipAddress,
                    completed,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    forceInstall: false,
                    cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Bir bileşenin kurulumu başarısız olsa bile diğer varsayılanlar kurulmaya devam etsin.
                logger.LogWarning(exception, "Default component {ComponentId} failed to install; continuing.", component.Id);
                results.Add(new ComponentInstallResultDto
                {
                    ComponentId = component.Id,
                    DisplayName = component.DisplayName,
                    Version = component.Version,
                    Status = ComponentInstallStatus.Failed.ToString(),
                    Message = exception.Message
                });
            }
        }

        return results;
    }

    private async Task<ComponentInstallResultDto> InstallWithDependenciesAsync(
        ComponentManifestItemDto component,
        IReadOnlyCollection<ComponentManifestItemDto> manifest,
        Guid? actorUserId,
        string? ipAddress,
        HashSet<string> completed,
        HashSet<string> visiting,
        bool forceInstall,
        CancellationToken cancellationToken)
    {
        if (completed.Contains(component.Id))
        {
            return await GetExistingInstallResultAsync(component, cancellationToken);
        }

        if (!visiting.Add(component.Id))
        {
            throw new AppException(
                "software.dependency_cycle",
                500,
                500,
                $"Component={component.Id}");
        }

        try
        {
            foreach (var dependencyId in component.Dependencies)
            {
                var dependency = manifest.FirstOrDefault(item =>
                    string.Equals(item.Id, dependencyId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new AppException(
                        "software.dependency_not_found",
                        500,
                        500,
                        $"Component={component.Id}\r\nDependency={dependencyId}");

                if (await IsDependencyReadyAsync(dependency, cancellationToken))
                {
                    completed.Add(dependency.Id);
                    continue;
                }

                await InstallWithDependenciesAsync(
                    dependency,
                    manifest,
                    actorUserId,
                    ipAddress,
                    completed,
                    visiting,
                    forceInstall: false,
                    cancellationToken);
            }

            if (!forceInstall)
            {
                var installed = await dbContext.InstalledComponents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        item => item.ComponentId == component.Id &&
                            item.Status == ComponentInstallStatus.Installed,
                        cancellationToken);
                if (installed is not null && await IsInstalledComponentReadyAsync(component, cancellationToken))
                {
                    completed.Add(component.Id);
                    return MapExistingInstallResult(component, installed);
                }
            }

            var result = await InstallComponentInternalAsync(
                component,
                actorUserId,
                ipAddress,
                cancellationToken);
            completed.Add(component.Id);
            return result;
        }
        finally
        {
            visiting.Remove(component.Id);
        }
    }

    private async Task<ComponentInstallResultDto> GetExistingInstallResultAsync(
        ComponentManifestItemDto component,
        CancellationToken cancellationToken)
    {
        var installed = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ComponentId == component.Id, cancellationToken);

        return installed is null
            ? new ComponentInstallResultDto
            {
                ComponentId = component.Id,
                DisplayName = component.DisplayName,
                Version = component.Version,
                Status = ComponentInstallStatus.Installed.ToString(),
                InstallDir = ResolveInstallDirectory(component.InstallDir),
                ServiceName = component.ServiceName,
                Message = "software.install.completed"
            }
            : MapExistingInstallResult(component, installed);
    }

    private ComponentInstallResultDto MapExistingInstallResult(
        ComponentManifestItemDto component,
        InstalledComponent installed)
        => new()
        {
            ComponentId = component.Id,
            DisplayName = installed.DisplayName,
            Version = installed.Version,
            Status = installed.Status.ToString(),
            InstallDir = installed.InstallDir,
            ServiceName = installed.ServiceName,
            GeneratedPassword = ReadGeneratedPassword(installed.ProtectedConfigurationJson),
            Message = "software.install.completed"
        };

    private async Task<bool> IsDependencyReadyAsync(
        ComponentManifestItemDto dependency,
        CancellationToken cancellationToken)
    {
        if (string.Equals(dependency.Id, "vc-redist-x64", StringComparison.OrdinalIgnoreCase) &&
            IsVisualCppRuntimeInstalled())
        {
            return true;
        }

        var installed = await dbContext.InstalledComponents
            .AsNoTracking()
            .AnyAsync(
                item => item.ComponentId == dependency.Id &&
                    item.Status == ComponentInstallStatus.Installed,
                cancellationToken);

        return installed && await IsInstalledComponentReadyAsync(dependency, cancellationToken);
    }

    private async Task<bool> IsInstalledComponentReadyAsync(
        ComponentManifestItemDto component,
        CancellationToken cancellationToken)
    {
        try
        {
            await ValidateInstallationAsync(
                component,
                ResolveInstallDirectory(component.InstallDir),
                cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyCollection<string>> RemoveAsync(
        string componentId,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        var manifestComponent = manifest.FirstOrDefault(item =>
            string.Equals(item.Id, componentId, StringComparison.OrdinalIgnoreCase));
        var installedComponent = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken);

        if (installedComponent is null && manifestComponent is null)
        {
            throw new AppException("software.component_not_installed", 404, 404);
        }

        var displayName = installedComponent?.DisplayName ?? manifestComponent?.DisplayName ?? componentId;
        var installDirectory = installedComponent?.InstallDir
            ?? ResolveInstallDirectory(manifestComponent?.InstallDir ?? string.Empty);
        var serviceName = installedComponent?.ServiceName ?? manifestComponent?.ServiceName ?? string.Empty;
        var cleanupErrors = new List<string>();
        var detectedConfiguration = ReadProtectedConfiguration(installedComponent?.ProtectedConfigurationJson);
        var isSystemDetected = ReadBoolean(detectedConfiguration, SystemDetectedKey);
        var canDeleteDetectedDirectory = ReadBoolean(detectedConfiguration, DeleteInstallDirectoryKey);
        detectedConfiguration.TryGetValue(UninstallCommandKey, out var discoveredUninstallCommand);

        if (isSystemDetected &&
            string.IsNullOrWhiteSpace(discoveredUninstallCommand) &&
            !canDeleteDetectedDirectory)
        {
            throw new AppException(
                "software.detected_uninstaller_missing",
                409,
                409,
                $"Component={componentId}\r\nSource={ReadConfigurationValue(detectedConfiguration, DetectionSourceKey)}");
        }

        if (isSystemDetected &&
            !string.IsNullOrWhiteSpace(discoveredUninstallCommand))
        {
            await RunDiscoveredUninstallerAsync(discoveredUninstallCommand, cancellationToken);
        }

        try
        {
            await TryRemoveServiceAsync(serviceName, cancellationToken);
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception.Message);
            logger.LogWarning(exception, "Service cleanup failed for component {ComponentId}", componentId);
        }

        try
        {
            await StopManagedRuntimeAsync(serviceName, installDirectory, cancellationToken);
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception.Message);
            logger.LogWarning(exception, "Runtime cleanup failed for component {ComponentId}", componentId);
        }

        try
        {
            if (!isSystemDetected)
            {
                await TryRunManagedUninstallerAsync(manifestComponent, componentId, installDirectory, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception.Message);
            logger.LogWarning(exception, "Application uninstaller failed for component {ComponentId}", componentId);
        }

        try
        {
            if (isSystemDetected && canDeleteDetectedDirectory)
            {
                detectedConfiguration.TryGetValue(DetectionExecutableKey, out var detectionExecutable);
                DeleteDetectedInstallPath(installDirectory, detectionExecutable ?? string.Empty);
            }
            else
            {
                DeleteManagedPath(installDirectory, componentId);
            }
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception.Message);
            logger.LogWarning(exception, "Path cleanup failed for component {ComponentId}", componentId);
            if (isSystemDetected && canDeleteDetectedDirectory)
            {
                throw new AppException(
                    "software.remove.failed",
                    500,
                    500,
                    BuildDiagnosticDetails(exception));
            }
        }

        try
        {
            DeleteDownloadArtifacts(manifestComponent);
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(exception.Message);
            logger.LogWarning(exception, "Download cleanup failed for component {ComponentId}", componentId);
        }

        try
        {
            if (installedComponent is not null)
            {
                dbContext.InstalledComponents.Remove(installedComponent);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await auditService.WriteAsync(
                "software.remove.completed",
                "software",
                cleanupErrors.Count == 0
                    ? $"Component '{displayName}' was removed."
                    : $"Component '{displayName}' record was removed. Cleanup warnings: {string.Join("; ", cleanupErrors)}",
                actorUserId,
                ipAddress,
                cancellationToken);

            // Temizlenemeyenler ÇAĞIRANA döner; kullanıcı "kaldırıldı" yerine neyin kaldığını görür.
            return cleanupErrors;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Component removal failed for {ComponentId}", componentId);
            await auditService.WriteAsync(
                "software.remove.failed",
                "software",
                $"Component '{displayName}' failed to remove: {exception.Message}",
                actorUserId,
                ipAddress,
                cancellationToken);

            throw new AppException("software.remove.failed", 500, 500);
        }
    }

    internal async Task<ComponentInstallResultDto> InstallComponentInternalAsync(
        ComponentManifestItemDto component,
        Guid? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var downloadsRoot = Path.Combine(Environment.ExpandEnvironmentVariables(runtimeOptions.DataRoot), "downloads");
        Directory.CreateDirectory(downloadsRoot);

        var client = httpClientFactory.CreateClient();
        // ResponseHeadersRead ile bu Timeout yalnız bağlan+header fazını sınırlar (gövde indirme idle-
        // timeout ile yönetilir). Infinite olsaydı takılı bir sunucuya bağlanış SONSUZA asılırdı; ayrıca
        // bu client metadata (GitHub API/checksum) sorgularında da kullanılıyor — onları da bağlar.
        client.Timeout = TimeSpan.FromMinutes(2);
        // Bazı dağıtım siteleri (ör. hmailserver.com) tarayıcı dışı isteklere 403 döner;
        // varsayılan bir tarayıcı User-Agent başlığı ekle.
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        component = await ResolveDynamicComponentAsync(component, client, cancellationToken);
        EnsureIntegrityMetadata(component);
        var fileName = ResolveDownloadFileName(component);
        var downloadPath = Path.Combine(downloadsRoot, fileName);
        var installDirectory = ResolveInstallDirectory(component.InstallDir);
        var silentArgs = component.SilentArgs ?? string.Empty;
        var previous = await dbContext.InstalledComponents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ComponentId == component.Id, cancellationToken);
        var generatedPassword = ResolveInstallPassword(
            silentArgs.Contains("__GENERATED__", StringComparison.Ordinal),
            previous is null ? string.Empty : ReadGeneratedPassword(previous.ProtectedConfigurationJson),
            DataDirectoriesToPreserve(component.Id).Any(relative =>
                Directory.Exists(Path.Combine(installDirectory, relative)) &&
                Directory.EnumerateFileSystemEntries(Path.Combine(installDirectory, relative)).Any()),
            GeneratePassword);

        string cleanupPath = string.Empty;
        var stateRestored = true;
        var installationFilesChanged = false;

        // Canlı ilerleme: "şu an X kuruluyor". Begin/Complete'i çağıran yönetir (ilk kurulum
        // sihirbazı veya Yazılım Mağazası); burada sadece bu bileşenin adımı raporlanır.
        operationProgress.StartStep(component.DisplayName);

        try
        {
            // Mail sunucusu çakışmasını engelle: biri kurulurken diğeri durdurulup yedeğe alınır
            // (ikisi de port 25/110/143'e bağlanamaz). Stalwart birincil, MailEnable yedek.
            await ResolveMailServerConflictAsync(component.Id, cancellationToken);

            // ÖN KONTROL (Plesk paritesi): bileşenin portunu tutan yabancı yazılım veya çakışan
            // yabancı servis varsa kurulum İNDİRMEDEN ÖNCE, suçlunun ADIYLA reddedilir. Aksi halde
            // hata çok sonra yanıltıcı biçimde patlar (ör. MySQL 8 3306'dayken MariaDB probe'u
            // yabancı sunucuya bağlanıp "Access denied" der). Kendi Ada* servislerimiz muaftır.
            await EnsureNoComponentConflictsAsync(component, cancellationToken);

            // memcached'in Windows (mingw) ikili dosyalari Windows Defender tarafindan
            // YANLIS POZITIF "PUA" olarak isaretlenip indirilir indirilmez siliniyor; resmi
            // bir Windows derlemesi olmadigindan kaynak degistirmek bunu cozmuyor. AdaPanel'in
            // KENDI yonettigi indirme ve calisma dizinlerini Defender taramasindan haric tutarak
            // guvenli ikilinin silinmesini onle (best-effort; Defender yoksa sessizce gecer).
            if (component.Id.Equals("memcached", StringComparison.OrdinalIgnoreCase))
            {
                await TryAddDefenderExclusionsAsync(new[] { downloadsRoot, installDirectory }, cancellationToken);
            }

            await DownloadAsync(client, component.DownloadUrl, downloadPath, cancellationToken);
            await VerifyPackageIntegrityAsync(component, downloadPath, cancellationToken);

            var executionArgs = silentArgs
                .Replace("__GENERATED__", generatedPassword, StringComparison.Ordinal)
                .Replace("__INSTALL_DIR__", installDirectory, StringComparison.Ordinal);

            // Onceki bir kurulumdan kalma calisan surec (orn. Stalwart) dosyalari kilitliyorsa
            // serbest birak ki yeniden kurulumda dosyalar uzerine yazilabilsin.
            await StopManagedRuntimeAsync(component.ServiceName, installDirectory, cancellationToken);

            // VERI KORUMASI — bu satirlarin sirasi kritiktir. Asagidaki DeleteManagedPath kurulum
            // dizinini KOMPLE siler; veritabani motorlarinin veri klasoru (data/) KURULUM DIZININ
            // ICINDE oldugu icin, surum yukseltmesinde ya da onarim kurulumunda TUM VERITABANLARI
            // yok olurdu. postInstall icindeki "veri varsa dokunma" korumalari BURADAN SONRA
            // calistigi icin ise yaramaz — veri o noktada zaten silinmis olur.
            // Cozum: veri klasorlerini silmeden ONCE kenara al, kurulumdan SONRA geri koy.
            var preserved = PreserveDataDirectories(component, installDirectory);
            cleanupPath = installDirectory;
            stateRestored = false;

            // Onceki BASARISIZ kurulumdan kalan kismi durum yeni kurulumu bozar:
            // PostgreSQL'de data dizini doluysa initdb "cluster init failed" verir; MSI/zip'te
            // kilitli/yarim dosyalar cakisir. Temiz kurulum icin dizini sifirla (servis durduruldu).
            try
            {
                installationFilesChanged = true;
                DeleteManagedPath(installDirectory, component.Id);
                await ExecuteInstallAsync(component, downloadPath, installDirectory, executionArgs, cancellationToken);
            }
            finally
            {
                // Çıkarma/iptal hatasında da veriyi geri koy. Geri koyma başarısızsa
                // postInstall ve yıkıcı rollback çalışamaz; kurtarma yolu hata içinde kalır.
                preserved.Restore();
                stateRestored = true;
            }

            await ApplyPostInstallStepsAsync(component, installDirectory, generatedPassword, cancellationToken);
            await ComponentWebPublication.RestoreAsync(component.Id, installDirectory, cancellationToken);

            // KURULAN SURUM GERI OKUNUR. Asagida kaydedilecek surum artik manifest'in ISTEDIGI
            // degil, dogrulama sirasinda diskten OLCULEN surumdur (bkz. ComponentVersionProbe).
            var versionRecord = ComponentVersionProbe.Decide(
                component.Version,
                await ValidateInstallationAsync(component, installDirectory, cancellationToken, generatedPassword));

            var protectedConfiguration = string.IsNullOrWhiteSpace(generatedPassword)
                ? string.Empty
                : secretProtector.Protect(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [GeneratedPasswordKey] = generatedPassword
                }, SerializerOptions));

            var installedComponent = await dbContext.InstalledComponents
                .FirstOrDefaultAsync(item => item.ComponentId == component.Id, cancellationToken)
                ?? new InstalledComponent
                {
                    ComponentId = component.Id
                };

            installedComponent.DisplayName = component.DisplayName;
            installedComponent.Version = versionRecord.Version;
            installedComponent.Type = ParseComponentType(component.Type);
            installedComponent.InstallDir = installDirectory;
            installedComponent.ServiceName = component.ServiceName;
            installedComponent.Status = ComponentInstallStatus.Installed;
            installedComponent.ProtectedConfigurationJson = protectedConfiguration;
            installedComponent.LastError = string.Empty;
            installedComponent.InstalledAtUtc = DateTime.UtcNow;
            installedComponent.UpdatedAtUtc = DateTime.UtcNow;

            if (dbContext.Entry(installedComponent).State == EntityState.Detached)
            {
                dbContext.InstalledComponents.Add(installedComponent);
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            // KAYIT OLCUMDEN BESLENIR. Her sey yolundayken cumle SADE kalir ("version 1.2.3");
            // olculemediyse ya da sapma varsa ADIYLA yazilir (ComponentVersionRecord.Describe).
            await auditService.WriteAsync(
                "software.install.completed",
                "software",
                $"Component '{component.DisplayName}' installed into '{installDirectory}' ({versionRecord.Describe()}).",
                actorUserId,
                ipAddress,
                cancellationToken);

            operationProgress.CompleteStep(component.DisplayName, success: true);
            return new ComponentInstallResultDto
            {
                ComponentId = component.Id,
                DisplayName = component.DisplayName,
                Version = versionRecord.Version,
                VersionMeasured = versionRecord.Measured,
                Status = ComponentInstallStatus.Installed.ToString(),
                InstallDir = installDirectory,
                ServiceName = component.ServiceName,
                GeneratedPassword = generatedPassword,
                Message = "software.install.completed"
            };
        }
        catch (AppException exception)
        {
            operationProgress.CompleteStep(component.DisplayName, success: false, FormatInstallError(exception));
            logger.LogWarning(exception, "Component installation blocked for {ComponentId}", component.Id);
            await UpsertFailedInstallAsync(component, installDirectory, previous, installationFilesChanged, FormatInstallError(exception), cancellationToken);
            await auditService.WriteAsync(
                "software.install.failed",
                "software",
                $"Component '{component.DisplayName}' failed to install: {FormatInstallError(exception)}",
                actorUserId,
                ipAddress,
                cancellationToken);

            if (stateRestored) await RollbackFailedInstallAsync(component, cleanupPath, cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            operationProgress.CompleteStep(component.DisplayName, success: false, exception.Message);
            logger.LogError(exception, "Component installation failed for {ComponentId}", component.Id);
            await UpsertFailedInstallAsync(component, installDirectory, previous, installationFilesChanged, exception.Message, cancellationToken);
            await auditService.WriteAsync(
                "software.install.failed",
                "software",
                $"Component '{component.DisplayName}' failed to install: {exception.Message}",
                actorUserId,
                ipAddress,
                cancellationToken);

            if (stateRestored) await RollbackFailedInstallAsync(component, cleanupPath, cancellationToken);
            throw new AppException("software.install_failed", 500, 500, BuildDiagnosticDetails(exception));
        }
    }

    private InstalledComponentDto MapInstalledComponent(
        InstalledComponent component,
        IReadOnlyDictionary<string, ComponentUpdateInfo> updateCache)
    {
        var configuration = ReadProtectedConfiguration(component.ProtectedConfigurationJson);
        updateCache.TryGetValue(component.ComponentId, out var updateInfo);
        return new InstalledComponentDto
        {
            Id = component.Id,
            ComponentId = component.ComponentId,
            DisplayName = component.DisplayName,
            Version = component.Version,
            Type = component.Type.ToString(),
            InstallDir = component.InstallDir,
            ServiceName = component.ServiceName,
            Status = component.Status.ToString(),
            LastError = component.LastError,
            GeneratedPassword = ReadConfigurationValue(configuration, GeneratedPasswordKey),
            IsSystemDetected = ReadBoolean(configuration, SystemDetectedKey),
            DetectionSource = ReadConfigurationValue(configuration, DetectionSourceKey),
            AvailableVersion = updateInfo?.AvailableVersion ?? string.Empty,
            UpdateAvailable = !ReadBoolean(configuration, SystemDetectedKey) && component.Status == ComponentInstallStatus.Installed && updateInfo is not null && IsNewerVersion(updateInfo.AvailableVersion, component.Version),
            VersionCheckSupported = updateInfo?.VersionCheckSupported ?? SupportsVersionCheck(component.ComponentId),
            InstalledAtUtc = component.InstalledAtUtc
        };
    }

    private string UpdateCacheFilePath()
        => Path.Combine(
            Environment.ExpandEnvironmentVariables(runtimeOptions.DataRoot),
            "config",
            "component-updates.json");

    private Dictionary<string, ComponentUpdateInfo> ReadUpdateCache()
    {
        try
        {
            return ComponentUpdateCache.Read(UpdateCacheFilePath());
        }
        catch (Exception exception)
        {
            logger.LogInformation(exception, "Component update cache could not be read.");
            return new Dictionary<string, ComponentUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteUpdateCache(Dictionary<string, ComponentUpdateInfo> cache)
    {
        try
        {
            ComponentUpdateCache.Write(UpdateCacheFilePath(), cache);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Component update cache could not be written.");
        }
    }

    /// <summary>
    /// İki sürümü sayısal noktalı parçalara ayırıp karşılaştırır. Sürümlerden biri
    /// ("latest", "14.x" gibi) sayısal değilse güncelleme önerilmez (false).
    /// </summary>
    internal static bool IsNewerVersion(string available, string installed)
    {
        var a = ParseVersionParts(available);
        var b = ParseVersionParts(installed);
        if (a is null || b is null)
        {
            return false;
        }

        var length = Math.Max(a.Length, b.Length);
        for (var index = 0; index < length; index++)
        {
            var availablePart = index < a.Length ? a[index] : 0;
            var installedPart = index < b.Length ? b[index] : 0;
            if (availablePart != installedPart)
            {
                return availablePart > installedPart;
            }
        }

        return false;
    }

    /// <summary>Sürüm karşılaştırması: sol &gt; sağ ise pozitif, eşitse 0, küçükse negatif.</summary>
    internal static int CompareVersions(string? left, string? right)
    {
        var a = ParseVersionParts(left);
        var b = ParseVersionParts(right);
        if (a is null || b is null)
        {
            return 0;
        }

        var length = Math.Max(a.Length, b.Length);
        for (var index = 0; index < length; index++)
        {
            var leftPart = index < a.Length ? a[index] : 0;
            var rightPart = index < b.Length ? b[index] : 0;
            if (leftPart != rightPart)
            {
                return leftPart.CompareTo(rightPart);
            }
        }

        return 0;
    }

    private static int[]? ParseVersionParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = Regex.Match(version, @"\d+(?:\.\d+)+|\d+");
        if (!match.Success)
        {
            return null;
        }

        return match.Value
            .Split('.')
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToArray();
    }

    private string ReadGeneratedPassword(string protectedConfigurationJson)
        => ReadConfigurationValue(ReadProtectedConfiguration(protectedConfigurationJson), GeneratedPasswordKey);

    private Dictionary<string, string> ReadProtectedConfiguration(string? protectedConfigurationJson)
    {
        if (string.IsNullOrWhiteSpace(protectedConfigurationJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var payload = secretProtector.Unprotect(protectedConfigurationJson);
            var configuration = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, SerializerOptions);
            return configuration is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(configuration, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to read protected configuration for component.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ReadConfigurationValue(
        IReadOnlyDictionary<string, string> configuration,
        string key)
        => configuration.TryGetValue(key, out var value) ? value : string.Empty;

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, string> configuration,
        string key)
        => configuration.TryGetValue(key, out var value) &&
            bool.TryParse(value, out var parsed) &&
            parsed;

    private async Task RunDiscoveredUninstallerAsync(
        string uninstallCommand,
        CancellationToken cancellationToken)
    {
        var expanded = Environment.ExpandEnvironmentVariables(uninstallCommand).Trim();
        var executable = ComponentDiscoveryService.ExtractExecutablePath(expanded);
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Detected application uninstall command is invalid.");
        }

        var executableToken = expanded.StartsWith('"')
            ? $"\"{executable}\""
            : expanded[..Math.Min(expanded.Length, executable.Length)];
        var arguments = expanded.Length > executableToken.Length
            ? expanded[executableToken.Length..].Trim()
            : string.Empty;

        await RunProcessAsync(executable, arguments, cancellationToken);
    }

    private static void DeleteDetectedInstallPath(string installDirectory, string detectionExecutable)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return;
        }

        var candidate = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(candidate)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        var protectedRoots = new[]
        {
            root,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd(Path.DirectorySeparatorChar),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd(Path.DirectorySeparatorChar),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd(Path.DirectorySeparatorChar),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar)
        };

        if (protectedRoots.Any(path =>
                !string.IsNullOrWhiteSpace(path) &&
                string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Refusing to remove protected directory '{candidate}'.");
        }

        if (!string.IsNullOrWhiteSpace(detectionExecutable))
        {
            var marker = Path.GetFullPath(detectionExecutable);
            if (!marker.StartsWith(EnsureTrailingSeparator(candidate), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Detected executable is outside the installation directory.");
            }
        }

        Directory.Delete(candidate, recursive: true);
    }

    private async Task RollbackFailedInstallAsync(
        ComponentManifestItemDto component,
        string cleanupPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cleanupPath))
        {
            return;
        }

        try
        {
            await TryRemoveServiceAsync(component.ServiceName, cancellationToken);
            await StopManagedRuntimeAsync(component.ServiceName, cleanupPath, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed installation service rollback failed for {ComponentId}",
                component.Id);
        }

        var normalizedType = component.Type.Trim().ToLowerInvariant();
        if (normalizedType is "msi" or "installer")
        {
            try
            {
                await TryRunManagedUninstallerAsync(
                    component,
                    component.Id,
                    cleanupPath,
                    cancellationToken);
                DeleteManagedPath(cleanupPath, component.Id);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed installer rollback failed for component {ComponentId}",
                    component.Id);
            }

            return;
        }

        if (normalizedType is not ("zip" or "targz" or "phar" or "phpfile"))
        {
            return;
        }

        try
        {
            // GERI ALMA DA VERIYI SILMEMELI. Kurulum postInstall'da patlarsa buraya gelinir ve
            // burada dizin komple silinirdi — veri klasörü kurulum dizininin içinde olduğu için
            // başarısız bir YÜKSELTME, çalışan bir veritabanının tüm verisini yok ederdi.
            // Binary'ler silinir, veri geri konur; yönetici en kötü ihtimalle bileşeni yeniden kurar.
            var preserved = PreserveDataDirectories(component, cleanupPath);
            try { DeleteManagedPath(cleanupPath, component.Id); }
            finally { preserved.Restore(); }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Rollback cleanup failed for path {CleanupPath}", cleanupPath);
        }
    }

    private async Task UpsertFailedInstallAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        InstalledComponent? previous,
        bool installationFilesChanged,
        string error,
        CancellationToken cancellationToken)
    {
        var installedComponent = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == component.Id, cancellationToken)
            ?? new InstalledComponent
            {
                ComponentId = component.Id
            };

        installedComponent.DisplayName = component.DisplayName;
        // A requested version is not evidence of an installed version. Before replacement the
        // previous record still describes the files; after replacement/rollback it is unknown.
        installedComponent.Version = installationFilesChanged ? string.Empty : previous?.Version ?? string.Empty;
        installedComponent.Type = ParseComponentType(component.Type);
        installedComponent.InstallDir = installDirectory;
        installedComponent.ServiceName = component.ServiceName;
        installedComponent.Status = ComponentInstallStatus.Failed;
        installedComponent.LastError = error;
        installedComponent.UpdatedAtUtc = DateTime.UtcNow;

        if (dbContext.Entry(installedComponent).State == EntityState.Detached)
        {
            dbContext.InstalledComponents.Add(installedComponent);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<ComponentManifestItemDto>> LoadManifestAsync(CancellationToken cancellationToken)
    {
        var manifestPath = ResolveManifestPath();
        if (!File.Exists(manifestPath))
        {
            throw new AppException("software.manifest_not_found", 404, 404);
        }

        await using var stream = File.OpenRead(manifestPath);
        var document = await JsonSerializer.DeserializeAsync<ComponentManifestDocument>(stream, SerializerOptions, cancellationToken);

        var components = document?.Components ?? [];
        ValidateManifest(components);
        return components
            .OrderBy(component => component.DisplayName)
            .ToArray();
    }

    internal static void ValidateManifest(IReadOnlyCollection<ComponentManifestItemDto> components)
    {
        var byId = new Dictionary<string, ComponentManifestItemDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.Id) ||
                string.IsNullOrWhiteSpace(component.DisplayName) ||
                string.IsNullOrWhiteSpace(component.Type) ||
                string.IsNullOrWhiteSpace(component.DownloadUrl) ||
                string.IsNullOrWhiteSpace(component.InstallDir))
            {
                throw new AppException("software.manifest_invalid", 500, 500, $"Component={component.Id}");
            }

            if (!Uri.TryCreate(component.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new AppException(
                    "software.manifest_invalid",
                    500,
                    500,
                    $"Component={component.Id}\r\nDownloadUrl={component.DownloadUrl}");
            }

            if (!byId.TryAdd(component.Id, component))
            {
                throw new AppException(
                    "software.manifest_invalid",
                    500,
                    500,
                    $"Duplicate component id: {component.Id}");
            }

            _ = ParseComponentType(component.Type);
        }

        foreach (var component in components)
        {
            foreach (var dependencyId in component.Dependencies)
            {
                if (!byId.ContainsKey(dependencyId))
                {
                    throw new AppException(
                        "software.dependency_not_found",
                        500,
                        500,
                        $"Component={component.Id}\r\nDependency={dependencyId}");
                }

                if (string.Equals(component.Id, dependencyId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppException(
                        "software.dependency_cycle",
                        500,
                        500,
                        $"Component={component.Id}");
                }
            }

            foreach (var postInstallStep in component.PostInstall)
            {
                if (!KnownPostInstallSteps.Contains(postInstallStep))
                {
                    throw new AppException(
                        "software.manifest_invalid",
                        500,
                        500,
                        $"Component={component.Id}\r\nPostInstall={postInstallStep}");
                }
            }
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components)
        {
            VisitDependencyGraph(component, byId, visited, visiting);
        }
    }

    private static void VisitDependencyGraph(
        ComponentManifestItemDto component,
        IReadOnlyDictionary<string, ComponentManifestItemDto> components,
        HashSet<string> visited,
        HashSet<string> visiting)
    {
        if (visited.Contains(component.Id))
        {
            return;
        }

        if (!visiting.Add(component.Id))
        {
            throw new AppException(
                "software.dependency_cycle",
                500,
                500,
                $"Component={component.Id}");
        }

        foreach (var dependencyId in component.Dependencies)
        {
            VisitDependencyGraph(components[dependencyId], components, visited, visiting);
        }

        visiting.Remove(component.Id);
        visited.Add(component.Id);
    }

    private static string ResolveInstallDirectory(string installDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(installDirectory);
        if (Path.IsPathRooted(expanded))
        {
            return expanded;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));
    }

    private static string ResolveDownloadFileName(ComponentManifestItemDto component)
    {
        var downloadUri = new Uri(component.DownloadUrl);
        var uriFileName = Path.GetFileName(downloadUri.LocalPath);
        return string.IsNullOrWhiteSpace(uriFileName) ? $"{component.Id}.bin" : uriFileName;
    }

    private static string ResolveManifestPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "components.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, "components.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "components.json");
    }

    private async Task<ComponentManifestItemDto> ResolveDynamicComponentAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var normalizedId = component.Id.Trim().ToLowerInvariant();
        switch (normalizedId)
        {
            case "adminer":
                return await ResolveLatestGitHubAssetAsync(
                    component,
                    client,
                    "vrana/adminer",
                    assetName =>
                        assetName.EndsWith(".php", StringComparison.OrdinalIgnoreCase) &&
                        !assetName.Contains("mysql", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);

            case "memcached":
                return await ResolveLatestGitHubAssetAsync(
                    component,
                    client,
                    "jefyt/memcached-windows",
                    assetName =>
                        assetName.StartsWith("memcached-", StringComparison.OrdinalIgnoreCase) &&
                        assetName.EndsWith("win64-mingw.zip", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);

            case "nodejs-lts":
                return await ResolveLatestNodeLtsAsync(component, client, cancellationToken);

            // Webmail: GitHub releases'ta "roundcubemail-<surum>-complete.tar.gz". Manifeste sabit
            // surum yazilirsa asla guncelleme gorunmez — webmail internete acik oldugu icin
            // guncel kalmasi onemli.
            case "roundcube":
                return await ResolveLatestGitHubAssetAsync(
                    component,
                    client,
                    "roundcube/roundcubemail",
                    assetName =>
                        assetName.StartsWith("roundcubemail-", StringComparison.OrdinalIgnoreCase) &&
                        assetName.EndsWith("-complete.tar.gz", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);

            // --- Manifeste sabitlenmis surumler icin canli cozumleyiciler ---
            // ORTAK KURAL: hepsi bilesenin KENDI SERISINDE kalir (php-8.3 -> 8.3.x, mariadb-11 -> 11.8.x,
            // postgresql-16 -> 16.x). Seri atlamak (8.3 -> 8.4) uyumluluk kararidir ve panel surumuyle
            // gelir; guncelleme kontrolu sessizce major yukseltme YAPMAZ.
            case "php-8.3":
                return await ResolveLatestPhpAsync(component, client, cancellationToken);

            case "mariadb-11":
                return await ResolveLatestMariaDbAsync(component, client, cancellationToken);

            case "mysql-8":
                return await ResolveLatestMysqlAsync(component, client, cancellationToken);

            case "postgresql-16":
                return await ResolveLatestPostgresAsync(component, client, cancellationToken);

            case "pgadmin4":
                return await ResolveLatestPgAdminAsync(component, client, cancellationToken);

            case "nginx":
                return await ResolveLatestNginxAsync(component, client, cancellationToken);

            case "python-3":
                return await ResolveLatestPythonAsync(component, client, cancellationToken);

            case "redis":
                return await ResolveLatestGitHubAssetAsync(
                    component,
                    client,
                    "tporadowski/redis",
                    assetName =>
                        assetName.StartsWith("Redis-x64-", StringComparison.OrdinalIgnoreCase) &&
                        assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);

            // Antivirus imza motoru: eski surum = yeni tehditleri kacirma.
            case "clamav":
                return await ResolveLatestGitHubAssetAsync(
                    component,
                    client,
                    "Cisco-Talos/clamav",
                    assetName =>
                        assetName.EndsWith(".win.x64.zip", StringComparison.OrdinalIgnoreCase) &&
                        !assetName.Contains("debug", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);

            case "win-acme":
                return await ResolveLatestGitHubAssetAsync(
                    component,
                    client,
                    "win-acme/win-acme",
                    assetName => assetName.EndsWith(".x64.trimmed.zip", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);

            case "composer":
                return await ResolveChecksumTextAsync(
                    component,
                    client,
                    "https://getcomposer.org/download/latest-stable/composer.phar.sha256",
                    cancellationToken);

            case "phpmyadmin":
                return await ResolveChecksumTextAsync(
                    component,
                    client,
                    $"{component.DownloadUrl}.sha256",
                    cancellationToken);

            case "docker-engine":
                // Docker's official Windows static directory does not publish checksums.
                // Keep the manifest-pinned package so ZIP integrity remains verifiable.
                return component;

            case "stalwart-mail":
                return await ResolveLatestGitHubAssetAsync(
                    component,
                    client,
                    "stalwartlabs/stalwart",
                    assetName => assetName.Equals("stalwart-x86_64-pc-windows-msvc.zip", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);

            case "stalwart-cli":
                return await ResolveLatestGitHubAssetAsync(
                    component,
                    client,
                    "stalwartlabs/cli",
                    assetName => assetName.Equals("stalwart-cli-x86_64-pc-windows-msvc.zip", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);
        }

        if (!component.Id.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
        {
            return component;
        }

        var phpBranch = component.Id["php-".Length..];
        if (phpBranch.Count(character => character == '.') != 1 ||
            phpBranch.Any(character => !char.IsDigit(character) && character != '.'))
        {
            return component;
        }

        try
        {
            using var response = await client.GetAsync(
                "https://downloads.php.net/~windows/releases/releases.json",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Unable to resolve latest PHP package for {ComponentId}. Status={StatusCode}",
                    component.Id,
                    response.StatusCode);
                return component;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty(phpBranch, out var release) ||
                !release.TryGetProperty("nts-vs16-x64", out var ntsPackage) ||
                !ntsPackage.TryGetProperty("zip", out var zipPackage) ||
                !zipPackage.TryGetProperty("path", out var zipPathElement))
            {
                return component;
            }

            var zipPath = zipPathElement.GetString();
            if (string.IsNullOrWhiteSpace(zipPath))
            {
                return component;
            }

            var version = release.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString() ?? component.Version
                : component.Version;
            var sha256 = zipPackage.TryGetProperty("sha256", out var shaElement)
                ? shaElement.GetString() ?? component.Sha256
                : component.Sha256;

            logger.LogInformation(
                "Resolved {ComponentId} to PHP {PhpVersion} package {PackagePath}.",
                component.Id,
                version,
                zipPath);

            return CopyComponent(
                component,
                version: version,
                downloadUrl: $"https://downloads.php.net/~windows/releases/{zipPath}",
                sha256: sha256);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to resolve latest PHP package for {ComponentId}.", component.Id);
            return component;
        }
    }

    private async Task<ComponentManifestItemDto> ResolveDockerEngineAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        const string baseUrl = "https://download.docker.com/win/static/stable/x86_64/";
        try
        {
            using var response = await client.GetAsync(baseUrl, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Unable to list Docker static binaries. Status={StatusCode}", response.StatusCode);
                return component;
            }

            var listing = await response.Content.ReadAsStringAsync(cancellationToken);
            var latest = Regex.Matches(listing, @"docker-(\d+\.\d+\.\d+)\.zip", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .Select(value => Version.TryParse(value, out var parsed) ? parsed : null)
                .Where(version => version is not null)
                .OrderByDescending(version => version)
                .FirstOrDefault();

            if (latest is null)
            {
                logger.LogWarning("Could not determine the latest Docker engine version from the listing.");
                return component;
            }

            var url = $"{baseUrl}docker-{latest}.zip";
            logger.LogInformation("Resolved Docker engine to {Url}", url);
            return CopyComponent(component, version: latest.ToString(), downloadUrl: url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to resolve the latest Docker engine package.");
            return component;
        }
    }

    private async Task<ComponentManifestItemDto> ResolveLatestGitHubAssetAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        string repository,
        Func<string, bool> assetPredicate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("AdaPanel");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Unable to resolve latest GitHub release for {ComponentId}. Repository={Repository}, Status={StatusCode}",
                    component.Id,
                    repository,
                    response.StatusCode);
                return component;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var tagName = document.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? component.Version
                : component.Version;

            if (!document.RootElement.TryGetProperty("assets", out var assets))
            {
                return component;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;
                if (!assetPredicate(name) ||
                    !asset.TryGetProperty("browser_download_url", out var urlElement))
                {
                    continue;
                }

                var downloadUrl = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                var sha256 = asset.TryGetProperty("digest", out var digestElement)
                    ? ParseSha256(digestElement.GetString())
                    : null;
                if (string.IsNullOrWhiteSpace(sha256))
                {
                    logger.LogWarning(
                        "GitHub release asset for {ComponentId} does not publish a SHA256 digest. Keeping the pinned manifest package.",
                        component.Id);
                    return component;
                }

                logger.LogInformation(
                    "Resolved {ComponentId} to GitHub release {ReleaseTag} asset {AssetName}.",
                    component.Id,
                    tagName,
                    name);

                return CopyComponent(
                    component,
                    version: tagName.TrimStart('v'),
                    downloadUrl: downloadUrl,
                    sha256: sha256);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to resolve GitHub package for {ComponentId}.", component.Id);
        }

        return component;
    }

    private async Task<ComponentManifestItemDto> ResolveLatestNodeLtsAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                "https://nodejs.org/dist/index.json",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Unable to resolve latest Node.js LTS package for {ComponentId}. Status={StatusCode}",
                    component.Id,
                    response.StatusCode);
                return component;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (!release.TryGetProperty("lts", out var ltsElement) ||
                    ltsElement.ValueKind != JsonValueKind.String ||
                    !release.TryGetProperty("version", out var versionElement) ||
                    !release.TryGetProperty("files", out var filesElement))
                {
                    continue;
                }

                var hasWindowsX64Msi = filesElement
                    .EnumerateArray()
                    .Any(file => string.Equals(file.GetString(), "win-x64-msi", StringComparison.OrdinalIgnoreCase));
                if (!hasWindowsX64Msi)
                {
                    continue;
                }

                var versionWithPrefix = versionElement.GetString();
                if (string.IsNullOrWhiteSpace(versionWithPrefix))
                {
                    continue;
                }

                var version = versionWithPrefix.TrimStart('v');
                var fileName = $"node-{versionWithPrefix}-x64.msi";
                var sha256 = await ResolveChecksumFromListAsync(
                    client,
                    $"https://nodejs.org/dist/{versionWithPrefix}/SHASUMS256.txt",
                    fileName,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(sha256))
                {
                    logger.LogWarning(
                        "Unable to resolve the official Node.js checksum for {Version}. Keeping the pinned manifest package.",
                        version);
                    return component;
                }

                return CopyComponent(
                    component,
                    version: version,
                    downloadUrl: $"https://nodejs.org/dist/{versionWithPrefix}/{fileName}",
                    sha256: sha256);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to resolve latest Node.js LTS package for {ComponentId}.", component.Id);
        }

        return component;
    }

    /// <summary>Sürüm dizesinin ilk <paramref name="parts"/> parçası ("8.3.31", 2 → "8.3").</summary>
    internal static string VersionSeries(string version, int parts)
    {
        var segments = (version ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length < parts ? string.Empty : string.Join('.', segments.Take(parts));
    }

    /// <summary>
    /// Aday sürümlerden, bileşenin KENDİ serisinde kalan ve kurulu olandan yeni olanları en
    /// yeniden eskiye sıralar. Seri filtresi kritik: nginx sayfası hem 1.31.x hem 1.30.x listeler,
    /// Python dizini 3.12 ile 3.14'ü yan yana tutar — dal atlamak uyumluluğu bozar.
    /// </summary>
    internal static IReadOnlyList<string> SelectNewerInSeries(
        IEnumerable<string> candidates,
        string currentVersion,
        int seriesParts)
    {
        var series = VersionSeries(currentVersion, seriesParts);
        if (string.IsNullOrEmpty(series))
        {
            return [];
        }

        return candidates
            .Where(version => VersionSeries(version, seriesParts) == series)
            .Where(version => CompareVersions(version, currentVersion) > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, Comparer<string>.Create(CompareVersions))
            .ToArray();
    }

    /// <summary>Bir URL'in gerçekten indirilebilir olduğunu HEAD ile doğrular (yanlış sürüm yazmayalım).</summary>
    private static async Task<bool> UrlExistsAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// releases.json'daki bir dal nesnesinden (ör. "8.3") sürüm + Windows zip adı + sha256 çıkarır.
    /// Kurulu paketin lezzeti (nts-vs16-x64 gibi) korunur; yoksa herhangi bir nts-x64 kullanılır.
    /// ts (thread-safe) paketler ELENİR: panel FastCGI ile nts kurar.
    /// </summary>
    internal static (string Version, string FileName, string Sha256)? ParsePhpBranch(
        JsonElement branch,
        string currentDownloadUrl)
    {
        if (branch.ValueKind != JsonValueKind.Object ||
            !branch.TryGetProperty("version", out var versionElement))
        {
            return null;
        }

        var version = versionElement.GetString();
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var flavours = branch
            .EnumerateObject()
            .Where(property =>
                property.Name.StartsWith("nts-", StringComparison.OrdinalIgnoreCase) &&
                property.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Object)
            // Kurulu URL hangi derleyici etiketini içeriyorsa (vs16/vs17) o tercih edilir.
            .OrderByDescending(property => currentDownloadUrl.Contains(
                property.Name[4..],
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(property => property.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var flavour in flavours)
        {
            if (!flavour.Value.TryGetProperty("zip", out var zip) ||
                !zip.TryGetProperty("path", out var pathElement) ||
                !zip.TryGetProperty("sha256", out var shaElement))
            {
                continue;
            }

            var path = pathElement.GetString();
            var sha256 = shaElement.GetString();
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(sha256))
            {
                return (version, path, sha256);
            }
        }

        return null;
    }

    /// <summary>
    /// MariaDB REST cevabından, kurulu sürümden yeni olan EN GÜNCEL sürümün winx64 zip'ini ve
    /// sha256'sını çıkarır.
    /// </summary>
    internal static (string Version, string FileName, string Sha256)? ParseMariaDbReleases(
        JsonElement root,
        string currentVersion)
    {
        if (!root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var ordered = releases
            .EnumerateObject()
            .Select(property => property.Name)
            .Where(name => CompareVersions(name, currentVersion) > 0)
            .OrderByDescending(name => name, Comparer<string>.Create(CompareVersions));

        foreach (var version in ordered)
        {
            if (!releases.TryGetProperty(version, out var release) ||
                !release.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var file in files.EnumerateArray())
            {
                var fileName = file.TryGetProperty("file_name", out var nameElement) ? nameElement.GetString() : null;
                if (fileName is null || !fileName.EndsWith("winx64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sha256 = file.TryGetProperty("checksum", out var checksum) &&
                             checksum.TryGetProperty("sha256sum", out var shaElement)
                    ? shaElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(sha256))
                {
                    return (version, fileName, sha256);
                }
            }
        }

        return null;
    }

    // PHP: windows.php.net/downloads/releases/releases.json dal başına EN GÜNCEL yamayı ve
    // dosyanın SHA256'sını verir → tam doğrulama korunur (checksum muafiyeti gerekmez).
    private async Task<ComponentManifestItemDto> ResolveLatestPhpAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var branch = VersionSeries(component.Version, 2);
            if (string.IsNullOrEmpty(branch))
            {
                return component;
            }

            using var response = await client.GetAsync(
                "https://windows.php.net/downloads/releases/releases.json",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return component;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty(branch, out var branchElement) ||
                !branchElement.TryGetProperty("version", out var versionElement))
            {
                return component;
            }

            var parsed = ParsePhpBranch(branchElement, component.DownloadUrl);
            if (parsed is null)
            {
                return component;
            }

            return CopyComponent(
                component,
                version: parsed.Value.Version,
                downloadUrl: $"https://windows.php.net/downloads/releases/{parsed.Value.FileName}",
                sha256: parsed.Value.Sha256);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "PHP sürümü çözülemedi ({ComponentId}); manifest sürümü korunuyor.", component.Id);
        }

        return component;
    }

    // MariaDB: resmi REST API seri içindeki sürümleri ve her dosyanın sha256'sını verir.
    private async Task<ComponentManifestItemDto> ResolveLatestMariaDbAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var series = VersionSeries(component.Version, 2);
            if (string.IsNullOrEmpty(series))
            {
                return component;
            }

            using var response = await client.GetAsync(
                $"https://downloads.mariadb.org/rest-api/mariadb/{series}/",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return component;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var parsed = ParseMariaDbReleases(document.RootElement, component.Version);
            if (parsed is not null)
            {
                return CopyComponent(
                    component,
                    version: parsed.Value.Version,
                    // API http:// döndürebiliyor; kalıcı arşiv adresini HTTPS ile kuruyoruz.
                    downloadUrl: $"https://archive.mariadb.org/mariadb-{parsed.Value.Version}/winx64-packages/{parsed.Value.FileName}",
                    sha256: parsed.Value.Sha256);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "MariaDB sürümü çözülemedi ({ComponentId}); manifest sürümü korunuyor.", component.Id);
        }

        return component;
    }

    // MySQL: dev.mysql.com kazımayı engelliyor (403) ve checksum sidecar'ı yok. CDN ise olmayan
    // dosyaya 404, olana 200 döndüğü için yama numarasını ileri YOKLAYARAK güvenilir şekilde bulunur.
    private async Task<ComponentManifestItemDto> ResolveLatestMysqlAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var series = VersionSeries(component.Version, 2);
            var parts = component.Version.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (string.IsNullOrEmpty(series) || parts.Length < 3 || !int.TryParse(parts[2], out var patch))
            {
                return component;
            }

            var bestVersion = component.Version;
            var bestUrl = component.DownloadUrl;
            var misses = 0;

            for (var candidate = patch + 1; candidate <= patch + 15 && misses < 2; candidate++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = $"{series}.{candidate}";
                var url = $"https://cdn.mysql.com/Downloads/MySQL-{series}/mysql-{version}-winx64.zip";
                if (await UrlExistsAsync(client, url, cancellationToken))
                {
                    bestVersion = version;
                    bestUrl = url;
                    misses = 0;
                }
                else
                {
                    misses++;
                }
            }

            return bestVersion == component.Version
                ? component
                : CopyComponent(component, version: bestVersion, downloadUrl: bestUrl, sha256: string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "MySQL sürümü çözülemedi ({ComponentId}); manifest sürümü korunuyor.", component.Id);
        }

        return component;
    }

    // PostgreSQL: postgresql.org/versions.json her ana sürümün güncel yamasını bildirir; paket EDB'de.
    private async Task<ComponentManifestItemDto> ResolveLatestPostgresAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var major = VersionSeries(component.Version, 1);
            if (string.IsNullOrEmpty(major))
            {
                return component;
            }

            using var response = await client.GetAsync(
                "https://www.postgresql.org/versions.json",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return component;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("major", out var majorElement) ||
                    !string.Equals(majorElement.GetString(), major, StringComparison.Ordinal) ||
                    !entry.TryGetProperty("latestMinor", out var minorElement))
                {
                    continue;
                }

                var minor = minorElement.GetString();
                if (string.IsNullOrWhiteSpace(minor))
                {
                    return component;
                }

                var version = $"{major}.{minor}";
                var url = $"https://get.enterprisedb.com/postgresql/postgresql-{version}-1-windows-x64.exe";
                if (CompareVersions(version, component.Version) <= 0 ||
                    !await UrlExistsAsync(client, url, cancellationToken))
                {
                    return component;
                }

                return CopyComponent(component, version: version, downloadUrl: url, sha256: string.Empty);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "PostgreSQL sürümü çözülemedi ({ComponentId}); manifest sürümü korunuyor.", component.Id);
        }

        return component;
    }

    // pgAdmin: resmi versions.json (uygulamanın kendi güncelleme kontrolü de bunu kullanır).
    private async Task<ComponentManifestItemDto> ResolveLatestPgAdminAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                "https://www.pgadmin.org/versions.json",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return component;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("pgadmin4", out var entry) ||
                !entry.TryGetProperty("version", out var versionElement))
            {
                return component;
            }

            var version = versionElement.GetString();
            if (string.IsNullOrWhiteSpace(version) || CompareVersions(version, component.Version) <= 0)
            {
                return component;
            }

            var url = $"https://ftp.postgresql.org/pub/pgadmin/pgadmin4/v{version}/windows/pgadmin4-{version}-x64.exe";
            return await UrlExistsAsync(client, url, cancellationToken)
                ? CopyComponent(component, version: version, downloadUrl: url, sha256: string.Empty)
                : component;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "pgAdmin sürümü çözülemedi ({ComponentId}); manifest sürümü korunuyor.", component.Id);
        }

        return component;
    }

    // nginx: indirme sayfasındaki sürümlerden AYNI DALDAKİ (ör. 1.31.x) en yenisi alınır.
    private async Task<ComponentManifestItemDto> ResolveLatestNginxAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var branch = VersionSeries(component.Version, 2);
            if (string.IsNullOrEmpty(branch))
            {
                return component;
            }

            var html = await client.GetStringAsync("https://nginx.org/en/download.html", cancellationToken);
            var best = SelectNewerInSeries(
                    Regex.Matches(html, @"nginx-(\d+\.\d+\.\d+)\.zip").Select(match => match.Groups[1].Value),
                    component.Version,
                    2)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(best))
            {
                return component;
            }

            return CopyComponent(
                component,
                version: best,
                downloadUrl: $"https://nginx.org/download/nginx-{best}.zip",
                sha256: string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "nginx sürümü çözülemedi ({ComponentId}); manifest sürümü korunuyor.", component.Id);
        }

        return component;
    }

    // Python: DİKKAT — bir seri güvenlik-yalnız moda geçince (3.12.11+) yeni yamalar KAYNAK olarak
    // yayınlanır, Windows kurulumu ÜRETİLMEZ. Bu yüzden en yeni dizini körlemesine almak bozuk URL
    // uretir; adaylar yüksekten aşağı denenip installer'ı GERÇEKTEN olan ilk sürüm seçilir.
    private async Task<ComponentManifestItemDto> ResolveLatestPythonAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var series = VersionSeries(component.Version, 2);
            if (string.IsNullOrEmpty(series))
            {
                return component;
            }

            var html = await client.GetStringAsync("https://www.python.org/ftp/python/", cancellationToken);
            var candidates = SelectNewerInSeries(
                    Regex.Matches(html, @"href=""(\d+\.\d+\.\d+)/""").Select(match => match.Groups[1].Value),
                    component.Version,
                    2)
                .Take(6)
                .ToArray();

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = $"https://www.python.org/ftp/python/{candidate}/python-{candidate}-amd64.exe";
                if (await UrlExistsAsync(client, url, cancellationToken))
                {
                    return CopyComponent(component, version: candidate, downloadUrl: url, sha256: string.Empty);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Python sürümü çözülemedi ({ComponentId}); manifest sürümü korunuyor.", component.Id);
        }

        return component;
    }

    private async Task<ComponentManifestItemDto> ResolveChecksumTextAsync(
        ComponentManifestItemDto component,
        HttpClient client,
        string checksumUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                checksumUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Unable to resolve checksum for {ComponentId}. Status={StatusCode}",
                    component.Id,
                    response.StatusCode);
                return component;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var sha256 = ParseSha256(content);
            if (string.IsNullOrWhiteSpace(sha256))
            {
                logger.LogWarning("Checksum response for {ComponentId} did not contain a SHA256 value.", component.Id);
                return component;
            }

            return CopyComponent(component, sha256: sha256);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unable to resolve checksum for {ComponentId}.", component.Id);
            return component;
        }
    }

    private static async Task<string?> ResolveChecksumFromListAsync(
        HttpClient client,
        string checksumUrl,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            checksumUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseSha256ForFile(content, fileName);
    }

    // Stream'i parça parça kopyalar; her okuma için idle-timeout uygular. Veri akarken süresiz devam
    // eder (büyük dosya OK) ama akış belirtilen süre veri göndermezse iptal olur (OperationCanceled →
    // DownloadAsync'in retry/timeout dalında yakalanır). Sonsuz asılmayı önler.
    private static async Task CopyWithIdleTimeoutAsync(Stream source, Stream destination, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (true)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleCts.CancelAfter(idleTimeout);
            var read = await source.ReadAsync(buffer.AsMemory(), idleCts.Token);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task DownloadAsync(HttpClient client, string url, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = destinationPath + ".part";
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteFile(temporaryPath);

            try
            {
                using var response = await client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new AppException(
                        "software.download_not_found",
                        404,
                        404,
                        BuildDownloadDetails(url, response));
                }

                if (!response.IsSuccessStatusCode)
                {
                    var failure = new AppException(
                        "software.download_failed",
                        (int)response.StatusCode,
                        502,
                        BuildDownloadDetails(url, response));
                    if (!IsTransientDownloadStatus(response.StatusCode) || attempt == 3)
                    {
                        throw failure;
                    }

                    lastException = failure;
                }
                else
                {
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using (var destination = File.Create(temporaryPath))
                    {
                        // Idle-timeout'lu kopya: büyük/yavaş indirme sorun değil (veri aktıkça sürer),
                        // ama stream 60sn veri göndermezse (takılı sunucu) iptal edilir → retry/fail.
                        await CopyWithIdleTimeoutAsync(source, destination, TimeSpan.FromSeconds(60), cancellationToken);
                    }

                    if (new FileInfo(temporaryPath).Length <= 0)
                    {
                        throw new IOException("The downloaded package is empty.");
                    }

                    await FinalizeDownloadAsync(temporaryPath, destinationPath, cancellationToken);
                    return;
                }
            }
            catch (AppException exception) when (
                exception.MessageKey == "software.download_failed" &&
                attempt < 3)
            {
                lastException = exception;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException($"Download timed out. Url={url}");
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
            }
            catch (IOException exception)
            {
                lastException = exception;
            }

            TryDeleteFile(temporaryPath);
            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }

        TryDeleteFile(temporaryPath);
        throw new AppException(
            "software.download_failed",
            502,
            502,
            lastException is null
                ? $"Url={url}"
                : BuildDownloadDetails(url, lastException));
    }

    private static bool IsTransientDownloadStatus(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // A later install attempt will replace the temporary file.
        }
    }

    /// <summary>
    /// İndirilen .part dosyasını hedefe taşır. Büyük indirmelerde Windows Defender
    /// gerçek-zamanlı tarama dosyayı (kaynak veya hedef) kısa süre kilitleyip
    /// MoveFile'da "Access denied" verebilir; bu yüzden hedefi temizleyip taşımayı
    /// kısa beklemelerle birkaç kez dener.
    /// </summary>
    private static async Task FinalizeDownloadAsync(
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        // Hedef yanlışlıkla bir DIZIN ise Move erişim reddi verir — net hata.
        if (Directory.Exists(destinationPath))
        {
            throw new AppException("software.install_destination_invalid", 500, 500, destinationPath);
        }

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TryDeleteFile(destinationPath);
                // overwrite:true ŞART: hedef silinememişse (AV kilidi vb.) bile üzerine yaz;
                // aksi halde "Halen varolan bir dosya oluşturulamaz" (IOException) atar.
                File.Move(temporaryPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                (exception is UnauthorizedAccessException or IOException) &&
                attempt < 6)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
        }

        // Yedek 1: hedef kilitli/erişimsizse YANA AL (rename), sonra temp'i yerine taşı.
        // Açık (paylaşımlı-silme) dosyaları yeniden adlandırmak çoğu zaman daha güvenilir.
        try
        {
            if (File.Exists(destinationPath))
            {
                var sideline = destinationPath + $".old-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                File.Move(destinationPath, sideline);
                TryDeleteFile(sideline);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return;
        }
        catch (Exception outer) when (outer is UnauthorizedAccessException or IOException)
        {
            // Yedek 2: kopyala (bazı kilit/birim durumlarında taşımadan daha esnek).
            try
            {
                File.Copy(temporaryPath, destinationPath, overwrite: true);
                TryDeleteFile(temporaryPath);
                return;
            }
            catch (Exception inner) when (inner is UnauthorizedAccessException or IOException)
            {
                // Ham .NET stack'i yerine temiz, kullanıcıya gösterilebilir hata.
                throw new AppException(
                    "software.install_file_locked",
                    423,
                    423,
                    $"{destinationPath} :: {inner.Message}");
            }
        }
    }

    /// <summary>
    /// Verilen yolları Windows Defender (Microsoft Defender) tarama istisnasına ekler.
    /// AdaPanel'in kendi yönettiği dizinler için kullanılır; AV yoksa veya yetki yetmezse
    /// sessizce geçilir (kurulumu bloklamaz).
    /// </summary>
    private async Task TryAddDefenderExclusionsAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(path);
                var escaped = path.Replace("'", "''");
                await ProcessRunner.RunPowerShellAsync(
                    $"Add-MpPreference -ExclusionPath '{escaped}' -ErrorAction SilentlyContinue",
                    cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogInformation(
                    exception,
                    "Windows Defender exclusion could not be added for {Path}.",
                    path);
            }
        }
    }

    private static async Task VerifyPackageIntegrityAsync(
        ComponentManifestItemDto component,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(component.Sha256))
        {
            await VerifyChecksumAsync(component.Sha256, filePath, cancellationToken);
            return;
        }

        var normalizedType = component.Type.Trim().ToLowerInvariant();
        if (normalizedType is "msi" or "installer")
        {
            await VerifyAuthenticodeSignatureAsync(filePath, cancellationToken);
            return;
        }

        throw new AppException(
            "software.checksum_required",
            400,
            400,
            $"Component={component.Id}\r\nFile={Path.GetFileName(filePath)}");
    }

    private static void EnsureIntegrityMetadata(ComponentManifestItemDto component)
    {
        var normalizedType = component.Type.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(component.Sha256) ||
            normalizedType is "msi" or "installer" ||
            IsTrustedNoChecksumHost(component.DownloadUrl))
        {
            return;
        }

        throw new AppException(
            "software.checksum_required",
            400,
            400,
            $"Component={component.Id}\r\nUrl={component.DownloadUrl}");
    }

    /// <summary>
    /// Checksum sidecar'ı YAYINLAMAYAN resmi HTTPS dağıtım sunucuları. Bu listeye yalnızca üreticinin
    /// KENDİ dağıtım adresi girer ve yalnızca HTTPS kabul edilir (sertifika + host doğrulaması kalır).
    /// Neden gerekli: bu bileşenlerin sürümü aksi halde manifeste sabit kalır ve sunucu eski, açık
    /// sürümde takılır — yayınlanmamış bir sha256'yı beklemek, bilinen CVE'lerle yaşamaktan daha
    /// riskli. sha256 YAYINLAYAN kaynaklar (PHP, MariaDB, Node, Composer, GitHub) bu listede DEĞİL;
    /// onlarda tam doğrulama zorunlu kalır.
    /// </summary>
    private static readonly string[] TrustedNoChecksumHosts =
    [
        "download.docker.com",   // Docker Engine (static)
        "cdn.mysql.com",         // MySQL Community Server
        "get.enterprisedb.com",  // PostgreSQL (EDB resmi Windows paketi)
        "ftp.postgresql.org",    // pgAdmin 4
        "nginx.org",             // nginx (yalnız PGP imzası yayınlar)
        "www.python.org"         // CPython Windows installer
    ];

    internal static bool IsTrustedNoChecksumHostForTests(string downloadUrl) => IsTrustedNoChecksumHost(downloadUrl);

    private static bool IsTrustedNoChecksumHost(string downloadUrl)
    {
        return Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            TrustedNoChecksumHosts.Any(host =>
                uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task VerifyChecksumAsync(
        string expectedSha256,
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash);

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("software.checksum_mismatch", 400, 400);
        }
    }

    private static async Task VerifyAuthenticodeSignatureAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var escapedPath = filePath.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"$signature = Get-AuthenticodeSignature -LiteralPath '{escapedPath}'\r\n" +
            "[Console]::Out.Write($signature.Status.ToString())\r\n" +
            "if ($signature.SignerCertificate) {\r\n" +
            "  [Console]::Out.Write('|')\r\n" +
            "  [Console]::Out.Write($signature.SignerCertificate.Subject)\r\n" +
            "}";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new AppException("software.signature_invalid", 400, 400);
        // TAVANLI OKUMA: kural ProcessRunner'da tek kaynakta; bu yol kendi surecini kurdugu
        // icin oradan GECMIYORDU. Imza dogrulama araci beklenmedik bicimde cok yazabilir.
        var standardOutputTask = ProcessRunner.TavanliOkuAsync(
            process.StandardOutput, ProcessRunner.DefaultOutputCharLimit, cancellationToken);
        var standardErrorTask = ProcessRunner.TavanliOkuAsync(
            process.StandardError, ProcessRunner.DefaultOutputCharLimit, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode == 0 &&
            standardOutput.StartsWith("Valid|", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new AppException(
            "software.signature_invalid",
            400,
            400,
            $"File={Path.GetFileName(filePath)}\r\nStatus={standardOutput}\r\n{standardError}".Trim());
    }

    internal static string? ParseSha256(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var match = Regex.Match(content, @"(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])");
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    internal static string? ParseSha256ForFile(string? content, string fileName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return ParseSha256(line);
            }
        }

        return null;
    }

    private async Task ExecuteInstallAsync(
        ComponentManifestItemDto component,
        string downloadPath,
        string installDirectory,
        string executionArgs,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(installDirectory);

        switch (component.Type.Trim().ToLowerInvariant())
        {
            case "zip":
                ZipFile.ExtractToDirectory(downloadPath, installDirectory, overwriteFiles: true);
                FlattenSingleTopDirectory(installDirectory);
                break;

            case "targz":
                await ExtractTarGzAsync(downloadPath, installDirectory, cancellationToken);
                FlattenSingleTopDirectory(installDirectory);
                break;

            case "phar":
                File.Copy(downloadPath, Path.Combine(installDirectory, "composer.phar"), overwrite: true);
                break;

            case "phpfile":
                File.Copy(downloadPath, Path.Combine(installDirectory, "adminer.php"), overwrite: true);
                break;

            case "msi":
                await RunProcessAsync("msiexec.exe", $"/i \"{downloadPath}\" {executionArgs}".Trim(), cancellationToken, TimeSpan.FromMinutes(12));
                break;

            case "installer":
                // Sessiz kurulum beklenmedik bir istemde takılırsa 12 dk sonra iptal et
                // (ör. MailEnable /s); aksi halde tüm kurulum zinciri sonsuza dek bloklanır.
                await RunProcessAsync(downloadPath, executionArgs, cancellationToken, TimeSpan.FromMinutes(12));
                break;

            default:
                throw new AppException("software.unsupported_component_type", 400, 400);
        }
    }

    // Roundcube vb. bileşenler yalnızca .tar.gz dağıtır. .NET 8 yerleşik TarFile + GZipStream
    // ile çıkarıyoruz (harici tar.exe gerekmez).
    private static async Task ExtractTarGzAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        await using var fileStream = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, cancellationToken);
    }

    private async Task RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new AppException("software.process_start_failed", 500, 500);

        // Bazı sessiz kurulumlar (ör. MailEnable /s) beklenmedik bir istem yüzünden TAKILABILIR.
        // Bir zaman aşımı verildiyse süre dolduğunda süreci öldür ve net hata fırlat; böylece
        // tüm kurulum zinciri (ilk kurulum sihirbazı dahil) sonsuza kadar bloklanmaz.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            timeoutCts.CancelAfter(limit);
        }

        // TAVANLI OKUMA: bilesen kurulumu dis bir yukleyici calistirir ve onun ne kadar
        // yazacagini panel belirlemez. Kural ProcessRunner'da tek kaynakta.
        var standardOutputTask = ProcessRunner.TavanliOkuAsync(
            process.StandardOutput, ProcessRunner.DefaultOutputCharLimit, cancellationToken);
        var errorOutputTask = ProcessRunner.TavanliOkuAsync(
            process.StandardError, ProcessRunner.DefaultOutputCharLimit, cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeout is not null && !cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* en iyi çaba */ }
            throw new AppException(
                "software.process_timeout",
                500,
                500,
                BuildTimeoutDetails(fileName, arguments, timeout.Value));
        }
        var standardOutput = await standardOutputTask;
        var errorOutput = await errorOutputTask;
        var isWindowsInstaller = string.Equals(
            Path.GetFileName(fileName),
            "msiexec.exe",
            StringComparison.OrdinalIgnoreCase);
        if (process.ExitCode == 0 ||
            isWindowsInstaller && process.ExitCode is 1641 or 3010)
        {
            if (process.ExitCode != 0)
            {
                logger.LogInformation(
                    "Windows Installer completed with reboot-required exit code {ExitCode}. File={FileName}",
                    process.ExitCode,
                    fileName);
            }

            return;
        }

        // GUNLUK DE BIR HEDEFTIR. `Details` maskelendi ama AYNI iki dize buradan Serilog'a HAM
        // gidiyordu: log dosyasi diske yazilir, yedeklenir ve destek icin disari tasinir -- yani
        // silinmesi yanit govdesinden DAHA zor bir yerdir. Dis kurulumcular kendi komut satirlarini
        // (parola dahil) hata metnine basabildigi icin maskeleme burada da uygulanir.
        // Serilog YAPILANDIRMASI degistirilmedi; duzeltme kaynagin kendisinde.
        logger.LogError(
            "External installer process failed. File={FileName}, ExitCode={ExitCode}, StdOut={StdOut}, StdErr={StdErr}",
            fileName,
            process.ExitCode,
            AuditSecretRedactor.Redact(standardOutput),
            AuditSecretRedactor.Redact(errorOutput));

        throw new AppException("software.process_failed", process.ExitCode, 500, BuildProcessDetails(fileName, arguments, process.ExitCode, standardOutput, errorOutput));
    }

    private static string FormatInstallError(AppException exception)
    {
        return string.IsNullOrWhiteSpace(exception.Details)
            ? exception.MessageKey
            : $"{exception.MessageKey}\r\n{exception.Details}";
    }

    private static string BuildDownloadDetails(string url, HttpResponseMessage response)
    {
        return $"Url={url}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}";
    }

    private static string BuildDownloadDetails(string url, Exception exception)
    {
        return $"Url={url}\r\n{BuildDiagnosticDetails(exception)}";
    }

    /// <summary>
    /// Zaman aşımı arıza ayrıntısı. <see cref="BuildProcessDetails"/> ile AYNI sebeple maskelenir
    /// ve AYNI sebeple ayrı bir metottur: satır içinde kurulan dize hiçbir testin göremediği bir
    /// noktaydı — mutasyon denemesi tam da bu yüzden bu yolu "kapsanmamış" olarak yakaladı.
    /// </summary>
    internal static string BuildTimeoutDetails(string fileName, string arguments, TimeSpan timeout)
    {
        return $"Process timed out after {timeout.TotalMinutes:0} min and was terminated."
            + $"\r\nFile={fileName}"
            + $"\r\nArguments={AuditSecretRedactor.Redact(arguments)}";
    }

    /// <summary>
    /// Kurulum sürecinin arıza ayrıntısı. <b>Maskeleme burada ZORUNLU:</b> bu metin
    /// <see cref="AppException.Details"/> olarak fırlatılır ve
    /// <c>ApiExceptionHandlingMiddleware</c> onu HTTP YANIT GÖVDESİNE olduğu gibi yazar (ortam
    /// ayrımı yok). Argümanlar arasında düz metin parola GEÇİYOR — ölçüldü:
    /// <c>--password="&lt;MariaDB root&gt;"</c> (bkz. mariadb kurulum/upgrade çağrıları). Yani panel
    /// yöneticisi başarısız bir bileşen kurulumunu tetikleyerek veritabanı root parolasını yanıtta
    /// geri okuyabiliyordu; aynı metin ilerleme kaydına, DB'ye ve denetim kaydına da gidiyor.
    /// <para>
    /// Maskeleme <see cref="AuditSecretRedactor"/> ile yapılır — kural TEK KAYNAKTA kalsın diye
    /// yeni bir desen yazılmaz. StandardOutput/StandardError de geçirilir: dış kurulumcular
    /// kendi komut satırlarını hata metnine basabiliyor.
    /// </para>
    /// </summary>
    internal static string BuildProcessDetails(string fileName, string arguments, int exitCode, string standardOutput, string errorOutput)
    {
        var details = $"""
            File={fileName}
            Arguments={AuditSecretRedactor.Redact(arguments)}
            ExitCode={exitCode}
            StandardOutput:
            {AuditSecretRedactor.Redact(standardOutput)}
            StandardError:
            {AuditSecretRedactor.Redact(errorOutput)}
            """;

        return TruncateDetails(details);
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        return TruncateDetails(exception.ToString());
    }

    private static string TruncateDetails(string details)
    {
        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    /// <summary>
    /// Kurulumu dogrular ve <b>OLCULEN surumu</b> dondurur (olculemediyse <c>null</c>).
    /// </summary>
    private async Task<string?> ValidateInstallationAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        CancellationToken cancellationToken,
        string? generatedPassword = null)
    {
        var componentId = component.Id.Trim().ToLowerInvariant();
        var expectedFile = componentId switch
        {
            "composer" => Path.Combine(installDirectory, "composer.phar"),
            "phpmyadmin" => Path.Combine(installDirectory, "index.php"),
            "adminer" => Path.Combine(installDirectory, "adminer.php"),
            "pgadmin4" => FindInstalledFile(installDirectory, "pgAdmin4.exe"),
            "nodejs-lts" => FindInstalledFile(installDirectory, "node.exe"),
            "python-3" => FindInstalledFile(installDirectory, "python.exe"),
            "nginx" => FindInstalledFile(installDirectory, "nginx.exe"),
            "stalwart-cli" => FindInstalledFile(installDirectory, "stalwart-cli.exe"),
            "docker-engine" => FindInstalledFile(installDirectory, "dockerd.exe"),
            "win-acme" => FindInstalledFile(installDirectory, "wacs.exe"),
            "redis" => FindInstalledFile(installDirectory, "redis-server.exe"),
            "memcached" => FindInstalledFile(installDirectory, "memcached.exe"),
            "mariadb-11" => FindInstalledFile(installDirectory, "mariadbd.exe"),
            "mysql-8" => FindInstalledFile(installDirectory, "mysqld.exe"),
            // MailEnable kendi sessiz kurulumuyla (/s) Program Files\Mail Enable altina kurulur;
            // BIN\MEInstaller.exe kurulum tamamlandiginda olusur (dogrulama isareti).
            "mailenable-standard" => FindInstalledFile(installDirectory, "MEInstaller.exe"),
            "postgresql-16" => FindInstalledFile(installDirectory, "postgres.exe"),
            "url-rewrite" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "inetsrv",
                "rewrite.dll"),
            // ARR doğrulaması aşağıda çoklu konumla ayrıca yapılır (dosya buraya konmaz).
            "iis-arr" => string.Empty,
            "http-platform-handler" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "inetsrv",
                "httpPlatformHandler.dll"),
            // AŞAĞIDAKİ ÜÇÜ HİÇBİR DOĞRULAMA KAPISINDAN GEÇMİYORDU.
            //
            // roundcube / clamav / vandelay üçü de `_ => string.Empty` dalına düşüyor, çalışma
            // zamanı denetiminde `_ => null` dalına düşüyor ve `serviceName` boş olduğu için
            // servis/port denetiminden de ERKEN DÖNÜYORDU. Yani kurulumun tek kanıtı arşivin
            // SHA-256'sı ve "açma işlemi patlamadı" idi: yanlış kök klasörle açılmış, yarım
            // açılmış ya da boş bir arşiv "kuruldu" olarak kaydediliyordu. Bedeli sessizdir —
            // vandelay yoksa site yedekleri posta İLETİLERİNİ içermez, clamav yoksa tarama hiç
            // koşmaz, roundcube yoksa webmail açılmaz — ve panel üçünde de "kurulu" der.
            //
            // Aranan dosya adları UYDURULMADI: üçü de deponun KENDİ tüketicilerinden alındı —
            // StalwartAccountArchiver.ToolExecutable ("vandelay.exe"), AntivirusService'in
            // aradığı "clamscan.exe", ve roundcube'un kendi giriş noktası "index.php".
            // Tüketicinin aradığı dosya ile kurulumun doğruladığı dosya AYNI olmalı, yoksa
            // "kurulu" diyen kapı ile "bulamadım" diyen kapı sessizce ayrışır.
            "roundcube" => FindInstalledFile(installDirectory, "index.php"),
            "clamav" => FindInstalledFile(installDirectory, "clamscan.exe"),
            "vandelay" => FindInstalledFile(installDirectory, "vandelay.exe"),
            _ when componentId.StartsWith("php-", StringComparison.Ordinal) =>
                FindInstalledFile(installDirectory, "php-cgi.exe"),
            _ => string.Empty
        };

        // FindInstalledFile ARAMA yapar ve bulamazsa BOŞ döner; boş dize aşağıdaki kapıyı
        // ATLATIR. Yani "dosya bulunamadı" ile "bu bileşen için doğrulama tanımlı değil" aynı
        // görüntüye düşüyordu — bu deponun tekrarlanan hata sınıfı. Doğrulama TANIMLI olan
        // bileşenlerde boş sonuç artık ayrı bir hatadır.
        if (string.IsNullOrWhiteSpace(expectedFile)
            && componentId is "roundcube" or "clamav" or "vandelay")
        {
            throw new AppException(
                "software.install_validation_failed",
                500,
                500,
                $"'{componentId}' kurulumu tamamlandi gorundu ama kurulum klasorunde beklenen dosya BULUNAMADI: {installDirectory}");
        }

        if (!string.IsNullOrWhiteSpace(expectedFile) && !File.Exists(expectedFile))
        {
            throw new AppException(
                "software.install_validation_failed",
                500,
                500,
                $"Required file was not created: {expectedFile}");
        }

        if (componentId == "iis-arr")
        {
            // ARR 3.0 requestRouter.dll'i "Program Files\IIS\Application Request Routing"
            // altına kurar; bazı sürümler ayrıca inetsrv'ye/şema dosyasına iz bırakır.
            // Bu konumlardan herhangi biri varsa kurulum geçerlidir.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var arrMarkers = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IIS", "Application Request Routing", "requestRouter.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "IIS", "Application Request Routing", "requestRouter.dll"),
                Path.Combine(windows, "System32", "inetsrv", "requestRouter.dll"),
                Path.Combine(windows, "System32", "inetsrv", "config", "schema", "arr_schema.xml")
            };

            if (!arrMarkers.Any(File.Exists))
            {
                throw new AppException(
                    "software.install_validation_failed",
                    500,
                    500,
                    $"Application Request Routing dosyaları bulunamadı. Beklenen konumlardan biri: {arrMarkers[0]}");
            }
        }

        if (componentId == "phpmyadmin" &&
            !File.Exists(Path.Combine(installDirectory, "config.sample.inc.php")))
        {
            throw new AppException(
                "software.install_validation_failed",
                500,
                500,
                "phpMyAdmin package marker config.sample.inc.php is missing.");
        }

        if (componentId == "vc-redist-x64" && !IsVisualCppRuntimeInstalled())
        {
            throw new AppException(
                "software.install_validation_failed",
                500,
                500,
                "Microsoft Visual C++ x64 runtime registry marker was not found.");
        }

        // OLCULEN SURUM. Bu deger asagida kaydedilir; okunamazsa null kalir ve panel istenen
        // surumu "olculmedi" damgasiyla gosterir.
        var measuredVersion = await ValidateComponentRuntimeAsync(
            component,
            componentId,
            installDirectory,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(component.ServiceName))
        {
            return measuredVersion;
        }

        if (componentId == "stalwart-mail")
        {
            // Stalwart ilk açılışta yönetim portunu (8080) hemen dinlemeyebilir (sürüm/config
            // farkı). Kurulumu SERT ✕ ile patlatma: dinleme doğrulanamazsa uyar + "kuruldu" say.
            // Gerçek sebep run-stalwart wrapper'ının logs\stalwart.log dosyasında görünür.
            try
            {
                await ValidateScheduledTaskRuntimeAsync(component.ServiceName, 8080, cancellationToken);
            }
            catch (AppException exception)
            {
                logger.LogWarning(
                    "Stalwart kuruldu ancak yönetim portu (8080) doğrulanamadı: {Detail}. " +
                    "Ayrıntı için logs\\stalwart.log incelenmeli.",
                    exception.Details);
            }

            return measuredVersion;
        }

        if (componentId == "memcached")
        {
            // memcached zamanlanmis gorev olarak calisir (Windows servisi degil);
            // gorevin kayitli + 11211'i dinliyor oldugunu, ardindan protokolu dogrula.
            await ValidateScheduledTaskRuntimeAsync(component.ServiceName, 11211, cancellationToken);
            await ValidateMemcachedRuntimeAsync(cancellationToken);
            return measuredVersion;
        }

        var serviceResult = await WaitForWindowsServiceRunningAsync(component.ServiceName, cancellationToken);
        if (serviceResult is null)
        {
            throw new AppException(
                "software.service_not_running",
                500,
                500,
                $"Windows service is not running: {component.ServiceName}");
        }

        switch (componentId)
        {
            case "mariadb-11":
            case "mysql-8":
                await ValidateMySqlRuntimeAsync(
                    componentId,
                    installDirectory,
                    await ResolveValidationPasswordAsync(componentId, generatedPassword, cancellationToken),
                    cancellationToken);
                break;
            case "postgresql-16":
                await ValidatePostgreSqlRuntimeAsync(
                    installDirectory,
                    await ResolveValidationPasswordAsync(componentId, generatedPassword, cancellationToken),
                    cancellationToken);
                break;
            case "redis":
                await ValidateRedisRuntimeAsync(installDirectory, cancellationToken);
                break;
            case "docker-engine":
                await ValidateDockerRuntimeAsync(installDirectory, cancellationToken);
                break;
        }

        return measuredVersion;
    }

    /// <summary>
    /// Kurulan bileseni CALISTIRARAK dogrular ve <b>okudugu surumu geri dondurur</b>.
    /// </summary>
    /// <remarks>
    /// <c>--version</c> zaten kosuyordu; ciktisi ATILIYOR, yalniz cikis koduna bakiliyordu. Yani
    /// "hangi surum kuruldu" sorusunun cevabi elimizdeydi ve kullanilmiyordu. Bkz.
    /// <see cref="ComponentVersionProbe"/>.
    /// </remarks>
    private async Task<string?> ValidateComponentRuntimeAsync(
        ComponentManifestItemDto component,
        string componentId,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        CommandResult? result = componentId switch
        {
            "composer" => await ValidateComposerAsync(installDirectory, cancellationToken),
            "nodejs-lts" => await ProcessRunner.RunAsync(
                FindInstalledFile(installDirectory, "node.exe"),
                "--version",
                cancellationToken: cancellationToken),
            "python-3" => await ProcessRunner.RunAsync(
                FindInstalledFile(installDirectory, "python.exe"),
                "--version",
                cancellationToken: cancellationToken),
            "stalwart-cli" => await ProcessRunner.RunAsync(
                FindInstalledFile(installDirectory, "stalwart-cli.exe"),
                "--version",
                cancellationToken: cancellationToken),
            "win-acme" => await ProcessRunner.RunAsync(
                FindInstalledFile(installDirectory, "wacs.exe"),
                "--version",
                cancellationToken: cancellationToken),
            _ when componentId.StartsWith("php-", StringComparison.Ordinal) =>
                await ValidatePhpRuntimeAsync(installDirectory, cancellationToken),
            _ => null
        };

        if (result is not null && !result.Success)
        {
            throw new AppException(
                "software.install_validation_failed",
                500,
                500,
                $"Component={component.Id}\r\n{BuildCommandDetails(result)}");
        }

        if (componentId == "nginx")
        {
            var nginxResult = await NginxInstallationSupport.TestCurrentConfigurationAsync(
                installDirectory,
                cancellationToken);
            if (!nginxResult.Success)
            {
                throw new AppException(
                    "software.install_validation_failed",
                    500,
                    500,
                    $"Component={component.Id}\r\n{BuildCommandDetails(nginxResult)}");
            }
        }

        // PHP dogrulamasi surum DEGIL eklenti listesi basar ('AdaPanel-PHP-OK'); oradan surum
        // ayiklamak sahte bir olcum olurdu. O yuzden yalniz gercekten `--version` kosan yollar
        // olcum sayilir; digerleri "olculemedi" olarak raporlanir.
        return componentId.StartsWith("php-", StringComparison.Ordinal)
            ? null
            : ComponentVersionProbe.FromCommandOutput(result?.StandardOutput);
    }

    private static async Task<CommandResult> ValidatePhpRuntimeAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var php = FindInstalledFile(installDirectory, "php.exe");
        var requiredExtensions = new[]
        {
            "curl",
            "fileinfo",
            "intl",
            "mbstring",
            "mysqli",
            "openssl",
            "pdo_mysql",
            "pdo_pgsql",
            "session",
            "zip"
        };
        var extensionList = string.Join(
            ",",
            requiredExtensions.Select(extension => $"'{extension}'"));
        var script =
            $"foreach ([{extensionList}] as $extension) {{ " +
            "if (!extension_loaded($extension)) { fwrite(STDERR, 'Missing extension: ' . $extension); exit(2); } } " +
            "echo 'AdaPanel-PHP-OK';";

        return await ProcessRunner.RunAsync(
            php,
            $"-r \"{script}\"",
            cancellationToken: cancellationToken);
    }

    private async Task<CommandResult> ValidateComposerAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var phpRoot = Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\php");
        var php = Directory.Exists(phpRoot)
            ? Directory.EnumerateFiles(phpRoot, "php.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
        if (php is null)
        {
            return new CommandResult
            {
                ExitCode = -1,
                StandardError = "PHP dependency executable was not found."
            };
        }

        return await ProcessRunner.RunAsync(
            php,
            $"\"{Path.Combine(installDirectory, "composer.phar")}\" --version --no-ansi",
            cancellationToken: cancellationToken);
    }

    private async Task<string> ResolveValidationPasswordAsync(
        string componentId,
        string? generatedPassword,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(generatedPassword))
        {
            return generatedPassword;
        }

        var installed = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken);
        return installed is null ? string.Empty : ReadGeneratedPassword(installed.ProtectedConfigurationJson);
    }

    private static async Task<CommandResult?> WaitForWindowsServiceRunningAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        CommandResult? lastResult = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = await ProcessRunner.RunAsync(
                "sc.exe",
                $"query \"{serviceName}\"",
                cancellationToken: cancellationToken);
            if (lastResult.Success && IsServiceRunning(lastResult.StandardOutput))
            {
                return lastResult;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return null;
    }

    internal static bool IsServiceRunning(string output)
        => output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
           Regex.IsMatch(output, @"(?im)^\s*[^:\r\n]+\s*:\s*4\s+(?!\()[\p{L}_-]+");

    private async Task ValidateMySqlRuntimeAsync(
        string componentId,
        string installDirectory,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new AppException(
                "database.admin_password_missing",
                409,
                409,
                $"Component={componentId}");
        }

        var admin = componentId == "mariadb-11"
            ? FindExecutable(installDirectory, "mariadb-admin.exe")
            : FindExecutable(installDirectory, "mysqladmin.exe");
        await WaitForDatabaseReadyAsync(
            admin,
            componentId == "mariadb-11" ? "AdaMariaDB" : "AdaMySQL",
            ResolveDatabasePort(componentId),
            password,
            cancellationToken);
    }

    private static async Task ValidatePostgreSqlRuntimeAsync(
        string installDirectory,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new AppException("database.admin_password_missing", 409, 409);
        }

        var psql = FindInstalledFile(installDirectory, "psql.exe");
        var result = await ProcessRunner.RunAsync(
            psql,
            "--host=127.0.0.1 --port=5432 --username=postgres --dbname=postgres --no-password --tuples-only --command=\"SELECT 1\"",
            cancellationToken: cancellationToken,
            environmentVariables: new Dictionary<string, string> { ["PGPASSWORD"] = password });
        if (!result.Success || !result.StandardOutput.Contains('1'))
        {
            throw new AppException(
                "software.database_start_failed",
                500,
                500,
                BuildCommandDetails(result));
        }
    }

    private static async Task ValidateRedisRuntimeAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            FindInstalledFile(installDirectory, "redis-cli.exe"),
            "-h 127.0.0.1 -p 6379 ping",
            cancellationToken: cancellationToken);
        if (!result.Success ||
            !result.StandardOutput.Contains("PONG", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                "software.service_validation_failed",
                500,
                500,
                BuildCommandDetails(result));
        }
    }

    private static async Task ValidateMemcachedRuntimeAsync(CancellationToken cancellationToken)
    {
        var response = await ProbeTcpCommandAsync(
            "127.0.0.1",
            11211,
            "version\r\n",
            cancellationToken);
        if (!response.StartsWith("VERSION ", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                "software.service_validation_failed",
                500,
                500,
                $"Memcached did not answer the version probe. Response={response}");
        }
    }

    private static async Task ValidateDockerRuntimeAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            FindInstalledFile(installDirectory, "docker.exe"),
            "version --format \"{{.Server.Version}}\"",
            cancellationToken: cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new AppException(
                "software.service_validation_failed",
                500,
                500,
                "Docker daemon did not answer. The Windows Containers feature may require a restart.\r\n" +
                BuildCommandDetails(result));
        }
    }

    private static async Task ValidateScheduledTaskRuntimeAsync(
        string taskName,
        int port,
        CancellationToken cancellationToken)
    {
        var task = await ProcessRunner.RunAsync(
            "schtasks.exe",
            $"/Query /TN \"{taskName}\" /FO LIST /V",
            cancellationToken: cancellationToken);
        if (!task.Success)
        {
            throw new AppException(
                "software.service_validation_failed",
                500,
                500,
                $"Scheduled task was not registered: {taskName}\r\n{BuildCommandDetails(task)}");
        }

        // Stalwart ilk açılışta RocksDB veri deposunu hazırlar ve dinleyici yapılandırması
        // görevi sonda yeniden başlatır; bu yüzden tek bir deneme erken kalır. Açılması için
        // makul bir süre (~45 sn) boyunca 127.0.0.1 ve ::1 (IPv6) üzerinde tekrar tekrar dene.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            if (await CanConnectTcpAsync("127.0.0.1", port, cancellationToken) ||
                await CanConnectTcpAsync("::1", port, cancellationToken))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new AppException(
                    "software.service_not_running",
                    500,
                    500,
                    $"Scheduled task is registered but the runtime is not listening: {taskName}, Port={port}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static async Task<bool> CanConnectTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ProbeTcpCommandAsync(
        string host,
        int port,
        string command,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(request, timeout.Token);
        var buffer = new byte[512];
        var read = await stream.ReadAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer, 0, read).Trim();
    }

    private static string BuildCommandDetails(CommandResult result)
        => $"ExitCode={result.ExitCode}\r\nStandardOutput={result.StandardOutput}\r\nStandardError={result.StandardError}";

    private static string FindInstalledFile(string installDirectory, string fileName)
    {
        if (!Directory.Exists(installDirectory))
        {
            return Path.Combine(installDirectory, fileName);
        }

        return Directory.EnumerateFiles(installDirectory, fileName, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault()
            ?? Path.Combine(installDirectory, fileName);
    }

    private async Task ApplyPostInstallStepsAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        string generatedPassword,
        CancellationToken cancellationToken)
    {
        foreach (var postInstallStep in component.PostInstall)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (postInstallStep.Trim().ToLowerInvariant())
            {
                case "writephpini":
                    await EnsurePhpIniAsync(installDirectory, cancellationToken);
                    break;

                case "initmysqlzip":
                    await InitializeMySqlZipAsync(component, installDirectory, generatedPassword, cancellationToken);
                    break;

                case "initmariadbzip":
                    await InitializeMariaDbZipAsync(component, installDirectory, generatedPassword, cancellationToken);
                    break;

                case "installredisservice":
                    await InstallRedisServiceAsync(component, installDirectory, cancellationToken);
                    break;

                case "installmemcachedservice":
                    await InstallMemcachedServiceAsync(component, installDirectory, cancellationToken);
                    break;

                case "installdockerengine":
                    await InstallDockerEngineAsync(component, installDirectory, cancellationToken);
                    break;

                case "installstalwartservice":
                    await InstallStalwartServiceAsync(component, installDirectory, generatedPassword, cancellationToken);
                    break;

                case "preparenginx":
                    await NginxInstallationSupport.PrepareAsync(installDirectory, cancellationToken);
                    break;

                case "registerfastcgi":
                    logger.LogInformation(
                        "PHP component {ComponentId} is ready for FastCGI registration by the PHP service.",
                        component.Id);
                    break;

                default:
                    throw new AppException(
                        "software.manifest_invalid",
                        500,
                        500,
                        $"Component={component.Id}\r\nPostInstall={postInstallStep}");
            }
        }
    }

    private async Task InitializeMySqlZipAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        string generatedPassword,
        CancellationToken cancellationToken)
    {
        var mysqld = FindExecutable(installDirectory, "mysqld.exe");
        var mysql = FindExecutable(installDirectory, "mysql.exe");
        var dataDirectory = Path.Combine(installDirectory, "data");
        var configPath = Path.Combine(installDirectory, "my.ini");
        var serviceName = string.IsNullOrWhiteSpace(component.ServiceName) ? "AdaMySQL" : component.ServiceName;
        var port = ResolveDatabasePort(component.Id);

        await TryRemoveServiceAsync(serviceName, cancellationToken);
        Directory.CreateDirectory(installDirectory);

        await File.WriteAllTextAsync(
            configPath,
            $$"""
            [mysqld]
            basedir={{ToConfigPath(installDirectory)}}
            datadir={{ToConfigPath(dataDirectory)}}
            port={{port}}
            bind-address=127.0.0.1
            log-error={{ToConfigPath(Path.Combine(dataDirectory, "mysql-error.log"))}}
            """,
            cancellationToken);

        if (!Directory.Exists(dataDirectory) || !Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Any())
        {
            Directory.CreateDirectory(dataDirectory);
            await RunProcessAsync(mysqld, $"--defaults-file=\"{configPath}\" --initialize-insecure", cancellationToken);
        }

        // MySQL doc: servis adı '--install'in HEMEN ardından gelmeli, '--defaults-file' ise
        // servis adından SONRA. Ters sıra (defaults-file önce) ExitCode=1 ile başarısız olur.
        await RunProcessAsync(mysqld, $"--install \"{serviceName}\" --defaults-file=\"{configPath}\"", cancellationToken);
        await RunProcessAsync("sc.exe", $"start \"{serviceName}\"", cancellationToken);
        await WaitForDatabaseReadyAsync(
            FindExecutable(installDirectory, "mysqladmin.exe"),
            serviceName,
            port,
            password: string.Empty,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(generatedPassword))
        {
            var sql = $"ALTER USER 'root'@'localhost' IDENTIFIED BY '{generatedPassword}'; FLUSH PRIVILEGES;";
            await RunProcessAsync(
                mysql,
                $"--user=root --protocol=TCP --host=127.0.0.1 --port={port} --execute=\"{sql}\"",
                cancellationToken);
            await WaitForDatabaseReadyAsync(
                FindExecutable(installDirectory, "mysqladmin.exe"),
                serviceName,
                port,
                generatedPassword,
                cancellationToken);
        }
    }

    private async Task InitializeMariaDbZipAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        string generatedPassword,
        CancellationToken cancellationToken)
    {
        var installer = FindExecutable(installDirectory, "mariadb-install-db.exe");
        var admin = FindExecutable(installDirectory, "mariadb-admin.exe");
        var dataDirectory = Path.Combine(installDirectory, "data");
        var serviceName = string.IsNullOrWhiteSpace(component.ServiceName) ? "AdaMariaDB" : component.ServiceName;
        var port = ResolveDatabasePort(component.Id);
        var password = string.IsNullOrWhiteSpace(generatedPassword) ? GeneratePassword() : generatedPassword;

        // VERİ KAYBI KORUMASI: burası eskiden KOŞULSUZ olarak veri dizinini siliyordu. O hâliyle
        // bileşen yeniden kurulduğunda (sürüm yükseltme, ya da yalnızca doğrulama başarısız olduğu
        // için tetiklenen onarım kurulumu) sunucudaki TÜM VERİTABANLARI yok oluyordu. Artık MySQL
        // tarafındaki kalıpla aynı: veri dizini VARSA ve BOŞ DEĞİLSE ona DOKUNULMAZ; yalnızca
        // binary'ler değişir ve şema yeni sürüme yükseltilir (mariadb-upgrade).
        var hasExistingData = Directory.Exists(dataDirectory) &&
            Directory.EnumerateFileSystemEntries(dataDirectory).Any();

        await TryRemoveServiceAsync(serviceName, cancellationToken);

        if (!hasExistingData)
        {
            DeleteManagedPath(dataDirectory);
            Directory.CreateDirectory(dataDirectory);

            await RunProcessAsync(
                installer,
                $"--datadir=\"{dataDirectory}\" --service=\"{serviceName}\" --password=\"{password}\" --port={port}",
                cancellationToken);
        }
        else
        {
            // Mevcut veriyle devam: servis kaydını yeni binary'lerle yeniden oluştur.
            // mysqld.exe MariaDB'de de --install destekler; --defaults-file veri dizinindeki my.ini.
            var daemon = FindExecutable(installDirectory, "mysqld.exe");
            var configPath = Path.Combine(dataDirectory, "my.ini");
            var defaultsArgument = File.Exists(configPath) ? $" --defaults-file=\"{configPath}\"" : string.Empty;
            await RunProcessAsync(daemon, $"--install \"{serviceName}\"{defaultsArgument}", cancellationToken);
        }

        // B5: mariadb-install-db kendi my.ini'sini uretir ve varsayilanda bind-address YAZMAZ (tum
        // arabirimleri dinler). Servis baslamadan ONCE, gercekten okudugu config'e 127.0.0.1 baglamasini ekle.
        await EnsureMariaDbBindAddressAsync(serviceName, dataDirectory, installDirectory, cancellationToken);
        await RunProcessAsync("sc.exe", $"start \"{serviceName}\"", cancellationToken);

        // Hazır olmayı ÖNCE bekle: sunucu bağlantı kabul etmeden mariadb-upgrade çalıştırmak
        // "Can't connect to server (10061)" ile patlar ve kurulumu geri almaya sürükler.
        await WaitForDatabaseReadyAsync(admin, serviceName, port, password, cancellationToken);

        if (hasExistingData)
        {
            // Sürüm yükseltmesinde şema/sistem tabloları yeni sürüme taşınmalı; aksi halde MariaDB
            // uyarı verir ya da bazı özellikler çalışmaz. Parola mevcut kurulumdan gelir.
            //
            // BAŞARISIZLIK ÖLÜMCÜL DEĞİLDİR: sunucu bu noktada verisiyle birlikte ÇALIŞIYOR.
            // Şema taşımasında bir aksaklık yüzünden kurulumu geri almak, çalışan bir veritabanını
            // yok etmek anlamına gelirdi. Uyarı yazılır, yönetici gerekirse elle çalıştırır.
            var upgrade = FindExecutable(installDirectory, "mariadb-upgrade.exe");
            if (!string.IsNullOrWhiteSpace(upgrade))
            {
                try
                {
                    await RunProcessAsync(
                        upgrade,
                        $"--user=root --password=\"{password}\" --protocol=TCP --host=127.0.0.1 --port={port}",
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "mariadb-upgrade tamamlanamadı; sunucu verisiyle çalışıyor. Gerekirse elle çalıştırın: {Path}",
                        upgrade);
                }
            }
        }
    }

    // MariaDB TUM arayuzlerde dinlesin (bind-address=0.0.0.0) ki panelin "Uzak Erisim" toggle'i (profesyonel paneller gibi)
    // calissin. GUVENLIK = Windows Guvenlik Duvari: 3306 varsayilan KAPALI (kural yok), toggle acilinca
    // firewall yalniz istenen IP'ye acilir. mariadb-install-db varsayilanda bind-address yazmaz; servisin
    // gercek config'ini (ImagePath'teki --defaults-file) bulup [mysqld] altina ekleriz. Zaten varsa dokunmayiz.
    private static async Task EnsureMariaDbBindAddressAsync(
        string serviceName,
        string dataDirectory,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = new List<string>();
            var fromService = ResolveServiceDefaultsFile(serviceName);
            if (!string.IsNullOrWhiteSpace(fromService))
            {
                candidates.Add(fromService);
            }

            candidates.Add(Path.Combine(dataDirectory, "my.ini"));
            candidates.Add(Path.Combine(installDirectory, "my.ini"));
            candidates.Add(Path.Combine(installDirectory, "data", "my.ini"));

            var configPath = candidates.FirstOrDefault(File.Exists);
            if (configPath is null)
            {
                return;
            }

            var lines = (await File.ReadAllLinesAsync(configPath, cancellationToken)).ToList();
            if (lines.Any(line => Regex.IsMatch(line, @"^\s*bind-address\s*=", RegexOptions.IgnoreCase)))
            {
                return;
            }

            var mysqldIndex = lines.FindIndex(line =>
                line.Trim().Equals("[mysqld]", StringComparison.OrdinalIgnoreCase));
            if (mysqldIndex >= 0)
            {
                lines.Insert(mysqldIndex + 1, "bind-address=0.0.0.0");
            }
            else
            {
                lines.Add("[mysqld]");
                lines.Add("bind-address=0.0.0.0");
            }

            await File.WriteAllLinesAsync(configPath, lines, cancellationToken);
        }
        catch
        {
            // Best-effort sertlestirme: basarisiz olursa kurulumu bozma.
        }
    }

    // Servisin ImagePath'inden --defaults-file yolunu cikarir (servisin gercekten okudugu config). Bulamazsa null.
    private static string? ResolveServiceDefaultsFile(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        if (key?.GetValue("ImagePath") is not string imagePath || string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var match = Regex.Match(
            imagePath,
            "--defaults-file=(?:\"(?<q>[^\"]+)\"|(?<u>\\S+))",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var path = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["u"].Value;
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    private async Task WaitForDatabaseReadyAsync(
        string adminExecutable,
        string serviceName,
        int port,
        string password,
        CancellationToken cancellationToken)
    {
        var passwordArgument = string.IsNullOrWhiteSpace(password)
            ? string.Empty
            : $" --password=\"{password}\"";
        CommandResult? lastResult = null;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = await ProcessRunner.RunAsync(
                adminExecutable,
                $"--user=root --protocol=TCP --host=127.0.0.1 --port={port}{passwordArgument} ping",
                cancellationToken: cancellationToken);
            if (lastResult.Success &&
                lastResult.StandardOutput.Contains("alive", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var serviceResult = await ProcessRunner.RunAsync(
            "sc.exe",
            $"query \"{serviceName}\"",
            cancellationToken: cancellationToken);
        throw new AppException(
            "software.database_start_failed",
            500,
            500,
            $"Service={serviceName}\r\nPort={port}\r\n" +
            $"ServiceState={serviceResult.StandardOutput}\r\n" +
            $"ProbeOut={lastResult?.StandardOutput}\r\nProbeError={lastResult?.StandardError}");
    }

    /// <summary>
    /// Kurulum ön kontrolü: bileşenin portlarını tutan YABANCI süreçler (software.port_conflict)
    /// ve çakışan yabancı servisler (software.conflicting_service) suçlunun adıyla raporlanır.
    /// Probe'un kendisi başarısız olursa kurulum ENGELLENMEZ (best-effort teşhis).
    /// </summary>
    /// <summary>
    /// IIS site bağlamalarını okur. Yalnızca HTTP.sys çakışması varken çağrılır ve başarısızlığı
    /// KURULUMU ENGELLEMEZ: amacı hata mesajını zenginleştirmek, yeni bir hata kaynağı olmak değil.
    /// </summary>
    private async Task<IReadOnlyList<(string SiteName, string Binding, int Port)>> ReadIisBindingsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var appcmd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "inetsrv",
                "appcmd.exe");
            if (!File.Exists(appcmd))
            {
                return [];
            }

            var result = await ProcessRunner.RunAsync(
                appcmd,
                "list sites",
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(10));

            return ComponentPreflight.ParseAppcmdSiteBindings(result.StandardOutput);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "IIS bağlamaları okunamadı; port çakışması mesajı site adı olmadan gösterilecek.");
            return [];
        }
    }

    private async Task EnsureNoComponentConflictsAsync(ComponentManifestItemDto component, CancellationToken cancellationToken)
    {
        var ports = ComponentPreflight.GetRequiredPorts(component.Id);
        var patterns = ComponentPreflight.GetConflictingServicePatterns(component.Id);
        if (ports.Count == 0 && patterns.Count == 0)
        {
            return;
        }

        IReadOnlyList<ComponentPreflight.PortOccupancy> occupied = [];
        IReadOnlyList<ComponentPreflight.ServiceInfo> services = [];
        try
        {
            var probe = await ProcessRunner.RunPowerShellAsync(
                ComponentPreflight.BuildProbeScript(ports, patterns),
                cancellationToken);
            if (!probe.Success || string.IsNullOrWhiteSpace(probe.StandardOutput))
            {
                return;
            }

            using var document = JsonDocument.Parse(probe.StandardOutput);
            var root = document.RootElement;
            occupied = ReadPreflightArray<ComponentPreflight.PortOccupancy>(root, "Ports");
            services = ReadPreflightArray<ComponentPreflight.ServiceInfo>(root, "Services");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Kurulum ön kontrol probe'u çalıştırılamadı; kontrol atlanıyor ({ComponentId}).", component.Id);
            return;
        }

        // Scheduled-task runtimes have no Win32_Service owner. Match only the exact
        // managed executable; a process with the same name elsewhere remains a conflict.
        var ownExecutablePath = component.Id.Trim().ToLowerInvariant() switch
        {
            "stalwart-mail" => Path.Combine(ResolveInstallDirectory(component.InstallDir), "stalwart.exe"),
            "memcached" => Path.Combine(ResolveInstallDirectory(component.InstallDir), "memcached.exe"),
            _ => null
        };
        var portConflicts = ComponentPreflight.SelectForeignPortConflicts(occupied, component.ServiceName, ownExecutablePath);
        if (portConflicts.Count > 0)
        {
            // HTTP.sys'in tuttuğu portlarda süreç adı ("System", PID 4) yöneticiye hiçbir şey söylemez;
            // gerçek sahip olan IIS sitesini bul ve onu yaz. Bulunamazsa mesaj eski hâlinde kalır.
            var iisBindings = portConflicts.Any(item => ComponentPreflight.IsHttpSysHolder(item.Pid, item.Process))
                ? await ReadIisBindingsAsync(cancellationToken)
                : [];

            var lines = portConflicts.Select(item =>
            {
                var owner = ComponentPreflight.IsHttpSysHolder(item.Pid, item.Process)
                    ? ComponentPreflight.DescribeHttpSysPortOwner(item.Port, iisBindings)
                    : string.Empty;

                return $"Port {item.Port}: {(string.IsNullOrWhiteSpace(item.Process) ? "bilinmeyen süreç" : item.Process + ".exe")} (PID {item.Pid})" +
                       (string.IsNullOrWhiteSpace(item.Service) ? string.Empty : $" — Windows servisi: {item.Service}") +
                       (string.IsNullOrWhiteSpace(owner) ? string.Empty : $" — {owner}");
            });

            var remedy = iisBindings.Count > 0
                ? "Cozum: portu bir IIS sitesi kullaniyor. O sitenin baglamasini baska bir porta alin (Siteler > site > Baglamalar) ya da siteyi durdurun, sonra kurulumu tekrar deneyin."
                : "Cozum: yukaridaki yazilimi kaldirin ya da servisini durdurup devre disi birakin (sc stop <servis> && sc config <servis> start= disabled), sonra kurulumu tekrar deneyin.";

            throw new AppException(
                "software.port_conflict",
                409,
                409,
                $"Component={component.DisplayName}\r\n{string.Join("\r\n", lines)}\r\n{remedy}");
        }

        var blockingServices = ComponentPreflight.SelectBlockingServices(services, patterns, component.ServiceName);
        if (blockingServices.Count > 0)
        {
            var lines = blockingServices.Select(item => $"Servis: {item.Name} (durum: {item.State}, başlangıç: {item.StartMode})");
            throw new AppException(
                "software.conflicting_service",
                409,
                409,
                $"Component={component.DisplayName}\r\n{string.Join("\r\n", lines)}\r\n" +
                "Cozum: bu servis su an calisiyor ya da otomatik baslangicta — ayni portlar icin cakisir. Kaldirin veya devre disi birakin (sc config <servis> start= disabled), sonra tekrar deneyin.");
        }
    }

    // PS 5.1 ConvertTo-Json tek elemanı diziye sarmayabilir → hem dizi hem tek nesne kabul edilir.
    private static IReadOnlyList<T> ReadPreflightArray<T>(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<T>>(value) ?? [],
            JsonValueKind.Object => JsonSerializer.Deserialize<T>(value) is { } single ? [single] : [],
            _ => []
        };
    }

    internal static int ResolveDatabasePort(string componentId)
        => componentId.Trim().ToLowerInvariant() switch
        {
            "mariadb-11" => MariaDbPort,
            "mysql-8" => MySqlPort,
            _ => 0
        };

    private static bool IsVisualCppRuntimeInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        return Convert.ToInt32(key?.GetValue("Installed", 0)) == 1;
    }

    private async Task InstallDockerEngineAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var serviceName = string.IsNullOrWhiteSpace(component.ServiceName) ? "docker" : component.ServiceName;
        var dockerd = FindExecutable(installDirectory, "dockerd.exe");
        var binDirectory = Path.GetDirectoryName(dockerd) ?? installDirectory;
        var binLiteral = binDirectory.Replace("'", "''");

        await TryRemoveServiceAsync(serviceName, cancellationToken);

        // Windows "Containers" ozelligini etkinlestir ve docker dizinini sistem PATH'ine ekle.
        var prepareScript = $$"""
            try {
              Enable-WindowsOptionalFeature -Online -FeatureName Containers -All -NoRestart -ErrorAction Stop | Out-Null
            } catch {
              try { & dism.exe /online /enable-feature /featurename:Containers /all /norestart | Out-Null } catch {}
            }
            $bin = '{{binLiteral}}'
            $path = [Environment]::GetEnvironmentVariable('Path', 'Machine')
            if ($path -notlike "*$bin*") {
              [Environment]::SetEnvironmentVariable('Path', ($path.TrimEnd(';') + ';' + $bin), 'Machine')
            }
            """;
        await ProcessRunner.RunPowerShellAsync(prepareScript, cancellationToken);

        // dockerd'yi 'docker' servisi olarak kaydet.
        await RunProcessAsync(dockerd, "--register-service", cancellationToken);

        // Baslat (Containers ozelligi yeni acildiysa yeniden baslatma gerekebilir; basarisizlik kuruluma engel olmasin).
        var startResult = await ProcessRunner.RunAsync(
            "sc.exe",
            $"start \"{serviceName}\"",
            cancellationToken: cancellationToken);
        if (!startResult.Success)
        {
            logger.LogWarning(
                "Docker service registered but could not start yet (a reboot may be required to finish enabling the Containers feature). Output={Output} Error={Error}",
                startResult.StandardOutput,
                startResult.StandardError);
        }
    }

    private async Task InstallStalwartServiceAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        string generatedPassword,
        CancellationToken cancellationToken)
    {
        var stalwart = FindExecutable(installDirectory, "stalwart.exe");
        var etcDirectory = Path.Combine(installDirectory, "etc");
        var dataDirectory = Path.Combine(installDirectory, "data");
        Directory.CreateDirectory(etcDirectory);
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.Combine(installDirectory, "logs"));

        var adminPassword = string.IsNullOrWhiteSpace(generatedPassword) ? GeneratePassword() : generatedPassword;
        var configPath = Path.Combine(etcDirectory, "config.json");

        // Stalwart v0.16 config dosyasi yoksa yalniz kurulum sihirbazi sunan "bootstrap mode"da
        // kalir (mail portlari ve yonetim API'si kapali). config.json'in tamami minimal veri
        // deposu tanimidir; dosyayi onceden yazmak sihirbazi tamamen atlar ve sunucu ilk
        // acilista varsayilan yapilandirmayi kendisi olusturup dogrudan normal modda baslar.
        //
        // STORE = SQLITE (RocksDB DEGIL): Stalwart'in Windows binary'sinde RocksDB store'u
        // acilirken native tarafta cokuyor (0xC0000005 access violation, hicbir cikti/log
        // yazmadan). Tracer henuz yuklenmediginden sebep gorunmuyordu. SQLite tek-dugum
        // Windows kurulumunda saglam calisir ve ek servis gerektirmez; path bir .db DOSYASIDIR.
        var sqlitePath = ToConfigPath(Path.Combine(dataDirectory, "stalwart.db"));
        var sqliteConfig = $$"""{"@type":"Sqlite","path":"{{sqlitePath}}"}""";
        // Yoksa yaz; ayrica eski kurulumlardan kalma RocksDB config'ini de SQLite'a tasi
        // (RocksDB hic calismadigindan kaybedilecek veri yok).
        if (!File.Exists(configPath) ||
            (await File.ReadAllTextAsync(configPath, cancellationToken))
                .Contains("RocksDb", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllTextAsync(configPath, sqliteConfig, cancellationToken);
        }

        // STALWART_RECOVERY_ADMIN sabit bir admin kimligi saglar ve normal modda da gecerli
        // kalir; panel (MailService) CLI yonetimini bu parolayla yapar.
        var wrapperPath = Path.Combine(installDirectory, "run-stalwart.cmd");
        var stalwartLog = Path.Combine(installDirectory, "logs", "stalwart.log");
        var secretPath = Path.Combine(etcDirectory, StalwartRecoverySecretFileName);

        // K1 GÜVENLİK — ÖLÇÜLDÜ: run-stalwart.cmd süper-admin parolasını DÜZ METİN taşıyordu ve
        // %ProgramData%\AdaPanel\runtimes kiracıya RX açık olduğu için barındırılan HERHANGİ bir
        // sitenin PHP'si dosyayı okuyup sunucudaki BÜTÜN alan adlarının postasına tam yetkiyle
        // sahip olabiliyordu (yükseltilmemiş kullanıcı olarak okundu, 270 bayt).
        //
        // Artık kimlik diske KORUNMUŞ yazılır (DPAPI, LocalMachine kapsamı — servisi SYSTEM olarak
        // açan zamanlanmış görev aynı makinede çözebilsin diye) ve dosyanın DACL'i sıfırdan
        // kurulup yalnız SYSTEM/Administrators/servis hesabına bırakılır. Wrapper artık parola
        // İÇERMEZ; açılışta korunan dosyayı çözer. Ayrıca DataRootHardeningService runtimes\stalwart
        // ağacını tamamen kiracıya kapatır (iki bağımsız savunma).
        await File.WriteAllBytesAsync(secretPath, ProtectStalwartSecret($"admin:{adminPassword}"), cancellationToken);
        DataRootHardeningService.ProtectFile(secretPath, logger);

        // Stalwart cikti/hatalarini log'a yaz ki acilis sorunlari (port dinlememe vb.) teshis
        // edilebilsin. Zamanlanmis gorev sessiz calistigindan bu olmadan sebep gorunmuyordu.
        await File.WriteAllTextAsync(
            wrapperPath,
            BuildStalwartWrapperContent(stalwart, configPath, stalwartLog, secretPath),
            cancellationToken);
        DataRootHardeningService.ProtectFile(wrapperPath, logger);

        const string taskName = "AdaStalwart";
        // Mevcut gorevi temizle (varsa) ve yeniden olustur.
        await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", cancellationToken: cancellationToken);
        await RegisterLongRunningTaskAsync(taskName, wrapperPath, cancellationToken);

        var run = await ProcessRunner.RunAsync("schtasks.exe", $"/Run /TN \"{taskName}\"", cancellationToken: cancellationToken);
        if (!run.Success)
        {
            logger.LogWarning(
                "Stalwart scheduled task created but could not be started immediately. Output={Output} Error={Error}",
                run.StandardOutput,
                run.StandardError);
        }

        // Dinleyicileri tamamla: varsayilanlar yalniz IPv6'ya ([::]) baglanir (Windows'ta
        // IPv4 erisilemez olur) ve 143/587 portlari hic acilmaz. Basarisizlik kurulumu bozmasin.
        try
        {
            await ConfigureStalwartListenersAsync(adminPassword, taskName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Stalwart listener configuration failed; mail server is still reachable over IPv6.");
        }
    }

    /// <summary>
    /// Dışarıya AÇIK olması GEREKEN posta portları. Gerekçe koddan: 25 = MX/gelen posta, 587 + 143 =
    /// istemci/Roundcube STARTTLS (<c>ConfigureStalwartListenersAsync</c> ikisini bilerek oluşturur),
    /// 465/993/995 = örtük TLS istemci portları (<c>OpenMailServerFirewallPorts</c> ile aynı küme).
    /// Listede OLMAYAN her dinleyici (ManageSieve 4190, düz metin POP3 110, yönetim 8080) loopback'e alınır.
    /// </summary>
    private static readonly int[] PublicMailListenerPorts = [25, 143, 465, 587, 993, 995];

    /// <summary>
    /// AĞ YÜZEYİ — SAF KARAR: bir Stalwart dinleyicisinin bind kümesini üretir.
    /// Yalnız <see cref="PublicMailListenerPorts"/> çift yığına (IPv4 + IPv6) çıkar; diğerleri
    /// <c>127.0.0.1</c> + <c>[::1]</c> ile sınırlanır (Roundcube/panel aynı makinede, işlevsel kayıp yok).
    /// </summary>
    /// <summary>
    /// Uzun ömürlü bir yardımcı sürecin (posta sunucusu, önbellek) ONSTART zamanlanmış görev
    /// tanımını üretir. Ayarlar AÇIKÇA yazılır; Task Scheduler varsayılanlarına bırakılmaz.
    /// </summary>
    /// <remarks>
    /// ÖLÇÜLDÜ — BOZUK: eski hâl <c>schtasks /Create /TN … /SC ONSTART /RU SYSTEM /RL HIGHEST /F</c>
    /// idi. Bu komut görevi kurar ve "SUCCESS" yazar, ama AYARLARI Task Scheduler'ın
    /// VARSAYILANLARINA bırakır — ve <c>schtasks.exe</c>'in bu ayarları verecek bir anahtarı
    /// YOKTUR (<c>/ET</c> "bitiş saati"dir, ExecutionTimeLimit değil).
    ///
    /// Bu makinede o komutun kurduğu gerçek AdaStalwart görevi okundu:
    /// <c>ExecutionTimeLimit=PT72H</c>, <c>RestartCount=0</c>, <c>StopIfGoingOnBatteries=True</c>,
    /// tek tetikleyici <c>BootTrigger</c>. Microsoft'un belgesi de aynısını söylüyor:
    /// "By default, a task will be stopped 72 hours after it starts to run."
    ///
    /// Sonuç: panel postayı kurar, kurulum doğrulaması (port dinliyor mu) YEŞİL yanar; 72 saat
    /// sonra Task Scheduler süreci ÖLDÜRÜR ve tek tetikleyici açılış olduğu, yeniden başlatma da
    /// tanımlı olmadığı için makine yeniden başlatılana kadar posta bir daha AÇILMAZ. Aynı
    /// makinede son çalışma sonucu bunu doğruluyor: <c>LastTaskResult=267014</c> =
    /// <c>0x41306</c> = <c>SCHED_S_TASK_TERMINATED</c>, ve ölçüm anında 25/143/465/587/993
    /// portlarının HİÇBİRİ dinlenmiyordu.
    ///
    /// Bu yüzden görev tam XML ile kaydedilir. <c>PT0S</c> belgede "run indefinitely" demektir.
    /// Eleman SIRASI tahmin EDİLMEDİ: Windows'un kendi <c>Export-ScheduledTask</c> çıktısından
    /// alındı ve bu şablonun birebir kendisi <c>schtasks /Create /XML</c> ile kaydedilip geri
    /// okundu (ETL=PT0S, RestartCount=3, StopIfBatt=False, Principal=SYSTEM, RunLevel=Highest).
    /// </remarks>
    public static string BuildLongRunningTaskXml(string command)
    {
        // Yol içindeki &, &lt; gibi karakterler XML'i bozarsa görev HİÇ kurulmaz; kaçış şart.
        var kacisliKomut = System.Security.SecurityElement.Escape(command) ?? string.Empty;

        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
               "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
               "  <Principals>\r\n" +
               "    <Principal id=\"Author\">\r\n" +
               // S-1-5-18 = LOCAL SYSTEM. SID kullanılır: "SYSTEM" adı yerelleştirilmiş
               // Windows'ta farklı yazılır, SID her dilde aynıdır.
               "      <UserId>S-1-5-18</UserId>\r\n" +
               "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
               "    </Principal>\r\n" +
               "  </Principals>\r\n" +
               "  <Settings>\r\n" +
               // Dizüstü/UPS'li sunucuda pil moduna geçmek posta sunucusunu ÖLDÜRMEMELİ.
               "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
               "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
               // ASIL DÜZELTME: varsayılan PT72H yerine süresiz.
               "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
               "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
               // Süreç çökerse açılışı beklemeden geri gelsin.
               "    <RestartOnFailure>\r\n" +
               "      <Count>3</Count>\r\n" +
               "      <Interval>PT1M</Interval>\r\n" +
               "    </RestartOnFailure>\r\n" +
               "    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
               "  </Settings>\r\n" +
               "  <Triggers>\r\n" +
               "    <BootTrigger />\r\n" +
               "  </Triggers>\r\n" +
               "  <Actions Context=\"Author\">\r\n" +
               "    <Exec>\r\n" +
               $"      <Command>{kacisliKomut}</Command>\r\n" +
               "    </Exec>\r\n" +
               "  </Actions>\r\n" +
               "</Task>\r\n";
    }

    /// <summary>
    /// Uzun ömürlü yardımcı süreci ONSTART görevi olarak kaydeder ve AYARIN GERÇEKTEN UYGULANDIĞINI
    /// geri okuyup doğrular.
    /// </summary>
    /// <remarks>
    /// Geri okuma isteğe bağlı değil: bu sınıfın düzelttiği hatanın tamamı "komut SUCCESS dedi,
    /// kimse sonucu ölçmedi"di. XML reddedilirse görev hiç kurulmamış olur — bu, 72 saatlik
    /// sessiz ölümden bile kötüdür, o yüzden sesli hata veriyoruz.
    /// </remarks>
    private async Task RegisterLongRunningTaskAsync(
        string taskName,
        string command,
        CancellationToken cancellationToken)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"adapanel-gorev-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks /XML dosyayı UTF-16 bekler; UTF-8 verilince "The task XML is malformed" der.
            await File.WriteAllTextAsync(
                xmlPath,
                BuildLongRunningTaskXml(command),
                Encoding.Unicode,
                cancellationToken);

            await RunProcessAsync(
                "schtasks.exe",
                $"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F",
                cancellationToken);

            // GERİ OKU: görev gerçekten süresiz mi? Varsayılan PT72H geri gelirse düzeltme
            // uygulanmamış demektir ve sessizce geçmemeli.
            var dogrula = await ProcessRunner.RunAsync(
                "schtasks.exe",
                $"/Query /TN \"{taskName}\" /XML",
                cancellationToken: cancellationToken);
            if (dogrula.Success &&
                dogrula.StandardOutput.Contains("PT72H", StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(
                    "software.service_validation_failed",
                    500,
                    500,
                    $"Scheduled task '{taskName}' still has the 72-hour default execution limit; " +
                    "the long-running daemon would be terminated after three days.\r\n" +
                    BuildCommandDetails(dogrula));
            }
        }
        finally
        {
            try
            {
                if (File.Exists(xmlPath))
                {
                    File.Delete(xmlPath);
                }
            }
            catch (IOException)
            {
                // Geçici dosya silinemedi; görev kaydı bundan etkilenmez.
            }
        }
    }

    internal static SortedSet<string> ResolveStalwartListenerBinds(IReadOnlyCollection<string> binds)
    {
        var ports = binds.Select(ParseBindPort).Where(port => port > 0).ToArray();

        // Portu okunamayan (beklenmedik biçimli) dinleyiciye DOKUNMA: tahminle bind yazmak,
        // çalışan bir posta portunu sessizce kapatmaktan daha kötüdür.
        if (ports.Length == 0)
        {
            return new SortedSet<string>(binds, StringComparer.Ordinal);
        }

        // Dinleyicinin TEK bir portu olmayabilir; hepsi kamuya açık listede değilse tamamı loopback'e iner.
        if (!ports.All(PublicMailListenerPorts.Contains))
        {
            var loopback = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var port in ports.Distinct())
            {
                loopback.Add($"127.0.0.1:{port}");
                loopback.Add($"[::1]:{port}");
            }

            return loopback;
        }

        // Kamuya açık posta portu: Windows'ta "[::]" IPv6-ONLY demektir, IPv4 erişimi için 0.0.0.0 şart.
        var desired = new SortedSet<string>(binds, StringComparer.Ordinal);
        foreach (var address in binds)
        {
            if (address.StartsWith("[::]:", StringComparison.Ordinal))
            {
                desired.Add("0.0.0.0:" + address["[::]:".Length..]);
            }
        }

        return desired;
    }

    /// <summary>"[::]:143" / "0.0.0.0:25" gibi bir bind adresinden port numarasını çıkarır; okunamazsa 0.</summary>
    private static int ParseBindPort(string address)
    {
        var separatorIndex = address.LastIndexOf(':');
        return separatorIndex >= 0 && int.TryParse(address[(separatorIndex + 1)..], out var port) ? port : 0;
    }

    /// <summary>Korunan (DPAPI) Stalwart kurtarma-admin kimliğinin dosya adı; <c>etc\</c> altında durur.</summary>
    internal const string StalwartRecoverySecretFileName = "recovery-admin.dat";

    /// <summary>
    /// K1: <c>admin:&lt;parola&gt;</c> kimliğini DPAPI ile korur. Kapsam <see cref="DataProtectionScope.LocalMachine"/>'dir
    /// çünkü çözen taraf panel değil, servisi açan zamanlanmış görevin (SYSTEM) kabuğudur — kullanıcı kapsamı
    /// servis hesabı değiştiğinde postayı açılamaz hale getirirdi.
    /// DPAPI tek başına yeterli DEĞİLDİR (aynı makinedeki her süreç çözebilir); asıl kilit dosyanın
    /// DACL'idir (<see cref="DataRootHardeningService.ProtectFile"/>) — ikisi birlikte kullanılır.
    /// </summary>
    internal static byte[] ProtectStalwartSecret(string credential) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(credential), optionalEntropy: null, scope: DataProtectionScope.LocalMachine);

    /// <summary>
    /// K1 — SAF FONKSİYON: parola İÇERMEYEN <c>run-stalwart.cmd</c> içeriğini üretir. Wrapper, korunan
    /// kimlik dosyasını açılışta çözüp <c>STALWART_RECOVERY_ADMIN</c>'e koyar; dosya yolu ortam
    /// değişkeniyle geçilir (PowerShell komutunun içine tırnaklı yol gömmek cmd tarafında kırılgan).
    /// Diske dokunmaz → içerik testte birebir doğrulanabilir ve wrapper gerçekten çalıştırılabilir.
    /// </summary>
    internal static string BuildStalwartWrapperContent(string stalwartExecutable, string configPath, string logPath, string secretPath)
    {
        const string decodeCommand =
            "Add-Type -AssemblyName System.Security; " +
            "[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(" +
            "[IO.File]::ReadAllBytes($env:ADA_STALWART_SECRET), $null, 'LocalMachine'))";

        return
            "@echo off\r\n" +
            "setlocal\r\n" +
            $"set \"ADA_STALWART_SECRET={secretPath}\"\r\n" +
            "set \"STALWART_RECOVERY_ADMIN=\"\r\n" +
            "for /f \"usebackq delims=\" %%A in (`powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
            $"-Command \"{decodeCommand}\"`) do set \"STALWART_RECOVERY_ADMIN=%%A\"\r\n" +
            $"\"{stalwartExecutable}\" --config \"{configPath}\" >> \"{logPath}\" 2>&1\r\n";
    }

    /// <summary>
    /// Stalwart yonetim API'si uzerinden (stalwart-cli ile) tum dinleyicilere IPv4 bind ekler
    /// ve eksikse IMAP(143, STARTTLS) + Submission(587, STARTTLS) dinleyicilerini olusturur.
    /// Bind degisiklikleri yeniden baslatma gerektirdiginden gorev sonda yeniden baslatilir.
    /// </summary>
    private async Task ConfigureStalwartListenersAsync(string adminPassword, string taskName, CancellationToken cancellationToken)
    {
        var cli = TryFindStalwartCli();
        if (cli is null)
        {
            logger.LogWarning("stalwart-cli not found; skipping Stalwart listener configuration.");
            return;
        }

        var environment = new Dictionary<string, string>
        {
            // localhost hem ::1 hem 127.0.0.1'i kapsar; ilk aciliste 8080 yalniz IPv6'da olabilir.
            ["STALWART_URL"] = "http://localhost:8080",
            ["STALWART_USER"] = "admin",
            ["STALWART_PASSWORD"] = adminPassword
        };

        if (!await WaitForStalwartReadyAsync(cli, environment, cancellationToken))
        {
            logger.LogWarning("Stalwart management API did not become ready in time; skipping listener configuration.");
            return;
        }

        var query = await ProcessRunner.RunAsync(cli, "query NetworkListener --json", cancellationToken: cancellationToken, environmentVariables: environment);
        if (!query.Success)
        {
            logger.LogWarning("Could not query Stalwart listeners: {Error}", query.StandardError);
            return;
        }

        var changed = false;
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundPorts = new HashSet<int>();
        foreach (var listener in ParseJsonLines(query.StandardOutput))
        {
            var id = listener.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (listener.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                existingNames.Add(nameValue.GetString()!);
            }

            if (id is null || !listener.TryGetProperty("bind", out var bind) || bind.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // 443 (HTTPS) IIS web siteleri icin ayrilmistir. Stalwart'in varsayilan "https"
            // dinleyicisi 443'u kaparsa IIS sitesi baslatilamaz (HTTP.sys sharing violation,
            // 0x80070020). Bu portu IIS'e birakmak icin 443 baglayan dinleyici silinir
            // (panel Stalwart yonetimini 8080-http uzerinden yapar; 443'e ihtiyac yok).
            if (bind.EnumerateObject().Any(property => property.Name.EndsWith(":443", StringComparison.Ordinal)))
            {
                var deleteHttps = await ProcessRunner.RunAsync(
                    cli,
                    $"delete NetworkListener --ids {id}",
                    cancellationToken: cancellationToken,
                    environmentVariables: environment);
                changed |= deleteHttps.Success;
                if (!deleteHttps.Success)
                {
                    logger.LogWarning("Could not delete Stalwart https(443) listener {ListenerId}: {Error}", id, deleteHttps.StandardError);
                }

                continue;
            }

            // "[::]:port" varsa "0.0.0.0:port" da ekle (cift yigin).
            var binds = bind.EnumerateObject().Select(property => property.Name).ToList();

            // Halihazirda bagli portlari topla (eksik dinleyici eklerken cakismayi onlemek icin).
            foreach (var address in binds)
            {
                var separatorIndex = address.LastIndexOf(':');
                if (separatorIndex >= 0 && int.TryParse(address[(separatorIndex + 1)..], out var boundPort))
                {
                    boundPorts.Add(boundPort);
                }
            }

            // M3 (sertleştirme) + AĞ YÜZEYİ (denetim): yönetim/HTTP portu (8080) YALNIZ LOOPBACK dinler.
            // Eskiden 8080 DIŞINDAKİ HER dinleyiciye "0.0.0.0:port" ekleniyordu — Stalwart'ın varsayılan
            // ManageSieve (4190) ve düz metin POP3 (110) dinleyicileri dahil, yani temiz bir kurulum bu
            // servisleri de tüm dünyaya açıyordu. Karar artık tek bir saf fonksiyonda toplanmıştır.
            var desired = ResolveStalwartListenerBinds(binds);

            // İçerik aynıysa (port/kümeler eşit) dokunma. SetEquals: loopback değişiminde sayı eşit ama içerik farklı olabilir.
            if (desired.SetEquals(binds))
            {
                continue;
            }

            var bindJson = "{" + string.Join(",", desired.Select(address => $"\"{address}\":true")) + "}";
            var update = await ProcessRunner.RunAsync(
                cli,
                $"update NetworkListener {id} --field {CliJsonArg("bind=" + bindJson)}",
                cancellationToken: cancellationToken,
                environmentVariables: environment);
            changed |= update.Success;
            if (!update.Success)
            {
                logger.LogWarning("Could not update Stalwart listener {ListenerId}: {Error}", id, update.StandardError);
            }
        }

        // Gelen/giden mail icin gerekli dinleyiciler.
        //
        // OLCULDU (Stalwart 0.16.8, panelin yazdigi minimal config.json ile yalitilmis bir kopya
        // ayaga kaldirilip `query NetworkListener --json` ile okundu). Varsayilan dinleyici kumesi
        // TAM OLARAK sudur:
        //   http [::]:8080 | https [::]:443 (tlsImplicit) | sieve [::]:4190 |
        //   pop3s [::]:995 | imaps [::]:993 | submissions [::]:465 | smtp [::]:25
        // Yani: 25 VARDIR (asagidaki create atlanir), 143 (IMAP STARTTLS) ve 587 (Submission
        // STARTTLS) HIC YOKTUR -- panel onlari kurmasa Roundcube ve klasik istemciler baglanamaz.
        // Duz metin POP3 (110) da varsayilanda YOKTUR.
        //
        // AYNI OLCUMDE OKUNAN ROLE POLITIKASI (panel bu alanlara DOKUNMAZ, varsayilan gecerlidir):
        //   MtaStageRcpt.allowRelaying = "!is_empty(authenticated_as)"  -> ACIK ROLE YOK
        //   MtaStageAuth.require       = "local_port != 25"             -> 25'te AUTH yok (MX dogru)
        //   MtaStageAuth.saslMechanisms= 25'te "false"; TLS'siz diger portlarda yalniz
        //                                oauthbearer/xoauth2 (duz metin parola TLS'siz gecmez)
        //   MtaStageAuth.mustMatchSender = "true" -> kimligi dogrulanmis kullanici BASKASININ
        //                                zarf gondericisiyle (MAIL FROM) gonderemez
        // Bu yuzden asagidaki `create` cagrilari YALNIZ dinleyici tanimlar; oturum/role politikasi
        // hicbir yerde ezilmez. Politika alanlarina yazan bir kod eklenirse acik role riski
        // dogar -- karar burada yazilidir ki bir sonraki tur yeniden olcmesin.
        var missingListeners = new (string Name, string Protocol, int Port)[]
        {
            ("smtp", "smtp", 25),
            ("imap", "imap", 143),
            ("submission", "smtp", 587)
        };
        foreach (var (name, protocol, port) in missingListeners)
        {
            // Ayni isimde dinleyici varsa ya da port zaten bir dinleyici tarafindan baglanmissa atla
            // (cift baglama 0x80070020 / port cakismasi yaratir).
            if (existingNames.Contains(name) || boundPorts.Contains(port))
            {
                continue;
            }

            var payload = $"{{\"name\":\"{name}\",\"protocol\":\"{protocol}\",\"bind\":{{\"[::]:{port}\":true,\"0.0.0.0:{port}\":true}},\"tlsImplicit\":false}}";
            var create = await ProcessRunner.RunAsync(
                cli,
                $"create NetworkListener --json {CliJsonArg(payload)}",
                cancellationToken: cancellationToken,
                environmentVariables: environment);
            changed |= create.Success;
            if (!create.Success)
            {
                logger.LogWarning("Could not create Stalwart listener {ListenerName}: {Error}", name, create.StandardError);
            }
        }

        // Mail portlarini Windows Guvenlik Duvari'nda ac (panel kurulumu daha once yalniz panel
        // portunu aciyordu; 25 kapali olunca DISARIDAN GELEN posta Stalwart'a ulasamiyordu).
        // Idempotent; degisiklik olsun olmasin her zaman calistir.
        OpenMailServerFirewallPorts();

        if (!changed)
        {
            return;
        }

        // Dinleyici bind degisiklikleri ancak yeniden baslatmayla uygulanir.
        await ProcessRunner.RunAsync("schtasks.exe", $"/End /TN \"{taskName}\"", cancellationToken: cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await ProcessRunner.RunAsync("schtasks.exe", $"/Run /TN \"{taskName}\"", cancellationToken: cancellationToken);
        await WaitForStalwartReadyAsync(cli, environment, cancellationToken);
    }

    // Stalwart mail portlarini Windows Guvenlik Duvari'nda acar. 25=SMTP/MX (gelen + sunucular arasi),
    // 143=IMAP STARTTLS, 587=Submission STARTTLS, 465=SMTPS, 993=IMAPS, 995=POP3S. Best-effort; netsh
    // yoksa/yetki yetmezse sessizce gecer. Idempotent (ayni isimli kurali silip yeniden ekler).
    private void OpenMailServerFirewallPorts()
    {
        foreach (var port in new[] { 25, 143, 465, 587, 993, 995 })
        {
            try
            {
                var ruleName = $"AdaPanel Mail {port}";
                RunFirewallNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
                RunFirewallNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not open mail firewall port {Port}.", port);
            }
        }
    }

    private static void RunFirewallNetsh(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(15000);
    }

    private static async Task<bool> WaitForStalwartReadyAsync(
        string cli,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await ProcessRunner.RunAsync(cli, "query domain --json", cancellationToken: cancellationToken, environmentVariables: environment);
            if (probe.Success)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return false;
    }

    private static string? TryFindStalwartCli()
    {
        var root = Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\stalwart-cli");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "stalwart-cli.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    // stalwart-cli ciktilari NDJSON'dur (satir basina bir JSON nesnesi).
    private static IReadOnlyList<JsonElement> ParseJsonLines(string output)
    {
        var items = new List<JsonElement>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                items.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                // bozuk satiri atla
            }
        }

        return items;
    }

    // Komut satirinda JSON tasiyan tek argüman: ic tirnaklar MSVC kurali ile kacirilir.
    private static string CliJsonArg(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private async Task InstallRedisServiceAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var redisServer = FindExecutable(installDirectory, "redis-server.exe");
        var serviceName = string.IsNullOrWhiteSpace(component.ServiceName) ? "AdaRedis" : component.ServiceName;

        await TryRemoveServiceAsync(serviceName, cancellationToken);

        // Gemiyle gelen redis.windows-service.conf GÖRELI yollar (dir ./, logfile) içerir;
        // Windows servisi olarak çalışma dizini System32 olduğundan bu yollar açılamaz ve
        // `--service-install` exit 1 ile (çıktısız) başarısız olur. Bu yüzden TÜM yolları
        // MUTLAK veren kendi conf dosyamızı yazıp onu kullanıyoruz.
        var dataDirectory = Path.Combine(installDirectory, "data");
        var logDirectory = Path.Combine(installDirectory, "logs");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);
        var configPath = Path.Combine(installDirectory, "redis.adapanel.conf");
        await File.WriteAllTextAsync(
            configPath,
            $$"""
            bind 127.0.0.1
            port 6379
            dir {{ToConfigPath(dataDirectory)}}
            logfile {{ToConfigPath(Path.Combine(logDirectory, "redis.log"))}}
            """,
            cancellationToken);

        await RunProcessAsync(redisServer, $"--service-install \"{configPath}\" --service-name \"{serviceName}\" --port 6379", cancellationToken);
        await RunProcessAsync("sc.exe", $"start \"{serviceName}\"", cancellationToken);
    }

    private async Task InstallMemcachedServiceAsync(
        ComponentManifestItemDto component,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var memcached = FindExecutable(installDirectory, "memcached.exe");
        var taskName = string.IsNullOrWhiteSpace(component.ServiceName) ? "AdaMemcached" : component.ServiceName;

        // Eski (yanlis) Windows servisini ve onceki gorevi temizle.
        await TryRemoveServiceAsync(taskName, cancellationToken);
        await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", cancellationToken: cancellationToken);

        // memcached.exe (mingw) Windows Servis Denetim Protokolu'nu UYGULAMAZ; 'sc' ile servis
        // olarak baslatilirsa SCM SERVICE_RUNNING bildirimi alamaz ve 1053 ("hizmet yanit
        // vermedi") doner. Bu yuzden ONSTART zamanlanmis gorev olarak calistiriyoruz (Stalwart
        // ile ayni mantik): exe normal surec olarak acilir, SCM beklentisi yoktur.
        var wrapperPath = Path.Combine(installDirectory, "run-memcached.cmd");
        await File.WriteAllTextAsync(
            wrapperPath,
            $"@echo off\r\n\"{memcached}\" -m 64 -p 11211 -l 127.0.0.1\r\n",
            cancellationToken);
        await RegisterLongRunningTaskAsync(taskName, wrapperPath, cancellationToken);
        await ProcessRunner.RunAsync("schtasks.exe", $"/Run /TN \"{taskName}\"", cancellationToken: cancellationToken);
    }

    internal static async Task EnsurePhpIniAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var phpIniPath = Path.Combine(installDirectory, "php.ini");
        if (!File.Exists(phpIniPath))
        {
            var productionTemplate = Path.Combine(installDirectory, "php.ini-production");
            if (File.Exists(productionTemplate))
            {
                File.Copy(productionTemplate, phpIniPath, overwrite: false);
            }
            else
            {
                var developmentTemplate = Path.Combine(installDirectory, "php.ini-development");
                if (File.Exists(developmentTemplate))
                {
                    File.Copy(developmentTemplate, phpIniPath, overwrite: false);
                }
                else
                {
                    await File.WriteAllTextAsync(
                        phpIniPath,
                        "; AdaPanel generated a minimal php.ini during bootstrap.\r\n",
                        cancellationToken);
                }
            }
        }

        var lines = (await File.ReadAllLinesAsync(phpIniPath, cancellationToken)).ToList();
        SetPhpIniDirective(lines, "extension_dir", "\"ext\"");
        SetPhpIniDirective(lines, "date.timezone", "UTC");
        SetPhpIniDirective(lines, "cgi.force_redirect", "0");
        SetPhpIniDirective(lines, "fastcgi.impersonate", "1");
        SetPhpIniDirective(lines, "cgi.fix_pathinfo", "1");
        // KRİTİK (IIS): fastcgi.logging açıkken PHP en küçük uyarıyı/notice'i FastCGI stderr'ine yazar ve
        // IIS bunu hata sayıp "500 - İç sunucu hatası" döndürür (sayfa aslında çalışsa bile). 0 = kapat.
        SetPhpIniDirective(lines, "fastcgi.logging", "0");
        // Hatalar stderr yerine bir DOSYAYA yazılsın (IIS 500'ü tetiklemez, yine de teşhis için loglanır).
        // PHP Windows'ta ileri-bölü yolu kabul eder; ters-bölü kaçış belirsizliğinden kaçınılır.
        SetPhpIniDirective(lines, "log_errors", "On");
        SetPhpIniDirective(lines, "error_log", "\"" + Path.Combine(installDirectory, "php_errors.log").Replace("\\", "/") + "\"");
        // GÜVENLİK (K3): boş disable_functions, barındırılan bir sitenin OS komutu çalıştırmasına (RCE) izin verir
        // → K1 (dosya okuma) ile birleşince tam ele geçirme. Sadece komut-çalıştırma fonksiyonlarını kapat;
        // curl/parse_ini gibi uygulama-gerekli fonksiyonlar AÇIK kalır. preserveExisting YOK: varsayılan
        // php.ini'deki boş "disable_functions =" + "expose_php = On" satırlarını EZMEK gerekir.
        SetPhpIniDirective(lines, "disable_functions", "exec,passthru,shell_exec,system,proc_open,popen,pcntl_exec");
        SetPhpIniDirective(lines, "expose_php", "Off");
        // Tuning değerleri: optimal varsayılan ama kullanıcının aktif ayarını BOZMA.
        SetPhpIniDirective(lines, "memory_limit", "512M", preserveExisting: true);
        SetPhpIniDirective(lines, "max_execution_time", "300", preserveExisting: true);
        // DB içe aktarma / büyük yükleme için makul tavan (IIS maxAllowedContentLength 1GB; PHP asıl sınırı belirler).
        SetPhpIniDirective(lines, "upload_max_filesize", "256M", preserveExisting: true);
        SetPhpIniDirective(lines, "post_max_size", "256M", preserveExisting: true);

        var extensionDirectory = Path.Combine(installDirectory, "ext");
        foreach (var extension in new[]
                 {
                     "curl",
                     "exif",
                     "fileinfo",
                     "gd",
                     "gettext",
                     "iconv",
                     "intl",
                     "mbstring",
                     "openssl",
                     "pdo_mysql",
                     "pdo_pgsql",
                     "pdo_sqlite",
                     "mysqli",
                     "pgsql",
                     "sodium",
                     "sqlite3",
                     "zip"
                 })
        {
            if (File.Exists(Path.Combine(extensionDirectory, $"php_{extension}.dll")))
            {
                SetPhpIniExtension(lines, extension);
            }
        }

        // OPcache: yoğun PHP siteleri için EN BÜYÜK performans kazancı — derlenmiş script'leri RAM'de tutar,
        // her istekte yeniden derleme olmaz (çoğu uygulamada 3-5x throughput). Windows PHP'de php_opcache.dll
        // ext\ içinde hazır gelir ama varsayılan php.ini'de KAPALIDIR. validate_timestamps=1 + revalidate_freq=2:
        // kullanıcı dosya güncelleyince ~2 sn içinde yansır (hosting için hız/tazelik dengesi).
        if (File.Exists(Path.Combine(extensionDirectory, "php_opcache.dll")))
        {
            SetPhpIniDirective(lines, "zend_extension", "opcache");
            SetPhpIniDirective(lines, "opcache.enable", "1", preserveExisting: true);
            SetPhpIniDirective(lines, "opcache.enable_cli", "0", preserveExisting: true);
            SetPhpIniDirective(lines, "opcache.memory_consumption", "192", preserveExisting: true);
            SetPhpIniDirective(lines, "opcache.interned_strings_buffer", "16", preserveExisting: true);
            SetPhpIniDirective(lines, "opcache.max_accelerated_files", "20000", preserveExisting: true);
            SetPhpIniDirective(lines, "opcache.validate_timestamps", "1", preserveExisting: true);
            SetPhpIniDirective(lines, "opcache.revalidate_freq", "2", preserveExisting: true);
        }

        await File.WriteAllLinesAsync(phpIniPath, lines, cancellationToken);
    }

    /// <summary>
    /// php.ini'nin GENEL bölümüne yönerge yazar.
    /// </summary>
    /// <remarks>
    /// KAPSAM SINIRI: dosyanın sonunda site başına kiracı izolasyonu blokları (<c>[PATH=...]</c>,
    /// bkz. <c>PhpIniSiteIsolation</c>) durur ve bir PATH başlığından sonraki HER satır o siteye aittir.
    /// Yönergeyi dosya sonuna eklemek, "genel" sanılan ayarı son sitenin bölümüne gömerdi — genel
    /// olarak hiç uygulanmazdı. Hem arama hem ekleme ilk PATH/HOST başlığında durur.
    /// </remarks>
    private static void SetPhpIniDirective(List<string> lines, string name, string value, bool preserveExisting = false)
    {
        var globalEnd = AdaPanel.Infrastructure.Php.PhpIniSiteIsolation.GlobalInsertIndex(lines);
        var activeIndex = -1;
        var commentedIndex = -1;
        for (var i = 0; i < globalEnd; i++)
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
                    if (commentedIndex < 0)
                    {
                        commentedIndex = i;
                    }
                }
                else
                {
                    activeIndex = i;
                    break;
                }
            }
        }

        // Kullanıcının aktif (yorumsuz) ayarını koru: tuning direktiflerinde bileşen/uygulama
        // kurulumu bunu EZMEZ (profesyonel paneller gibi: optimal varsayılan ilk kurulumda, sonra kullanıcı serbest).
        if (preserveExisting && activeIndex >= 0)
        {
            return;
        }

        var directive = $"{name} = {value}";
        if (activeIndex >= 0)
        {
            lines[activeIndex] = directive;
        }
        else if (commentedIndex >= 0)
        {
            lines[commentedIndex] = directive;
        }
        else
        {
            lines.Insert(globalEnd, directive);
        }
    }

    private static void SetPhpIniExtension(List<string> lines, string extension)
    {
        var index = lines.FindIndex(line =>
        {
            var normalized = line.Trim().TrimStart(';').TrimStart().Replace(" ", string.Empty);
            if (!normalized.StartsWith("extension=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var value = normalized["extension=".Length..].Trim('"');
            return string.Equals(value, extension, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, $"php_{extension}.dll", StringComparison.OrdinalIgnoreCase);
        });
        var directive = $"extension={extension}";
        if (index >= 0)
        {
            lines[index] = directive;
        }
        else
        {
            // GENEL bölüme: site [PATH=] bloklarından SONRA eklenen bir extension yalnız o siteye
            // yüklenirdi (öteki bütün siteler eklentisiz kalırdı).
            lines.Insert(AdaPanel.Infrastructure.Php.PhpIniSiteIsolation.GlobalInsertIndex(lines), directive);
        }
    }

    private static string FindExecutable(string installDirectory, string fileName)
    {
        var direct = Path.Combine(installDirectory, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        var bin = Path.Combine(installDirectory, "bin", fileName);
        if (File.Exists(bin))
        {
            return bin;
        }

        var nested = Directory.Exists(installDirectory)
            ? Directory.EnumerateFiles(installDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault()
            : null;

        return nested ?? throw new AppException("software.executable_not_found", 404, 404);
    }

    /// <summary>
    /// Verisini KURULUM DIZININ ICINDE tutan bileşenler ve korunacak alt klasörleri.
    /// Bu bileşenlerde kurulum dizinini silmek = müşterinin verisini silmek.
    /// </summary>
    internal static IReadOnlyList<string> DataDirectoriesToPreserve(string componentId)
    {
        return componentId.Trim().ToLowerInvariant() switch
        {
            "mariadb-11" or "mysql-8" or "postgresql-16" => ["data"],
            // Stalwart posta kutularını data/, üretilmiş yapılandırmayı (admin parolası dahil) etc/ altında tutar.
            "stalwart-mail" => ["data", "etc"],
            "roundcube" => ["adapanel-data", "temp", "logs", "public_html/tmp"],
            "adminer" => ["webroot/tmp", "webroot/adapanel-tokens"],
            "phpmyadmin" => ["tmp", "adapanel-tokens"],
            _ => []
        };
    }

    /// <summary>
    /// Paket varsayılanlarıyla değiştirilmemesi gereken kullanıcı yapılandırmaları.
    /// </summary>
    internal static IReadOnlyList<string> ConfigurationFilesToPreserve(string componentId)
        => componentId.Trim().ToLowerInvariant() switch
        {
            // phpredis is installed separately and is absent from the official PHP ZIP.
            // This component stays on the same 8.3 / NTS / x64 ABI; bundled extensions
            // must come from the new package, rather than preserving the entire ext folder.
            "php-8.3" => ["php.ini", "ext/php_redis.dll"],
            "mariadb-11" or "mysql-8" => ["my.ini"],
            "roundcube" => ["config/config.inc.php", "web.config", "public_html/web.config", "public_html/.user.ini", "public_html/adapanel-health.php"],
            "phpmyadmin" => ["config.inc.php", "web.config", ".user.ini", "adapanel-health.php", "adapanel-signon.php"],
            "adminer" => ["web.config", "webroot/web.config", "webroot/.user.ini", "webroot/adapanel-health.php", "webroot/adapanel-signon.php"],
            _ => []
        };

    private static ComponentStatePreservation PreserveDataDirectories(
        ComponentManifestItemDto component, string installDirectory)
        => ComponentStatePreservation.Capture(installDirectory,
            DataDirectoriesToPreserve(component.Id).Concat(ConfigurationFilesToPreserve(component.Id)));

    internal static string ResolveInstallPassword(bool required, string existingPassword, bool hasExistingData, Func<string> generate)
    {
        if (!required) return string.Empty;
        if (!string.IsNullOrWhiteSpace(existingPassword)) return existingPassword;
        // Yeni parola var olan veritabanında geçerli değildir. Servisi durdurmadan önce dur.
        if (hasExistingData)
            throw new AppException("software.data_preserve_failed", 500, 500,
                "Existing component data was found but its stored administrator credential is unavailable.");
        return generate();
    }
    private static void FlattenSingleTopDirectory(string destination)
    {
        var root = new DirectoryInfo(destination);
        var directories = root.GetDirectories();
        if (directories.Length != 1 || root.GetFiles().Length > 0)
        {
            return;
        }

        var sourceDirectory = directories[0];
        foreach (var directory in sourceDirectory.GetDirectories("*", SearchOption.AllDirectories).OrderBy(item => item.FullName.Length))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(sourceDirectory.FullName, directory.FullName)));
        }

        foreach (var file in sourceDirectory.GetFiles("*", SearchOption.AllDirectories))
        {
            var targetPath = Path.Combine(destination, Path.GetRelativePath(sourceDirectory.FullName, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            file.MoveTo(targetPath, overwrite: true);
        }

        sourceDirectory.Delete(recursive: true);
    }

    private static string ToConfigPath(string path)
        => Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>
    /// Bir bilesenin calisan calistirilabilirini serbest birakir: ayni isimli zamanlanmis
    /// gorevi durdurup siler ve kurulum dizininden calisan tum surecleri sonlandirir.
    /// Boylece kilitli dosyalar (orn. stalwart.exe) silinebilir/uzerine yazilabilir.
    /// </summary>
    /// <summary>
    /// İki mail sunucusunun (Stalwart birincil, MailEnable yedek) aynı anda çalışıp port
    /// çakışması yaratmasını engeller. Biri kurulurken diğerini durdurup devre dışı bırakır.
    /// Best-effort: ilgili sunucu yoksa sessizce geçer.
    /// </summary>
    private async Task ResolveMailServerConflictAsync(string componentId, CancellationToken cancellationToken)
    {
        var id = (componentId ?? string.Empty).Trim().ToLowerInvariant();

        if (id is "stalwart-mail")
        {
            // MailEnable yedeğe: tüm MailEnable Windows servislerini durdur + devre dışı bırak.
            await ProcessRunner.RunAsync(
                "powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-Service -DisplayName 'MailEnable*' -ErrorAction SilentlyContinue | " +
                "ForEach-Object { Stop-Service $_ -Force -ErrorAction SilentlyContinue; Set-Service $_ -StartupType Disabled -ErrorAction SilentlyContinue }\"",
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(45));
        }
        else if (id is "mailenable-standard")
        {
            // Stalwart yedeğe: zamanlanmış görevini durdur+devre dışı bırak, sürecini sonlandır.
            await ProcessRunner.RunAsync("schtasks.exe", "/End /TN \"AdaStalwart\"", cancellationToken: cancellationToken);
            await ProcessRunner.RunAsync("schtasks.exe", "/Change /TN \"AdaStalwart\" /DISABLE", cancellationToken: cancellationToken);
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("stalwart"))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    internal async Task StopManagedRuntimeAsync(string serviceName, string installDirectory, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            // Let database services flush their data before files are moved. A stop
            // failure must abort the update, not fall through to killing the process.
            var stopped = await ProcessRunner.RunPowerShellAsync($$"""
                $ErrorActionPreference = 'Stop'
                $service = Get-Service -Name '{{serviceName.Replace("'", "''")}}' -ErrorAction SilentlyContinue
                if ($null -ne $service -and $service.Status -ne 'Stopped') {
                    $service.Stop()
                    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
                }
                # A task-based component has no Windows service. Get-Service's suppressed
                # not-found result must not become powershell.exe's final exit status.
                exit 0
                """, cancellationToken);
            if (stopped.ExitCode != 0)
                throw new AppException("software.process_failed", 500, 500, "Could not stop component service before replacing its files: " + serviceName);

            // Zamanlanmis gorev (Stalwart gibi) varsa durdur ve sil; yoksa sessizce gecer.
            await ProcessRunner.RunAsync("schtasks.exe", $"/End /TN \"{serviceName}\"", cancellationToken: cancellationToken);
            await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{serviceName}\" /F", cancellationToken: cancellationToken);
        }

        TryKillProcessesUnder(installDirectory);
    }

    private void TryKillProcessesUnder(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    logger.LogInformation("Stopped process {ProcessName} ({Path}) locking the component directory.", process.ProcessName, path);
                }
            }
            catch
            {
                // Sistem surecleri / erisim reddi / zaten cikmis surecler atlanir.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private async Task TryRemoveServiceAsync(string serviceName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return;
        }

        try
        {
            await RunProcessAsync("sc.exe", $"stop \"{serviceName}\"", cancellationToken);
        }
        catch (AppException exception)
        {
            logger.LogInformation(exception, "Skipping service stop for {ServiceName}", serviceName);
        }

        try
        {
            await RunProcessAsync("sc.exe", $"delete \"{serviceName}\"", cancellationToken);
        }
        catch (AppException exception)
        {
            logger.LogInformation(exception, "Skipping service delete for {ServiceName}", serviceName);
        }

        // Windows 'sc delete' servisi SILINMEK UZERE isaretler; tam kalkmadan ayni isimle
        // --service-install / yeni kurulum patlar (exit 1). Servis gercekten gidene kadar bekle.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await RunProcessAsync("sc.exe", $"query \"{serviceName}\"", cancellationToken);
                await Task.Delay(500, cancellationToken); // hala kayitli
            }
            catch
            {
                break; // sc query basarisiz => servis tamamen kalkti
            }
        }
    }

    private async Task TryRunManagedUninstallerAsync(
        ComponentManifestItemDto? component,
        string componentId,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        if (componentId.Equals("nginx", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(installDirectory))
            {
                await NginxInstallationSupport.RemoveAsync(installDirectory, cancellationToken);
            }

            return;
        }

        if (componentId.Equals("postgresql-16", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(installDirectory))
        {
            var postgreSqlUninstaller = Directory
                .EnumerateFiles(installDirectory, "uninstall-postgresql*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (postgreSqlUninstaller is not null)
            {
                await RunProcessAsync(
                    postgreSqlUninstaller,
                    "--mode unattended",
                    cancellationToken);
                return;
            }
        }

        if (componentId.Equals("pgadmin4", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(installDirectory))
        {
            var pgAdminUninstaller = Directory
                .EnumerateFiles(installDirectory, "unins*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName)
                .FirstOrDefault();
            if (pgAdminUninstaller is not null)
            {
                await RunProcessAsync(
                    pgAdminUninstaller,
                    "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                    cancellationToken);
                return;
            }
        }

        var installerPath = ResolveDownloadedInstaller(component);
        if (installerPath is null)
        {
            return;
        }

        if (string.Equals(component?.Type, "msi", StringComparison.OrdinalIgnoreCase))
        {
            await RunProcessAsync(
                "msiexec.exe",
                $"/x \"{installerPath}\" /qn /norestart",
                cancellationToken);
            return;
        }

        var uninstallArguments = componentId.ToLowerInvariant() switch
        {
            "vc-redist-x64" => "/uninstall /quiet /norestart",
            "python-3" => "/uninstall /quiet",
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(uninstallArguments))
        {
            await RunProcessAsync(installerPath, uninstallArguments, cancellationToken);
        }
    }

    private string? ResolveDownloadedInstaller(ComponentManifestItemDto? component)
    {
        if (component is null)
        {
            return null;
        }

        var downloadsRoot = Path.Combine(
            Environment.ExpandEnvironmentVariables(runtimeOptions.DataRoot),
            "downloads");
        if (!Directory.Exists(downloadsRoot))
        {
            return null;
        }

        var exactPath = Path.Combine(downloadsRoot, ResolveDownloadFileName(component));
        if (File.Exists(exactPath))
        {
            return exactPath;
        }

        var pattern = component.Id.ToLowerInvariant() switch
        {
            "nodejs-lts" => "node-*-x64.msi",
            "url-rewrite" => "rewrite_*.msi",
            "iis-arr" => "requestRouter_*.msi",
            "http-platform-handler" => "httpPlatformHandler_*.msi",
            "python-3" => "python-*-amd64.exe",
            "vc-redist-x64" => "vc_redist.x64.exe",
            _ => string.Empty
        };
        return string.IsNullOrWhiteSpace(pattern)
            ? null
            : Directory.EnumerateFiles(downloadsRoot, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
    }

    private void DeleteManagedPath(string path, string? componentId = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var expandedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!IsManagedPath(expandedPath))
        {
            logger.LogWarning("Skipping cleanup outside managed roots: {Path}", expandedPath);
            return;
        }

        if (Directory.Exists(expandedPath))
        {
            if (string.Equals(componentId, "roundcube", StringComparison.OrdinalIgnoreCase))
                ComponentWebPublication.DetachRoundcubeAssets(expandedPath);
            Directory.Delete(expandedPath, recursive: true);
        }
        else if (File.Exists(expandedPath))
        {
            File.Delete(expandedPath);
        }
    }

    private void DeleteDownloadArtifacts(ComponentManifestItemDto? component)
    {
        if (component is null)
        {
            return;
        }

        var downloadsRoot = Path.Combine(Environment.ExpandEnvironmentVariables(runtimeOptions.DataRoot), "downloads");
        if (!Directory.Exists(downloadsRoot))
        {
            return;
        }

        var downloadPath = Path.Combine(downloadsRoot, ResolveDownloadFileName(component));
        if (File.Exists(downloadPath))
        {
            File.Delete(downloadPath);
        }

        if (component.Id.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
        {
            var phpBranch = component.Id["php-".Length..];
            foreach (var file in Directory.EnumerateFiles(downloadsRoot, $"php-{phpBranch}.*-nts-Win32-vs16-x64.zip"))
            {
                File.Delete(file);
            }
        }
    }

    private bool IsManagedPath(string path)
    {
        var candidate = Path.GetFullPath(path);
        var dataRoot = EnsureTrailingSeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.DataRoot)));
        var applicationRoot = EnsureTrailingSeparator(Path.GetFullPath(AppContext.BaseDirectory));

        return candidate.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(applicationRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static ComponentType ParseComponentType(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "zip" => ComponentType.Zip,
            "targz" => ComponentType.TarGz,
            "msi" => ComponentType.Msi,
            "installer" => ComponentType.Installer,
            "phar" => ComponentType.Phar,
            "phpfile" => ComponentType.PhpFile,
            _ => throw new AppException("software.unsupported_component_type", 400, 400)
        };
    }

    private static string GeneratePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes)
            .Replace('+', 'A')
            .Replace('/', 'b')
            .Replace("=", string.Empty);
    }

    // DİKKAT (sha256): null "manifesttekini KORU" demektir. Checksum yayınlamayan bir kaynaktan
    // YENİ sürüm çözerken sha256 olarak MUTLAKA string.Empty geç — null geçilirse eski sürümün
    // hash'i yeni dosyayla karşılaştırılır ve kurulum "checksum uyuşmuyor" ile patlar.
    private static ComponentManifestItemDto CopyComponent(
        ComponentManifestItemDto component,
        string? version = null,
        string? downloadUrl = null,
        string? sha256 = null)
        => new()
        {
            Id = component.Id,
            DisplayName = component.DisplayName,
            Version = version ?? component.Version,
            Type = component.Type,
            DownloadUrl = downloadUrl ?? component.DownloadUrl,
            Sha256 = sha256 ?? component.Sha256,
            InstallDir = component.InstallDir,
            PostInstall = component.PostInstall,
            Dependencies = component.Dependencies,
            ServiceName = component.ServiceName,
            SilentArgs = component.SilentArgs,
            Note = component.Note,
            Default = component.Default
        };

    private sealed class ComponentManifestDocument
    {
        public ComponentManifestItemDto[] Components { get; init; } = [];
    }
}
