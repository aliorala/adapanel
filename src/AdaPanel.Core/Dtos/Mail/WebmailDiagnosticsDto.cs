namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Webmail (Roundcube) tanılama sonucu: panelin kendi kendini denetleyip "neden açılmıyor"u söylemesi için.
/// IIS site/binding durumu, FastCGI sürecinde GERÇEKTEN yüklü PHP eklentileri, Roundcube kök yanıtı ve hata
/// günlüğü tek bir okunur rapora derlenir (kullanıcı/destek bunu paylaşıp kök nedeni anında görür).
/// </summary>
public sealed class WebmailDiagnosticsDto
{
    /// <summary>Roundcube kökü (/) açılıyor mu (genel sonuç).</summary>
    public bool Healthy { get; set; }

    /// <summary>Çok satırlı tanılama raporu (panelde &lt;pre&gt; olarak gösterilir).</summary>
    public string Report { get; set; } = string.Empty;

    /// <summary>Webmail adresi (kurulmuşsa).</summary>
    public string? Url { get; set; }
}
