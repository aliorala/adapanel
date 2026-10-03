namespace AdaPanel.Core.Common;

/// <summary>Panel-managed database identities, including PostgreSQL quoted names.</summary>
public static class DatabaseResourceIdentity
{
    public static string NormalizeEngine(string? engine) => (engine ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "postgres" => "postgresql",
        "sqlserver" => "mssql",
        var value => value
    };

    public static string NormalizeName(string? engine, string? name)
    {
        var value = (name ?? string.Empty).Trim();
        return NormalizeEngine(engine) == "postgresql" ? value : value.ToLowerInvariant();
    }

    public static string Key(string? engine, string? name) =>
        $"{NormalizeEngine(engine)}|{NormalizeName(engine, name)}";

    public static bool Same(string? engine1, string? name1, string? engine2, string? name2) =>
        string.Equals(Key(engine1, name1), Key(engine2, name2), StringComparison.Ordinal);
}
