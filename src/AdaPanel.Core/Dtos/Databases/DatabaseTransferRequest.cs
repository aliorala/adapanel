namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Sunucu taraflı veritabanı taşıma (profesyonel paneller gibi): kaynak (uzak olabilir) MySQL/MariaDB'den
/// mysqldump ile alıp yerel hedef veritabanına aktarır. Tarayıcı upload'ı yok → 413/timeout yaşanmaz.
/// </summary>
public sealed class DatabaseTransferRequest
{
    /// <summary>Hedef motor: "mariadb" veya "mysql" (yerel).</summary>
    public string Engine { get; set; } = "mariadb";

    public string SourceHost { get; set; } = string.Empty;

    public int SourcePort { get; set; } = 3306;

    public string SourceUserName { get; set; } = string.Empty;

    public string SourcePassword { get; set; } = string.Empty;

    public string SourceDatabase { get; set; } = string.Empty;

    /// <summary>
    /// Kaynağa bağlanırken şifreleme: <c>auto</c> (varsayılan) | <c>required</c> | <c>disabled</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>auto</c>: kaynak UZAKSA şifreleme zorunlu, loopback'te gereksiz (araya girecek ağ yok).
    /// <c>disabled</c> kullanıcının BİLİNÇLİ tercihidir — TLS desteklemeyen eski bir sunucudan
    /// göç ediyor olabilir ve bu engellenmez.
    /// </para>
    /// <para>
    /// Boş bırakılırsa <c>auto</c> sayılır: eski istemciler bu alanı hiç göndermez ve onların da
    /// varsayılanı GÜVENLİ olmalıdır. Karar <c>MySqlTransferTls</c>te.
    /// </para>
    /// </remarks>
    public string SourceTlsMode { get; set; } = string.Empty;

    /// <summary>Boş bırakılırsa kaynak veritabanı adı kullanılır.</summary>
    public string TargetDatabase { get; set; } = string.Empty;

    /// <summary>Hedef DB'ye erişecek kullanıcı (taşınan uygulamanın bağlanacağı). Boşsa DB adından türetilir;
    /// oluşturulup hedef DB'ye tam yetki alır.</summary>
    public string TargetUserName { get; set; } = string.Empty;

    /// <summary>Hedef kullanıcının parolası. Boşsa güçlü bir parola üretilir ve sonuçta gösterilir.</summary>
    public string TargetPassword { get; set; } = string.Empty;

    public bool AdaptDefiners { get; set; }
}
