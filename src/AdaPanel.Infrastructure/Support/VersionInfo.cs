using System.Reflection;

namespace AdaPanel.Infrastructure.Support;

internal static class VersionInfo
{
    public static string Current =>
        typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(VersionInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
