using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>Refresh package entry points without recreating existing IIS sites or SSL bindings.</summary>
internal static class ComponentWebPublication
{
    internal static void DetachRoundcubeAssets(string installRoot)
    {
        var root = Path.GetFullPath(installRoot);
        var documentRoot = Path.Combine(root, "public_html");
        SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(root)!, documentRoot);
        foreach (var name in new[] { "skins", "program", "plugins" })
        {
            var link = Path.Combine(documentRoot, name);
            if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(link, recursive: false);
        }
    }

    internal static async Task RestoreAsync(string componentId, string installRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(installRoot);
        if (componentId.Equals("adminer", StringComparison.OrdinalIgnoreCase))
        {
            var entry = Path.Combine(root, "webroot");
            // The preserved health file marks an already published managed tool.
            if (!File.Exists(Path.Combine(entry, "adapanel-health.php"))) return;
            foreach (var path in new[] { entry, Path.Combine(root, "adminer.php"), Path.Combine(entry, "index.php") })
                SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(root)!, path);
            File.Copy(Path.Combine(root, "adminer.php"), Path.Combine(entry, "index.php"), overwrite: true);
        }
        else if (componentId.Equals("roundcube", StringComparison.OrdinalIgnoreCase) &&
                 File.Exists(Path.Combine(root, "public_html", "adapanel-health.php")))
        {
            await EnsureRoundcubeAssetsAsync(root, Path.Combine(root, "public_html"), cancellationToken);
        }
    }

    internal static async Task EnsureRoundcubeAssetsAsync(string installRoot, string documentRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(installRoot);
        if (!Path.GetFullPath(documentRoot).Equals(Path.Combine(root, "public_html"), StringComparison.OrdinalIgnoreCase))
            throw new AppException("software.data_preserve_failed", 500, 500, "Unexpected Roundcube document root.");
        SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(root)!, documentRoot);
        foreach (var name in new[] { "skins", "program", "plugins" })
        {
            var target = Path.Combine(root, name);
            var link = Path.Combine(documentRoot, name);
            SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(root)!, target);
            if (!Directory.Exists(target)) continue;
            if (Directory.Exists(link))
            {
                var info = new DirectoryInfo(link);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 &&
                    !string.Equals(info.ResolveLinkTarget(true)?.FullName, target, StringComparison.OrdinalIgnoreCase))
                    throw new AppException("software.data_preserve_failed", 500, 500, "Roundcube asset link targets an unexpected directory.");
                continue;
            }
            if (File.Exists(link)) throw new IOException("A file blocks the Roundcube asset directory: " + link);
            var result = await ProcessRunner.RunPowerShellAsync(
                "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") +
                "' -Target '" + target.Replace("'", "''") + "' | Out-Null", cancellationToken);
            if (result.ExitCode != 0 || !Directory.Exists(link))
                throw new IOException("Could not restore Roundcube asset directory: " + link);
        }
    }
}
