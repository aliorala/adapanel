namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Off-site yedek hedefini (FTP / S3 / WebDAV) gerçek yedek almadan doğrular: küçük bir test dosyasını yükler
/// → kimlik bilgileri + bağlantı + yazma izni denetlenir (Plesk "test connection" gibi).
/// </summary>
public sealed class BackupRemoteTestRequest
{
    public string TargetType { get; init; } = "local";

    public string RemoteUrl { get; init; } = string.Empty;

    public string RemoteUserName { get; init; } = string.Empty;

    /// <summary>Boş ise sunucudaki kayıtlı (korumalı) parola kullanılır.</summary>
    public string RemotePassword { get; init; } = string.Empty;

    public string RemoteRegion { get; init; } = string.Empty;
}

/// <summary>Off-site yedek bağlantı testi sonucu.</summary>
public sealed class BackupRemoteTestResultDto
{
    public bool Success { get; set; }

    /// <summary>i18n anahtarı.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Hata ayrıntısı (varsa, ham).</summary>
    public string Details { get; set; } = string.Empty;
}
