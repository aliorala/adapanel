namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Bir posta alan adı için otomatik yapılandırmanın İSTEMCİYE ULAŞMA durumu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden "uç var mı" değil de bu:</b> panelin autodiscover/autoconfig uçları çalışıyordu ama
/// istemciler oraya HİÇ gelmiyordu — müşterinin alan adı IIS'te 80/443'ten servis ediliyor, panel
/// ise ayrı bir portta. Yani ölçülmesi gereken şey ucun varlığı değil, isteğin GERÇEKTEN o uca
/// düşüp düşmediğidir. Bu DTO tam olarak o zincirin adımlarını taşır.
/// </para>
/// <para>
/// <b>"Bilmiyorum" ayrı bir durumdur.</b> Ölçülemeyen adım <c>unknown</c> döner; "ok" demek
/// kullanıcıyı çalışmayan bir kuruluma güvendirir.
/// </para>
/// </remarks>
public sealed class MailAutodiscoverPublicationDto
{
    public string Domain { get; init; } = string.Empty;

    /// <summary>Alan adını 80/443'ten servis eden IIS sitesi; bulunamadıysa boş.</summary>
    public string SiteName { get; init; } = string.Empty;

    /// <summary>Vekilin ilettiği yerel panel adresi (ör. https://localhost:8890).</summary>
    public string BackendUrl { get; init; } = string.Empty;

    /// <summary>Üç yönlendirme kuralının üçü de yerinde mi (yani istek panele düşüyor mu).</summary>
    public bool IsPublished { get; init; }

    /// <summary>Adım adım ölçüm: <c>ok</c> / <c>warn</c> / <c>fail</c> / <c>unknown</c>.</summary>
    public IReadOnlyList<MailAutodiscoverStepDto> Steps { get; init; } = [];

    /// <summary>İstemcilerin gerçekte çağıracağı adresler — elle doğrulama için.</summary>
    public IReadOnlyList<string> ProbeUrls { get; init; } = [];

    /// <summary>
    /// Alan adının bölgesi bu sunucudaki Windows DNS'ten yayınlanıyor mu. Yayınlanmıyorsa panel
    /// kaydı YAZAMAZ ve <see cref="RequiredDnsRecords"/> kullanıcıya ne yapması gerektiğini söyler.
    /// </summary>
    public bool DnsManagedByPanel { get; init; }

    /// <summary>
    /// Kullanıcının kendi DNS sağlayıcısında oluşturması gereken kayıtlar. Panel kaydı kendisi
    /// yazdıysa liste boştur — "yapıldı" ile "yapman gerekiyor" asla karıştırılmaz.
    /// </summary>
    public IReadOnlyList<MailAutodiscoverDnsRecordDto> RequiredDnsRecords { get; init; } = [];

    public DateTime CheckedAtUtc { get; init; }
}

/// <summary>Zincirin tek bir halkası.</summary>
public sealed class MailAutodiscoverStepDto
{
    /// <summary>Adım kimliği: <c>site</c>, <c>rewrite</c>, <c>binding</c>, <c>https</c>, <c>dns</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary><c>ok</c> / <c>warn</c> / <c>fail</c> / <c>unknown</c>.</summary>
    public string Status { get; init; } = "unknown";

    /// <summary>Kısa değer (site adı, kural sayısı, bulunan ana ad...).</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Ölçümün ham ayrıntısı — kullanıcı "neden" diye sorduğunda bakacağı yer.</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>Otomatik yapılandırma için gereken tek bir DNS kaydı.</summary>
public sealed class MailAutodiscoverDnsRecordDto
{
    public string Type { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}
