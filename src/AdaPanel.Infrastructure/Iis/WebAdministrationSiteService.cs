using System.Collections;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Iis;

public sealed partial class WebAdministrationSiteService(
    IConcurrencyLock concurrencyLock,
    ILogger<WebAdministrationSiteService> logger) : IIisSiteService
{
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";
    private const string ManagedPoolPrefix = "AdaPanel.";

    public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteReadAsync<IReadOnlyCollection<SiteDto>>(serverManager =>
        {
            var sites = GetSitesCollection(serverManager);
            return EnumerateSites(sites)
                .Select(MapSite)
                .Where(site => !IsSystemSite(site))
                .OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, "sites.read_failed", cancellationToken);
    }

    public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        return ExecuteReadAsync(serverManager =>
        {
            var site = FindSite(GetSitesCollection(serverManager), siteName);
            return MapSite(site);
        }, "sites.read_failed", cancellationToken);
    }

    public async Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureIisManagementAvailableAsync(cancellationToken);

        var site = await ExecuteWriteAsync(serverManager =>
        {
            var sites = GetSitesCollection(serverManager);
            var normalizedName = NormalizeSiteName(request.Name);

            // Aynı isimde IIS sitesi zaten varsa (genelde önceki bir AdaPanel kurulumu
            // kaldırılınca IIS'te kalan artık site), onu KALDIR ve yeniden oluştur. Böylece
            // kullanıcı domaini tekrar ekleyebilir (profesyonel paneller gibi: aynı domain = aynı site).
            var existingSite = EnumerateSites(sites).FirstOrDefault(site =>
                string.Equals(GetStringProperty(site, "Name"), normalizedName, StringComparison.OrdinalIgnoreCase));
            if (existingSite is not null)
            {
                InvokeMethod(sites, "Remove", existingSite);
            }

            var protocol = NormalizeProtocol(request.Protocol);
            EnsureSupportedBindingProtocol(protocol);
            var ipAddress = NormalizeIpAddress(request.IpAddress);
            var hostName = NormalizeHostName(request.HostName);
            var selectedPort = ResolveAvailableBindingPort(sites, protocol, ipAddress, request.Port, hostName);
            var bindingInformation = BuildBindingInformation(ipAddress, selectedPort, hostName);
            var physicalPath = ResolvePhysicalPath(request.PhysicalPath);
            Directory.CreateDirectory(physicalPath);
            EnsureStarterContent(physicalPath, normalizedName, hostName, selectedPort);

            var site = InvokeMethod(
                sites,
                "Add",
                normalizedName,
                protocol,
                bindingInformation,
                physicalPath);

            var appPoolName = string.IsNullOrWhiteSpace(request.ApplicationPoolName)
                ? BuildManagedPoolName(normalizedName)
                : request.ApplicationPoolName.Trim();

            EnsureApplicationPool(serverManager, appPoolName);
            SetSiteDefaults(site, request.AutoStart, appPoolName);

            return new SiteDto
            {
                Id = 0,
                Name = normalizedName,
                State = request.AutoStart ? "Started" : "Stopped",
                AutoStart = request.AutoStart,
                PhysicalPath = physicalPath,
                ApplicationRootPath = physicalPath,
                ApplicationPoolName = appPoolName,
                Bindings =
                [
                    new SiteBindingDto
                    {
                        Protocol = protocol,
                        HostName = hostName,
                        IpAddress = ipAddress,
                        Port = selectedPort,
                        BindingInformation = bindingInformation
                    }
                ]
            };
        }, "sites.write_failed", cancellationToken);

        var firewallPort = site.Bindings.FirstOrDefault()?.Port is > 0
            ? site.Bindings.First().Port
            : request.Port;
        await EnsureFirewallRuleAsync(site.Name, firewallPort, cancellationToken);
        return site;
    }

    public Task<SiteDto> UpdatePhysicalPathAsync(
        string siteName,
        string physicalPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(serverManager =>
        {
            var site = FindSite(GetSitesCollection(serverManager), siteName);
            var resolvedPath = ResolvePhysicalPath(physicalPath);
            Directory.CreateDirectory(resolvedPath);

            var rootApplication = GetRootApplication(site);
            var rootVirtualDirectory = GetRootVirtualDirectory(rootApplication);
            SetPropertyValue(rootVirtualDirectory, "PhysicalPath", resolvedPath);
            return MapSite(site);
        }, "sites.write_failed", cancellationToken);
    }

    public async Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync<object?>(serverManager =>
        {
            var sites = GetSitesCollection(serverManager);
            // Silme idempotent: site zaten yoksa (önceki silme/uninstall kalıntısı) hata
            // fırlatma, başarı say.
            var normalizedName = NormalizeSiteName(siteName);
            var site = EnumerateSites(sites).FirstOrDefault(s =>
                string.Equals(GetStringProperty(s, "Name"), normalizedName, StringComparison.OrdinalIgnoreCase));
            if (site is null)
            {
                return null;
            }
            var rootApplication = GetRootApplication(site);
            var appPoolName = GetStringProperty(rootApplication, "ApplicationPoolName");

            InvokeMethod(sites, "Remove", site);

            if (IsManagedAppPool(appPoolName) && CountSitesUsingApplicationPool(sites, appPoolName) == 0)
            {
                var pools = GetPropertyObject(serverManager, "ApplicationPools");
                var pool = FindByName(pools, appPoolName);
                if (pool is not null)
                {
                    InvokeMethod(pools, "Remove", pool);
                }
            }

            return null;
        }, "sites.write_failed", cancellationToken);
    }

    public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(serverManager =>
        {
            var site = FindSite(GetSitesCollection(serverManager), siteName);
            InvokeMethod(site, "Start");
            return MapSite(site);
        }, "sites.write_failed", cancellationToken);
    }

    public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(serverManager =>
        {
            var site = FindSite(GetSitesCollection(serverManager), siteName);
            InvokeMethod(site, "Stop");
            return MapSite(site);
        }, "sites.write_failed", cancellationToken);
    }

    public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(serverManager =>
        {
            var site = FindSite(GetSitesCollection(serverManager), siteName);
            var protocol = NormalizeProtocol(request.Protocol);
            EnsureSupportedBindingProtocol(protocol);
            var bindingInformation = BuildBindingInformation(
                NormalizeIpAddress(request.IpAddress),
                request.Port,
                NormalizeHostName(request.HostName));

            var bindings = GetPropertyObject(site, "Bindings");
            var bindingExists = Enumerate(bindings).Any(binding =>
                string.Equals(GetStringProperty(binding, "Protocol"), protocol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetStringProperty(binding, "BindingInformation"), bindingInformation, StringComparison.OrdinalIgnoreCase));

            if (bindingExists)
            {
                throw new AppException("sites.binding_already_exists", 409, 409);
            }

            InvokeMethod(bindings, "Add", bindingInformation, protocol);
            return MapSite(site);
        }, "sites.write_failed", cancellationToken);
    }

    public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(serverManager =>
        {
            var site = FindSite(GetSitesCollection(serverManager), siteName);
            var protocol = NormalizeProtocol(request.Protocol);
            var bindingInformation = BuildBindingInformation(
                NormalizeIpAddress(request.IpAddress),
                request.Port,
                NormalizeHostName(request.HostName));

            var bindings = GetPropertyObject(site, "Bindings");
            var binding = Enumerate(bindings).FirstOrDefault(item =>
                string.Equals(GetStringProperty(item, "Protocol"), protocol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetStringProperty(item, "BindingInformation"), bindingInformation, StringComparison.OrdinalIgnoreCase));

            if (binding is null)
            {
                throw new AppException("sites.binding_not_found", 404, 404);
            }

            InvokeMethod(bindings, "Remove", binding);
            return MapSite(site);
        }, "sites.write_failed", cancellationToken);
    }

    public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default)
    {
        return GetSitesAsync(cancellationToken);
    }

    private Task<T> ExecuteReadAsync<T>(Func<object, T> operation, string fallbackMessageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        for (var attempt = 0; attempt < 2; attempt += 1)
        {
            try
            {
                using var serverManager = CreateServerManager();
                return Task.FromResult(operation(serverManager));
            }
            catch (AppException)
            {
                throw;
            }
            catch (Exception exception) when (attempt == 0 && IsStaleIisObjectException(exception))
            {
                logger.LogWarning(
                    exception,
                    "IIS returned a stale configuration object. Retrying the read with a fresh ServerManager.");
            }
            catch (Exception exception)
            {
                var mappedException = MapIisException(exception, fallbackMessageKey);
                logger.LogError(
                    exception,
                    "IIS read operation failed. MappedMessageKey={MessageKey}",
                    mappedException.MessageKey);
                throw mappedException;
            }
        }

        throw new AppException(fallbackMessageKey, 500, 500);
    }

    private Task<T> ExecuteWriteAsync<T>(Func<object, T> operation, string fallbackMessageKey, CancellationToken cancellationToken)
    {
        return concurrencyLock.RunExclusiveAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var serverManager = CreateServerManager();
                var result = operation(serverManager);
                InvokeMethod(serverManager, "CommitChanges");
                return Task.FromResult(result);
            }
            catch (AppException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var mappedException = MapIisException(exception, fallbackMessageKey);
                logger.LogError(
                    exception,
                    "IIS write operation failed. MappedMessageKey={MessageKey}",
                    mappedException.MessageKey);
                throw mappedException;
            }
        }, cancellationToken);
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

    private async Task EnsureIisManagementAvailableAsync(CancellationToken cancellationToken)
    {
        var assemblyPath = Environment.ExpandEnvironmentVariables(WebAdministrationAssemblyPath);
        if (File.Exists(assemblyPath))
        {
            return;
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "enable-iis-features.ps1");
        if (!File.Exists(scriptPath))
        {
            logger.LogWarning("IIS management assembly is missing and setup script was not found at {ScriptPath}.", scriptPath);
            throw new AppException("sites.iis_install_required", 503, 503);
        }

        logger.LogInformation("IIS management features are missing. Running the bundled IIS preparation script.");
        var result = await ProcessRunner.RunAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            cancellationToken: cancellationToken);

        if (!result.Success || !File.Exists(assemblyPath))
        {
            logger.LogError(
                "IIS feature preparation failed. Exit code {ExitCode}. Error: {Error}",
                result.ExitCode,
                result.StandardError);
            throw new AppException("sites.iis_install_required", 503, 503);
        }
    }

    private static void EnsureStarterContent(string physicalPath, string siteName, string hostName, int port)
    {
        // Klasörde zaten içerik varsa (örn. phpMyAdmin/Adminer veya kullanıcı dosyaları)
        // başlangıç sayfası YAZMA; aksi halde index.html, uygulamanın gerçek index.php'sini
        // gölgeler ve site placeholder gösterir.
        if (Directory.Exists(physicalPath) && Directory.EnumerateFileSystemEntries(physicalPath).Any())
        {
            return;
        }

        var indexPath = Path.Combine(physicalPath, "index.html");
        if (File.Exists(indexPath))
        {
            return;
        }

        var publicAddress = string.IsNullOrWhiteSpace(hostName)
            ? $"http://localhost:{port}"
            : $"http://{WebUtility.HtmlEncode(hostName)}:{port}";
        var safeSiteName = WebUtility.HtmlEncode(siteName);
        var html = $$"""
            <!doctype html>
            <html lang="tr">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{safeSiteName}}</title>
              <style>
                :root { color-scheme: dark; font-family: "Segoe UI", sans-serif; }
                body { min-height: 100vh; margin: 0; display: grid; place-items: center; background: #09151c; color: #edf7f1; }
                main { width: min(620px, calc(100% - 48px)); padding: 42px; border: 1px solid #24443b; border-radius: 20px; background: #10232a; box-shadow: 0 24px 80px #0008; }
                span { color: #62dc8d; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
                h1 { margin: 14px 0 10px; font-size: clamp(2rem, 7vw, 4rem); }
                p { color: #a9beb4; line-height: 1.7; }
                code { color: #d9f5e3; }
              </style>
            </head>
            <body>
              <main>
                <span>AdaPanel</span>
                <h1>{{safeSiteName}}</h1>
                <p>Siteniz yayinda. Dosyalarinizi bu klasore yukleyerek bu baslangic sayfasini degistirebilirsiniz.</p>
                <p><code>{{publicAddress}}</code></p>
              </main>
            </body>
            </html>
            """;

        File.WriteAllText(indexPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private async Task EnsureFirewallRuleAsync(string siteName, int port, CancellationToken cancellationToken)
    {
        var safeName = AppPoolNamePattern().Replace(siteName, "-");
        var result = await ProcessRunner.RunAsync(
            "netsh.exe",
            $"advfirewall firewall add rule name=\"AdaPanel Site {safeName} TCP {port}\" dir=in action=allow protocol=TCP localport={port}",
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            logger.LogWarning(
                "Site {SiteName} was created, but its firewall rule could not be added. Error: {Error}",
                siteName,
                result.StandardError);
        }
    }

    private static object GetSitesCollection(object serverManager)
    {
        return GetPropertyObject(serverManager, "Sites");
    }

    private static IEnumerable<object> EnumerateSites(object sitesCollection)
    {
        return Enumerate(sitesCollection);
    }

    internal SiteDto MapSite(object site)
    {
        var id = ReadSiteValue(site, "Id", () => GetLongProperty(site, "Id"), 0L);
        var name = ReadSiteValue(site, "Name", () => GetStringProperty(site, "Name"), string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id > 0 ? $"IIS Site {id}" : "Unknown IIS Site";
        }

        var autoStart = ReadSiteValue(
            site,
            "ServerAutoStart",
            () => GetBoolProperty(site, "ServerAutoStart"),
            false);
        var state = ReadSiteValue(
            site,
            "State",
            () => GetPropertyObject(site, "State").ToString() ?? "Unknown",
            "Unknown");

        var bindings = ReadSiteValue(
                site,
                "Bindings",
                () => Enumerate(GetPropertyObject(site, "Bindings"))
                    .Select(binding => ReadBinding(binding, name))
                    .Where(binding => binding is not null)
                    .Cast<SiteBindingDto>()
                    .ToArray(),
                [])
            .OrderBy(binding => binding.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binding => binding.Port)
            .ThenBy(binding => binding.HostName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var rootApplication = ReadSiteValue<object?>(
            site,
            "Applications",
            () => GetRootApplication(site),
            null);
        var rootVirtualDirectory = rootApplication is null
            ? null
            : ReadSiteValue<object?>(
                site,
                "VirtualDirectories",
                () => GetRootVirtualDirectory(rootApplication),
                null);

        var physicalPath = rootVirtualDirectory is null
            ? string.Empty
            : ReadSiteValue(
                site,
                "PhysicalPath",
                () => GetStringProperty(rootVirtualDirectory, "PhysicalPath"),
                string.Empty);
        var applicationPoolName = rootApplication is null
            ? string.Empty
            : ReadSiteValue(
                site,
                "ApplicationPoolName",
                () => GetStringProperty(rootApplication, "ApplicationPoolName"),
                string.Empty);
        return new SiteDto
        {
            Id = id,
            Name = name,
            State = state,
            AutoStart = autoStart,
            PhysicalPath = physicalPath,
            ApplicationRootPath = string.IsNullOrWhiteSpace(physicalPath)
                ? string.Empty
                : ReadSiteValue(
                    site,
                    "ApplicationRootPath",
                    () => ResolveApplicationRootPath(physicalPath),
                    physicalPath),
            ApplicationPoolName = applicationPoolName,
            Bindings = bindings
        };
    }

    private SiteBindingDto? ReadBinding(object binding, string siteName)
    {
        try
        {
            return MapBinding(binding);
        }
        catch (Exception exception) when (IsRecoverableIisReadException(exception))
        {
            logger.LogWarning(
                exception,
                "An IIS binding for site {SiteName} could not be read and was skipped.",
                siteName);
            return null;
        }
    }

    private T ReadSiteValue<T>(object site, string propertyName, Func<T> reader, T fallback)
    {
        try
        {
            return reader();
        }
        catch (Exception exception) when (IsRecoverableIisReadException(exception))
        {
            logger.LogWarning(
                exception,
                "IIS site property {PropertyName} could not be read from {SiteType}; using a safe fallback.",
                propertyName,
                site.GetType().FullName);
            return fallback;
        }
    }

    private static string ResolveApplicationRootPath(string physicalPath)
    {
        var resolvedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(physicalPath));
        if (!string.Equals(Path.GetFileName(resolvedPath), "public", StringComparison.OrdinalIgnoreCase))
        {
            return resolvedPath;
        }

        var parent = Directory.GetParent(resolvedPath)?.FullName;
        return !string.IsNullOrWhiteSpace(parent) &&
               File.Exists(Path.Combine(parent, "artisan")) &&
               File.Exists(Path.Combine(parent, "composer.json"))
            ? parent
            : resolvedPath;
    }

    private static SiteBindingDto MapBinding(object binding)
    {
        var bindingInformation = GetStringProperty(binding, "BindingInformation");
        var (ipAddress, port, hostName) = ParseBindingInformation(bindingInformation);

        return new SiteBindingDto
        {
            Protocol = GetStringProperty(binding, "Protocol"),
            HostName = hostName,
            IpAddress = ipAddress,
            Port = port,
            BindingInformation = bindingInformation
        };
    }

    private static object GetRootApplication(object site)
    {
        var applications = GetPropertyObject(site, "Applications");
        return Enumerate(applications).FirstOrDefault(application =>
                   string.Equals(GetStringProperty(application, "Path"), "/", StringComparison.OrdinalIgnoreCase))
               ?? Enumerate(applications).First();
    }

    private static object GetRootVirtualDirectory(object application)
    {
        var virtualDirectories = GetPropertyObject(application, "VirtualDirectories");
        return Enumerate(virtualDirectories).FirstOrDefault(directory =>
                   string.Equals(GetStringProperty(directory, "Path"), "/", StringComparison.OrdinalIgnoreCase))
               ?? Enumerate(virtualDirectories).First();
    }

    // AdaPanel'in iç/altyapı IIS siteleri (FTP, phpMyAdmin/Adminer, MailEnable web arayüzleri,
    // IIS varsayılan sitesi). profesyonel paneller gibi: bunlar kullanıcının web sitesi listesinde GÖRÜNMEZ.
    private static readonly HashSet<string> SystemSiteNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default Web Site",
        "AdaPanel FTP"
    };

    private static readonly string[] SystemSitePrefixes =
    {
        "AdaPanel.",   // AdaPanel.WebDb.PhpMyAdmin, AdaPanel.WebDb.Adminer
        "MailEnable"   // MailEnable WebAdmin, MailEnable WebMail
    };

    private static bool IsSystemSite(SiteDto site)
    {
        var name = site.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (SystemSiteNames.Contains(name) ||
            SystemSitePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Geleceğe dönük: gerçek bir alan adına (public binding) sahip OLMAYAN, yalnızca
        // loopback'e bağlı siteler dahili servislerdir (yönetim panelleri, webmail, db araçları).
        // Kullanıcının eklediği siteler her zaman bir hostname binding'i taşır.
        if (site.Bindings.Count > 0 && site.Bindings.All(IsLoopbackBinding))
        {
            return true;
        }

        return false;
    }

    private static bool IsLoopbackBinding(SiteBindingDto binding)
    {
        if (string.Equals(binding.HostName, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ip = binding.BindingInformation.Split(':').FirstOrDefault() ?? string.Empty;
        return ip is "127.0.0.1" or "::1";
    }

    private static object FindSite(object sitesCollection, string siteName)
    {
        var normalizedName = NormalizeSiteName(siteName);
        return EnumerateSites(sitesCollection).FirstOrDefault(site =>
                   string.Equals(GetStringProperty(site, "Name"), normalizedName, StringComparison.OrdinalIgnoreCase))
               ?? throw new AppException("sites.site_not_found", 404, 404);
    }

    private static object? FindByName(object collection, string name)
    {
        return Enumerate(collection).FirstOrDefault(item =>
            string.Equals(GetStringProperty(item, "Name"), name, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetSiteDefaults(object site, bool autoStart, string applicationPoolName)
    {
        SetPropertyValue(site, "ServerAutoStart", autoStart);
        var rootApplication = GetRootApplication(site);
        SetPropertyValue(rootApplication, "ApplicationPoolName", applicationPoolName);
    }

    private static void EnsureApplicationPool(object serverManager, string applicationPoolName)
    {
        var pools = GetPropertyObject(serverManager, "ApplicationPools");
        if (FindByName(pools, applicationPoolName) is not null)
        {
            return;
        }

        var pool = InvokeMethod(pools, "Add", applicationPoolName);
        SetPropertyValue(pool, "AutoStart", true);
        SetPropertyValue(pool, "ManagedRuntimeVersion", string.Empty);
    }

    private static int CountSitesUsingApplicationPool(object sitesCollection, string applicationPoolName)
    {
        return EnumerateSites(sitesCollection)
            .Count(site =>
            {
                var rootApplication = GetRootApplication(site);
                return string.Equals(
                    GetStringProperty(rootApplication, "ApplicationPoolName"),
                    applicationPoolName,
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string ResolvePhysicalPath(string physicalPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(physicalPath.Trim());
        if (string.IsNullOrWhiteSpace(expandedPath))
        {
            throw new AppException("sites.invalid_physical_path", 400, 400);
        }

        return Path.GetFullPath(expandedPath);
    }

    private static string NormalizeSiteName(string siteName)
    {
        var normalized = siteName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new AppException("sites.invalid_name", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeProtocol(string protocol)
    {
        var normalized = protocol.Trim().ToLowerInvariant();
        return normalized is "http" or "https"
            ? normalized
            : throw new AppException("sites.invalid_protocol", 400, 400);
    }

    private static void EnsureSupportedBindingProtocol(string protocol)
    {
        if (string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("sites.https_binding_requires_ssl", 400, 400);
        }
    }

    private static string NormalizeIpAddress(string ipAddress)
    {
        var normalized = string.IsNullOrWhiteSpace(ipAddress) ? "*" : ipAddress.Trim();
        if (normalized == "*" || string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return IPAddress.TryParse(normalized, out _)
            ? normalized
            : throw new AppException("sites.invalid_ip_address", 400, 400);
    }

    private static string NormalizeHostName(string hostName)
    {
        var normalized = hostName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (!HostNamePattern().IsMatch(normalized))
        {
            throw new AppException("sites.invalid_host_name", 400, 400);
        }

        return normalized;
    }

    private static string BuildBindingInformation(string ipAddress, int port, string hostName)
    {
        return $"{ipAddress}:{port}:{hostName}";
    }

    private static int ResolveAvailableBindingPort(
        object sitesCollection,
        string protocol,
        string ipAddress,
        int requestedPort,
        string hostName)
    {
        if (!IsBindingInUse(sitesCollection, protocol, ipAddress, requestedPort, hostName))
        {
            return requestedPort;
        }

        if (!string.IsNullOrWhiteSpace(hostName))
        {
            throw new AppException("sites.binding_conflict", 409, 409);
        }

        var firstFallbackPort = requestedPort is 80 or 443 ? 8080 : requestedPort + 1;
        for (var candidatePort = firstFallbackPort; candidatePort <= 65535 && candidatePort < firstFallbackPort + 500; candidatePort += 1)
        {
            if (!IsBindingInUse(sitesCollection, protocol, ipAddress, candidatePort, hostName))
            {
                return candidatePort;
            }
        }

        throw new AppException("sites.binding_conflict", 409, 409);
    }

    private static bool IsBindingInUse(
        object sitesCollection,
        string protocol,
        string ipAddress,
        int port,
        string hostName)
    {
        return EnumerateSites(sitesCollection)
            .SelectMany(site => Enumerate(GetPropertyObject(site, "Bindings")))
            .Where(binding => string.Equals(GetStringProperty(binding, "Protocol"), protocol, StringComparison.OrdinalIgnoreCase))
            .Select(binding => ParseBindingInformation(GetStringProperty(binding, "BindingInformation")))
            .Any(binding => BindingConflicts(binding.IpAddress, binding.Port, binding.HostName, ipAddress, port, hostName));
    }

    private static bool BindingConflicts(
        string existingIpAddress,
        int existingPort,
        string existingHostName,
        string requestedIpAddress,
        int requestedPort,
        string requestedHostName)
    {
        if (existingPort != requestedPort)
        {
            return false;
        }

        var hostNamesConflict =
            string.IsNullOrWhiteSpace(existingHostName) && string.IsNullOrWhiteSpace(requestedHostName) ||
            !string.IsNullOrWhiteSpace(existingHostName) &&
            !string.IsNullOrWhiteSpace(requestedHostName) &&
            string.Equals(existingHostName, requestedHostName, StringComparison.OrdinalIgnoreCase);

        if (!hostNamesConflict)
        {
            return false;
        }

        return string.Equals(existingIpAddress, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedIpAddress, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(existingIpAddress, requestedIpAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static (string IpAddress, int Port, string HostName) ParseBindingInformation(string bindingInformation)
    {
        var segments = bindingInformation.Split(':');
        if (segments.Length < 2)
        {
            return (bindingInformation, 0, string.Empty);
        }

        var hostName = segments.Length > 2 ? string.Join(':', segments.Skip(2)) : string.Empty;
        return (
            segments[0],
            int.TryParse(segments[1], out var port) ? port : 0,
            hostName);
    }

    private static string BuildManagedPoolName(string siteName)
    {
        return ManagedPoolPrefix + AppPoolNamePattern().Replace(siteName, "-");
    }

    private static bool IsManagedAppPool(string applicationPoolName)
    {
        return applicationPoolName.StartsWith(ManagedPoolPrefix, StringComparison.OrdinalIgnoreCase);
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
                ?? throw new AppException("sites.iis_not_available", 503, 503);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static string GetStringProperty(object target, string propertyName)
    {
        return GetPropertyObject(target, propertyName).ToString() ?? string.Empty;
    }

    private static long GetLongProperty(object target, string propertyName)
    {
        return Convert.ToInt64(GetPropertyObject(target, propertyName));
    }

    private static bool GetBoolProperty(object target, string propertyName)
    {
        return Convert.ToBoolean(GetPropertyObject(target, propertyName));
    }

    private static void SetPropertyValue(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            property.SetValue(target, value);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
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

    private static AppException MapIisException(Exception exception, string fallbackMessageKey)
    {
        var message = FlattenExceptionMessage(exception);

        // Dilden bağımsız HRESULT eşleştirmesi (Türkçe Windows'ta İngilizce metin
        // eşleşmeleri tutmadığı için önce bunlara bakıyoruz):
        // 0x80070020 = ERROR_SHARING_VIOLATION → port/dosya başka işlemde (ör. 443'ü
        // tutan bir posta sunucusu yüzünden site başlatılamıyor).
        if (message.Contains("0x80070020", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.port_in_use", 409, exception);
        }

        // 0x80070005 = E_ACCESSDENIED, 0x80070522 = ERROR_PRIVILEGE_NOT_HELD → yetki.
        if (message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("0x80070522", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_permission_denied", 403, exception);
        }

        // 0x800700B7 = ERROR_ALREADY_EXISTS → binding/kayıt çakışması.
        if (message.Contains("0x800700B7", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.binding_conflict", 409, exception);
        }

        if (message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("already been added", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot create a file when that file already exists", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot add duplicate collection entry", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unique key attribute", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("binding", StringComparison.OrdinalIgnoreCase) && message.Contains("assigned", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.binding_conflict", 409, exception);
        }

        if (message.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("insufficient privileges", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cannot write configuration file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("applicationhost.config", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("write", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_permission_denied", 403, exception);
        }

        if (message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("The process cannot access the file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("configuration file is locked", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_configuration_locked", 409, exception);
        }

        if (message.Contains("section", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("cannot be read", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_install_required", 503, exception);
        }

        return CreateDiagnosticException(fallbackMessageKey, 500, exception);
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
        // Mesajın yanına her seviyenin HRESULT hex kodunu da ekliyoruz. Sunucu Türkçe
        // (veya başka dilde) Windows olduğunda Win32/COM hata METİNLERİ yerelleşir ve
        // İngilizce substring eşleşmeleri tutmaz; HRESULT kodu ise dilden bağımsızdır.
        // Böylece "0x80070020" (paylaşım ihlali / port kullanımda) gibi durumları
        // dile bakmadan yakalayabiliriz.
        var builder = new StringBuilder();
        var current = exception;
        while (current is not null)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(current.Message);
            builder.Append(" (HRESULT=0x").Append(current.HResult.ToString("X8")).Append(')');
            current = current.InnerException;
        }

        return builder.ToString();
    }

    private static bool IsStaleIisObjectException(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is COMException comException &&
                comException.HResult == unchecked((int)0x800710D8))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private static bool IsRecoverableIisReadException(Exception exception)
    {
        return exception is COMException or InvalidOperationException or ObjectDisposedException or MissingMemberException ||
               IsStaleIisObjectException(exception);
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

    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.Compiled)]
    private static partial Regex AppPoolNamePattern();

    [GeneratedRegex("^[A-Za-z0-9*._:-]+$", RegexOptions.Compiled)]
    private static partial Regex HostNamePattern();
}
