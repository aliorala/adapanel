namespace AdaPanel.Core.Dtos.Security;

/// <summary>
/// Panel giriş ekranı için brute-force koruma ayarları. Eşik aşıldığında saldırgan
/// IP, Windows Firewall'da süreli bir engel kuralıyla bloklanır.
/// </summary>
public sealed class BruteForceSettingsDto
{
    public bool Enabled { get; set; } = true;

    /// <summary>Engelleme öncesi izin verilen başarısız giriş sayısı.</summary>
    public int MaxFailedAttempts { get; set; } = 10;

    /// <summary>Başarısız girişlerin sayıldığı pencere (dakika).</summary>
    public int WindowMinutes { get; set; } = 15;

    /// <summary>Engelin süresi (dakika). 0 = kalıcı.</summary>
    public int BanMinutes { get; set; } = 60;

    /// <summary>Hiçbir zaman engellenmeyecek IP adresleri/aralıkları.</summary>
    public IReadOnlyCollection<string> WhitelistedIps { get; set; } = [];
}
