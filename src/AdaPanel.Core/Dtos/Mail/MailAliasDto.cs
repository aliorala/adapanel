namespace AdaPanel.Core.Dtos.Mail;

public sealed class MailAliasDto
{
    public string Address { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    /// <summary>Takma ad etkin mi. <c>null</c> = ÖLÇÜLEMEDİ.</summary>
    /// <remarks>
    /// <para>
    /// <c>bool</c> DEĞİL <c>bool?</c>, ve bu bir tercih değil bir düzeltme. Eskiden değer
    /// <c>GetBoolField</c> ile okunuyordu; o yardımcı, alan hiç yoksa <c>true</c> döner — yani
    /// "sunucu bunu söylemedi" ile "sunucu evet dedi" AYNI cevaba iniyordu. Kapalı bir takma ad
    /// panelde yeşil "Etkin" görünürse kullanıcı hatayı BAŞKA yerde arar.
    /// </para>
    /// <para>
    /// Aynı hata posta kutusu için kapatılmıştı (<c>MailAccountDto.Active</c>); alan adı ve takma
    /// ad geride kalmıştı. Üçü de artık aynı kayıpsız okuyucudan (<c>MailService.OkunanEtkinlik</c>)
    /// besleniyor: alan varsa değeri, yoksa <c>null</c>.
    /// </para>
    /// </remarks>
    public bool? Active { get; set; }
}
