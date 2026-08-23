namespace AdaPanel.Core.Dtos.Htaccess;

/// <summary>
/// Motorun bir isteği değerlendirmek için ihtiyaç duyduğu HER ŞEY. Motorun IIS'e, HttpContext'e
/// ya da herhangi bir barındırma tipine bağımlılığı YOKTUR; barındırma katmanı bu nesneyi doldurur.
/// </summary>
/// <remarks>
/// Bu ayrım bilinçlidir: motor saf .NET olduğu için testlerde gerçek bir web sunucusu olmadan,
/// gerçek <c>.htaccess</c> dosyalarıyla ve gerçek isteklerle sınanabiliyor.
/// </remarks>
public sealed record HtaccessRequestContext
{
    /// <summary>
    /// Sitenin BELGE KÖKÜ (fiziksel yol). Motorun dokunabileceği alanın sınırı budur:
    /// hiçbir kural, hiçbir <c>ErrorDocument</c>, hiçbir <c>AuthUserFile</c> bunun dışına çıkamaz.
    /// </summary>
    public string SiteRootPath { get; init; } = string.Empty;

    /// <summary>
    /// İstenen URL yolu, <b>yüzde-çözülmüş</b>, başında <c>/</c> ile: <c>/blog/yazi-1</c>.
    /// Sorgu dizesi buraya DAHİL DEĞİLDİR.
    /// </summary>
    public string RequestPath { get; init; } = "/";

    /// <summary>Sorgu dizesi, baştaki <c>?</c> OLMADAN. Yoksa boş.</summary>
    public string QueryString { get; init; } = string.Empty;

    /// <summary><c>GET</c>, <c>POST</c>, ...</summary>
    public string Method { get; init; } = "GET";

    /// <summary><c>%{HTTP_HOST}</c> — istekteki Host başlığı (port dahil olabilir).</summary>
    public string HttpHost { get; init; } = string.Empty;

    /// <summary><c>%{SERVER_NAME}</c> — sunucunun bu site için yapılandırılmış adı.</summary>
    public string ServerName { get; init; } = string.Empty;

    public int ServerPort { get; init; } = 80;

    /// <summary><c>%{HTTPS}</c> — <c>on</c>/<c>off</c> buradan türetilir.</summary>
    public bool IsHttps { get; init; }

    /// <summary><c>%{REMOTE_ADDR}</c>. Erişim denetimi (<c>Deny from</c>, <c>Require ip</c>) buna bakar.</summary>
    public string RemoteAddress { get; init; } = string.Empty;

    /// <summary>
    /// <c>%{HTTP:...}</c> ve <c>%{HTTP_USER_AGENT}</c> gibi değişkenlerin kaynağı.
    /// Anahtarlar başlık adlarıdır (<c>User-Agent</c>); arama büyük/küçük harf duyarsızdır.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kimliği doğrulanmış kullanıcı (<c>%{REMOTE_USER}</c>). Henüz doğrulanmadıysa boş.
    /// </summary>
    public string RemoteUser { get; init; } = string.Empty;

    /// <summary>
    /// Sitenin kök URL'i altında bir alt yolda barındırıldığı durumlarda ön ek
    /// (IIS sanal dizini). Olağan durumda boştur.
    /// </summary>
    public string ApplicationPath { get; init; } = string.Empty;

    /// <summary>
    /// Bütçe. <c>null</c> ise <see cref="HtaccessLimits.Default"/> kullanılır — barındırma katmanı
    /// unutsa bile sınırsız çalışma OLMAZ.
    /// </summary>
    public HtaccessLimits? Limits { get; init; }
}
