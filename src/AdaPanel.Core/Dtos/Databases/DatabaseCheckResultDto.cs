namespace AdaPanel.Core.Dtos.Databases;

/// <summary>Bir tablonun denetim sonucu.</summary>
public sealed class DatabaseTableCheckDto
{
    public string Table { get; init; } = string.Empty;

    /// <summary>
    /// Depolama motoru (InnoDB, MyISAM, Aria...). Ekranda GÖSTERİLMESİ ZORUNLU: kullanıcının bazı
    /// tablolarda neden "Onar" düğmesi olmadığını anlamasının tek yolu budur.
    /// </summary>
    public string Engine { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    /// <summary>"healthy" | "problem" | "unsupported" | "unreadable"</summary>
    public string Status { get; init; } = "healthy";

    /// <summary>
    /// Bu tablo onarılabilir mi. YALNIZCA onarımı gerçekten destekleyen motorlarda true olur —
    /// InnoDB'de asla. Bkz. <see cref="DatabaseCheckResultDto"/> açıklaması.
    /// </summary>
    public bool CanRepair { get; init; }

    /// <summary>
    /// Yeniden inşa edilebilir mi (InnoDB). Bu bir ONARIM DEĞİLDİR ve ekranda da öyle
    /// adlandırılmamalıdır; tabloyu yeniden yazar, boş alanı iade eder, istatistikleri günceller.
    /// </summary>
    public bool CanRebuild { get; init; }

    /// <summary>Sunucunun döndürdüğü ham mesajlar ("error: ...", "note: ...").</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];
}

/// <summary>
/// Bir veritabanının denetim/onarım sonucu.
/// <para>
/// TASARIM KARARI — <c>mysqlcheck.exe</c> KULLANILMIYOR, SQL doğrudan çalıştırılıyor. Gerekçeler
/// ölçülebilir ve belgeli:
/// </para>
/// <list type="number">
/// <item>
/// mysqlcheck'in ÇIKIŞ KODU GÜVENİLMEZ: bozulma tespit edilse bile 0 döner (MySQL Bug #43555,
/// 2009'dan beri "Verified" ve açık). MySQL geliştiricilerinin kendi tavsiyesi, çıktı yerine
/// SQL sonuç kümesini okumaktır (Bug #101239, "Won't fix").
/// </item>
/// <item>
/// Metin çıktısının biçimi belgelenmemiştir ve hizalama boşluklarıyla kırılgandır. SQL yolunda
/// sonuç 4 sütunlu YAPISAL bir kümedir: Table, Op, Msg_type, Msg_text.
/// </item>
/// <item>
/// Parola komut satırına yazılmak zorunda kalırdı; Windows'ta komut satırı her yerel kullanıcı
/// tarafından okunabilir — paylaşımlı bir sunucuda bu, veritabanı yönetici parolasının
/// müşterilere sızması demektir.
/// </item>
/// <item>
/// MariaDB 10.5+ ile araç adı <c>mariadb-check.exe</c> oldu; ikili adı aramak fazladan bir
/// kırılganlık kaynağı.
/// </item>
/// </list>
/// <para>
/// EN ÖNEMLİ KURAL — InnoDB'ye ASLA <c>REPAIR TABLE</c> gönderilmez. REPAIR yalnızca MyISAM,
/// Aria, ARCHIVE ve CSV motorlarında çalışır. InnoDB'de çağrılırsa HATA VERMEZ; şunu döner:
/// <c>note: The storage engine for the table doesn't support repair</c> ardından
/// <c>status: OK</c>. Yani hiçbir şey yapılmamışken sonuç "OK" görünür. Modern kurulumların
/// neredeyse tamamı InnoDB olduğundan, motora bakmadan onarım çalıştıran bir panel
/// kullanıcılarının çoğuna "onardım" der ve HİÇBİR ŞEY YAPMAZ. Bu, bu özelliğin bir numaralı
/// hatasıdır; motor <c>information_schema</c>'dan ÖNCEDEN okunur ve karar ona göre verilir.
/// </para>
/// </summary>
public sealed class DatabaseCheckResultDto
{
    public string Engine { get; init; } = string.Empty;

    public string Database { get; init; } = string.Empty;

    public IReadOnlyList<DatabaseTableCheckDto> Tables { get; init; } = [];

    public int HealthyCount => Tables.Count(item => item.Status == "healthy");

    public int ProblemCount => Tables.Count(item => item.Status == "problem");

    /// <summary>Denetlenemeyen tablolar (motor desteklemiyor ya da tablo okunamadı).</summary>
    public int SkippedCount => Tables.Count(item => item.Status is "unsupported" or "unreadable");

    /// <summary>
    /// Onarım çalıştırıldıysa true. "Onarıldı" sözü YALNIZCA gerçekten bir onarım ifadesi
    /// çalıştıysa VE sonraki denetim temiz döndüyse kullanılır.
    /// </summary>
    public bool RepairAttempted { get; init; }
}
