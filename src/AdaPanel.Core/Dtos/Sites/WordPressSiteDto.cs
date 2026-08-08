namespace AdaPanel.Core.Dtos.Sites;

/// <summary>panel WordPress Toolkit benzeri: bir sitedeki WordPress kurulumunun durumu.</summary>
public sealed class WordPressSiteDto
{
    public string SiteName { get; set; } = string.Empty;

    public bool IsWordPress { get; set; }

    public string Version { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>Bakım modu (.maintenance dosyası) açık mı.</summary>
    public bool MaintenanceMode { get; set; }

    public string AdminUrl { get; set; } = string.Empty;

    /// <summary>Kurulu eklentiler (wp-content/plugins, dosya başlıklarından).</summary>
    public IReadOnlyCollection<WordPressComponentDto> Plugins { get; set; } = [];

    /// <summary>Kurulu temalar (wp-content/themes/*/style.css başlıklarından).</summary>
    public IReadOnlyCollection<WordPressComponentDto> Themes { get; set; } = [];

    /// <summary>wordpress.org'dan öğrenilen en güncel kararlı çekirdek sürümü (boş = bilinmiyor).</summary>
    public string LatestVersion { get; set; } = string.Empty;

    /// <summary>Kurulu sürüm en güncel sürümün gerisinde mi (çekirdek güncellemesi var).</summary>
    public bool UpdateAvailable { get; set; }

    /// <summary>Güvenlik önlemlerinin mevcut durumu (Plesk "Güvenlik" sekmesi gibi; dosya tabanlı okunur).</summary>
    public WordPressSecurityDto Security { get; set; } = new();
}

/// <summary>WordPress güvenlik önlemlerinin açık/kapalı durumu (HardenAsync ile uygulananların yansıması).</summary>
public sealed class WordPressSecurityDto
{
    /// <summary>wp-config'te DISALLOW_FILE_EDIT true mı (tema/eklenti dosya düzenleyici kapalı mı).</summary>
    public bool FileEditorDisabled { get; set; }

    /// <summary>AdaPanel güvenlik mu-plugin'i var mı → sürüm gizleme aktif mi.</summary>
    public bool VersionHidden { get; set; }

    /// <summary>Güvenlik mu-plugin'i var mı → XML-RPC pingback kapalı mı.</summary>
    public bool XmlRpcPingbackDisabled { get; set; }

    /// <summary>Güvenlik mu-plugin'i var mı → yazar taraması (?author=N) engelli mi.</summary>
    public bool AuthorScanBlocked { get; set; }
}

/// <summary>WordPress eklenti/tema bileşeni (ad + sürüm + slug + etkin mi).</summary>
public sealed class WordPressComponentDto
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>Bileşen etkin mi (DB'den wp-cli ile öğrenilir; bilinemezse false).</summary>
    public bool Active { get; set; }
}

public sealed class SetWordPressMaintenanceRequest
{
    public bool Enabled { get; set; }
}

/// <summary>Tek-tık WordPress yönetici girişi: tek kullanımlık, kısa ömürlü sihirli bağlantı.</summary>
public sealed class WordPressLoginLinkDto
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Bağlantının geçerlilik süresi (saniye).</summary>
    public int ExpiresInSeconds { get; set; }
}

/// <summary>panel "Veri Kopyala": kaynak WP'nin dosya/DB'sini VAR OLAN bir hedefe (ör. staging→canlı) taşıma isteği.</summary>
public sealed class CopyWordPressDataRequest
{
    /// <summary>Hedef sitenin adı (var olan bir WordPress sitesi olmalı).</summary>
    public string TargetSiteName { get; set; } = string.Empty;

    /// <summary>Dosyalar kopyalansın mı (hedef wp-config DB bilgileri korunur).</summary>
    public bool Files { get; set; } = true;

    /// <summary>Veritabanı kopyalansın mı (kaynak veriler hedefin kendi DB'sine aktarılır).</summary>
    public bool Database { get; set; } = true;
}

/// <summary>Veri kopyalama sonucu: hedef + alınan yedek yolu + neyin kopyalandığı.</summary>
public sealed class WordPressCopyDataResultDto
{
    public string TargetSiteName { get; set; } = string.Empty;

