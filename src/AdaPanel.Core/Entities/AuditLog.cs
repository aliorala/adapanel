namespace AdaPanel.Core.Entities;

public sealed class AuditLog
{
    public long Id { get; set; }

    /// <summary>Eylemi YAPAN kullanıcı (yoksa "system").</summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Eylemin KONUSU olan kaynağın sahibi abonelik. Aktörden AYRI bir sorudur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gerekli:</b> kayıtlar yalnız aktöre göre kapsamlandığında, bir YÖNETİCİNİN kiracının
    /// sitesinde yaptığı işlem kiracının kendi denetim görünümünde HİÇ ÇIKMIYORDU — oysa "benim
    /// siteme kim dokundu" tam da kiracının sorması gereken sorudur ve kayıt bu soru için var.
    /// </para>
    /// <para>
    /// <b>Neden nullable:</b> her eylemin bir kiracı konusu yoktur (panel açılışı, oturum açma,
    /// sunucu geneli ayarlar). Ayrıca sütun eklenmeden ÖNCE yazılmış kayıtlarda NULL kalır ve
    /// NULL, görünürlüğü bugünküyle birebir aynı bırakır: eski kayıtlarda gerileme olmaz.
    /// </para>
    /// </remarks>
    public Guid? SubscriptionUserId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string Resource { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    public string? IpAddress { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

