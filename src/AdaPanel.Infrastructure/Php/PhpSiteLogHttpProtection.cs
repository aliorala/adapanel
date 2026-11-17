using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using static AdaPanel.Infrastructure.AspNet.IisConfigurationAccess;

namespace AdaPanel.Infrastructure.Php;

/// <summary>Server-side, narrow request filtering; never rewrites the site's web.config.</summary>
internal sealed class PhpSiteLogHttpProtection(IConcurrencyLock gate)
{
    internal Task<string?> EnsureAsync(SiteDto site, string logPath, bool apply, CancellationToken token) =>
        gate.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
        {
            using var manager = CreateServerManager();
            var root = Path.GetFullPath(site.PhysicalPath).TrimEnd(Path.DirectorySeparatorChar);
            // Reject alias publication, including a virtual directory of another site.
            foreach (var candidate in Enumerate(GetPropertyObject(manager, "Sites")))
            foreach (var app in Enumerate(GetPropertyObject(candidate, "Applications")))
            foreach (var directory in Enumerate(GetPropertyObject(app, "VirtualDirectories")))
            {
                var physical = Path.GetFullPath(Environment.ExpandEnvironmentVariables(GetStringProperty(directory, "PhysicalPath"))).TrimEnd(Path.DirectorySeparatorChar);
                if (!Inside(physical, logPath)) continue;
                if (!GetStringProperty(candidate, "Name").Equals(site.Name, StringComparison.OrdinalIgnoreCase) ||
                    GetStringProperty(app, "Path") != "/" || GetStringProperty(directory, "Path") != "/" ||
                    !physical.Equals(root, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<string?>("Günlük yolu başka bir IIS site/uygulama veya sanal dizininden yayımlanıyor. İşlem durduruldu.");
            }
            if (!Inside(root, logPath)) return Task.FromResult<string?>(null);
            var relative = Path.GetRelativePath(root, logPath).Replace('\\', '/');
            var managed = relative.StartsWith(".adapanel-logs/", StringComparison.OrdinalIgnoreCase);
            var segment = managed ? ".adapanel-logs" : Path.GetFileName(logPath);
            var location = managed ? site.Name : site.Name + (relative.Contains('/') ? "/" + relative[..relative.LastIndexOf('/')] : "");
            var configuration = InvokeMethod(manager, "GetApplicationHostConfiguration");
            var section = InvokeMethod(configuration, "GetSection", "system.webServer/security/requestFiltering", location);
            var hidden = InvokeMethod(section, "GetChildElement", "hiddenSegments");
            var collection = InvokeMethod(hidden, "GetCollection");
            var found = Enumerate(collection).Any(entry => GetAttributeString(entry, "segment").Equals(segment, StringComparison.OrdinalIgnoreCase));
            var webDav = Convert.ToBoolean(InvokeMethod(hidden, "GetAttributeValue", "applyToWebDAV"));
            if (!found || !webDav)
            {
                if (!apply) return Task.FromResult<string?>("Günlüğün IIS HTTP erişim engeli doğrulanamadı.");
                if (!found)
                {
                    var entry = InvokeMethod(collection, "CreateElement", "add");
                    SetAttributeValue(entry, "segment", segment);
                    InvokeMethod(collection, "Add", entry);
                }
                SetAttributeValue(hidden, "applyToWebDAV", true);
                InvokeMethod(manager, "CommitChanges");
                // A fresh manager reads the committed configuration; do not trust the edited object.
                using var fresh = CreateServerManager();
                var actual = InvokeMethod(InvokeMethod(fresh, "GetApplicationHostConfiguration"), "GetSection", "system.webServer/security/requestFiltering", location);
                var actualHidden = InvokeMethod(actual, "GetChildElement", "hiddenSegments");
                if (!Enumerate(InvokeMethod(actualHidden, "GetCollection")).Any(entry => GetAttributeString(entry, "segment").Equals(segment, StringComparison.OrdinalIgnoreCase)) ||
                    !Convert.ToBoolean(InvokeMethod(actualHidden, "GetAttributeValue", "applyToWebDAV")))
                    return Task.FromResult<string?>("IIS günlük engeli kayıttan sonra geri okunamadı.");
            }
            return Task.FromResult<string?>(null);
        }, token);

    internal static bool Inside(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);
}