    public string TargetUrl { get; set; } = string.Empty;

    /// <summary>Kopyalamadan ÖNCE alınan hedef yedeğinin klasörü (geri dönüş için).</summary>
    public string BackupPath { get; set; } = string.Empty;

    public bool CopiedFiles { get; set; }

    public bool CopiedDatabase { get; set; }
}

/// <summary>panel "Klonla": WordPress sitesini yeni bir hedefe (staging) kopyalama isteği.</summary>
public sealed class CloneWordPressRequest
{
    /// <summary>Oluşturulacak hedef sitenin adı (IIS site adı).</summary>
    public string TargetSiteName { get; set; } = string.Empty;

    /// <summary>Hedef sitenin host adı (ör. staging.ornek.com).</summary>
    public string TargetHostName { get; set; } = string.Empty;
}

/// <summary>Güvenlik sıkılaştırma sonucu: uygulanan önlemlerin kodları (frontend etiketler).</summary>
public sealed class WordPressHardenResultDto
{
    public IReadOnlyList<string> Applied { get; set; } = [];
}

/// <summary>Klon sonucu: oluşan hedef site + (uygulamanın bağlandığı) yeni veritabanı bilgileri.</summary>
public sealed class WordPressCloneResultDto
{
    public string TargetSiteName { get; set; } = string.Empty;

    public string TargetUrl { get; set; } = string.Empty;

    public string TargetDatabase { get; set; } = string.Empty;

    public string DatabaseUser { get; set; } = string.Empty;

    public string DatabasePassword { get; set; } = string.Empty;
}

/// <summary>
/// Bir WordPress kurulumundaki güncellenebilir öğe (çekirdek, eklenti ya da tema).
/// </summary>
public sealed class WordPressUpdatableDto
{
    /// <summary>"core" | "plugin" | "theme".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Teknik ad (wp-cli'nin beklediği slug).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Kullanıcıya gösterilecek ad.</summary>
    public string Title { get; set; } = string.Empty;

    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>Güncelleme varsa yeni sürüm; yoksa boş.</summary>
    public string AvailableVersion { get; set; } = string.Empty;

    public bool UpdateAvailable { get; set; }

    /// <summary>"active" | "inactive" | "must-use" vb. (çekirdekte boş).</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>Bir sitenin güncelleme durumu.</summary>
public sealed class WordPressUpdatesDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// Liste OKUNABİLDİ mi. false ise aşağıdaki liste boş olabilir ama bu "güncelleme yok"
    /// DEMEK DEĞİLDİR — WordPress'in veritabanına ulaşılamamış ya da wp-cli çalışamamış olabilir.
    /// Ölçememeyi "her şey güncel" saymak, güvenlik açığı olan bir siteyi güvende göstermek olurdu.
    /// </summary>
    public bool Measured { get; set; }

    /// <summary>Ölçülemediyse sebebi (ham çıktı; ekranda ayrıntı olarak gösterilir).</summary>
    public string Error { get; set; } = string.Empty;

    public IReadOnlyCollection<WordPressUpdatableDto> Items { get; set; } = [];
}

/// <summary>Güncellenecek öğeler.</summary>
public sealed class WordPressUpdateRequest
{
    /// <summary>Çekirdeği güncelle.</summary>
    public bool Core { get; set; }

    /// <summary>Güncellenecek eklenti slug'ları.</summary>
    public IReadOnlyCollection<string> Plugins { get; set; } = [];

    /// <summary>Güncellenecek tema slug'ları.</summary>
    public IReadOnlyCollection<string> Themes { get; set; } = [];
}

/// <summary>Güncelleme sonucu — her öğe için ayrı, çünkü biri başarısız olsa diğerleri geçerlidir.</summary>
public sealed class WordPressUpdateResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public IReadOnlyCollection<WordPressUpdateItemResultDto> Results { get; set; } = [];
}

public sealed class WordPressUpdateItemResultDto
{
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Success { get; set; }

    /// <summary>Başarısızsa wp-cli'nin çıktısı (kısaltılmış).</summary>
    public string Message { get; set; } = string.Empty;
}
