namespace AdaPanel.Core.Dtos.Backup;

/// <summary>The exact backup IDs shown to the user before confirming a branch deletion.</summary>
public sealed class DeleteBackupChainRequest
{
    public string[]? ExpectedBackupIds { get; init; }
}
