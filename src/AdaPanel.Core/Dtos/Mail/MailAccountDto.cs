namespace AdaPanel.Core.Dtos.Mail;

public sealed class MailAccountDto
{
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Posta kutusu etkin mi. <c>null</c> = ÖLÇÜLEMEDİ (etkin DEĞİL demek değildir).
    /// </summary>
    /// <remarks>
    /// Üç değerli, çünkü iki ayrı gerçek var: "sunucu kapalı dedi" ile "sunucuya soramadık".
    /// Burada eskiden Stalwart tarafında SABİT <c>true</c> yazılıyordu — hesabın durumu HİÇ
    /// sorulmadan her posta kutusu yeşil "Etkin" rozeti alıyordu. Bir kutu gerçekten kapalıysa
    /// panel bunu göstermiyor, kullanıcı "panel etkin diyor ama posta gelmiyor" ile kalıyordu.
    /// Ölçemediğimizi söylemek, ölçmediğimiz bir şeyi doğru diye göstermekten iyidir.
    /// </remarks>
    public bool? Active { get; set; }

    public int MaxSizeMB { get; set; }

    /// <summary>Posta kutusunun mevcut kullanimi (MB).</summary>
    public int CurrentSizeMB { get; set; }
}
