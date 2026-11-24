namespace AdaPanel.Core.Dtos.Databases;

/// <summary>Bir abonelik veritabanına yetkili hesabın kilit durumu (askı için).</summary>
public sealed class DatabaseUserLockDto
{
    public string Engine { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;

    /// <summary>true = kilitli, false = açık, null = ölçülemedi.</summary>
    public bool? Locked { get; init; }

    /// <summary>
    /// Hesabın TÜM yetkileri sorulan veritabanlarının içinde mi. false ise başka kiracının veritabanına da
    /// erişir; kilitlemek onu da keseceği için ASKIYA ALINMAZ (raporlanır).
    /// </summary>
    public bool Exclusive { get; init; }
}
