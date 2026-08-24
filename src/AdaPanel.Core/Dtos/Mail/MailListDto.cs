namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Dağıtım listesi (Stalwart <c>MailingList</c> principal'ı): tek bir adrese (name@domain) gelen
/// posta, <see cref="Recipients"/> listesindeki tüm abonelere dağıtılır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Alanların kaynağı:</b> nesnenin şeması sunucudan OKUNDU
/// (<c>stalwart-cli describe MailingList</c>, Stalwart 0.16.8) ve tamamı şudur:
/// <c>name</c>, <c>domainId</c>, <c>description</c>, <c>aliases</c>, <c>recipients</c>,
/// <c>memberTenantId</c>, <c>emailAddress</c> (sunucu üretir).
/// </para>
/// <para>
/// <b><see cref="PostingPolicy"/> neden nesnede değil panelde:</b> Stalwart'ın MailingList
/// nesnesinde "kimler gönderebilir" alanı YOKTUR. Ama kural, sunucunun kendi eklenti noktasıyla
/// uygulanabiliyor: RCPT aşamasına bağlanan bir SİSTEM sieve script'i (canlı doğrulandı —
/// yetkisiz gönderici <c>550 5.7.2</c> ile RCPT aşamasında reddediliyor, normal posta kutuları
/// etkilenmiyor). Panelin otomatik yanıt özelliği de aynı deseni DATA aşamasında kullanıyor.
/// Bu yüzden ayar panelde saklanır, dayatma sunucuda yapılır.
/// </para>
/// </remarks>
public sealed class MailListDto
{
    public string Address { get; set; } = string.Empty;

    /// <summary>Listenin ne için olduğunu anlatan serbest metin (Stalwart <c>description</c>).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Aboneler. Sunucuda küme (harita) olarak durur; panelde sıralı liste gösterilir.</summary>
    public IReadOnlyCollection<string> Recipients { get; set; } = [];

    /// <summary>
    /// Listenin EK ADRESLERİ (Stalwart <c>aliases</c>): bu adreslerden birine gelen posta da aynı
    /// abonelere dağıtılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden var:</b> alan gerçekten şemada duruyor (yukarıdaki şema listesine bakın) ve bir
    /// listeye ikinci bir ad vermenin başka yolu yok. Somut ihtiyaç: liste
    /// <c>duyuru@ornek.com</c> adıyla açıldıktan sonra kartvizitlere <c>info@ornek.com</c>
    /// yazılmışsa, adres değiştirmek bütün abonelerin bildiği adresi kırar. Alias ile iki adres de
    /// aynı listeye düşer.
    /// </para>
    /// <para>
    /// <b>Neden TAM adres tutuluyor (yerel kısım değil):</b> Stalwart alias'ı <c>name</c> +
    /// <c>domainId</c> olarak saklar, yani alias listenin kendi alan adından BAŞKA bir alan
    /// adında olabilir. Panelde yalnız yerel kısım gösterilseydi <c>info@ornek.com</c> ile
    /// <c>info@baska.com</c> ekranda ayırt edilemezdi.
    /// </para>
    /// <para>
    /// <b>Plesk'te karşılığı YOK</b> — Plesk'in posta listesi (Mailman) tek adreslidir. Bu yüzden
    /// özellik "Plesk paritesi" değil, Stalwart'ın bize bedava verdiği bir fazlalıktır.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string> Aliases { get; set; } = [];

    /// <summary>
    /// Listeye kimler gönderebilir: <c>anyone</c> (herkes), <c>subscribers</c> (yalnız aboneler),
    /// <c>allowed</c> (yalnız <see cref="AllowedSenders"/> listesi).
    /// </summary>
    public string PostingPolicy { get; set; } = MailListPostingPolicy.Anyone;

    /// <summary><c>allowed</c> kipinde gönderim izni olan adresler (liste yöneticileri).</summary>
    public IReadOnlyCollection<string> AllowedSenders { get; set; } = [];
}

/// <summary>Dağıtım listesi gönderim politikası değerleri (Plesk'in "kimler gönderebilir" karşılığı).</summary>
public static class MailListPostingPolicy
{
    /// <summary>Herkes — kural yazılmaz; kimliği doğrulanmamış dış göndericiler de yazabilir.</summary>
    public const string Anyone = "anyone";

    /// <summary>Yalnız aboneler; gönderen KİMLİĞİ DOĞRULANMIŞ olmalı ve abone kümesinde bulunmalı.</summary>
    public const string Subscribers = "subscribers";

    /// <summary>Yalnız açıkça izin verilen adresler (liste yöneticileri).</summary>
    public const string Allowed = "allowed";

    public static bool IsKnown(string? value) =>
        value is Anyone or Subscribers or Allowed;

    /// <summary>Bilinmeyen/boş değer HERKES'e düşmez — en dar kip seçilemediğinde çağıran karar verir.</summary>
    public static string Normalize(string? value) =>
        IsKnown(value) ? value! : Anyone;
}
