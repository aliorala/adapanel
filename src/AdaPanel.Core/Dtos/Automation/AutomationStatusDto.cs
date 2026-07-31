namespace AdaPanel.Core.Dtos.Automation;

/// <summary>
/// Arka plan zamanlayıcısının çalışma durumu (son/sonraki çalışma, sonuç özeti).
/// </summary>
public sealed class AutomationStatusDto
{
    public AutomationSettingsDto Settings { get; set; } = new();

    public DateTime? LastBackupRunUtc { get; set; }

    public string LastBackupStatus { get; set; } = string.Empty;

    public string LastBackupDetails { get; set; } = string.Empty;

    public DateTime? NextBackupRunUtc { get; set; }

    public DateTime? LastSslRunUtc { get; set; }

    public string LastSslStatus { get; set; } = string.Empty;

    public string LastSslDetails { get; set; } = string.Empty;

    public DateTime? NextSslRunUtc { get; set; }

    public DateTime? LastLogCleanupRunUtc { get; set; }

    public string LastLogCleanupStatus { get; set; } = string.Empty;

    public string LastLogCleanupDetails { get; set; } = string.Empty;

    public DateTime? LastUpdateCheckRunUtc { get; set; }

    /// <summary>ok / applied / failed + kısa özet.</summary>
    public string LastUpdateCheckStatus { get; set; } = string.Empty;

    /// <summary>Güncellemesi çıkan bileşenlerin adı ve sürümleri.</summary>
    public string LastUpdateCheckDetails { get; set; } = string.Empty;

    /// <summary>Son kontrolde güncellemesi bulunan bileşen sayısı.</summary>
    public int PendingComponentUpdates { get; set; }
}
