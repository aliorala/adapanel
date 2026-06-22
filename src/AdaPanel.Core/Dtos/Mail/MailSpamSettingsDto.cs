namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Stalwart global spam filtresi ayarları (SpamSettings singleton).
/// </summary>
public sealed class MailSpamSettingsDto
{
    public bool Installed { get; set; }

    public bool Configured { get; set; }

    /// <summary>Spam filtresi açık mı.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Bu puanın üzerindeki mesajlar Spam olarak işaretlenir.</summary>
    public double ScoreSpam { get; set; } = 5;

    /// <summary>Bu puanın üzerindeki mesajlar reddedilir (0 = kapalı).</summary>
    public double ScoreReject { get; set; }

    /// <summary>Bu puanın üzerindeki mesajlar sessizce silinir (0 = kapalı).</summary>
    public double ScoreDiscard { get; set; }

    public bool GreylistEnabled { get; set; }

    /// <summary>Greylist bekletme süresi (dakika).</summary>
    public int GreylistMinutes { get; set; } = 30;

    /// <summary>Adres defterindeki gönderenler asla spam sayılmaz.</summary>
    public bool TrustContacts { get; set; } = true;

    /// <summary>Kullanıcının gönderdiği mesajlara gelen yanıtlar asla spam sayılmaz.</summary>
    public bool TrustReplies { get; set; } = true;
}
