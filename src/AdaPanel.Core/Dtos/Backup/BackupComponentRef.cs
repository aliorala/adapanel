namespace AdaPanel.Core.Dtos.Backup;

/// <summary>A measured logical component; Source identifies its site, domain, account or panel item.</summary>
public sealed class BackupComponentRef
{
    public string Kind { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;
}
