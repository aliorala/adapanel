using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AdaPanel.Infrastructure.Support;

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

    public async Task<AppRuntimeSiteConfigurationDto> GetSiteConfigurationAsync(
        string siteName,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        return ReadConfiguration(site);
    }

    public async Task<AppRuntimeSiteConfigurationDto> HostNodeAppAsync(
        string siteName,
        HostNodeAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        await EnsureHttpPlatformHandlerAsync(cancellationToken);
        var executablePath = await ResolveNodeExecutableAsync(request.ComponentId, cancellationToken);

        return await ApplyRuntimeConfigurationAsync(
            site,
            "node",
            request.ComponentId,
            executablePath,
            request.StartupFile,
            request.WorkingDirectory,
            request.Arguments,
            request.EnvironmentVariables,
            cancellationToken);
    }

    public async Task<AppRuntimeSiteConfigurationDto> HostPythonAppAsync(
        string siteName,
        HostPythonAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        await EnsureHttpPlatformHandlerAsync(cancellationToken);
        var executablePath = await ResolvePythonExecutableAsync(request.ComponentId, cancellationToken);

        return await ApplyRuntimeConfigurationAsync(
            site,
            "python",
            request.ComponentId,
            executablePath,
            request.StartupFile,
            request.WorkingDirectory,
            request.Arguments,
            request.EnvironmentVariables,
            cancellationToken);
    }

    private async Task<AppRuntimeSiteConfigurationDto> ApplyRuntimeConfigurationAsync(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        string runtimeType,
        string componentId,
        string processPath,
        string startupFile,
        string workingDirectory,
        string additionalArguments,
        IReadOnlyCollection<string> environmentVariables,
        CancellationToken cancellationToken)
    {
        var siteRoot = ResolveSiteRoot(site.PhysicalPath);
        Directory.CreateDirectory(siteRoot);

        var normalizedWorkingDirectory = ResolveSiteManagedPath(siteRoot, workingDirectory, allowCurrentDirectory: true);
        Directory.CreateDirectory(normalizedWorkingDirectory);

        var normalizedStartupFile = ResolveSiteManagedPath(siteRoot, startupFile, allowCurrentDirectory: false);
        var relativeStartupFile = ToSiteRelativePath(siteRoot, normalizedStartupFile);
        var startupArguments = BuildArguments(relativeStartupFile, additionalArguments);
        var mergedEnvironmentVariables = BuildEnvironmentVariables(runtimeType, environmentVariables);
        var webConfigPath = Path.Combine(siteRoot, "web.config");
        var logsDirectory = Path.Combine(siteRoot, LogsDirectoryName);

        return await concurrencyLock.RunExclusiveAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Directory.CreateDirectory(logsDirectory);
                UpdateWebConfig(
                    webConfigPath,
                    processPath,
                    startupArguments,
                    normalizedWorkingDirectory,
                    logsDirectory,
                    mergedEnvironmentVariables);

                EnsureApplicationPoolUsesNoManagedCode(site.ApplicationPoolName);

                return Task.FromResult(new AppRuntimeSiteConfigurationDto
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
                });
            }
            catch (AppException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var mappedException = MapAppRuntimeException(exception, "appruntime.configuration_failed");
                logger.LogError(exception, "Application runtime configuration failed for site {SiteName}.", site.Name);
                throw mappedException;
            }
        }, cancellationToken);
    }

    private AppRuntimeSiteConfigurationDto ReadConfiguration(AdaPanel.Core.Dtos.Sites.SiteDto site)
    {
        var siteRoot = ResolveSiteRoot(site.PhysicalPath);
        var webConfigPath = Path.Combine(siteRoot, "web.config");
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

    private async Task<string> ResolveNodeExecutableAsync(string componentId, CancellationToken cancellationToken)
    {
        return await ResolveRuntimeExecutableAsync(
            componentId,
            "nodejs-lts",
            "node",
            new[]
            {
                "%ProgramFiles%\\nodejs\\node.exe",
                "%ProgramFiles(x86)%\\nodejs\\node.exe"
            },
            cancellationToken);
    }

    private async Task EnsureHttpPlatformHandlerAsync(CancellationToken cancellationToken)
    {
        const string componentId = "http-platform-handler";
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var installed = catalog.Installed.Any(component =>
            string.Equals(component.ComponentId, componentId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(component.Status, "Installed", StringComparison.OrdinalIgnoreCase));

        if (!installed)
        {
            await componentBootstrapper.InstallAsync(componentId, null, null, force: false, cancellationToken: cancellationToken);
        }
    }

    private async Task<string> ResolvePythonExecutableAsync(string componentId, CancellationToken cancellationToken)
    {
        return await ResolveRuntimeExecutableAsync(
            componentId,
            "python-3",
            "python",
            new[]
            {
                "%ProgramFiles%\\Python312\\python.exe",
                "%ProgramFiles%\\Python311\\python.exe",
                "%LocalAppData%\\Programs\\Python\\Python312\\python.exe",
                "%LocalAppData%\\Programs\\Python\\Python311\\python.exe"
            },
            cancellationToken);
    }

    private async Task<string> ResolveRuntimeExecutableAsync(
        string requestedComponentId,
        string fallbackComponentId,
        string executableNameWithoutExtension,
        IReadOnlyCollection<string> fallbackCandidates,
        CancellationToken cancellationToken)
    {
        var normalizedComponentId = string.IsNullOrWhiteSpace(requestedComponentId)
            ? fallbackComponentId
            : requestedComponentId.Trim();

        InstalledComponent? installedComponent = null;
        if (!string.IsNullOrWhiteSpace(normalizedComponentId))
        {
            installedComponent = await dbContext.InstalledComponents
                .FirstOrDefaultAsync(
                    item => item.ComponentId == normalizedComponentId &&
                            item.Status == AdaPanel.Core.Enums.ComponentInstallStatus.Installed,
                    cancellationToken);
        }

        if (installedComponent is not null)
        {
            var directCandidate = Path.Combine(installedComponent.InstallDir, executableNameWithoutExtension + ".exe");
            if (File.Exists(directCandidate))
            {
                return directCandidate;
            }
        }

        foreach (var candidate in fallbackCandidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
            {
                return expanded;
            }
        }

        var resolvedFromPath = await TryResolveFromPathAsync(executableNameWithoutExtension, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedFromPath))
        {
            return resolvedFromPath;
        }

        throw new AppException("appruntime.runtime_not_installed", 404, 404);
    }

    private static async Task<string> TryResolveFromPathAsync(string executableNameWithoutExtension, CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "where.exe",
            Arguments = executableNameWithoutExtension,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            return string.Empty;
        }

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            return string.Empty;
        }

        // TAVANLI OKUMA: cikti tamamen bellege alinmamali. Kural ProcessRunner'da tek kaynakta;
        // bu yol kendi surecini kurdugu icin oradan GECMIYORDU.
        var output = await ProcessRunner.TavanliOkuAsync(
            process.StandardOutput, ProcessRunner.DefaultOutputCharLimit, cancellationToken);
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
    }

    private static void UpdateWebConfig(
        string webConfigPath,
        string processPath,
        string arguments,
        string workingDirectory,
        string logsDirectory,
        IReadOnlyCollection<string> environmentVariables)
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
        document.Save(webConfigPath);
    }

    private void EnsureApplicationPoolUsesNoManagedCode(string applicationPoolName)
    {
        if (string.IsNullOrWhiteSpace(applicationPoolName))
        {
            return;
        }

        try
        {
            using var serverManager = CreateServerManager();
            var applicationPools = GetPropertyObject(serverManager, "ApplicationPools");
            var applicationPool = Enumerate(applicationPools)
                .FirstOrDefault(item =>
                    string.Equals(GetStringProperty(item, "Name"), applicationPoolName, StringComparison.OrdinalIgnoreCase));

            if (applicationPool is null)
            {
                return;
            }

            SetPropertyValue(applicationPool, "ManagedRuntimeVersion", string.Empty);
            InvokeMethod(serverManager, "CommitChanges");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to switch application pool {AppPoolName} to No Managed Code.", applicationPoolName);
        }
    }

    private bool ReadManagedRuntimeDisabled(string applicationPoolName)
    {
        if (string.IsNullOrWhiteSpace(applicationPoolName))
        {
            return false;
        }

        try
        {
            using var serverManager = CreateServerManager();
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
