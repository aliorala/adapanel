namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Dağıtım listesi (Stalwart <c>MailingList</c> principal'ı): tek bir adrese (name@domain) gelen
/// posta, <see cref="Recipients"/> listesindeki tüm abonelere dağıtılır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden yalnız bu alanlar var:</b> nesnenin şeması sunucudan OKUNDU
/// (<c>stalwart-cli describe MailingList</c>, Stalwart 0.16.8) ve tamamı şudur:
/// <c>name</c>, <c>domainId</c>, <c>description</c>, <c>aliases</c>, <c>recipients</c>,
/// <c>memberTenantId</c>, <c>emailAddress</c> (sunucu üretir). Plesk'te bulunan "kimler
/// gönderebilir" ve "abonelikten çık" AYARLARININ Stalwart'ta KARŞILIĞI YOKTUR; bu yüzden burada
/// da uydurulmuş bir alan tutulmaz — olmayan bir ayarı DTO'da göstermek, panelde açılıp kapanan
/// ama sunucuda hiçbir şey yapmayan bir düğme demek olurdu.
/// </para>
/// </remarks>
public sealed class MailListDto
{
    public string Address { get; set; } = string.Empty;

    /// <summary>Listenin ne için olduğunu anlatan serbest metin (Stalwart <c>description</c>).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Aboneler. Sunucuda küme (harita) olarak durur; panelde sıralı liste gösterilir.</summary>
    public IReadOnlyCollection<string> Recipients { get; set; } = [];
}
