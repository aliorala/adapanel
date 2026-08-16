namespace AdaPanel.Core.Dtos.Backup;

public sealed class BackupRecordDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public long Size { get; set; }

    public string Status { get; set; } = string.Empty;

    public string TargetType { get; set; } = "local";

    public string RemoteUri { get; set; } = string.Empty;

    /// <summary>
    /// Yedek dosyası AES-256-GCM ile şifreli mi (dosya adı <c>.enc</c> ile biter).
    /// <para>
    /// Bu alan EKRAN İÇİNDİR; geri yüklerken biçim kararı dosyanın kendi başlığından verilir
    /// (eski/elle kopyalanmış dosyalarda kayıt yanılabilir, dosya başlığı yanılmaz).
    /// </para>
    /// <para>
    /// <b>Şifreliyse geri yükleme parola ister ve PAROLA KAYBOLURSA YEDEK GERİ GETİRİLEMEZ.</b>
    /// </para>
    /// </summary>
    public bool Encrypted { get; set; }

    /// <summary>
    /// Son geri yüklemenin YARIM KALAN adımları. Boş değilse <see cref="Status"/> da
    /// <c>restored_with_errors</c> olur — "restored" yazıp sorunu yutmak yasaktır.
    /// </summary>
    public List<BackupRestoreWarningDto> Warnings { get; set; } = [];

    /// <summary>
    /// Geri yükleme üzerine yazmadan önce sitenin O ANKİ içeriğinin taşındığı kardeş klasör.
    /// Kullanıcının "geri yükleme yanlış yedekti" dediğinde döneceği tek yer burasıdır.
    /// </summary>
    public string PreservedPath { get; set; } = string.Empty;

    // =================================================================================
    // ARTIMSAL (INCREMENTAL) ZİNCİR — Plesk'in kanıtlanmış modeli.
    //
    // Plesk'te bir TAM yedek + ardından gelen ARTIMSAL yedekler bir ZİNCİR oluşturur:
    // artimsal yedek yalnızca "son yedekten beri DEĞİŞEN"i taşır, geri yükleme ise tam
    // yedekten başlayıp halkaları SIRAYLA uygular. Halkalar birbirine BAĞIMLIDIR:
    // ortadaki bir halka giderse ondan SONRAKİ bütün halkalar çöpe döner.
    //
    // Bu alanlar eski kayıtlarda BOŞ kalır ve boş değerler "zincirsiz tekil tam yedek"
    // anlamına gelir — geriye dönük uyumluluk bu yüzden kendiliğinden sağlanır.
    // =================================================================================

    /// <summary>
    /// Zincir kimliği. Aynı <c>ChainId</c>'yi taşıyan kayıtlar TEK bir geri yükleme zinciridir.
    /// Boş ise kayıt zincire ait değildir (tekil, kendi başına yeterli tam yedek).
    /// </summary>
    public string ChainId { get; set; } = string.Empty;

    /// <summary>
    /// Zincirdeki sıra: <c>0</c> TAM yedek, <c>1..n</c> artımsal halkalar. Geri yükleme bu
    /// sıraya göre uygulanır; sıra bozulursa eski dosya yenisinin üzerine yazılır.
    /// </summary>
    public int ChainIndex { get; set; }

    /// <summary>"full" (tam) veya "incremental" (artımsal). Varsayılan tam.</summary>
    public string BackupMode { get; set; } = "full";

    /// <summary>
    /// Bu artımsal halkanın DAYANDIĞI bir önceki halkanın kimliği (tam yedekte boş).
    /// Zincirin kopup kopmadığı bu bağdan ölçülür.
    /// </summary>
    public string BaseBackupId { get; set; } = string.Empty;

    /// <summary>
    /// Abonelik yedeğinde sahibinin kullanıcı kimliği ("N" biçimi). Kiracı izolasyonunun
    /// kaydı: bir aboneliğin yedeği yalnızca kendi sahibine gösterilir/geri yüklenir.
    /// </summary>
    public string SubscriptionUserId { get; set; } = string.Empty;

    /// <summary>
    /// Yedeğin İÇİNDEKİ mantıksal öğe sayısı (site, veritabanı, posta alanı, FTP hesabı).
    /// Abonelik yedeğinde ekranda "8 öğe" demeyi mümkün kılar; 0 = ölçülmedi.
    /// </summary>
    public int ItemCount { get; set; }
}
