using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Database;

// Database names and account names have different comparison rules: MySQL user names,
// like PostgreSQL quoted roles, are case-sensitive even on Windows. Hosts are not.
internal static class DatabaseAccountIdentity
{
    internal static string Name(string engine, string name) =>
        DatabaseResourceIdentity.NormalizeEngine(engine) == "mssql"
            ? name.Trim().ToLowerInvariant() : name.Trim();

    internal static string Host(string engine, string? host) =>
        DatabaseResourceIdentity.NormalizeEngine(engine) is "mysql" or "mariadb"
            ? string.IsNullOrWhiteSpace(host) ? "%" : host.Trim().ToLowerInvariant()
            : "%";

    internal static string FamilyKey(string engine, string name) =>
        $"{DatabaseResourceIdentity.NormalizeEngine(engine)}|{Name(engine, name)}";

    internal static bool SameFamily(string engine, string name, string otherEngine, string otherName) =>
        string.Equals(FamilyKey(engine, name), FamilyKey(otherEngine, otherName), StringComparison.Ordinal);

    internal static bool Same(string engine, string name, string? host, string otherEngine, string otherName, string? otherHost) =>
        SameFamily(engine, name, otherEngine, otherName) && Host(engine, host) == Host(otherEngine, otherHost);
}
