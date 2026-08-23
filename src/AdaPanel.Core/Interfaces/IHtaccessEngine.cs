using AdaPanel.Core.Dtos.Htaccess;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// AdaPanel'in KENDİ <c>.htaccess</c> motoru. Apache'nin <c>mod_rewrite</c> + erişim denetimi +
/// başlık/önbellek direktiflerinin çalışma zamanında yorumlanan karşılığıdır.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR:</b> Plesk for Windows <c>.htaccess</c>'i HİÇ desteklemez ("IIS URL Rewrite
/// kullanın" der). WordPress, Laravel, Symfony ve on binlerce paylaşımlı barındırma müşterisi
/// <c>.htaccess</c> ile gelir. Bu motor, Linux'tan Windows'a taşınan bir siteyi elle
/// <c>web.config</c>'e çevirmeden çalıştırır.
/// </para>
/// <para>
/// <b>MOTOR İSTİSNA FIRLATMAZ.</b> Hiçbir üye <c>throw</c> etmez; başarısızlık her zaman
/// DÖNEN DEĞERDE bildirilir (<see cref="HtaccessEvaluationResult.EngineFailed"/>,
/// <see cref="HtaccessAuthResult.FailureCode"/>, <see cref="HtaccessDiagnostic"/>). Sebebi
/// basittir: bu kod barındırılan her sitenin her isteğinde çalışır ve motorun bir kusuru
/// isteği bozmamalıdır.
/// </para>
/// <para>
/// <b>Bu arayüz IIS bilmez.</b> Barındırma katmanı (ayrı bir modül) isteği
/// <see cref="HtaccessRequestContext"/>'e çevirir ve sonucu uygular.
/// </para>
/// </remarks>
public interface IHtaccessEngine
{
    /// <summary>
    /// Bir isteği değerlendirir: dizin basamaklandırmasını okur, kuralları uygular ve kararı döner.
    /// </summary>
    HtaccessEvaluationResult Evaluate(HtaccessRequestContext request);

    /// <summary>
    /// Bir dizinin (ve üstündeki bütün dizinlerin) <c>.htaccess</c> dosyalarını okur ve
    /// RAPORLAR. Panel ekranı için — istek değerlendirmez.
    /// </summary>
    /// <param name="siteRootPath">Sitenin belge kökü.</param>
    /// <param name="relativeDirectory">Site köküne göre dizin (<c>""</c>, <c>"blog"</c>).</param>
    HtaccessInspectionResult Inspect(string siteRootPath, string relativeDirectory = "");

    /// <summary>
    /// Kullanıcının panelde yazdığı metni KAYDETMEDEN ÖNCE doğrular. Diskte hiçbir şey okumaz.
    /// </summary>
    /// <remarks>
    /// Yalnızca SÖZ DİZİMİ ve direktif desteği bakılır: kapanmayan blok, kapanmayan tırnak,
    /// tanınmayan/uygulanmayan direktif. Desen riski, bayrak desteği ve erişim kuralı
    /// tutarsızlığı gibi derinlemesine bulgular <see cref="Inspect"/> ile gelir — çünkü onlar
    /// basamaklandırmanın tamamı birleştirildikten sonra anlamlıdır.
    /// </remarks>
    /// <param name="content"><c>.htaccess</c> içeriği.</param>
    /// <param name="virtualPath">Dosyanın kapsayacağı URL yolu (<c>/</c>, <c>/blog/</c>).</param>
    /// <param name="limits">Bütçe; <c>null</c> ise güvenli varsayılanlar.</param>
    HtaccessParseResult Validate(string content, string virtualPath = "/", HtaccessLimits? limits = null);

    /// <summary>
    /// <c>AuthUserFile</c> (htpasswd) üzerinden Basic Auth doğrulaması yapar.
    /// </summary>
    /// <remarks>
    /// Ayrı bir üyedir çünkü kimlik bilgisi <see cref="Evaluate"/>'in girdisi değildir:
    /// barındırma katmanı önce 401 sorgusunu alır, tarayıcı kimlik gönderince buraya döner.
    /// </remarks>
    HtaccessAuthResult Authenticate(HtaccessAuthChallenge challenge, string userName, string password, HtaccessLimits? limits = null);

    /// <summary>
    /// Sitenin ayrıştırma önbelleğini düşürür. Dosya değişikliği zaten damgalarla yakalanır;
    /// bu üye, panel dosyayı yazdıktan hemen sonra kesinlik için çağrılır.
    /// </summary>
    /// <param name="siteRootPath"><c>null</c> ya da boşsa BÜTÜN önbellek temizlenir.</param>
    void InvalidateCache(string? siteRootPath = null);
}
