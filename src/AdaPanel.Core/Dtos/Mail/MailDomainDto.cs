namespace AdaPanel.Core.Dtos.Mail;

public sealed class MailDomainDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Alan adı etkin mi. <c>null</c> = ÖLÇÜLEMEDİ.</summary>
    /// <remarks>
    /// <para>
    /// <c>bool</c> DEĞİL <c>bool?</c>, ve bu bir tercih değil bir düzeltme. Eskiden değer
    /// <c>GetBoolField</c> ile okunuyordu; o yardımcı, alan hiç yoksa <c>true</c> döner — yani
    /// "sunucu bunu söylemedi" ile "sunucu evet dedi" AYNI cevaba iniyordu. Kapalı bir alan adı
    /// panelde yeşil "Etkin" görünürse kullanıcı hatayı BAŞKA yerde arar.
    /// </para>
    /// <para>
    /// Aynı hata posta kutusu için kapatılmıştı (<c>MailAccountDto.Active</c>); alan adı ve takma
    /// ad geride kalmıştı. Üçü de artık aynı kayıpsız okuyucudan (<c>MailService.OkunanEtkinlik</c>)
    /// besleniyor: alan varsa değeri, yoksa <c>null</c>.
    /// </para>
    /// </remarks>
    public bool? Active { get; set; }

    public int AccountCount { get; set; }

    /// <summary>False means the domain mailbox count could not be measured, not zero mailboxes.</summary>
    public bool AccountCountMeasured { get; set; }

    /// <summary>Domain icin azami toplam boyut (MB). 0 = sinirsiz. Panel-tarafi tahsis kotasi (mail.json'da tutulur).</summary>
    public int MaxSizeMB { get; set; }

    /// <summary>Bu domaindeki posta kutularina tahsis edilen toplam kota (MB) — panel domain kotasini buna gore dayatir.</summary>
    public int AllocatedMB { get; set; }
}
