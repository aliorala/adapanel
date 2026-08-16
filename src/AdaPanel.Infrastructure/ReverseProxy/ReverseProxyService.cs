using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Proxy;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.ReverseProxy;

public sealed partial class ReverseProxyService(
    IIisSiteService iisSiteService,
    IComponentBootstrapper componentBootstrapper,
    IConcurrencyLock concurrencyLock,
    ILogger<ReverseProxyService> logger) : IReverseProxyService
{
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";
    private const string ProxySectionPath = "system.webServer/proxy";
    private const string RewriteSectionPath = "system.webServer/rewrite";
    private const string ProxyRulePrefix = "AdaPanel.ReverseProxy.Inbound|";
    private const string OutboundRulePrefix = "AdaPanel.ReverseProxy.Outbound|";
    private const string CustomRulePrefix = "AdaPanel.CustomRewrite|";
    private const string HtmlPreconditionName = "AdaPanel.IsHtml";

    public async Task<ReverseProxySiteConfigurationDto> GetSiteConfigurationAsync(
        string siteName,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);

        return await ExecuteReadAsync(serverManager =>
        {
            var arrSettings = ReadArrProxySettings(serverManager);
            return ReadConfiguration(site, arrSettings);
        }, site.Name, cancellationToken);
    }

    public async Task<ReverseProxySiteConfigurationDto> EnableProxyAsync(
        string siteName,
        EnableReverseProxyRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var destinationUrl = NormalizeDestinationUrl(request.DestinationUrl);
        var mountPath = NormalizeMountPath(request.MountPath);
        await EnsureComponentInstalledAsync("url-rewrite", cancellationToken);
        await EnsureComponentInstalledAsync("iis-arr", cancellationToken);

        return await ExecuteWriteAsync(serverManager =>
        {
            EnsureRewriteAvailable(serverManager, site.Name);
            var arrSettings = EnsureArrProxyEnabled(serverManager);
            var webConfigPath = ResolveWebConfigPath(site.PhysicalPath);
            var document = LoadWebConfig(webConfigPath);

            UpsertProxyRule(document, destinationUrl, mountPath);
            if (request.EnableHtmlLinkRewrite && !IsRootMountPath(mountPath))
            {
                UpsertOutboundRule(document, mountPath);
            }
            else
            {
                RemoveOutboundRules(document);
            }

            document.Save(webConfigPath);
            return ReadConfiguration(site, arrSettings with { IsEnabled = true }, document);
        }, site.Name, cancellationToken);
    }

    public async Task<ReverseProxySiteConfigurationDto> RemoveProxyAsync(
        string siteName,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);

        return await ExecuteWriteAsync(serverManager =>
        {
            var arrSettings = ReadArrProxySettings(serverManager);
            var webConfigPath = ResolveWebConfigPath(site.PhysicalPath);
            var document = LoadWebConfig(webConfigPath);

            RemoveProxyRules(document);
            document.Save(webConfigPath);

            return ReadConfiguration(site, arrSettings, document);
        }, site.Name, cancellationToken);
    }

    public async Task<ReverseProxySiteConfigurationDto> AddRewriteRuleAsync(
        string siteName,
        AddRewriteRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var normalizedRuleName = NormalizeCustomRuleName(request.Name);
        await EnsureComponentInstalledAsync("url-rewrite", cancellationToken);

        return await ExecuteWriteAsync(serverManager =>
        {
            EnsureRewriteAvailable(serverManager, site.Name);
            var arrSettings = ReadArrProxySettings(serverManager);
            var webConfigPath = ResolveWebConfigPath(site.PhysicalPath);
            var document = LoadWebConfig(webConfigPath);

            UpsertCustomRewriteRule(document, normalizedRuleName, request);
            document.Save(webConfigPath);

            return ReadConfiguration(site, arrSettings, document);
        }, site.Name, cancellationToken);
    }

    /// <inheritdoc />
    public async Task EnsureArrPrerequisitesAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        await EnsureComponentInstalledAsync("url-rewrite", cancellationToken);
        await EnsureComponentInstalledAsync("iis-arr", cancellationToken);

        await ExecuteWriteAsync<object?>(serverManager =>
        {
            EnsureRewriteAvailable(serverManager, site.Name);
            EnsureArrProxyEnabled(serverManager);
            return null;
        }, site.Name, cancellationToken);
    }

    private async Task EnsureComponentInstalledAsync(string componentId, CancellationToken cancellationToken)
    {
        var catalog = await componentBootstrapper.GetCatalogAsync(cancellationToken);
        var installed = catalog.Installed.Any(component =>
            string.Equals(component.ComponentId, componentId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(component.Status, "Installed", StringComparison.OrdinalIgnoreCase));

        if (!installed)
        {
            await componentBootstrapper.InstallAsync(componentId, null, null, force: false, cancellationToken: cancellationToken);
        }
    }

    private Task<T> ExecuteReadAsync<T>(Func<IDisposable, T> operation, string siteName, CancellationToken cancellationToken)
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
            var mappedException = MapProxyException(exception);
            logger.LogError(exception, "Reverse proxy read operation failed for site {SiteName}.", siteName);
            throw mappedException;
        }
    }

    private Task<T> ExecuteWriteAsync<T>(Func<IDisposable, T> operation, string siteName, CancellationToken cancellationToken)
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
                var mappedException = MapProxyException(exception);
                logger.LogError(exception, "Reverse proxy write operation failed for site {SiteName}.", siteName);
                throw mappedException;
            }
        }, cancellationToken);
    }

    private static ReverseProxySiteConfigurationDto ReadConfiguration(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        ArrProxySettings arrSettings,
        XDocument? document = null)
    {
        var webConfigPath = ResolveWebConfigPath(site.PhysicalPath);
        document ??= File.Exists(webConfigPath)
            ? LoadWebConfig(webConfigPath)
            : new XDocument(new XElement("configuration"));

        var rewriteRules = ReadCustomRewriteRules(document);
        var (isProxyEnabled, destinationUrl, mountPath, hasHtmlLinkRewrite) = ReadProxyRuleState(document);

        return new ReverseProxySiteConfigurationDto
        {
            SiteName = site.Name,
            WebConfigPath = webConfigPath,
            IsArrEnabled = arrSettings.IsEnabled,
            IsProxyEnabled = isProxyEnabled,
            DestinationUrl = destinationUrl,
            MountPath = mountPath,
            HasHtmlLinkRewrite = hasHtmlLinkRewrite,
            PreserveHostHeader = arrSettings.PreserveHostHeader,
            ReverseRewriteHostInResponseHeaders = arrSettings.ReverseRewriteHostInResponseHeaders,
            RewriteRules = rewriteRules
        };
    }

    private static ArrProxySettings EnsureArrProxyEnabled(IDisposable serverManager)
    {
        var configuration = InvokeMethod(serverManager, "GetApplicationHostConfiguration");
        object proxySection;
        try
        {
            proxySection = InvokeMethod(configuration, "GetSection", ProxySectionPath);
        }
        catch (Exception exception)
        {
            throw CreateDiagnosticException("proxy.arr_not_available", 503, exception);
        }

        SetAttributeValue(proxySection, "enabled", true);

        return new ArrProxySettings(
            IsEnabled: true,
            PreserveHostHeader: TryGetAttributeValue(proxySection, "preserveHostHeader"),
            ReverseRewriteHostInResponseHeaders: TryGetAttributeValue(proxySection, "reverseRewriteHostInResponseHeaders"));
    }

    private static ArrProxySettings ReadArrProxySettings(IDisposable serverManager)
    {
        try
        {
            var configuration = InvokeMethod(serverManager, "GetApplicationHostConfiguration");
            var proxySection = InvokeMethod(configuration, "GetSection", ProxySectionPath);

            return new ArrProxySettings(
                IsEnabled: Convert.ToBoolean(InvokeMethod(proxySection, "GetAttributeValue", "enabled")),
                PreserveHostHeader: TryGetAttributeValue(proxySection, "preserveHostHeader"),
                ReverseRewriteHostInResponseHeaders: TryGetAttributeValue(proxySection, "reverseRewriteHostInResponseHeaders"));
        }
        catch
        {
            return new ArrProxySettings(false, false, false);
        }
    }

    private static void EnsureRewriteAvailable(IDisposable serverManager, string siteName)
    {
        try
        {
            var configuration = InvokeMethod(serverManager, "GetWebConfiguration", siteName);
            InvokeMethod(configuration, "GetSection", RewriteSectionPath);
        }
        catch (Exception)
        {
            throw new AppException("proxy.rewrite_module_missing", 503, 503);
        }
    }

    private static void UpsertProxyRule(XDocument document, string destinationUrl, string mountPath)
    {
        RemoveProxyRules(document);

        var rules = EnsureRulesElement(document);
        var ruleName = ProxyRulePrefix + EncodeMountPathForRuleName(mountPath);
        var rule = new XElement(
            "rule",
            new XAttribute("name", ruleName),
            new XAttribute("stopProcessing", "true"),
            new XElement(
                "match",
                new XAttribute("url", BuildMatchPattern(mountPath)),
                new XAttribute("ignoreCase", "true")),
            new XElement(
                "action",
                new XAttribute("type", "Rewrite"),
                new XAttribute("url", BuildActionUrl(destinationUrl)),
                new XAttribute("appendQueryString", "true")));

        rules.Add(rule);
    }

    private static void UpsertOutboundRule(XDocument document, string mountPath)
    {
        RemoveOutboundRules(document);

        var rewrite = EnsureRewriteElement(document);
        var outboundRules = EnsureChildElement(rewrite, "outboundRules");
        var preConditions = EnsureChildElement(outboundRules, "preConditions");

        preConditions.Elements("preCondition")
            .Where(element => string.Equals((string?)element.Attribute("name"), HtmlPreconditionName, StringComparison.OrdinalIgnoreCase))
            .Remove();

        preConditions.Add(
            new XElement(
                "preCondition",
                new XAttribute("name", HtmlPreconditionName),
                new XElement(
                    "add",
                    new XAttribute("input", "{RESPONSE_CONTENT_TYPE}"),
                    new XAttribute("pattern", "^text/html"))));

        outboundRules.Add(
            new XElement(
                "rule",
                new XAttribute("name", OutboundRulePrefix + EncodeMountPathForRuleName(mountPath)),
                new XAttribute("preCondition", HtmlPreconditionName),
                new XElement(
                    "match",
                    new XAttribute("filterByTags", "A"),
                    new XAttribute("pattern", "^/(.*)")),
                new XElement(
                    "conditions",
                    new XElement(
                        "add",
                        new XAttribute("input", "{URL}"),
                        new XAttribute("pattern", "^/" + BuildOutboundConditionPattern(mountPath) + "(?:/.*)?$"))),
                new XElement(
                    "action",
                    new XAttribute("type", "Rewrite"),
                    new XAttribute("value", BuildOutboundRewriteValue(mountPath)))));
    }

    private static void UpsertCustomRewriteRule(XDocument document, string normalizedRuleName, AddRewriteRuleRequest request)
    {
        var rules = EnsureRulesElement(document);
        var internalRuleName = CustomRulePrefix + normalizedRuleName;

        rules.Elements("rule")
            .Where(element => string.Equals((string?)element.Attribute("name"), internalRuleName, StringComparison.OrdinalIgnoreCase))
            .Remove();

        var proxyRule = rules.Elements("rule")
            .FirstOrDefault(element => ((string?)element.Attribute("name"))?.StartsWith(ProxyRulePrefix, StringComparison.OrdinalIgnoreCase) == true);

        var newRule = new XElement(
            "rule",
            new XAttribute("name", internalRuleName),
            new XAttribute("stopProcessing", request.StopProcessing ? "true" : "false"),
            new XElement(
                "match",
                new XAttribute("url", request.MatchPattern.Trim()),
                new XAttribute("ignoreCase", request.IgnoreCase ? "true" : "false")),
            new XElement(
                "action",
                new XAttribute("type", "Rewrite"),
                new XAttribute("url", request.RewriteUrl.Trim()),
                new XAttribute("appendQueryString", request.AppendQueryString ? "true" : "false")));

        if (proxyRule is null)
        {
            rules.Add(newRule);
            return;
        }

        proxyRule.AddBeforeSelf(newRule);
    }

    private static void RemoveProxyRules(XDocument document)
    {
        var rewrite = GetRewriteElement(document);
        if (rewrite is null)
        {
            return;
        }

        rewrite.Element("rules")?
            .Elements("rule")
            .Where(element => ((string?)element.Attribute("name"))?.StartsWith(ProxyRulePrefix, StringComparison.OrdinalIgnoreCase) == true)
            .Remove();

        RemoveOutboundRules(document);
    }

    private static void RemoveOutboundRules(XDocument document)
    {
        var rewrite = GetRewriteElement(document);
        if (rewrite is null)
        {
            return;
        }

        var outboundRules = rewrite.Element("outboundRules");
        outboundRules?
            .Elements("rule")
            .Where(element => ((string?)element.Attribute("name"))?.StartsWith(OutboundRulePrefix, StringComparison.OrdinalIgnoreCase) == true)
            .Remove();

        var preConditions = outboundRules?.Element("preConditions");
        if (preConditions is null)
        {
            return;
        }

        var hasProxyOutboundRules = outboundRules!.Elements("rule")
            .Any(element => ((string?)element.Attribute("name"))?.StartsWith(OutboundRulePrefix, StringComparison.OrdinalIgnoreCase) == true);

        if (!hasProxyOutboundRules)
        {
            preConditions.Elements("preCondition")
                .Where(element => string.Equals((string?)element.Attribute("name"), HtmlPreconditionName, StringComparison.OrdinalIgnoreCase))
                .Remove();
        }
    }

    private static IReadOnlyCollection<RewriteRuleDto> ReadCustomRewriteRules(XDocument document)
    {
        var rules = GetRewriteElement(document)?.Element("rules");
        if (rules is null)
        {
            return [];
        }

        return rules.Elements("rule")
            .Where(element => ((string?)element.Attribute("name"))?.StartsWith(CustomRulePrefix, StringComparison.OrdinalIgnoreCase) == true)
            .Select(rule => new RewriteRuleDto
            {
                Name = DecodeCustomRuleName((string?)rule.Attribute("name")),
                MatchPattern = (string?)rule.Element("match")?.Attribute("url") ?? string.Empty,
                RewriteUrl = (string?)rule.Element("action")?.Attribute("url") ?? string.Empty,
                StopProcessing = ToBoolean(rule.Attribute("stopProcessing"), defaultValue: true),
                AppendQueryString = ToBoolean(rule.Element("action")?.Attribute("appendQueryString"), defaultValue: true),
                IgnoreCase = ToBoolean(rule.Element("match")?.Attribute("ignoreCase"), defaultValue: true)
            })
            .OrderBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (bool IsEnabled, string DestinationUrl, string MountPath, bool HasHtmlLinkRewrite) ReadProxyRuleState(XDocument document)
    {
        var rules = GetRewriteElement(document)?.Element("rules");
        if (rules is null)
        {
            return (false, string.Empty, "/", false);
        }

        var proxyRule = rules.Elements("rule")
            .FirstOrDefault(element => ((string?)element.Attribute("name"))?.StartsWith(ProxyRulePrefix, StringComparison.OrdinalIgnoreCase) == true);

        if (proxyRule is null)
        {
            return (false, string.Empty, "/", false);
        }

        var actionUrl = (string?)proxyRule.Element("action")?.Attribute("url") ?? string.Empty;
        var mountPath = DecodeMountPathFromRuleName((string?)proxyRule.Attribute("name"));
        var outboundRules = GetRewriteElement(document)?.Element("outboundRules");
        var hasHtmlLinkRewrite = outboundRules?.Elements("rule")
            .Any(element => ((string?)element.Attribute("name"))?.StartsWith(OutboundRulePrefix, StringComparison.OrdinalIgnoreCase) == true)
            ?? false;

        return (
            true,
            TrimProxyActionSuffix(actionUrl),
            mountPath,
            hasHtmlLinkRewrite);
    }

    private static string NormalizeDestinationUrl(string destinationUrl)
    {
        var normalized = destinationUrl.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AppException("proxy.invalid_destination_url", 400, 400);
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string NormalizeMountPath(string mountPath)
    {
        var normalized = string.IsNullOrWhiteSpace(mountPath)
            ? "/"
            : mountPath.Trim();

        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        if (normalized.Contains("//", StringComparison.Ordinal) ||
            !MountPathPattern().IsMatch(normalized))
        {
            throw new AppException("proxy.invalid_mount_path", 400, 400);
        }

        return normalized;
    }

    private static string NormalizeCustomRuleName(string name)
    {
        var normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new AppException("proxy.rule_name_required", 400, 400);
        }

        return normalized;
    }

    private static string ResolveWebConfigPath(string physicalPath)
    {
        var siteRoot = Environment.ExpandEnvironmentVariables(physicalPath.Trim());
        if (string.IsNullOrWhiteSpace(siteRoot))
        {
            throw new AppException("sites.invalid_physical_path", 400, 400);
        }

        Directory.CreateDirectory(siteRoot);
        return Path.Combine(Path.GetFullPath(siteRoot), "web.config");
    }

    private static XDocument LoadWebConfig(string webConfigPath)
    {
        return File.Exists(webConfigPath)
            ? XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));
    }

    private static XElement EnsureRewriteElement(XDocument document)
    {
        var configuration = document.Element("configuration") ?? new XElement("configuration");
        if (document.Element("configuration") is null)
        {
            document.Add(configuration);
        }

        var systemWebServer = EnsureChildElement(configuration, "system.webServer");
        return EnsureChildElement(systemWebServer, "rewrite");
    }

    private static XElement EnsureRulesElement(XDocument document)
    {
        return EnsureChildElement(EnsureRewriteElement(document), "rules");
    }

    private static XElement? GetRewriteElement(XDocument document)
    {
        return document.Element("configuration")?
            .Element("system.webServer")?
            .Element("rewrite");
    }

    private static XElement EnsureChildElement(XElement parent, string name)
    {
        var child = parent.Element(name);
        if (child is not null)
        {
            return child;
        }

        child = new XElement(name);
        parent.Add(child);
        return child;
    }

    private static bool IsRootMountPath(string mountPath)
    {
        return string.Equals(mountPath, "/", StringComparison.Ordinal);
    }

    private static string BuildMatchPattern(string mountPath)
    {
        if (IsRootMountPath(mountPath))
        {
            return "^(.*)$";
        }

        return "^" + Regex.Escape(mountPath.Trim('/')) + "(?:/(.*))?$";
    }

    private static string BuildActionUrl(string destinationUrl)
    {
        return destinationUrl + "/{R:1}";
    }

    private static string BuildOutboundConditionPattern(string mountPath)
    {
        return Regex.Escape(mountPath.Trim('/'));
    }

    private static string BuildOutboundRewriteValue(string mountPath)
    {
        return mountPath + "/{R:1}";
    }

    private static string EncodeMountPathForRuleName(string mountPath)
    {
        return IsRootMountPath(mountPath)
            ? "root"
            : mountPath.Trim('/').ToLowerInvariant();
    }

    private static string DecodeMountPathFromRuleName(string? ruleName)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            return "/";
        }

        var encoded = ruleName.Split('|').LastOrDefault() ?? "root";
        return string.Equals(encoded, "root", StringComparison.OrdinalIgnoreCase)
            ? "/"
            : "/" + encoded;
    }

    private static string DecodeCustomRuleName(string? ruleName)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            return string.Empty;
        }

        return ruleName.StartsWith(CustomRulePrefix, StringComparison.OrdinalIgnoreCase)
            ? ruleName[CustomRulePrefix.Length..]
            : ruleName;
    }

    private static string TrimProxyActionSuffix(string actionUrl)
    {
        const string suffix = "/{R:1}";
        return actionUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? actionUrl[..^suffix.Length]
            : actionUrl;
    }

    private static bool TryGetAttributeValue(object target, string attributeName)
    {
        try
        {
            return Convert.ToBoolean(InvokeMethod(target, "GetAttributeValue", attributeName));
        }
        catch
        {
            return false;
        }
    }

    private static bool ToBoolean(XAttribute? attribute, bool defaultValue)
    {
        return attribute is null
            ? defaultValue
            : string.Equals(attribute.Value, "true", StringComparison.OrdinalIgnoreCase);
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

    private static AppException MapProxyException(Exception exception)
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

        if (message.Contains("rewrite", StringComparison.OrdinalIgnoreCase) ||
            message.Contains(RewriteSectionPath, StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("proxy.rewrite_module_missing", 503, exception);
        }

        if (message.Contains("arr", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Application Request Routing", StringComparison.OrdinalIgnoreCase) ||
            message.Contains(ProxySectionPath, StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("proxy.arr_not_available", 503, exception);
        }

        return CreateDiagnosticException("proxy.configuration_failed", 500, exception);
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

    private sealed record ArrProxySettings(
        bool IsEnabled,
        bool PreserveHostHeader,
        bool ReverseRewriteHostInResponseHeaders);

    [GeneratedRegex("^/[a-zA-Z0-9._~!$&'()*+,;=:@/-]*$", RegexOptions.Compiled)]
    private static partial Regex MountPathPattern();
}
