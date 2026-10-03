using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Files;

namespace AdaPanel.Infrastructure.AppRuntimes;

public sealed partial class AppRuntimeService(
    AdaPanelDbContext dbContext,
    IIisSiteService iisSiteService,
    IComponentBootstrapper componentBootstrapper,
    IConcurrencyLock concurrencyLock,
    ILogger<AppRuntimeService> logger) : IAppRuntimeService
{
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";
    private const string HandlerName = "AdaPanel.HttpPlatform";
    private static readonly string LogsDirectoryName = Path.Combine("App_Data", "AdaPanel", "logs");
    internal Func<IDisposable> ManagerFactory { get; init; } = CreateServerManager;
    internal Func<Stream, Stream>? ConfigurationStream { get; init; }

    public async Task<AppRuntimeSiteConfigurationDto> GetSiteConfigurationAsync(
        string siteName,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        return ReadConfiguration(site);
    }

    public Task<AppRuntimeSiteConfigurationDto> HostNodeAppAsync(string siteName, HostNodeAppRequest request,
        CancellationToken cancellationToken = default) =>
        HostNodeAppAsync(siteName, request, (_, _) => Task.CompletedTask, cancellationToken);

    public Task<AppRuntimeSiteConfigurationDto> HostNodeAppAsync(string siteName, HostNodeAppRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        HostRuntimeAsync(siteName, "node", request.ComponentId, request.StartupFile, request.WorkingDirectory,
            request.Arguments, request.EnvironmentVariables, beforeMutation, null, cancellationToken);

    internal Task<AppRuntimeSiteConfigurationDto> HostNodeAppAsync(SiteMutationGuard.Target owner, HostNodeAppRequest request,
        CancellationToken cancellationToken) =>
        HostRuntimeAsync(owner.Site.Name, "node", request.ComponentId, request.StartupFile, request.WorkingDirectory,
            request.Arguments, request.EnvironmentVariables, (_, _) => Task.CompletedTask, owner, cancellationToken);

    public Task<AppRuntimeSiteConfigurationDto> HostPythonAppAsync(string siteName, HostPythonAppRequest request,
        CancellationToken cancellationToken = default) =>
        HostPythonAppAsync(siteName, request, (_, _) => Task.CompletedTask, cancellationToken);

    public Task<AppRuntimeSiteConfigurationDto> HostPythonAppAsync(string siteName, HostPythonAppRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        HostRuntimeAsync(siteName, "python", request.ComponentId, request.StartupFile, request.WorkingDirectory,
            request.Arguments, request.EnvironmentVariables, beforeMutation, null, cancellationToken);

    internal Task<AppRuntimeSiteConfigurationDto> HostPythonAppAsync(SiteMutationGuard.Target owner, HostPythonAppRequest request,
        CancellationToken cancellationToken) =>
        HostRuntimeAsync(owner.Site.Name, "python", request.ComponentId, request.StartupFile, request.WorkingDirectory,
            request.Arguments, request.EnvironmentVariables, (_, _) => Task.CompletedTask, owner, cancellationToken);

    private async Task<AppRuntimeSiteConfigurationDto> HostRuntimeAsync(string siteName, string runtimeType,
        string componentId, string startupFile, string workingDirectory, string additionalArguments,
        IReadOnlyCollection<string> environmentVariables, Func<SiteDto, CancellationToken, Task> beforeMutation,
        SiteMutationGuard.Target? owner, CancellationToken cancellationToken)
    {
        // Provisioning passes its own creator target; a new public capture must not bypass
        // that unfinished generation's private creation intent.
        using var captured = owner is null
            ? await SiteMutationGuard.CaptureAsync(iisSiteService, concurrencyLock, siteName, cancellationToken) : null;
        var target = owner ?? captured!;
        var submittedEnvironment = environmentVariables.ToArray();
        await target.RunAsync(site => beforeMutation(site, cancellationToken), cancellationToken);
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        using var reservation = await target.ReserveAsync(beforeMutation, cancellationToken);
        var installed = catalog.Installed.Any(component =>
            string.Equals(component.ComponentId, "http-platform-handler", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(component.Status, "Installed", StringComparison.OrdinalIgnoreCase));
        if (!installed)
        {
            var installation = await componentBootstrapper.InstallAsync("http-platform-handler", null, null,
                force: false, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(installation.Status, "Installed", StringComparison.OrdinalIgnoreCase))
                throw new AppException("appruntime.configuration_failed", 500, 500);
        }
        var executablePath = runtimeType == "node"
            ? await ResolveNodeExecutableAsync(componentId, cancellationToken)
            : await ResolvePythonExecutableAsync(componentId, cancellationToken);
        return await ApplyRuntimeConfigurationAsync(target, runtimeType, componentId, executablePath,
            startupFile, workingDirectory, additionalArguments, submittedEnvironment, beforeMutation, cancellationToken);
    }

    private async Task<AppRuntimeSiteConfigurationDto> ApplyRuntimeConfigurationAsync(
        SiteMutationGuard.Target target,
        string runtimeType,
        string componentId,
        string processPath,
        string startupFile,
        string workingDirectory,
        string additionalArguments,
        IReadOnlyCollection<string> environmentVariables,
        Func<SiteDto, CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken)
    {
        var site = target.Site;
        var siteRoot = ResolveSiteRoot(site.PhysicalPath);

        var normalizedWorkingDirectory = ResolveSiteManagedPath(siteRoot, workingDirectory, allowCurrentDirectory: true);

        var normalizedStartupFile = ResolveSiteManagedPath(siteRoot, startupFile, allowCurrentDirectory: false);
        var relativeStartupFile = ToSiteRelativePath(siteRoot, normalizedStartupFile);
        var startupArguments = BuildArguments(relativeStartupFile, additionalArguments);
        var mergedEnvironmentVariables = BuildEnvironmentVariables(runtimeType, environmentVariables);
        var webConfigPath = ResolveSiteManagedPath(siteRoot, "web.config", allowCurrentDirectory: false);
        var logsDirectory = ResolveSiteManagedPath(siteRoot, LogsDirectoryName, allowCurrentDirectory: true);

        AppRuntimeSiteConfigurationDto? result = null;
        await target.RunAsync(async current =>
        {
            await beforeMutation(current, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Action? restorePool = null;
            try
            {
                // A failed or unmeasured pool commit must not replace the working web.config.
                EnsureApplicationPoolUsesNoManagedCode(current.ApplicationPoolName, out restorePool);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(siteRoot);
                Directory.CreateDirectory(normalizedWorkingDirectory);
                Directory.CreateDirectory(logsDirectory);
                UpdateWebConfig(
                    webConfigPath,
                    processPath,
                    startupArguments,
                    normalizedWorkingDirectory,
                    logsDirectory,
                    mergedEnvironmentVariables,
                    cancellationToken);
                restorePool = null;

                result = new AppRuntimeSiteConfigurationDto
                {
                    SiteName = site.Name,
                    RuntimeType = runtimeType,
                    ComponentId = componentId,
                    ProcessPath = processPath,
                    StartupFile = relativeStartupFile,
                    WorkingDirectory = ToSiteRelativeDirectory(siteRoot, normalizedWorkingDirectory),
                    Arguments = additionalArguments.Trim(),
                    WebConfigPath = webConfigPath,
                    LogsDirectory = logsDirectory,
                    IsEnabled = true,
                    HasHttpPlatformHandler = true,
                    ManagedRuntimeDisabled = true,
                    EnvironmentVariables = mergedEnvironmentVariables.ToArray()
                };
            }
            catch (Exception exception)
            {
                if (restorePool is not null)
                {
                    try
                    {
                        // Compensation keeps this same IIS lease, ignores request cancellation,
                        // and never follows a replacement site's new application pool.
                        await target.ValidateAsync(CancellationToken.None);
                        restorePool();
                    }
                    catch (Exception recoveryError)
                    {
                        logger.LogError(recoveryError, "Unable to restore the previous application pool setting for site {SiteName}.", site.Name);
                    }
                }
                if (exception is AppException or OperationCanceledException) throw;
                var mappedException = MapAppRuntimeException(exception, "appruntime.configuration_failed");
                logger.LogError(exception, "Application runtime configuration failed for site {SiteName}.", site.Name);
                throw mappedException;
            }
        }, cancellationToken);
        return result!;
    }

    private AppRuntimeSiteConfigurationDto ReadConfiguration(AdaPanel.Core.Dtos.Sites.SiteDto site)
    {
        var siteRoot = ResolveSiteRoot(site.PhysicalPath);
        var webConfigPath = ResolveSiteManagedPath(siteRoot, "web.config", allowCurrentDirectory: false);
        var logsDirectory = Path.Combine(siteRoot, LogsDirectoryName);
        var managedRuntimeDisabled = ReadManagedRuntimeDisabled(site.ApplicationPoolName);

        if (!File.Exists(webConfigPath))
        {
            return new AppRuntimeSiteConfigurationDto
            {
                SiteName = site.Name,
                WorkingDirectory = ".",
                WebConfigPath = webConfigPath,
                LogsDirectory = logsDirectory,
                ManagedRuntimeDisabled = managedRuntimeDisabled
            };
        }

        try
        {
            var document = XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace);
            var configuration = document.Element("configuration");
            var systemWebServer = configuration?.Element("system.webServer");
            var handlers = systemWebServer?.Element("handlers");
            var httpPlatformHandler = handlers?
                .Elements("add")
                .FirstOrDefault(element =>
                    string.Equals((string?)element.Attribute("name"), HandlerName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string?)element.Attribute("modules"), "httpPlatformHandler", StringComparison.OrdinalIgnoreCase));

            var httpPlatform = systemWebServer?.Element("httpPlatform");
            if (httpPlatform is null)
            {
                return new AppRuntimeSiteConfigurationDto
                {
                    SiteName = site.Name,
                    WorkingDirectory = ".",
                    WebConfigPath = webConfigPath,
                    LogsDirectory = logsDirectory,
                    HasHttpPlatformHandler = httpPlatformHandler is not null,
                    ManagedRuntimeDisabled = managedRuntimeDisabled
                };
            }

            var processPath = (string?)httpPlatform.Attribute("processPath") ?? string.Empty;
            var arguments = (string?)httpPlatform.Attribute("arguments") ?? string.Empty;
            var workingDirectory = (string?)httpPlatform.Attribute("workingDirectory") ?? ".";
            var startupFile = ExtractStartupFile(arguments);
            var additionalArguments = RemoveStartupFileFromArguments(arguments);
            var runtimeType = DetectRuntimeType(processPath);
            var componentId = runtimeType switch
            {
                "node" => "nodejs-lts",
                "python" => "python-3",
                _ => string.Empty
            };

            var environmentVariables = httpPlatform
                .Element("environmentVariables")?
                .Elements("environmentVariable")
                .Select(item => $"{(string?)item.Attribute("name") ?? string.Empty}={(string?)item.Attribute("value") ?? string.Empty}")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
                ?? [];

            return new AppRuntimeSiteConfigurationDto
            {
                SiteName = site.Name,
                RuntimeType = runtimeType,
                ComponentId = componentId,
                ProcessPath = processPath,
                StartupFile = NormalizeDisplayedPath(siteRoot, startupFile),
                WorkingDirectory = NormalizeDisplayedPath(siteRoot, workingDirectory),
                Arguments = additionalArguments,
                WebConfigPath = webConfigPath,
                LogsDirectory = logsDirectory,
                IsEnabled = !string.IsNullOrWhiteSpace(processPath),
                HasHttpPlatformHandler = httpPlatformHandler is not null,
                ManagedRuntimeDisabled = managedRuntimeDisabled,
                EnvironmentVariables = environmentVariables
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to read application runtime configuration for site {SiteName}.", site.Name);
            throw CreateDiagnosticException("appruntime.read_failed", 500, exception);
        }
    }

    private async Task<string> ResolveNodeExecutableAsync(string componentId, CancellationToken cancellationToken) =>
        await AppRuntimeExecutableResolver.ResolveAsync(dbContext, componentId, "node", cancellationToken)
            ?? throw new AppException("appruntime.runtime_not_installed", 404, 404);

    private async Task<string> ResolvePythonExecutableAsync(string componentId, CancellationToken cancellationToken) =>
        await AppRuntimeExecutableResolver.ResolveAsync(dbContext, componentId, "python", cancellationToken)
            ?? throw new AppException("appruntime.runtime_not_installed", 404, 404);

    private void UpdateWebConfig(
        string webConfigPath,
        string processPath,
        string arguments,
        string workingDirectory,
        string logsDirectory,
        IReadOnlyCollection<string> environmentVariables,
        CancellationToken cancellationToken)
    {
        var document = File.Exists(webConfigPath)
            ? XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));

        var configuration = document.Element("configuration");
        if (configuration is null)
        {
            configuration = new XElement("configuration");
            document.Add(configuration);
        }

        var systemWebServer = EnsureChild(configuration, "system.webServer");
        var handlers = EnsureChild(systemWebServer, "handlers");

        foreach (var existingHandler in handlers.Elements("add").Where(element =>
                     string.Equals((string?)element.Attribute("name"), HandlerName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals((string?)element.Attribute("modules"), "httpPlatformHandler", StringComparison.OrdinalIgnoreCase))
                 .ToArray())
        {
            existingHandler.Remove();
        }

        handlers.Add(
            new XElement(
                "add",
                new XAttribute("name", HandlerName),
                new XAttribute("path", "*"),
                new XAttribute("verb", "*"),
                new XAttribute("modules", "httpPlatformHandler"),
                new XAttribute("resourceType", "Unspecified")));

        systemWebServer.Elements("httpPlatform").Remove();

        var httpPlatform = new XElement(
            "httpPlatform",
            new XAttribute("processPath", processPath),
            new XAttribute("arguments", arguments),
            new XAttribute("workingDirectory", workingDirectory),
            new XAttribute("stdoutLogEnabled", "true"),
            new XAttribute("stdoutLogFile", Path.Combine(logsDirectory, "httpplatform-stdout")),
            new XAttribute("startupTimeLimit", "60"),
            new XAttribute("requestTimeout", "00:10:00"));

        var environmentVariablesElement = new XElement("environmentVariables");
        foreach (var variable in environmentVariables)
        {
            var separatorIndex = variable.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = variable[..separatorIndex].Trim();
            var value = variable[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            environmentVariablesElement.Add(
                new XElement(
                    "environmentVariable",
                    new XAttribute("name", name),
                    new XAttribute("value", value)));
        }

        if (environmentVariablesElement.HasElements)
        {
            httpPlatform.Add(environmentVariablesElement);
        }

        systemWebServer.Add(httpPlatform);
        WebConfigurationFile.Save(webConfigPath, document, ConfigurationStream, cancellationToken);
    }

    private void EnsureApplicationPoolUsesNoManagedCode(string applicationPoolName, out Action? restorePool)
    {
        restorePool = null;
        if (string.IsNullOrWhiteSpace(applicationPoolName))
            throw new AppException("appruntime.configuration_failed", 500, 500);
        using (var serverManager = ManagerFactory())
        {
            var applicationPools = GetPropertyObject(serverManager, "ApplicationPools");
            var applicationPool = Enumerate(applicationPools)
                .FirstOrDefault(item =>
                    string.Equals(GetStringProperty(item, "Name"), applicationPoolName, StringComparison.OrdinalIgnoreCase));

            if (applicationPool is null)
                throw new AppException("appruntime.configuration_failed", 500, 500);

            var previousRuntime = GetStringProperty(applicationPool, "ManagedRuntimeVersion");
            restorePool = () => RestoreApplicationPoolRuntime(applicationPoolName, previousRuntime);
            SetPropertyValue(applicationPool, "ManagedRuntimeVersion", string.Empty);
            InvokeMethod(serverManager, "CommitChanges");
        }
        using var verification = ManagerFactory();
        var committedPool = Enumerate(GetPropertyObject(verification, "ApplicationPools"))
            .FirstOrDefault(item => string.Equals(GetStringProperty(item, "Name"), applicationPoolName, StringComparison.OrdinalIgnoreCase));
        if (committedPool is null || !string.IsNullOrWhiteSpace(GetStringProperty(committedPool, "ManagedRuntimeVersion")))
            throw new AppException("appruntime.configuration_failed", 500, 500);
    }

    private void RestoreApplicationPoolRuntime(string applicationPoolName, string previousRuntime)
    {
        if (previousRuntime.Length == 0) return;
        using var manager = ManagerFactory();
        var pool = Enumerate(GetPropertyObject(manager, "ApplicationPools"))
            .FirstOrDefault(item => string.Equals(GetStringProperty(item, "Name"), applicationPoolName, StringComparison.OrdinalIgnoreCase));
        // Restore only our own empty CLR setting. A removed pool or a subsequently changed
        // value is not ours to recreate or replace, even while recovering a failed request.
        if (pool is null || GetStringProperty(pool, "ManagedRuntimeVersion") != string.Empty) return;
        SetPropertyValue(pool, "ManagedRuntimeVersion", previousRuntime);
        InvokeMethod(manager, "CommitChanges");
    }

    private bool ReadManagedRuntimeDisabled(string applicationPoolName)
    {
        if (string.IsNullOrWhiteSpace(applicationPoolName))
        {
            return false;
        }

        try
        {
            using var serverManager = ManagerFactory();
            var applicationPools = GetPropertyObject(serverManager, "ApplicationPools");
            var applicationPool = Enumerate(applicationPools)
                .FirstOrDefault(item =>
                    string.Equals(GetStringProperty(item, "Name"), applicationPoolName, StringComparison.OrdinalIgnoreCase));

            if (applicationPool is null)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(GetStringProperty(applicationPool, "ManagedRuntimeVersion"));
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveSiteRoot(string physicalPath)
    {
        var normalized = Environment.ExpandEnvironmentVariables(physicalPath.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new AppException("sites.invalid_physical_path", 400, 400);
        }

        return Path.GetFullPath(normalized);
    }

    private static string ResolveSiteManagedPath(string siteRoot, string path, bool allowCurrentDirectory)
    {
        var normalized = string.IsNullOrWhiteSpace(path)
            ? "."
            : Environment.ExpandEnvironmentVariables(path.Trim());

        if (!allowCurrentDirectory && string.Equals(normalized, ".", StringComparison.Ordinal))
        {
            throw new AppException("appruntime.startup_file_required", 400, 400);
        }

        var resolved = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(siteRoot, normalized));

        var siteRootWithSeparator = EnsureTrailingSeparator(siteRoot);
        if (!resolved.StartsWith(siteRootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resolved, siteRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("appruntime.path_outside_site_root", 400, 400);
        }

        // A lexical prefix does not contain junction/symlink targets within the site.
        SiteBuilderPathGuard.EnsureNoReparsePointOnPath(siteRoot, resolved);
        return resolved;
    }

    private static string ToSiteRelativePath(string siteRoot, string path)
    {
        return Path.GetRelativePath(siteRoot, path).Replace('\\', '/');
    }

    private static string ToSiteRelativeDirectory(string siteRoot, string path)
    {
        var relative = Path.GetRelativePath(siteRoot, path).Replace('\\', '/');
        return string.Equals(relative, ".", StringComparison.Ordinal) ? "." : relative;
    }

    private static string BuildArguments(string startupFile, string additionalArguments)
    {
        var trimmedArguments = additionalArguments.Trim();
        return string.IsNullOrWhiteSpace(trimmedArguments)
            ? $"\"{startupFile}\""
            : $"\"{startupFile}\" {trimmedArguments}";
    }

    private static IReadOnlyCollection<string> BuildEnvironmentVariables(string runtimeType, IReadOnlyCollection<string> requestedVariables)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PORT"] = "%HTTP_PLATFORM_PORT%"
        };

        if (string.Equals(runtimeType, "python", StringComparison.OrdinalIgnoreCase))
        {
            values["PYTHONUNBUFFERED"] = "1";
        }

        foreach (var variable in requestedVariables)
        {
            var separatorIndex = variable.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = variable[..separatorIndex].Trim();
            var value = variable[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            values[name] = value;
        }

        return values
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key}={item.Value}")
            .ToArray();
    }

    private static string DetectRuntimeType(string processPath)
    {
        var fileName = Path.GetFileName(processPath).ToLowerInvariant();
        if (fileName == "node.exe")
        {
            return "node";
        }

        if (fileName == "python.exe" || PythonExecutablePattern().IsMatch(fileName))
        {
            return "python";
        }

        return string.IsNullOrWhiteSpace(fileName) ? "none" : "custom";
    }

    private static string ExtractStartupFile(string arguments)
    {
        var match = StartupFilePattern().Match(arguments);
        if (!match.Success)
        {
            return string.Empty;
        }

        return match.Groups["value"].Value;
    }

    private static string RemoveStartupFileFromArguments(string arguments)
    {
        var match = StartupFilePattern().Match(arguments);
        if (!match.Success)
        {
            return arguments.Trim();
        }

        return arguments[match.Length..].Trim();
    }

    private static string NormalizeDisplayedPath(string siteRoot, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var resolved = Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine(siteRoot, value));

            return ToSiteRelativeDirectory(siteRoot, resolved);
        }
        catch
        {
            return value.Replace('\\', '/');
        }
    }

    private static XElement EnsureChild(XElement parent, string name)
    {
        var existing = parent.Element(name);
        if (existing is not null)
        {
            return existing;
        }

        var created = new XElement(name);
        parent.Add(created);
        return created;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
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
                ?? throw new AppException("appruntime.configuration_failed", 500, 500);
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

    private static AppException MapAppRuntimeException(Exception exception, string fallbackMessageKey)
    {
        // Dile bağımsız (HRESULT + İngilizce) sınıflandırma — Türkçe Windows'ta da doğru çalışır.
        var message = Support.Win32ErrorHelper.Flatten(exception);
        if (Support.Win32ErrorHelper.IsAccessDenied(message))
        {
            return CreateDiagnosticException("sites.iis_permission_denied", 403, exception);
        }

        if (Support.Win32ErrorHelper.IsInUse(message))
        {
            return CreateDiagnosticException("sites.iis_configuration_locked", 409, exception);
        }

        if (message.Contains("section", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("applicationHost.config", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Microsoft.Web.Administration", StringComparison.OrdinalIgnoreCase))
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

    [GeneratedRegex("^\"(?<value>[^\"]+)\"\\s*", RegexOptions.Compiled)]
    private static partial Regex StartupFilePattern();

    [GeneratedRegex("^python(?:\\d+(?:\\.\\d+)*)?\\.exe$", RegexOptions.Compiled)]
    private static partial Regex PythonExecutablePattern();
}
