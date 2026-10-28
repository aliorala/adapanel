namespace AdaPanel.Core.Dtos.Backup;

public sealed record BackupNetworkTrustDto(string Target, bool Allowed);
public sealed record SetBackupNetworkTrustRequest(string Target, bool Allowed);
