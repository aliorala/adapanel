using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Php;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.AspNet;

public sealed class AspNetService(
    AdaPanelDbContext dbContext,
    AdaPanel.Core.Interfaces.IConcurrencyLock concurrencyLock,
    ILogger<AspNetService> logger) : AdaPanel.Core.Interfaces.IAspNetService
{
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";
    private const string ClassicAspHandlerName = "AdaPanel.ClassicAsp";
    private const string ClassicAspDllPath = "%windir%\\system32\\inetsrv\\asp.dll";
    private const string PhpHandlerPrefix = "AdaPanel.Php.";

    public Task<AspSiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default)
    {
        return ExecuteReadAsync(serverManager =>
        {
            var site = FindSite(GetPropertyObject(serverManager, "Sites"), siteName);
            return MapSiteConfiguration(serverManager, site);
        }, "aspnet.configuration_failed", cancellationToken);
    }

    public Task<AspSiteConfigurationDto> SetClassicAspAsync(
        string siteName,
        UpdateClassicAspRequest request,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(serverManager =>
        {
            var site = FindSite(GetPropertyObject(serverManager, "Sites"), siteName);
            SetClassicAspEnabled(serverManager, GetStringProperty(site, "Name"), request.Enabled);
            return MapSiteConfiguration(serverManager, site);
        }, "aspnet.configuration_failed", cancellationToken);
    }

    public Task<AspSiteConfigurationDto> SetDotNetVersionAsync(
        string siteName,
        UpdateDotNetRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWriteAsync(serverManager =>
        {
            var site = FindSite(GetPropertyObject(serverManager, "Sites"), siteName);
            var appPool = GetApplicationPoolForSite(serverManager, site);
            ConfigureManagedRuntime(appPool, request.ManagedRuntimeVersion, request.ManagedPipelineMode, request.Enable32BitAppOnWin64);
            return MapSiteConfiguration(serverManager, site);
        }, "aspnet.configuration_failed", cancellationToken);
    }

    public async Task<AspSiteConfigurationDto> EnableMixedHandlersAsync(
        string siteName,
        EnableMixedHandlersRequest request,
        CancellationToken cancellationToken = default)
    {
        var phpComponent = await GetInstalledPhpComponentAsync(request.PhpComponentId, cancellationToken);
        var executablePath = ResolvePhpExecutablePath(phpComponent.InstallDir);
        if (!File.Exists(executablePath))
        {
            throw new AppException("php.executable_not_found", 404, 404);
        }

        return await ExecuteWriteAsync(serverManager =>
        {
            var site = FindSite(GetPropertyObject(serverManager, "Sites"), siteName);
            var normalizedSiteName = GetStringProperty(site, "Name");
            var appPool = GetApplicationPoolForSite(serverManager, site);

            SetClassicAspEnabled(serverManager, normalizedSiteName, request.EnableClassicAsp);
            ConfigureManagedRuntime(appPool, request.ManagedRuntimeVersion, request.ManagedPipelineMode, request.Enable32BitAppOnWin64);
            EnsureFastCgiApplication(serverManager, executablePath);
            SetSitePhpHandler(serverManager, normalizedSiteName, executablePath, BuildPhpHandlerName(request.PhpComponentId));

            return MapSiteConfiguration(serverManager, site);
        }, "aspnet.configuration_failed", cancellationToken);
    }

    private async Task<AdaPanel.Core.Entities.InstalledComponent> GetInstalledPhpComponentAsync(string componentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(componentId))
        {
            throw new AppException("aspnet.php_component_required", 400, 400);
        }

        if (!componentId.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("php.invalid_component", 400, 400);
        }

        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == componentId, cancellationToken);

        if (component is null || !string.Equals(component.Status.ToString(), "Installed", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("php.version_not_installed", 404, 404);
        }

        return component;
    }

    private Task<T> ExecuteReadAsync<T>(Func<IDisposable, T> operation, string fallbackMessageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var serverManager = CreateServerManager();
            return Task.FromResult(operation(serverManager));
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var mappedException = MapAspNetConfigurationException(exception, fallbackMessageKey);
            logger.LogError(
                exception,
                "ASP.NET/IIS read operation failed. MappedMessageKey={MessageKey}",
                mappedException.MessageKey);
            throw mappedException;
        }
    }

    private Task<T> ExecuteWriteAsync<T>(Func<IDisposable, T> operation, string fallbackMessageKey, CancellationToken cancellationToken)
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
                var mappedException = MapAspNetConfigurationException(exception, fallbackMessageKey);
                logger.LogError(
                    exception,
                    "ASP.NET/IIS write operation failed. MappedMessageKey={MessageKey}",
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

    private static object FindSite(object sitesCollection, string siteName)
    {
        var normalized = NormalizeSiteName(siteName);
        return Enumerate(sitesCollection).FirstOrDefault(site =>
                   string.Equals(GetStringProperty(site, "Name"), normalized, StringComparison.OrdinalIgnoreCase))
               ?? throw new AppException("sites.site_not_found", 404, 404);
    }

    private static AspSiteConfigurationDto MapSiteConfiguration(IDisposable serverManager, object site)
    {
        var siteName = GetStringProperty(site, "Name");
        var appPool = GetApplicationPoolForSite(serverManager, site);
        var managedRuntimeVersion = GetStringProperty(appPool, "ManagedRuntimeVersion");
        var pipelineMode = GetPropertyObject(appPool, "ManagedPipelineMode").ToString() ?? "Integrated";
        var enable32Bit = GetBoolProperty(appPool, "Enable32BitAppOnWin64");
        var classicAspEnabled = GetClassicAspEnabled(serverManager, siteName);
        var (phpHandlerName, phpComponentId) = GetPhpHandlerInfo(serverManager, siteName);

        return new AspSiteConfigurationDto
        {
            SiteName = siteName,
            ApplicationPoolName = GetStringProperty(GetRootApplication(site), "ApplicationPoolName"),
            ClassicAspEnabled = classicAspEnabled,
            ManagedRuntimeVersion = managedRuntimeVersion,
            ManagedPipelineMode = pipelineMode,
            Enable32BitAppOnWin64 = enable32Bit,
            HasPhpHandler = !string.IsNullOrWhiteSpace(phpHandlerName),
            PhpComponentId = phpComponentId,
            PhpHandlerName = phpHandlerName,
            IsMixedHandlersEnabled = classicAspEnabled &&
                                     !string.IsNullOrWhiteSpace(managedRuntimeVersion) &&
                                     !string.IsNullOrWhiteSpace(phpHandlerName)
        };
    }

    private static object GetApplicationPoolForSite(IDisposable serverManager, object site)
    {
        var appPoolName = GetStringProperty(GetRootApplication(site), "ApplicationPoolName");
        if (string.IsNullOrWhiteSpace(appPoolName))
        {
            throw new AppException("aspnet.configuration_failed", 500, 500);
        }

        var pools = GetPropertyObject(serverManager, "ApplicationPools");
        var existing = Enumerate(pools).FirstOrDefault(pool =>
            string.Equals(GetStringProperty(pool, "Name"), appPoolName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        var pool = InvokeMethod(pools, "Add", appPoolName);
        SetPropertyValue(pool, "AutoStart", true);
        SetPropertyValue(pool, "ManagedRuntimeVersion", string.Empty);
        return pool;
    }

    private static void SetClassicAspEnabled(IDisposable serverManager, string siteName, bool enabled)
    {
        if (!enabled)
        {
            RemoveAdaPanelClassicAspHandler(serverManager, siteName);
            return;
        }

        EnsureClassicAspSectionAvailable(serverManager, siteName);
        EnsureClassicAspHandler(serverManager, siteName);
    }

    private static void EnsureClassicAspSectionAvailable(IDisposable serverManager, string siteName)
    {
        var configuration = InvokeMethod(serverManager, "GetWebConfiguration", siteName);
        _ = InvokeMethod(configuration, "GetSection", "system.webServer/asp");
    }

    private static bool GetClassicAspEnabled(IDisposable serverManager, string siteName)
    {
        try
        {
            EnsureClassicAspSectionAvailable(serverManager, siteName);
            var handlers = GetHandlersCollection(serverManager, siteName);
            return Enumerate(handlers).Any(IsClassicAspHandler);
        }
        catch
        {
            return false;
        }
    }

    private static void ConfigureManagedRuntime(object appPool, string managedRuntimeVersion, string managedPipelineMode, bool enable32BitAppOnWin64)
    {
        SetPropertyValue(appPool, "ManagedRuntimeVersion", NormalizeManagedRuntimeVersion(managedRuntimeVersion));
        SetPropertyValue(appPool, "ManagedPipelineMode", NormalizePipelineMode(managedPipelineMode));
        SetPropertyValue(appPool, "Enable32BitAppOnWin64", enable32BitAppOnWin64);
    }

    private static (string HandlerName, string ComponentId) GetPhpHandlerInfo(IDisposable serverManager, string siteName)
    {
        var configuration = InvokeMethod(serverManager, "GetWebConfiguration", siteName);
        var handlersSection = InvokeMethod(configuration, "GetSection", "system.webServer/handlers");
        var handlers = GetConfigurationCollection(handlersSection);

        var handler = Enumerate(handlers).FirstOrDefault(item =>
        {
            var name = GetAttributeString(item, "name");
            var path = GetAttributeString(item, "path");
            return name.StartsWith(PhpHandlerPrefix, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(path, "*.php", StringComparison.OrdinalIgnoreCase);
        });

        if (handler is null)
        {
            return (string.Empty, string.Empty);
        }

        var handlerName = GetAttributeString(handler, "name");
        return handlerName.StartsWith(PhpHandlerPrefix, StringComparison.OrdinalIgnoreCase)
            ? (handlerName, handlerName[PhpHandlerPrefix.Length..])
            : (handlerName, string.Empty);
    }

    private static void EnsureFastCgiApplication(IDisposable serverManager, string executablePath)
    {
        var configuration = InvokeMethod(serverManager, "GetApplicationHostConfiguration");
        var section = InvokeMethod(configuration, "GetSection", "system.webServer/fastCgi");
        var fastCgiCollection = GetConfigurationCollection(section);

        var existing = Enumerate(fastCgiCollection).FirstOrDefault(item =>
            string.Equals(GetAttributeString(item, "fullPath"), executablePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // FastCGI requestTimeout/activityTimeout varsayılanları (90/30sn) PHP'nin uzun işlemlerine yetmez
            // (büyük import → 500 0x80070102). Mevcut kayıtta da yükselt.
            SetAttributeValue(existing, "activityTimeout", 600);
            SetAttributeValue(existing, "requestTimeout", 600);
            return;
        }

        var application = InvokeMethod(fastCgiCollection, "CreateElement", "application");
        SetAttributeValue(application, "fullPath", executablePath);
        SetAttributeValue(application, "arguments", string.Empty);
        SetAttributeValue(application, "activityTimeout", 600);
        SetAttributeValue(application, "requestTimeout", 600);
        InvokeMethod(fastCgiCollection, "Add", application);
    }

    private static void SetSitePhpHandler(IDisposable serverManager, string siteName, string executablePath, string handlerName)
    {
        var handlers = GetHandlersCollection(serverManager, siteName);

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
        // KRİTİK: handler'ı listenin BAŞINA ekle. Devralınan StaticFile (path="*") *.php'yi
        // ondan önce yakalarsa .php dosyaları "404.3" verir (php-info.php açılmaz).
        try { InvokeMethod(handlers, "AddAt", 0, handler); }
        catch { InvokeMethod(handlers, "Add", handler); }
    }

    private static void EnsureClassicAspHandler(IDisposable serverManager, string siteName)
    {
        var aspDllPath = Environment.ExpandEnvironmentVariables(ClassicAspDllPath);
        if (!File.Exists(aspDllPath))
        {
            throw new AppException("sites.iis_install_required", 503, 503, $"Classic ASP engine was not found: {aspDllPath}");
        }

        var handlers = GetHandlersCollection(serverManager, siteName);
        if (Enumerate(handlers).Any(IsClassicAspHandler))
        {
            return;
        }

        var handler = InvokeMethod(handlers, "CreateElement", "add");
        SetAttributeValue(handler, "name", ClassicAspHandlerName);
        SetAttributeValue(handler, "path", "*.asp");
        SetAttributeValue(handler, "verb", "GET,HEAD,POST");
        SetAttributeValue(handler, "modules", "IsapiModule");
        SetAttributeValue(handler, "scriptProcessor", aspDllPath);
        SetAttributeValue(handler, "resourceType", "File");
        SetAttributeValue(handler, "requireAccess", "Script");
        // *.asp da StaticFile'dan ÖNCE değerlendirilmeli (aynı 404.3 sınıfı).
        try { InvokeMethod(handlers, "AddAt", 0, handler); }
        catch { InvokeMethod(handlers, "Add", handler); }
    }

    private static void RemoveAdaPanelClassicAspHandler(IDisposable serverManager, string siteName)
    {
        var handlers = GetHandlersCollection(serverManager, siteName);
        foreach (var existing in Enumerate(handlers).Where(item =>
                     string.Equals(GetAttributeString(item, "name"), ClassicAspHandlerName, StringComparison.OrdinalIgnoreCase))
                 .ToArray())
        {
            InvokeMethod(handlers, "Remove", existing);
        }
    }

    private static object GetHandlersCollection(IDisposable serverManager, string siteName)
    {
        var configuration = InvokeMethod(serverManager, "GetWebConfiguration", siteName);
        var handlersSection = InvokeMethod(configuration, "GetSection", "system.webServer/handlers");
        return GetConfigurationCollection(handlersSection);
    }

    private static bool IsClassicAspHandler(object handler)
    {
        var path = TryGetAttributeString(handler, "path");
        if (!string.Equals(path, "*.asp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = TryGetAttributeString(handler, "name");
        var modules = TryGetAttributeString(handler, "modules");
        var scriptProcessor = TryGetAttributeString(handler, "scriptProcessor");
        return string.Equals(name, ClassicAspHandlerName, StringComparison.OrdinalIgnoreCase) ||
               name.Contains("asp", StringComparison.OrdinalIgnoreCase) ||
               modules.Contains("IsapiModule", StringComparison.OrdinalIgnoreCase) ||
               scriptProcessor.EndsWith("asp.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static object GetRootApplication(object site)
    {
        var applications = GetPropertyObject(site, "Applications");
        return Enumerate(applications).FirstOrDefault(application =>
                   string.Equals(GetStringProperty(application, "Path"), "/", StringComparison.OrdinalIgnoreCase))
               ?? Enumerate(applications).First();
    }

    private static string ResolvePhpExecutablePath(string installDir)
    {
        return Path.Combine(installDir, "php-cgi.exe");
    }

    private static string BuildPhpHandlerName(string componentId)
    {
        return PhpHandlerPrefix + componentId.Replace(' ', '-');
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

    private static string NormalizeManagedRuntimeVersion(string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "no managed code", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return normalized switch
        {
            "v2.0" => normalized,
            "v4.0" => normalized,
            _ => throw new AppException("aspnet.invalid_runtime_version", 400, 400)
        };
    }

    private static string NormalizePipelineMode(string value)
    {
        var normalized = value.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "integrated" => "Integrated",
            "classic" => "Classic",
            _ => throw new AppException("aspnet.invalid_pipeline_mode", 400, 400)
        };
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
                ?? throw new AppException("aspnet.configuration_failed", 500, 500);
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

    private static bool GetBoolProperty(object target, string propertyName)
    {
        return Convert.ToBoolean(GetPropertyObject(target, propertyName));
    }

    private static string GetAttributeString(object target, string attributeName)
    {
        return InvokeMethod(target, "GetAttributeValue", attributeName).ToString() ?? string.Empty;
    }

    private static string TryGetAttributeString(object target, string attributeName)
    {
        try
        {
            return GetAttributeString(target, attributeName);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SetAttributeValue(object target, string attributeName, object? value)
    {
        InvokeMethod(target, "SetAttributeValue", attributeName, value);
    }

    private static void SetPropertyValue(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            if (value is null)
            {
                property.SetValue(target, null);
                return;
            }

            var propertyType = property.PropertyType;
            var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

            if (targetType.IsEnum)
            {
                var enumValue = value is string text
                    ? Enum.Parse(targetType, text, ignoreCase: true)
                    : Enum.ToObject(targetType, value);

                property.SetValue(target, enumValue);
                return;
            }

            if (targetType == typeof(string))
            {
                property.SetValue(target, value.ToString());
                return;
            }

            var converted = Convert.ChangeType(value, targetType);
            property.SetValue(target, converted);
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

    private static AppException MapAspNetConfigurationException(Exception exception, string fallbackMessageKey)
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

        if (message.Contains("system.webServer/asp", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Classic ASP", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("section", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("handler", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ISAPI", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("FastCgiModule", StringComparison.OrdinalIgnoreCase))
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
}
