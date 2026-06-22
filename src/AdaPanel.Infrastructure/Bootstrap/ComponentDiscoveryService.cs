using System.Diagnostics;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AdaPanel.Infrastructure.Bootstrap;

public sealed record DetectedComponent(
    string ComponentId,
    string Version,
    string InstallDirectory,
    string ServiceName,
    string DetectionSource,
    string ExecutablePath,
    string UninstallCommand,
    bool DeleteInstallDirectory);

public sealed partial class ComponentDiscoveryService(ILogger<ComponentDiscoveryService> logger)
{
    private const string UninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ServicesRegistryPath = @"SYSTEM\CurrentControlSet\Services";
    private const string AppPathsRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    public Task<IReadOnlyCollection<DetectedComponent>> DiscoverAsync(
        IReadOnlyCollection<ComponentManifestItemDto> manifest,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyCollection<DetectedComponent>>([]);
        }

        var snapshot = BuildSnapshot(cancellationToken);
        var detected = new List<DetectedComponent>();

        foreach (var component in manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var match = DetectComponent(component, snapshot);
                if (match is not null)
                {
                    detected.Add(match);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Installed component discovery failed for {ComponentId}.",
                    component.Id);
            }
        }

        return Task.FromResult<IReadOnlyCollection<DetectedComponent>>(detected);
    }

    private static DiscoverySnapshot BuildSnapshot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return new DiscoverySnapshot(
            ReadUninstallEntries(),
            ReadServices(),
            ReadAppPaths(),
            ReadPathDirectories(),
            ReadIisConfiguration());
    }

    private static DetectedComponent? DetectComponent(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot)
    {
        var managedDirectory = ExpandPath(component.InstallDir);
        var managedMatch = DetectManagedInstallation(component, managedDirectory);

        return component.Id.ToLowerInvariant() switch
        {
            "vc-redist-x64" => DetectVisualCpp(component, snapshot) ?? managedMatch,
            "php-8.3" => DetectPhp(component, snapshot, managedDirectory) ?? managedMatch,
            "mariadb-11" => DetectDatabaseService(
                component,
                snapshot,
                ["mariadb", "mariadbd.exe"],
                ["mariadbd.exe", "mysqld.exe"],
                path => path.Contains("mariadb", StringComparison.OrdinalIgnoreCase)) ?? managedMatch,
            "mysql-8" => DetectDatabaseService(
                component,
                snapshot,
                ["mysql"],
                ["mysqld.exe"],
                path => !path.Contains("mariadb", StringComparison.OrdinalIgnoreCase)) ?? managedMatch,
            "postgresql-16" => DetectDatabaseService(
                component,
                snapshot,
                ["postgres", "postgresql"],
                ["postgres.exe"],
                _ => true) ?? managedMatch,
            "pgadmin4" => DetectExecutableComponent(
                component,
                snapshot,
                ["pgadmin4.exe"],
                ["pgAdmin"],
                ["pgAdmin4"],
                [managedDirectory, Path.Combine(GetProgramFiles(), "pgAdmin 4")]) ?? managedMatch,
            "composer" => DetectExecutableComponent(
                component,
                snapshot,
                ["composer.bat", "composer.phar"],
                ["Composer"],
                ["composer.bat", "composer"],
                [managedDirectory, Path.Combine(GetProgramData(), "ComposerSetup", "bin")]) ?? managedMatch,
            "phpmyadmin" => DetectWebTool(component, snapshot, managedDirectory, "phpmyadmin", "index.php") ?? managedMatch,
            "adminer" => DetectWebTool(component, snapshot, managedDirectory, "adminer", "adminer.php") ?? managedMatch,
            "redis" => DetectServiceOrExecutable(
                component,
                snapshot,
                ["redis"],
                ["redis-server.exe"],
                ["Redis"],
                [managedDirectory]) ?? managedMatch,
            "memcached" => DetectServiceOrExecutable(
                component,
                snapshot,
                ["memcached"],
                ["memcached.exe"],
                ["Memcached"],
                [managedDirectory]) ?? managedMatch,
            "nodejs-lts" => DetectExecutableComponent(
                component,
                snapshot,
                ["node.exe"],
                ["Node.js"],
                ["node.exe"],
                [managedDirectory, Path.Combine(GetProgramFiles(), "nodejs")]) ?? managedMatch,
            "python-3" => DetectPython(component, snapshot, managedDirectory) ?? managedMatch,
            "url-rewrite" => DetectIisModule(
                component,
                snapshot,
                ["IIS URL Rewrite", "URL Rewrite"],
                Path.Combine(GetWindowsDirectory(), "System32", "inetsrv", "rewrite.dll")) ?? managedMatch,
            "iis-arr" => DetectIisModule(
                component,
                snapshot,
                ["Application Request Routing"],
                Path.Combine(GetProgramFiles(), "IIS", "Application Request Routing", "requestRouter.dll"),
                Path.Combine(GetWindowsDirectory(), "System32", "inetsrv", "config", "schema", "arr_schema.xml")) ?? managedMatch,
            "nginx" => DetectServiceOrExecutable(
                component,
                snapshot,
                ["nginx"],
                ["nginx.exe"],
                ["Nginx"],
                [managedDirectory, @"C:\nginx"]) ?? managedMatch,
            "http-platform-handler" => DetectIisModule(
                component,
                snapshot,
                ["HttpPlatformHandler", "HTTP Platform Handler"],
                Path.Combine(GetWindowsDirectory(), "System32", "inetsrv", "httpPlatformHandler.dll"),
                Path.Combine(GetProgramFiles(), "IIS", "HttpPlatformHandler", "httpPlatformHandler.dll")) ?? managedMatch,
            "stalwart-cli" => DetectExecutableComponent(
                component,
                snapshot,
                ["stalwart-cli.exe"],
                ["Stalwart CLI"],
                ["stalwart-cli.exe"],
                [managedDirectory]) ?? managedMatch,
            "stalwart-mail" => DetectServiceOrExecutable(
                component,
                snapshot,
                ["stalwart"],
                ["stalwart.exe"],
                ["Stalwart"],
                [managedDirectory]) ?? managedMatch,
            "docker-engine" => DetectServiceOrExecutable(
                component,
                snapshot,
                ["docker", "com.docker"],
                ["dockerd.exe", "docker.exe"],
                ["Docker Desktop", "Docker Engine"],
                [managedDirectory, Path.Combine(GetProgramFiles(), "Docker")]) ?? managedMatch,
            "win-acme" => DetectExecutableComponent(
                component,
                snapshot,
                ["wacs.exe"],
                ["win-acme"],
                ["wacs.exe"],
                [managedDirectory, Path.Combine(GetProgramData(), "win-acme")]) ?? managedMatch,
            _ => managedMatch
        };
    }

    private static DetectedComponent? DetectVisualCpp(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot)
    {
        var uninstall = FindUninstallEntry(
            snapshot,
            entry => entry.DisplayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase) &&
                entry.DisplayName.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) &&
                entry.DisplayName.Contains("x64", StringComparison.OrdinalIgnoreCase));

        if (uninstall is not null)
        {
            return FromUninstallEntry(component, uninstall, "Windows Registry");
        }

        foreach (var view in RegistryViews())
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (Convert.ToInt32(key?.GetValue("Installed") ?? 0) != 1)
            {
                continue;
            }

            var version = Convert.ToString(key?.GetValue("Version")) ?? component.Version;
            return new DetectedComponent(
                component.Id,
                version,
                Path.Combine(GetWindowsDirectory(), "System32"),
                string.Empty,
                "Windows Registry",
                string.Empty,
                string.Empty,
                false);
        }

        return null;
    }

    private static DetectedComponent? DetectDatabaseService(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot,
        IReadOnlyCollection<string> serviceTokens,
        IReadOnlyCollection<string> executableNames,
        Func<string, bool> pathFilter)
    {
        var service = snapshot.Services.FirstOrDefault(item =>
            serviceTokens.Any(token =>
                item.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
            executableNames.Any(name => item.ExecutablePath.EndsWith(name, StringComparison.OrdinalIgnoreCase)) &&
            pathFilter(item.ExecutablePath));

        if (service is not null)
        {
            var uninstall = FindUninstallForPath(snapshot, service.ExecutablePath);
            return FromExecutable(
                component,
                service.ExecutablePath,
                $"Windows Service ({service.Name})",
                service.Name,
                uninstall);
        }

        var uninstallEntry = FindUninstallEntry(snapshot, entry =>
            serviceTokens.Any(token => entry.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
            pathFilter(entry.InstallLocation + entry.DisplayIcon));

        return uninstallEntry is null
            ? null
            : FromUninstallEntry(component, uninstallEntry, "Installed Applications");
    }

    private static DetectedComponent? DetectServiceOrExecutable(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot,
        IReadOnlyCollection<string> serviceTokens,
        IReadOnlyCollection<string> executableNames,
        IReadOnlyCollection<string> uninstallNames,
        IReadOnlyCollection<string> commonDirectories)
    {
        var service = snapshot.Services.FirstOrDefault(item =>
            serviceTokens.Any(token =>
                item.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
            executableNames.Any(name => item.ExecutablePath.EndsWith(name, StringComparison.OrdinalIgnoreCase)));

        if (service is not null)
        {
            return FromExecutable(
                component,
                service.ExecutablePath,
                $"Windows Service ({service.Name})",
                service.Name,
                FindUninstallForPath(snapshot, service.ExecutablePath));
        }

        return DetectExecutableComponent(
            component,
            snapshot,
            executableNames,
            uninstallNames,
            executableNames,
            commonDirectories);
    }

    private static DetectedComponent? DetectExecutableComponent(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot,
        IReadOnlyCollection<string> executableNames,
        IReadOnlyCollection<string> uninstallNames,
        IReadOnlyCollection<string> appPathNames,
        IReadOnlyCollection<string> commonDirectories,
        bool includeIisPaths = false)
    {
        var uninstall = FindUninstallEntry(
            snapshot,
            entry => uninstallNames.Any(name =>
                MatchesProductName(entry.DisplayName, name)));

        if (uninstall is not null)
        {
            var executable = FindExecutableInDirectory(uninstall.InstallLocation, executableNames)
                ?? NormalizeExistingFile(uninstall.DisplayIcon);
            return FromUninstallEntry(component, uninstall, "Installed Applications", executable);
        }

        foreach (var appPathName in appPathNames)
        {
            if (snapshot.AppPaths.TryGetValue(appPathName, out var appPath) && File.Exists(appPath))
            {
                return FromExecutable(component, appPath, "Windows App Paths");
            }
        }

        foreach (var directory in commonDirectories)
        {
            var executable = FindExecutableInDirectory(directory, executableNames);
            if (executable is not null)
            {
                return FromExecutable(component, executable, "Executable Search");
            }
        }

        foreach (var directory in snapshot.PathDirectories)
        {
            foreach (var executableName in executableNames)
            {
                var executable = Path.Combine(directory, executableName);
                if (File.Exists(executable))
                {
                    return FromExecutable(component, executable, "System PATH");
                }
            }
        }

        if (includeIisPaths)
        {
            var executable = snapshot.IisPaths.FirstOrDefault(path =>
                executableNames.Any(name => path.EndsWith(name, StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(path));
            if (executable is not null)
            {
                return FromExecutable(component, executable, "IIS Configuration");
            }
        }

        return null;
    }

    private static DetectedComponent? DetectPython(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot,
        string managedDirectory)
    {
        var detected = DetectExecutableComponent(
            component,
            snapshot,
            ["python.exe"],
            ["Python 3", "Python 3."],
            ["python.exe", "python3.exe"],
            [
                managedDirectory,
                Path.Combine(GetProgramFiles(), "Python"),
                Path.Combine(GetLocalAppData(), "Programs", "Python")
            ]);

        if (detected is not null && IsUsablePythonPath(detected.ExecutablePath))
        {
            return detected with
            {
                Version = ReadPythonRuntimeVersion(detected.ExecutablePath, component.Version)
            };
        }

        var fallbackDirectories = new[]
            {
                managedDirectory,
                Path.Combine(GetProgramFiles(), "Python"),
                Path.Combine(GetLocalAppData(), "Programs", "Python")
            }
            .Concat(snapshot.PathDirectories)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in fallbackDirectories)
        {
            var executable = FindExecutableInDirectory(directory, ["python.exe"]);
            if (executable is not null && IsUsablePythonPath(executable))
            {
                return FromExecutable(component, executable, "Executable Search") with
                {
                    Version = ReadPythonRuntimeVersion(executable, component.Version)
                };
            }
        }

        return null;
    }

    private static DetectedComponent? DetectPhp(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot,
        string managedDirectory)
    {
        var detected = DetectExecutableComponent(
            component,
            snapshot,
            ["php-cgi.exe", "php.exe"],
            ["PHP"],
            ["php.exe", "php-cgi.exe"],
            [managedDirectory, @"C:\php"],
            includeIisPaths: true);

        return detected is not null &&
            detected.Version.StartsWith("8.3", StringComparison.OrdinalIgnoreCase)
                ? detected
                : null;
    }

    private static DetectedComponent? DetectIisModule(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot,
        IReadOnlyCollection<string> uninstallNames,
        params string[] markerPaths)
    {
        var uninstall = FindUninstallEntry(
            snapshot,
            entry => uninstallNames.Any(name =>
                entry.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)));

        if (uninstall is not null)
        {
            return FromUninstallEntry(component, uninstall, "Installed IIS Module");
        }

        var marker = markerPaths.FirstOrDefault(File.Exists);
        if (marker is null)
        {
            return null;
        }

        return new DetectedComponent(
            component.Id,
            ReadFileVersion(marker, component.Version),
            Path.GetDirectoryName(marker) ?? string.Empty,
            string.Empty,
            "IIS Module",
            marker,
            string.Empty,
            false);
    }

    private static DetectedComponent? DetectWebTool(
        ComponentManifestItemDto component,
        DiscoverySnapshot snapshot,
        string managedDirectory,
        string pathToken,
        string markerFile)
    {
        var marker = FindExecutableInDirectory(managedDirectory, [markerFile]);
        if (marker is null)
        {
            var iisDirectory = snapshot.IisPaths
                .Where(Directory.Exists)
                .FirstOrDefault(path => path.Contains(pathToken, StringComparison.OrdinalIgnoreCase));
            marker = iisDirectory is null ? null : FindExecutableInDirectory(iisDirectory, [markerFile]);
        }

        if (marker is null)
        {
            return null;
        }

        return new DetectedComponent(
            component.Id,
            component.Version,
            Path.GetDirectoryName(marker) ?? managedDirectory,
            string.Empty,
            "IIS Web Application",
            marker,
            string.Empty,
            true);
    }

    private static DetectedComponent? DetectManagedInstallation(
        ComponentManifestItemDto component,
        string installDirectory)
    {
        if (!Directory.Exists(installDirectory))
        {
            return null;
        }

        var markerNames = component.Id.ToLowerInvariant() switch
        {
            "php-8.3" => new[] { "php-cgi.exe", "php.exe" },
            "mariadb-11" => ["mariadbd.exe", "mysqld.exe"],
            "mysql-8" => ["mysqld.exe"],
            "postgresql-16" => ["postgres.exe"],
            "pgadmin4" => ["pgAdmin4.exe"],
            "composer" => ["composer.phar", "composer.bat"],
            "phpmyadmin" => ["index.php"],
            "adminer" => ["adminer.php"],
            "redis" => ["redis-server.exe"],
            "memcached" => ["memcached.exe"],
            "nodejs-lts" => ["node.exe"],
            "python-3" => ["python.exe"],
            "nginx" => ["nginx.exe"],
            "stalwart-cli" => ["stalwart-cli.exe"],
            "stalwart-mail" => ["stalwart.exe"],
            "docker-engine" => ["dockerd.exe", "docker.exe"],
            "win-acme" => ["wacs.exe"],
            _ => []
        };

        var marker = FindExecutableInDirectory(installDirectory, markerNames);
        if (marker is null)
        {
            return null;
        }

        return FromExecutable(component, marker, "AdaPanel Managed Directory", component.ServiceName);
    }

    private static DetectedComponent FromUninstallEntry(
        ComponentManifestItemDto component,
        UninstallEntry entry,
        string source,
        string? executablePath = null)
    {
        var installDirectory = NormalizeExistingDirectory(entry.InstallLocation)
            ?? GetDirectoryFromExecutable(executablePath)
            ?? GetDirectoryFromExecutable(NormalizeExistingFile(entry.DisplayIcon))
            ?? string.Empty;

        return new DetectedComponent(
            component.Id,
            string.IsNullOrWhiteSpace(entry.DisplayVersion) ? component.Version : entry.DisplayVersion,
            installDirectory,
            component.ServiceName,
            source,
            executablePath ?? string.Empty,
            NormalizeUninstallCommand(entry),
            false);
    }

    private static DetectedComponent FromExecutable(
        ComponentManifestItemDto component,
        string executablePath,
        string source,
        string? serviceName = null,
        UninstallEntry? uninstall = null)
    {
        var directory = GetComponentDirectory(component.Id, executablePath);
        return new DetectedComponent(
            component.Id,
            ReadFileVersion(executablePath, component.Version),
            directory,
            serviceName ?? component.ServiceName,
            source,
            executablePath,
            uninstall is null ? string.Empty : NormalizeUninstallCommand(uninstall),
            uninstall is null);
    }

    private static string GetComponentDirectory(string componentId, string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        if (componentId is "mariadb-11" or "mysql-8" or "postgresql-16" &&
            string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(directory)?.FullName ?? directory;
        }

        if (componentId == "pgadmin4" &&
            string.Equals(Path.GetFileName(directory), "runtime", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.GetParent(directory)?.FullName ?? directory;
        }

        return directory;
    }

    private static UninstallEntry? FindUninstallForPath(DiscoverySnapshot snapshot, string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        return snapshot.UninstallEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.InstallLocation))
            .OrderByDescending(entry => entry.InstallLocation.Length)
            .FirstOrDefault(entry =>
                directory.StartsWith(
                    EnsureTrailingSeparator(entry.InstallLocation),
                    StringComparison.OrdinalIgnoreCase));
    }

    private static UninstallEntry? FindUninstallEntry(
        DiscoverySnapshot snapshot,
        Func<UninstallEntry, bool> predicate)
        => snapshot.UninstallEntries.FirstOrDefault(predicate);

    private static IReadOnlyCollection<UninstallEntry> ReadUninstallEntries()
    {
        var results = new List<UninstallEntry>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in RegistryViews())
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(UninstallRegistryPath);
                    if (uninstallKey is null)
                    {
                        continue;
                    }

                    foreach (var keyName in uninstallKey.GetSubKeyNames())
                    {
                        using var entryKey = uninstallKey.OpenSubKey(keyName);
                        var displayName = ReadRegistryString(entryKey, "DisplayName");
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            continue;
                        }

                        results.Add(new UninstallEntry(
                            keyName,
                            displayName,
                            ReadRegistryString(entryKey, "DisplayVersion"),
                            ExpandPath(ReadRegistryString(entryKey, "InstallLocation")),
                            ReadRegistryString(entryKey, "DisplayIcon"),
                            ReadRegistryString(entryKey, "QuietUninstallString"),
                            ReadRegistryString(entryKey, "UninstallString")));
                    }
                }
                catch
                {
                    // Registry views can be unavailable on older Windows editions.
                }
            }
        }

        return results;
    }

    private static IReadOnlyCollection<ServiceEntry> ReadServices()
    {
        var results = new List<ServiceEntry>();
        try
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(ServicesRegistryPath);
            if (servicesKey is null)
            {
                return results;
            }

            foreach (var serviceName in servicesKey.GetSubKeyNames())
            {
                using var serviceKey = servicesKey.OpenSubKey(serviceName);
                var imagePath = ReadRegistryString(serviceKey, "ImagePath");
                var executablePath = ExtractExecutablePath(imagePath);
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                results.Add(new ServiceEntry(
                    serviceName,
                    ReadRegistryString(serviceKey, "DisplayName"),
                    executablePath));
            }
        }
        catch
        {
            // A restricted service account may not be able to enumerate every service.
        }

        return results;
    }

    private static IReadOnlyDictionary<string, string> ReadAppPaths()
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in RegistryViews())
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appPathsKey = baseKey.OpenSubKey(AppPathsRegistryPath);
                    if (appPathsKey is null)
                    {
                        continue;
                    }

                    foreach (var keyName in appPathsKey.GetSubKeyNames())
                    {
                        using var appKey = appPathsKey.OpenSubKey(keyName);
                        var path = NormalizeExistingFile(Convert.ToString(appKey?.GetValue(null)));
                        if (path is not null)
                        {
                            results[keyName] = path;
                        }
                    }
                }
                catch
                {
                    // Continue with the other registry view.
                }
            }
        }

        return results;
    }

    private static IReadOnlyCollection<string> ReadPathDirectories()
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ExpandPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyCollection<string> ReadIisConfiguration()
    {
        var configurationPath = Path.Combine(
            GetWindowsDirectory(),
            "System32",
            "inetsrv",
            "config",
            "applicationHost.config");
        if (!File.Exists(configurationPath))
        {
            return [];
        }

        try
        {
            var content = File.ReadAllText(configurationPath);
            var executablePaths = QuotedPathRegex()
                .Matches(content)
                .Select(match => match.Groups["path"].Value);
            var physicalPaths = IisPhysicalPathRegex()
                .Matches(content)
                .Select(match => match.Groups["path"].Value);

            return executablePaths
                .Concat(physicalPaths)
                .Select(ExpandPath)
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    internal static string ExtractExecutablePath(string commandLine)
    {
        var expanded = ExpandPath(commandLine).Trim();
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return string.Empty;
        }

        if (expanded[0] == '"')
        {
            var closingQuote = expanded.IndexOf('"', 1);
            return closingQuote > 1 ? expanded[1..closingQuote] : string.Empty;
        }

        var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (executableEnd >= 0)
        {
            return expanded[..(executableEnd + 4)].Trim();
        }

        return expanded.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
    }

    internal static string NormalizeUninstallCommand(UninstallEntry entry)
    {
        var command = string.IsNullOrWhiteSpace(entry.QuietUninstallString)
            ? entry.UninstallString
            : entry.QuietUninstallString;
        if (string.IsNullOrWhiteSpace(command) && ProductCodeRegex().IsMatch(entry.RegistryKeyName))
        {
            command = $"msiexec.exe /x {entry.RegistryKeyName} /qn /norestart";
        }

        if (!command.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            return command;
        }

        command = MsiInstallSwitchRegex().Replace(command, "/x ");
        if (!command.Contains("/q", StringComparison.OrdinalIgnoreCase))
        {
            command += " /qn";
        }

        if (!command.Contains("/norestart", StringComparison.OrdinalIgnoreCase))
        {
            command += " /norestart";
        }

        return command.Trim();
    }

    private static string? FindExecutableInDirectory(
        string directory,
        IReadOnlyCollection<string> fileNames)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || fileNames.Count == 0)
        {
            return null;
        }

        foreach (var fileName in fileNames)
        {
            var direct = Path.Combine(directory, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .FirstOrDefault(path => fileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static string ReadFileVersion(string filePath, string fallback)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(filePath).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? fallback : version;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ReadPythonRuntimeVersion(string executablePath, string fallback)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return fallback;
            }

            return ParsePythonVersionOutput(
                $"{process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}",
                fallback);
        }
        catch
        {
            return fallback;
        }
    }

    internal static string ParsePythonVersionOutput(string output, string fallback)
    {
        var match = PythonVersionRegex().Match(output);
        return match.Success ? match.Groups["version"].Value : fallback;
    }

    private static string? NormalizeExistingDirectory(string? path)
    {
        var expanded = ExpandPath(path);
        return Directory.Exists(expanded) ? Path.GetFullPath(expanded) : null;
    }

    private static string? NormalizeExistingFile(string? path)
    {
        var candidate = ExtractExecutablePath(path ?? string.Empty);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    private static string? GetDirectoryFromExecutable(string? filePath)
        => string.IsNullOrWhiteSpace(filePath) ? null : Path.GetDirectoryName(filePath);

    private static string ReadRegistryString(RegistryKey? key, string valueName)
        => Convert.ToString(key?.GetValue(valueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames))
            ?? string.Empty;

    private static string ExpandPath(string? path)
        => Environment.ExpandEnvironmentVariables(path ?? string.Empty)
            .Trim()
            .Trim('"');

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    internal static bool MatchesProductName(string displayName, string productName)
    {
        var candidate = displayName.Trim();
        var expected = productName.Trim();
        return candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(expected + " (", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(expected + "-", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUsablePythonPath(string executablePath)
        => !string.IsNullOrWhiteSpace(executablePath) &&
            !executablePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);

    private static string GetProgramFiles()
        => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static string GetProgramData()
        => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static string GetLocalAppData()
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string GetWindowsDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static RegistryView[] RegistryViews()
        => Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Registry32];

    [GeneratedRegex("\"(?<path>[^\"\\r\\n]+(?:\\.exe|\\.dll|\\.php|\\\\))\"", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedPathRegex();

    [GeneratedRegex(@"physicalPath\s*=\s*""(?<path>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex IisPhysicalPathRegex();

    [GeneratedRegex(@"^\{[0-9A-F-]{36}\}$", RegexOptions.IgnoreCase)]
    private static partial Regex ProductCodeRegex();

    [GeneratedRegex(@"(?<!\S)/(?:i|I)(?=\s*\{)", RegexOptions.IgnoreCase)]
    private static partial Regex MsiInstallSwitchRegex();

    [GeneratedRegex(@"\bPython\s+(?<version>\d+\.\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PythonVersionRegex();

    internal sealed record UninstallEntry(
        string RegistryKeyName,
        string DisplayName,
        string DisplayVersion,
        string InstallLocation,
        string DisplayIcon,
        string QuietUninstallString,
        string UninstallString);

    private sealed record ServiceEntry(string Name, string DisplayName, string ExecutablePath);

    private sealed record DiscoverySnapshot(
        IReadOnlyCollection<UninstallEntry> UninstallEntries,
        IReadOnlyCollection<ServiceEntry> Services,
        IReadOnlyDictionary<string, string> AppPaths,
        IReadOnlyCollection<string> PathDirectories,
        IReadOnlyCollection<string> IisPaths);
}
