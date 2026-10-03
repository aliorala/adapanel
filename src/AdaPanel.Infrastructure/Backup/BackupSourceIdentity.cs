using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

internal static class BackupSourceIdentity
{
    public static string Key(BackupRecordDto record) => Key(record.Type, record.Source);

    public static string Key(string? type, string? source)
    {
        var kind = (type ?? string.Empty).Trim().ToLowerInvariant();
        var name = source ?? string.Empty;
        var separator = name.IndexOf(':');
        if (kind == "database" && separator >= 0)
            return $"{kind}|{DatabaseResourceIdentity.Key(name[..separator], name[(separator + 1)..])}";

        // Site, subscription and legacy non-database sources keep their original
        // case-insensitive identity. Consumers compare the normalized key ordinally.
        return $"{kind}|{name.ToUpperInvariant()}";
    }

    public static bool Same(BackupRecordDto record, string type, string source) =>
        string.Equals(Key(record), Key(type, source), StringComparison.Ordinal);

    public static bool Same(BackupRecordDto first, BackupRecordDto second) =>
        string.Equals(Key(first), Key(second), StringComparison.Ordinal);
}
