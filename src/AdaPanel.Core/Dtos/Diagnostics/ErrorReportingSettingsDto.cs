namespace AdaPanel.Core.Dtos.Diagnostics;

/// <summary>
/// Hata bildirimi ayarları (<c>config/error-reporting.json</c>). Onay KURULUMDA BİR KEZ alınır;
/// <see cref="ConsentGrantedAtUtc"/> o anı saklar, sonrasında kullanıcıya hata başına sorulmaz.
/// </summary>
public sealed class ErrorReportingSettingsDto
{
    /// <summary>Kullanıcı kurulumda onay verdi mi. False ise kayıtlar yerelde kalır, gönderim olmaz.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Toplayıcı adresi. BOŞSA gönderim YAPILMAZ — kayıtlar yerelde birikir. Böylece adres
    /// yapılandırılmamış bir kurulumda hiçbir şey dışarı çıkmaz (varsayılan güvenli durum).
    /// </summary>
    public string CollectorUrl { get; set; } = string.Empty;

    public DateTime? ConsentGrantedAtUtc { get; set; }

    /// <summary>Tek gönderimde kaç kayıt yollanır (1..50 aralığına kırpılır).</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Gönderilmiş kayıtların yerelde tutulma süresi.</summary>
    public int RetentionDays { get; set; } = 30;

    public DateTime? LastSentAtUtc { get; set; }

    /// <summary>Son gönderim hatası (yalnız yerel teşhis için; dışarı gönderilmez).</summary>
    public string LastError { get; set; } = string.Empty;
}
