using System.Xml.Linq;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// Prepares PHP web application roots without replacing existing IIS handlers,
/// rewrite rules, or application-specific security configuration.
/// </summary>
internal static class WebAppPublisher
{
    public static void EnsurePhpEntry(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            var indexPhp = Path.Combine(root, "index.php");
            if (!File.Exists(indexPhp))
            {
                return;
            }

            var indexHtml = Path.Combine(root, "index.html");
            if (File.Exists(indexHtml))
            {
                File.Delete(indexHtml);
            }

            var webConfig = Path.Combine(root, "web.config");
            XDocument document;
            if (File.Exists(webConfig))
            {
                document = XDocument.Load(webConfig, LoadOptions.PreserveWhitespace);
            }
            else
            {
                document = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("configuration"));
            }

            var configuration = document.Root ?? new XElement("configuration");
            if (document.Root is null)
            {
                document.Add(configuration);
            }

            var systemWebServer = configuration.Element("system.webServer");
            if (systemWebServer is null)
            {
                systemWebServer = new XElement("system.webServer");
                configuration.Add(systemWebServer);
            }

            var defaultDocument = systemWebServer.Element("defaultDocument");
            if (defaultDocument is null)
            {
                defaultDocument = new XElement("defaultDocument");
                systemWebServer.Add(defaultDocument);
            }

            var files = defaultDocument.Element("files");
            if (files is null)
            {
                files = new XElement("files");
                defaultDocument.Add(files);
            }

            var existingIndexPhp = files.Elements("add").FirstOrDefault(element =>
                string.Equals(
                    (string?)element.Attribute("value"),
                    "index.php",
                    StringComparison.OrdinalIgnoreCase));
            if (existingIndexPhp is null)
            {
                files.AddFirst(new XElement("add", new XAttribute("value", "index.php")));
            }
            else
            {
                existingIndexPhp.Remove();
                files.AddFirst(existingIndexPhp);
            }

            // Büyük yükleme (ör. phpMyAdmin'den SQL içe aktarma): IIS RequestFiltering varsayılanı ~30MB,
            // PHP upload limitiyle uyumsuz → "413 Request Entity Too Large". 1GB tavan koy (PHP'nin
            // upload_max_filesize/post_max_size'ı asıl sınırı belirler). Kullanıcı/SiteHostingSettings
            // değer koymuşsa DOKUNMA (preserve).
            var security = systemWebServer.Element("security");
            if (security is null) { security = new XElement("security"); systemWebServer.Add(security); }
            var requestFiltering = security.Element("requestFiltering");
            if (requestFiltering is null) { requestFiltering = new XElement("requestFiltering"); security.Add(requestFiltering); }
            var requestLimits = requestFiltering.Element("requestLimits");
            if (requestLimits is null) { requestLimits = new XElement("requestLimits"); requestFiltering.Add(requestLimits); }
            if (requestLimits.Attribute("maxAllowedContentLength") is null)
            {
                requestLimits.SetAttributeValue("maxAllowedContentLength", "1073741824");
            }

            document.Save(webConfig);
        }
        catch
        {
            // The caller's deployment health check reports any resulting IIS issue.
        }
    }

    // Bir sitenin web.config'ine IIS maxAllowedContentLength'i AÇIKÇA yazar (üzerine yazar). Panelden upload
    // limiti değişince çağrılır → IIS de yükselir, "413 Request Entity Too Large" olmaz. PHP ve ASP.NET'te geçerli.
    public static void SetMaxAllowedContentLength(string root, long bytes)
    {
        if (string.IsNullOrWhiteSpace(root) || bytes <= 0)
        {
            return;
        }

        try
        {
            var webConfig = Path.Combine(root, "web.config");
            var document = File.Exists(webConfig)
                ? XDocument.Load(webConfig, LoadOptions.PreserveWhitespace)
                : new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("configuration"));
            var configuration = document.Root ?? new XElement("configuration");
            if (document.Root is null)
            {
                document.Add(configuration);
            }

            var requestLimits = EnsureChild(EnsureChild(EnsureChild(EnsureChild(configuration, "system.webServer"), "security"), "requestFiltering"), "requestLimits");
            requestLimits.SetAttributeValue("maxAllowedContentLength", bytes.ToString());
            document.Save(webConfig);
        }
        catch
        {
            // Best-effort: yazılamazsa varsayılan IIS limiti geçerli kalır.
        }
    }

    // #2 — Laravel/WordPress/Symfony "front controller" yönlendirmesi: istenen yol gerçek bir dosya/dizin
    // DEĞİLSE index.php'ye yeniden yaz (pretty URL'ler). URL Rewrite MODÜLÜ gerekir; MODÜL YOKSA bir <rewrite>
    // bölümü tüm siteyi 500.19'a sokar → rewrite.dll yoksa HİÇBİR ŞEY yazma + false döndür (çağıran net hata verir).
    // Idempotent: aynı isimli kural varsa tekrar eklemez. Mevcut başka rewrite kurallarını korur.
    public static bool TryWriteFrontControllerRewrite(string root)
        => TryWriteFrontControllerRewrite(root,
            File.Exists(Path.Combine(Environment.SystemDirectory, "inetsrv", "rewrite.dll")));

    internal static bool TryWriteFrontControllerRewrite(string root, bool moduleAvailable)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        if (!moduleAvailable)
        {
            return false;
        }

        try
        {
            var webConfig = Path.Combine(root, "web.config");
            var document = File.Exists(webConfig)
                ? XDocument.Load(webConfig, LoadOptions.PreserveWhitespace)
                : new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("configuration"));
            var configuration = document.Root ?? new XElement("configuration");
            if (document.Root is null)
            {
                document.Add(configuration);
            }

            var rules = EnsureChild(EnsureChild(EnsureChild(configuration, "system.webServer"), "rewrite"), "rules");
            var exists = rules.Elements("rule").Any(rule =>
                string.Equals((string?)rule.Attribute("name"), "AdaPanel Front Controller", StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                rules.Add(new XElement(
                    "rule",
                    new XAttribute("name", "AdaPanel Front Controller"),
                    new XAttribute("stopProcessing", "true"),
                    new XElement("match", new XAttribute("url", "^(.*)$")),
                    new XElement(
                        "conditions",
                        new XAttribute("logicalGrouping", "MatchAll"),
                        new XElement("add", new XAttribute("input", "{REQUEST_FILENAME}"), new XAttribute("matchType", "IsFile"), new XAttribute("negate", "true")),
                        new XElement("add", new XAttribute("input", "{REQUEST_FILENAME}"), new XAttribute("matchType", "IsDirectory"), new XAttribute("negate", "true"))),
                    new XElement("action", new XAttribute("type", "Rewrite"), new XAttribute("url", "index.php"))));
                document.Save(webConfig);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static XElement EnsureChild(XElement parent, string name)
    {
        var child = parent.Element(name);
        if (child is null)
        {
            child = new XElement(name);
            parent.Add(child);
        }

        return child;
    }
}
