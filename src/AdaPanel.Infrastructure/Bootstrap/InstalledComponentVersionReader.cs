using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>Reads version resources from known managed executables without starting them.</summary>
internal static partial class InstalledComponentVersionReader
{
    internal static bool Supports(string componentId) =>
        componentId.Equals("redis", StringComparison.OrdinalIgnoreCase)
        || componentId.Equals("win-acme", StringComparison.OrdinalIgnoreCase);

    internal static string? Read(string componentId, string installDirectory)
    {
        if (!Supports(componentId) || !Path.IsPathFullyQualified(installDirectory)) return null;
        var fileName = componentId.Equals("redis", StringComparison.OrdinalIgnoreCase)
            ? "redis-server.exe" : "wacs.exe";
        // These packages install the executable at their root. Do not pick an arbitrary
        // executable from an old copy, download directory or nested backup.
        var path = Path.Combine(installDirectory, fileName);
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return FromResources(info.ProductVersion, info.FileVersion);
    }

    internal static string? FromResources(string? productVersion, string? fileVersion) =>
        string.IsNullOrWhiteSpace(productVersion) ? Parse(fileVersion) : Parse(productVersion);

    internal static string? Parse(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return null;
        var match = StableVersion().Match(resource.Trim());
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) && version > new Version(0, 0, 0, 0)
            ? match.Groups[1].Value : null;
    }

    // ProductVersion may include a source revision. A prerelease must not be recorded as stable.
    [GeneratedRegex(@"^(\d+\.\d+(?:\.\d+){0,2})(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersion();
}
