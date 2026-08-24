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

    /// <summary>
    /// Hedefin veritabanındaki KAYNAK adresi hedef adresle değiştirilebildi mi (ya da hiç
    /// gerekmedi mi). <c>false</c> ise hedef hâlâ kaynağın adresini servis eder — kullanıcıya
    /// söylenmesi gerekir, çünkü WordPress adresi veritabanından okur.
    /// </summary>
    public bool UrlRewritten { get; set; } = true;

    /// <summary>
    /// Kaynağın <c>wp-config.php</c>'si hedefin veritabanı bilgileriyle BİRLEŞTİRİLEBİLDİ mi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aynalama (<c>robocopy /MIR</c>) kaynağın wp-config'ini hedefe getirir; panel sonra hedefin
    /// kendi DB bilgilerini o dosyaya geri yazar. Geri yazım bir regex değiştirmesidir ve kaynak
    /// wp-config'i düz metin <c>define</c> kullanmıyorsa (<c>getenv()</c>, <c>$_ENV</c>,
    /// <c>require</c> edilen ayrı dosya) yapılamaz.
    /// </para>
    /// <para>
    /// <c>false</c> ⇒ birleştirme YAPILAMADI ve hedefin KENDİ wp-config'i, yıkıcı adımdan önce
    /// alınan yedekten geri konuldu: <b>hedef kendi veritabanına bağlı kalır</b> ama yalnız kaynağın
    /// wp-config'inde tanımlı ayarlar (WP_DEBUG, özel sabitler, salt'lar) TAŞINMAMIŞTIR. Sessiz
    /// bırakılamaz; denetleyici bu bayrağa göre farklı bir mesaj anahtarı döndürür.
    /// </para>
    /// <para>
    /// Normal koşulda hep <c>true</c>'dur: aynı ölçüm yıkıcı adımdan ÖNCE yapılır ve yazılamayacak
    /// bir kopyalama hiç başlatılmaz (<c>wp.copy_config_not_rewritable</c>).
    /// </para>
    /// </remarks>
    public bool ConfigRewritten { get; set; } = true;
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

    /// <summary>
    /// Klonun veritabanındaki KAYNAK adresi hedef adresle değiştirilebildi mi (ya da hiç gerekmedi
    /// mi). <c>false</c> ise klon ÜRETİM ADRESİNİ servis eder: staging'de yapılan işlem canlı
    /// siteye gider. Bu yüzden sonuç gizlenmez, mesaj anahtarı buna göre seçilir.
    /// </summary>
    public bool UrlRewritten { get; set; } = true;

    /// <summary>
    /// Klonun <c>wp-config.php</c>'sine YENİ veritabanı bilgileri yazılabildi mi.
    /// </summary>
    /// <remarks>
    /// <c>false</c> ⇒ klon, kopyalanan dosyalarla birlikte gelen KAYNAK yapılandırmasında kaldı:
    /// staging sandığı sitede yapılan her yazma <b>canlı veritabanına</b> gidiyor olabilir. Bu,
    /// adresin değişmemesinden (<see cref="UrlRewritten"/>) daha ağırdır ve mesaj seçiminde önceliği
    /// vardır. Sebebi ikisinden biridir: kopyalamada <c>wp-config.php</c> hiç oluşmadı, ya da dosya
    /// düz metin <c>define</c> kullanmıyor. İkincisi klonlama BAŞLAMADAN elenir
    /// (<c>wp.clone_config_not_rewritable</c>).
    /// </remarks>
    public bool ConfigRewritten { get; set; } = true;
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
