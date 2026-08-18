using AdaPanel.Core.Dtos.Billing;
using AdaPanel.Core.Enums;

namespace AdaPanel.Infrastructure.Billing;

/// <summary>
/// Faturalama sisteminin isteğiyle bir hesabın silinip silinemeyeceğine karar veren SAF kural.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı ve saf bir sınıf:</b> burada verilen karar geri alınamaz bir silmeye dönüşüyor.
/// Veritabanına, HTTP bağlamına ya da dosya sistemine bağlı olsaydı yalnızca uçtan uca bir düzenekle
/// sınanabilirdi; saf olduğu için her kural tek tek ve ucuzca ölçülebiliyor. Silme kararının testi
/// pahalıysa yazılmaz — bu depoda o bedel bir kez ödendi.
/// </para>
/// <para>
/// <b>Varsayılan REDDETMEKTİR.</b> Bilinmeyen her durum "silme" değil "dur" ile sonuçlanır. Yanlış
/// yönde hata yapmanın bedeli asimetriktir: gereksiz reddedilen bir iptal WHMCS'te elle
/// tamamlanabilir, yanlış silinen bir müşteri sitesi tamamlanamaz.
/// </para>
/// </remarks>
public static class BillingTerminationGuard
{
    /// <summary>Karar sebepleri — modül ve arayüz bu sabitleri çevirir.</summary>
    public const string ReasonOk = "ok";

    /// <summary>Panelde o adda hesap yok.</summary>
    public const string ReasonNotFound = "not_found";

    /// <summary>Aynı ada birden çok hesap uyuyor; hangisinin silineceği KANITLANAMIYOR.</summary>
    public const string ReasonAmbiguous = "ambiguous";

    /// <summary>Hesap müşteri değil (yönetici/bayi/operatör). Faturalama bunları asla silmemeli.</summary>
    public const string ReasonRoleNotCustomer = "role_not_customer";

    /// <summary>Hizmetin alan adı hesabın kapsamında yok; bu hesap o hizmete ait olmayabilir.</summary>
    public const string ReasonDomainMismatch = "domain_mismatch";

    /// <summary>Kararın konusu olan aday hesap (paneldeki gerçek kaydın gerekli alanları).</summary>
    /// <param name="UserId">Panel kullanıcı kimliği.</param>
    /// <param name="UserName">Panel kullanıcı adı.</param>
    /// <param name="Role">Panel rolü.</param>
    /// <param name="AssignedSites">Aboneliğe atanmış site/alan adları.</param>
    public sealed record Candidate(Guid UserId, string UserName, UserRole Role, IReadOnlyList<string> AssignedSites);

    /// <summary>
    /// <paramref name="userName"/> ile gelen iptal isteğinin güvenli olup olmadığına karar verir.
    /// </summary>
    /// <param name="candidates">Paneldeki hesaplar (çağıran filtrelemez; eşleştirme burada yapılır).</param>
    /// <param name="userName">WHMCS hizmetinin kullanıcı adı.</param>
    /// <param name="domain">
    /// WHMCS hizmetinin alan adı. Boş geçilirse alan adı denetimi UYGULANMAZ; bu bilinçli bir
    /// gevşemedir çünkü alan adı olmayan hizmetler (yalnız hesap satan paketler) vardır. Doluysa
    /// hesabın kapsamında BULUNMAK ZORUNDADIR.
    /// </param>
    public static BillingTerminationCheckDto Evaluate(
        IReadOnlyCollection<Candidate> candidates,
        string? userName,
        string? domain)
    {
        var aranan = (userName ?? string.Empty).Trim();
        if (aranan.Length == 0)
        {
            // Boş kullanıcı adı hiçbir hesapla eşleşmemeli. Aksi hâlde "hepsi eşleşir" gibi bir
            // yorum, tek çağrıyla toplu silmeye kapı açardı.
            return Reddet(ReasonNotFound, aranan);
        }

        var eslesenler = candidates
            .Where(aday => string.Equals(aday.UserName?.Trim(), aranan, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (eslesenler.Count == 0)
        {
            return Reddet(ReasonNotFound, aranan);
        }

        if (eslesenler.Count > 1)
        {
            // Panel kullanıcı adını benzersiz tutar; buraya düşmek veri bozulmasına işarettir.
            // Böyle bir anda "ilkini sil" demek, kura çekerek veri silmektir.
            return Reddet(ReasonAmbiguous, aranan);
        }

        var hesap = eslesenler[0];

        if (hesap.Role != UserRole.Customer)
        {
            // Faturalama sistemi YALNIZCA müşteri hesabı açar; başka bir rolü silmeye kalkması,
            // ya ad çakışması ya da yanlış yapılandırma demektir. İkisinde de doğru cevap "hayır".
            return Reddet(ReasonRoleNotCustomer, hesap.UserName, hesap.UserId);
        }

        var beklenen = (domain ?? string.Empty).Trim();
        if (beklenen.Length == 0)
        {
            return new BillingTerminationCheckDto
            {
                UserId = hesap.UserId,
                UserName = hesap.UserName,
                SafeToTerminate = true,
                Reason = ReasonOk,
                MatchedDomain = string.Empty
            };
        }

        var eslesenAlan = hesap.AssignedSites?.FirstOrDefault(site => AlanAdiEsit(site, beklenen));
        if (eslesenAlan is null)
        {
            // Hizmetin alan adı hesabın kapsamında yoksa bu hesabın o hizmete ait olduğu
            // KANITLANAMAZ. Kanıtsız silme yapılmaz.
            return Reddet(ReasonDomainMismatch, hesap.UserName, hesap.UserId);
        }

        return new BillingTerminationCheckDto
        {
            UserId = hesap.UserId,
            UserName = hesap.UserName,
            SafeToTerminate = true,
            Reason = ReasonOk,
            MatchedDomain = eslesenAlan
        };
    }

    /// <summary>
    /// Alan adı karşılaştırması: büyük/küçük harf ve baştaki "www." yok sayılır.
    /// </summary>
    /// <remarks>
    /// WHMCS'te hizmet "ornek.com" olarak sipariş edilir; panelde site adı "www.ornek.com" olabilir.
    /// Bunları farklı saymak, doğru hesabı "eşleşmedi" diye reddederdi — yani koruma, işi yapılamaz
    /// hâle getirip kendi devre dışı bırakılmasını davet ederdi.
    /// </remarks>
    private static bool AlanAdiEsit(string? site, string beklenen) =>
        string.Equals(Sadelestir(site), Sadelestir(beklenen), StringComparison.OrdinalIgnoreCase);

    private static string Sadelestir(string? deger)
    {
        var metin = (deger ?? string.Empty).Trim().TrimEnd('.');
        return metin.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? metin[4..] : metin;
    }

    private static BillingTerminationCheckDto Reddet(string sebep, string kullaniciAdi, Guid? kimlik = null) =>
        new()
        {
            UserId = kimlik,
            UserName = kullaniciAdi,
            SafeToTerminate = false,
            Reason = sebep,
            MatchedDomain = string.Empty
        };
}
