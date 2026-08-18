namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Site yedeğine GERÇEK posta (ileti) koyulabilir mi sorusunun ÖLÇÜLMÜŞ cevabı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ön kontrol var:</b> posta arşivi üretip geri yüklenemeyeceğini felaket anında
/// öğrenmek, hiç yedek almamaktan KÖTÜDÜR. Bu yüzden yedek almadan ÖNCE ölçülür ve ölçüm
/// başarısızsa arşiv "posta dahil" DEMEZ.
/// </para>
/// </remarks>
public sealed class MailArchivePreflightDto
{
    /// <summary>Posta iletileri gerçekten arşivlenebilir mi?</summary>
    public bool IsReady { get; init; }

    /// <summary>Hazır DEĞİLSE nedeni — kullanıcıya ve manifeste birebir bu metin gider.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Yoklanan JMAP oturum adresi (yedeğin kullanacağı adresin TA KENDİSİ).</summary>
    public string SessionUrl { get; init; } = string.Empty;

    /// <summary>Bulunan vandelay çalıştırılabiliri; yoksa boş.</summary>
    public string ToolPath { get; init; } = string.Empty;
}

/// <summary>Tek bir posta hesabının arşivi.</summary>
public sealed class MailAccountArchiveDto
{
    /// <summary>Hesabın tam adresi (kutu@alan).</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Üretilen SQLite arşivinin diskteki yolu.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Arşivlenen ileti sayısı (ölçülemezse -1).</summary>
    public int EmailCount { get; init; } = -1;
}

/// <summary>Bir alan adının posta arşivleme sonucu — KISMİ başarı dürüstçe taşınır.</summary>
public sealed class MailArchiveResultDto
{
    /// <summary>Başarıyla arşivlenen hesaplar.</summary>
    public IReadOnlyList<MailAccountArchiveDto> Accounts { get; init; } = [];

    /// <summary>Arşivlenemeyen hesaplar ve nedenleri.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>
    /// Alan adının postası EKSİKSİZ mi arşivlendi? Tek bir hesap bile düştüyse false — ve arşiv
    /// "posta dahil" demez. Yarım bir söz, hiç söz vermemekten kötüdür.
    /// </summary>
    public bool IsComplete => Accounts.Count > 0 && Failures.Count == 0;
}
