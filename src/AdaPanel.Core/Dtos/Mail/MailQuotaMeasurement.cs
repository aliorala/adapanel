using AdaPanel.Core.Common;

namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Posta kotasi dayatilmadan ONCE sorulan tek soru: <b>sayabildik mi?</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN TEK KAYNAK.</b> Posta kotasi iki AYRI yerde dayatiliyor ve ikisi de ayni hatayi
/// yapiyordu:
/// </para>
/// <list type="number">
/// <item><c>MailController.EnsureMailboxQuotaAsync</c> — kiracinin KUTU SAYISI kotasi
/// (<c>MaxMailboxes</c>); <c>workspace.Accounts?.Count ?? 0</c> okuyordu.</item>
/// <item><c>MailService.EnsureDomainAllocationAsync</c> / <c>GetDomainAllocatedMbAsync</c> —
/// alan adinin TAHSIS EDILEN DISK toplami; <c>workspace.Accounts?.Sum(...) ?? 0</c> okuyordu.</item>
/// </list>
/// <para>
/// Iki okumada da bos liste "kota dolu degil" anlamina geliyordu. Oysa liste, Stalwart CLI
/// sorgusu HIC KOSMADIGINDA da bos donuyordu (bkz. <c>MailService.MailQueryResult</c>). Yani
/// posta sunucusu cevap vermediginde kota kendiliginden DEVRE DISI kaliyor, kiraci sinirsiz
/// kutu acabiliyor ve panel bunu hicbir yere yazmiyordu.
/// </para>
/// <para>
/// <b>NEDEN REDDETMEK DOGRU YON.</b> Bu kararda yanilmanin iki yonu esit degildir. Olcemedigimiz
/// halde izin vermek, sinirsiz kaynak kullanimina yol acar ve GERI ALINAMAZ (acilan kutular,
/// dolan disk). Olcemedigimiz halde reddetmek ise gecicidir: posta sunucusu cevap verir vermez
/// islem tekrarlanabilir. Bu yuzden olculemeyen kota "asilmadi" degil, "olculemedi"dir ve islem
/// bu adla reddedilir.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA NOBETI.</b> Kotanin KENDISI yoksa (limitsiz plan) hicbir olcume gerek yoktur
/// ve bu sinif hicbir sey sormadan gecirir — aksi halde limitsiz musteriler, posta sunucusu her
/// aksadiginda kutu acamaz hale gelirdi.
/// </para>
/// </remarks>
public static class MailQuotaMeasurement
{
    /// <summary>Kutu SAYISI kotasi icin: sayilamayan kutular "sifir kutu" sayilmaz.</summary>
    public const string MailboxCountKey = "mail.mailbox_quota_unmeasured";

    /// <summary>Alan adi DISK tahsisi kotasi icin: okunamayan tahsis "sifir MB" sayilmaz.</summary>
    public const string DomainAllocationKey = "mail.domain_quota_unmeasured";

    /// <summary>
    /// Kotanin dayatilabilmesi icin gereken olcumun yapilip yapilmadigini soyler.
    /// </summary>
    /// <param name="limit">
    /// Yururlukteki sinir. <c>0</c> ya da altiysa sinir YOKTUR; olcum de gerekmez.
    /// </param>
    /// <param name="measured">Sayim/tahsis GERCEKTEN okundu mu.</param>
    /// <returns>
    /// Sinir varsa ve olcum yapilamadiysa <c>false</c>; diger her durumda <c>true</c>.
    /// </returns>
    public static bool CanEnforce(int limit, bool measured) => limit <= 0 || measured;

    /// <summary>
    /// Sinir varken olcum yapilamadiysa ADIYLA reddeder; aksi halde HICBIR SEY YAPMAZ.
    /// </summary>
    /// <param name="messageKey">
    /// <see cref="MailboxCountKey"/> ya da <see cref="DomainAllocationKey"/>.
    /// </param>
    /// <param name="subject">Hangi kiraci / hangi alan adi icin olculemedigi (kayda gecer).</param>
    public static void EnsureMeasured(int limit, bool measured, string messageKey, string subject)
    {
        if (!CanEnforce(limit, measured))
        {
            throw new AppException(messageKey, 502, 502, subject);
        }
    }
}
