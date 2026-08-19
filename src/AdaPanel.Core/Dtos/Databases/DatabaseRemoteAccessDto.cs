namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Uzak veritabanı erişiminin TEK BİR veritabanı kullanıcısı için durumu.
/// </summary>
/// <remarks>
/// <para>
/// Ayar kullanıcı bazındadır çünkü erişim sınırı da kullanıcı bazındadır. Sunucu geneli tek bir
/// anahtar, çok kiracılı bir panelde bir müşterinin isteğiyle bütün müşterilerin veritabanlarını
/// aynı anda dışarı açardı.
/// </para>
/// <para>
/// <see cref="EngineHosts"/> panelin sakladığı değil, MOTORDAN OKUNAN gerçektir. Panelin kaydı
/// ile motorun durumu ayrışabilir (elle çalıştırılmış bir SQL, eski sürümden kalan bir hesap);
/// ekranda gösterilmesi gereken, gerçekte kimin bağlanabildiğidir.
/// </para>
/// </remarks>
public sealed class DatabaseUserRemoteAccessDto
{
    public string Engine { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    /// <summary>closed | allowlist | anywhere</summary>
    public string Mode { get; init; } = "closed";

    /// <summary>Normalleştirilmiş kaynak listesi (IP, CIDR ya da <c>10.0.%</c> deseni).</summary>
    public IReadOnlyCollection<string> AllowedOrigins { get; init; } = [];

    /// <summary>Motorda ŞU AN geçerli olan host/adres kayıtları (panelin kaydı değil).</summary>
    public IReadOnlyCollection<string> EngineHosts { get; init; } = [];

    /// <summary>Motorda fiilen uzaktan bağlanılabiliyor mu (yerel olmayan bir kayıt var mı).</summary>
    public bool RemoteOpen { get; init; }

    /// <summary>Bu kullanıcının yetkili olduğu veritabanları — kapsam/sahiplik burada görünür.</summary>
    public IReadOnlyCollection<string> Databases { get; init; } = [];
}

/// <summary>Bir veritabanı motorunun uzak erişim tablosu.</summary>
public sealed class DatabaseRemoteAccessEngineDto
{
    public string Engine { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool Installed { get; init; }

    /// <summary>Motor uzak erişim yönetimini destekliyor mu.</summary>
    public bool Supported { get; init; }

    /// <summary>mysqlHost | pgHba | none — arayüz mekanizmayı açıkça yazabilsin diye.</summary>
    public string Mechanism { get; init; } = "none";

    public string ServerHost { get; init; } = string.Empty;

    public int Port { get; init; }

    /// <summary>Windows Güvenlik Duvarı'nda bu port için panel kuralı var mı.</summary>
    public bool FirewallOpen { get; init; }

    /// <summary>Kuralın izin verdiği adresler ("Any" = her yer).</summary>
    public IReadOnlyCollection<string> FirewallAllowedAddresses { get; init; } = [];

    /// <summary>
    /// Motor tarafında ek bir engel varsa i18n anahtarı; yoksa boş.
    /// </summary>
    /// <remarks>
    /// Örnek: PostgreSQL yalnız yerel arayüzü dinliyorsa <c>pg_hba.conf</c> doğru olsa bile
    /// bağlantı kurulamaz. Panel bunu "açıldı" diye raporlarsa kullanıcı çalışmayan bir ayarı
    /// açık sanar.
    /// </remarks>
    public string WarningKey { get; init; } = string.Empty;

    public IReadOnlyCollection<DatabaseUserRemoteAccessDto> Users { get; init; } = [];
}

/// <summary>Kurulu bütün motorların uzak erişim görünümü.</summary>
public sealed class DatabaseRemoteAccessOverviewDto
{
    public IReadOnlyCollection<DatabaseRemoteAccessEngineDto> Engines { get; init; } = [];
}

/// <summary>Tek bir veritabanı kullanıcısının uzak erişimini değiştirir.</summary>
public sealed class UpdateDatabaseUserRemoteAccessRequest
{
    public string Engine { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    /// <summary>closed | allowlist | anywhere. Geçersiz değer reddedilir (sessizce "kapalı"ya düşmez).</summary>
    public string Mode { get; init; } = "closed";

    /// <summary>allowlist kipinde zorunlu: IP, CIDR ya da <c>10.0.%</c> deseni.</summary>
    public string[]? AllowedOrigins { get; init; }

    /// <summary>
    /// "Her yer" kipi için AÇIK onay. Onaysız istek reddedilir.
    /// </summary>
    /// <remarks>
    /// Veritabanını internetin tamamına açmak tek tıkla ve yan etki olarak olmamalıdır; kullanıcı
    /// ne yaptığını gördüğünü ayrıca beyan eder.
    /// </remarks>
    public bool ConfirmAnywhere { get; init; }

    /// <summary>
    /// Güvenlik duvarı kuralı da uygulansın mı.
    /// </summary>
    /// <remarks>
    /// Varsayılan HAYIR. Bir portu dışarı açmak, veritabanı yetkisi vermekten ayrı ve daha geniş
    /// sonuçlu bir karardır; panel bunu isteğin yan etkisi olarak SESSİZCE yapmaz. Kapalıyken
    /// motor tarafı uygulanır ve sonuç "erişim verildi ama port kapalı" olarak raporlanır.
    /// </remarks>
    public bool ApplyFirewall { get; init; }
}
