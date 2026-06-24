namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Stalwart mail sunucusunun bir ağ dinleyicisi (NetworkListener): hangi protokol için
/// hangi adres:port'larda dinlediği. Salt-okunur bilgi (panel "mail server IP" görünümü).
/// </summary>
public sealed class MailListenerDto
{
    public string Name { get; set; } = string.Empty;

    public string Protocol { get; set; } = string.Empty;

    /// <summary>Dinlenen adres:port'lar (örn. "[::]:25", "0.0.0.0:587").</summary>
    public IReadOnlyCollection<string> Binds { get; set; } = [];
}
