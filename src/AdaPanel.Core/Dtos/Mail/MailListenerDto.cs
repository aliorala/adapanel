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

/// <summary>
/// Dinleyici listesi <b>ve listenin OLCULUP OLCULMEDIGI</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayri bir bayrak:</b> "dinleyici YOK" ile "dinleyicileri OKUYAMADIM" ayni bos
/// listeye dusuyordu. Arayuz bos listeyi "dinleyici yok" diye yaziyor; yani yonetim arayuzune
/// baglanilamayan (CLI yok / parola okunamadi / sorgu dustu) bir sunucuda panel, 25/587/143'u
/// GERCEKTEN dinleyen bir posta sunucusu icin "hic dinleyici yok" diyordu. Bu, ayni sinifin
/// <see cref="MailQueueDto"/> ve <see cref="MailRelayDto"/> icin zaten kapatilmis olan
/// halidir; dinleyicilerde acik kalmisti.
/// </para>
/// </remarks>
public sealed class MailListenerSnapshotDto
{
    /// <summary>Posta sunucusu bu makinede kurulu mu?</summary>
    public bool Installed { get; set; }

    /// <summary>Liste GERCEKTEN okunabildi mi? <c>false</c> ise <see cref="Listeners"/> bos olsa da "yok" ANLAMINA GELMEZ.</summary>
    public bool Measured { get; set; }

    public IReadOnlyCollection<MailListenerDto> Listeners { get; set; } = [];
}
