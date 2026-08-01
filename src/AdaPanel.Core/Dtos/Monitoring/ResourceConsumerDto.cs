namespace AdaPanel.Core.Dtos.Monitoring;

/// <summary>
/// PAYLAŞILAN bir sürecin İÇİNDEKİ tek bir "tüketici" satırı.
///
/// NEDEN GEREKLİ: süreç listesi (<see cref="ProcessUsageDto"/>) "MariaDB %42 CPU" der ve orada durur —
/// çünkü MySQL/MariaDB/PostgreSQL TEK bir süreçtir ve tüm siteler onu paylaşır. O %42'nin hangi sitenin
/// sorgusundan geldiğini işletim sistemi ASLA bilemez; bunu yalnız motorun KENDİ oturum listesi söyler
/// (SHOW FULL PROCESSLIST / pg_stat_activity). IIS'te de w3wp app pool başına ayrıdır ama "şu an hangi
/// istek asılı" bilgisi yalnız <c>appcmd list requests</c> ile görülür. Bu DTO işte o kırılımdır.
/// </summary>
public sealed class ResourceConsumerDto
{
    /// <summary>Sabit kaynak türü anahtarı: "database" | "web". UI bunu i18n anahtarına çevirir.</summary>
    public string ResourceType { get; init; } = "database";

    /// <summary>Hangi kaynaktan geldi: "mysql" | "mariadb" | "postgresql" | "iis".</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Çözülebildiyse SİTE adı — bu ekranın ASIL DEĞERİ. Panelin site↔veritabanı bağından
    /// (config/site-databases.json) ya da IIS'in kendi site adından gelir. Çözülemezse null.
    /// </summary>
    public string? SiteName { get; init; }

    /// <summary>Teknik hedef: veritabanı adı ya da app pool / IIS site adı. Site bilinmiyorsa UI bunu gösterir.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>Bağlantıyı açan kullanıcı (DB kullanıcı adı). IIS satırlarında boştur.</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>İstemcinin adresi/host'u (DB: HOST sütunu, IIS: client IP). Boş olabilir.</summary>
    public string ClientHost { get; init; } = string.Empty;

    /// <summary>Bu işlem KAÇ SANİYEDİR sürüyor. Uzun süre = asılı kalmış/ağır sorgu.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Motorun bildirdiği ham durum ("Sending data", "active", "ExecuteRequestHandler" vb.).</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>İşlem türü: DB'de COMMAND ("Query", "Execute"), IIS'te HTTP fiili ("GET", "POST").</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>KISALTILMIŞ işlem metni: SQL sorgusu ya da istek URL'i. Parola içerenler maskelenmiştir.</summary>
    public string Statement { get; init; } = string.Empty;

    /// <summary>true ise <see cref="Statement"/> içindeki değişmez metinler gizlilik için maskelendi.</summary>
    public bool Redacted { get; init; }

    /// <summary>Motorun bağlantı/istek kimliği (aynı satırı iki ölçüm arasında eşlemek için).</summary>
    public string ConnectionId { get; init; } = string.Empty;
}

/// <summary>
/// Tek bir kaynağın anlık tüketici listesi + o kaynağın DURUMU.
/// Kurulu olmayan motor HATA DEĞİLDİR: <see cref="Installed"/>=false ile sakin bir mesaj döner,
/// böylece UI "MySQL yok" ile "MySQL var ama sorgulanamadı" durumlarını ayırt edebilir.
/// </summary>
public sealed class ResourceConsumerSnapshotDto
{
    public string Source { get; init; } = string.Empty;

    /// <summary>Okunabilir kaynak adı: "MariaDB", "MySQL", "PostgreSQL", "IIS".</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Motor/rol bu sunucuda kurulu mu.</summary>
    public bool Installed { get; init; }

    /// <summary>Kurulu VE bu turda gerçekten sorgulanabildi mi (false ise liste güvenilmez, boş demek değil).</summary>
    public bool Available { get; init; }

    /// <summary>
    /// Sabit durum anahtarı — UI i18n'e çevirir, ASLA çevrilmiş metin dönmez:
    /// "" (sorunsuz) | "not_installed" | "credentials_missing" | "unavailable".
    /// </summary>
    public string StatusCode { get; init; } = string.Empty;

    public DateTime CapturedAtUtc { get; init; }

    /// <summary>Kırpmadan önceki toplam aktif tüketici sayısı.</summary>
    public int TotalCount { get; init; }

    public IReadOnlyCollection<ResourceConsumerDto> Consumers { get; init; } = [];
}
