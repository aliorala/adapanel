namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Her yedek isteğinde ortak olan HEDEF ve ŞİFRELEME alanları.
/// <para>
/// Bu alanlar dört ayrı istek sınıfında (site/veritabanı/mail/panel) birebir kopyalanmıştı; her yeni
/// alan dört yere birden eklenmek zorundaydı ve biri unutulunca o yedek türü sessizce özellikten
/// mahrum kalıyordu. Tek kaynak burasıdır.
/// </para>
/// </summary>
public abstract class BackupTargetRequest
{
    /// <summary>"local", "ftp", "s3", "webdav" ya da "http".</summary>
    public string TargetType { get; set; } = "local";

    public string RemoteUrl { get; set; } = string.Empty;

    public string RemoteUserName { get; set; } = string.Empty;

    public string RemotePassword { get; set; } = string.Empty;

    /// <summary>S3 için bölge (örn. eu-central-1). Boşsa us-east-1 varsayılır.</summary>
    public string RemoteRegion { get; set; } = string.Empty;

    /// <summary>
    /// Yedeği AES-256-GCM ile şifreleyecek parola. Boş bırakılırsa panelde KAYITLI parola
    /// kullanılır (zamanlanmış yedekler böyle çalışır); o da yoksa yedek ŞİFRESİZ alınır.
    /// <para>
    /// <b>UYARI — PAROLA KAYBOLURSA YEDEK GERİ GETİRİLEMEZ.</b> Panelin kurtarma kapısı yoktur:
    /// anahtar yalnızca bu paroladan (PBKDF2-SHA256, 600.000 tur, kayıt başına rastgele tuz)
    /// türetilir. Parolanın panelin dışında da bir kopyası olmalıdır.
    /// </para>
    /// <para>
    /// Uzak hedefe (FTP/S3/WebDAV) gönderilen yedeklerde şifreleme ÖNERİLİR: aksi hâlde veritabanı
    /// dökümü ve posta verisi üçüncü taraf depoda düz metin durur.
    /// </para>
    /// </summary>
    public string EncryptionPassword { get; set; } = string.Empty;
}
