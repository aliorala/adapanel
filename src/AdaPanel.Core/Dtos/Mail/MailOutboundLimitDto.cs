namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Bir alan adının giden posta limiti.</summary>
public sealed class MailOutboundLimitDto
{
    public string Domain { get; set; } = string.Empty;

    /// <summary>Saatte gönderilebilecek en fazla ileti; 0 = limitsiz.</summary>
    public int MessagesPerHour { get; set; }

    /// <summary>
    /// Limit mail sunucusunda GERÇEKTEN kurulu mu.
    /// <para>
    /// Ayrı tutuluyor çünkü "0" iki ayrı şey demek olabilirdi: "limitsiz seçildi" ve "kurulamadı".
    /// İkisini karıştırmak, sınırsız sanılan bir sunucuda tek müşterinin IP'yi kara listeye
    /// düşürmesine yol açardı.
    /// </para>
    /// </summary>
    public bool Configured { get; set; }
}
