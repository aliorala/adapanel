using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Dtos.Users;

/// <summary>
/// Kullanıcı silmenin GERÇEK sonucu: ne silindi, <b>ne kaldı</b>.
/// </summary>
/// <remarks>
/// <para>
/// NEDEN VAR (denetim Y7): uç eskiden <c>void</c> idi ve her koşulda <c>200 OK</c> dönüyordu.
/// Cascade'in "veritabanı düşürülemedi", "klasör kısmen silindi", "mail domaini kaldı" sonucu
/// okunmadan çöpe gidiyordu; ardından kullanıcı kaydı siliniyor ve o kalıntıların SAHİBİ ortadan
/// kalkıyordu. WHMCS bu çağrıya "success" deyip hizmeti iptal ediyor, kimse bir daha bakmıyordu.
/// </para>
/// <para>
/// Artık sonuç çağırana dönüyor ve <see cref="Completed"/> <c>false</c> ise uç <b>2xx dönmez</b>
/// (bkz. <c>UsersController.DeleteUserAsync</c>): hiçbir istemci bu yanıta bakıp "hizmet tamamen
/// kaldırıldı" sonucuna varamaz.
/// </para>
/// </remarks>
public sealed class UserDeletionResultDto
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Kullanıcı KAYDI silindi mi. Yanıt 409 olsa bile bu <c>true</c> olabilir: "iş yarım kaldı"
    /// ile "hiçbir şey olmadı" farklı şeylerdir ve istemcinin ikisini ayırt edebilmesi gerekir.
    /// </summary>
    public bool UserDeleted { get; set; }

    /// <summary>İstek sitelerin de silinmesini istedi mi (<c>removeSites</c>).</summary>
    public bool SiteRemovalRequested { get; set; }

    /// <summary>Cascade'in TAMAMEN sildiği siteler (adıyla).</summary>
    public List<string> DeletedSites { get; set; } = [];

    /// <summary>
    /// Sunucuda KALANLAR — hangi site, hangi veritabanı, hangi alan adı, hangi klasör.
    /// </summary>
    public List<UserDeletionLeftoverDto> Leftovers { get; set; } = [];

    /// <summary>
    /// İstenen iş eksiksiz yapıldı mı.
    /// </summary>
    /// <remarks>
    /// BİLİNÇLİ bırakılanlar (<c>removeSites=false</c> seçilip sitelerin sunucuda bırakılması,
    /// sahipliği kanıtlanamayan bir veritabanının korunması) bunu <c>false</c> YAPMAZ — istenen
    /// buydu ve yanıt bunları yine de adıyla listeler. Yalnızca <b>başarısız</b> kalıntılar
    /// (silinemeyen klasör, düşürülemeyen veritabanı) sonucu eksik yapar.
    /// </remarks>
    public bool Completed => Leftovers.All(leftover => leftover.Deliberate);
}

/// <summary>Kullanıcının ardında sunucuda kalan tek bir kaynak.</summary>
public sealed class UserDeletionLeftoverDto
{
    /// <summary>Hangi siteye aitti (site dışı kalıntılarda boş).</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Kaynak tipi — bkz. <see cref="SiteLeftoverKinds"/>.</summary>
    public string Kind { get; set; } = SiteLeftoverKinds.Other;

    /// <summary>Kaynağın ADI: <c>mysql:musteri_db</c>, <c>musteri.com</c>, <c>D:\web\musteri.com</c>…</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Neden kaldığı.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Panel bunu bilerek mi bıraktı (politika) yoksa BAŞARISIZ mı oldu.</summary>
    public bool Deliberate { get; set; }
}
