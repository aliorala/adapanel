using System.Text;
using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Kullanıcı metnini üretilen HTML'e sokmanın TEK yolu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı sınıf:</b> burada korunan şey tek bir alan değil, bir SINIF — "kullanıcı metni
/// işaretlemeye kaçırılmadan girdi". Kurucuda kullanıcı metni onlarca yere giriyor (başlık,
/// slogan, gövde, kart başlığı, adres, görsel alt metni). Kaçırma her çağrı yerinde elle
/// yazılsaydı biri unutulurdu ve o tek yer, müşterinin KENDİ SİTESİNE kalıcı script gömmesi
/// demek olurdu (depolanmış XSS): metin panelde saklanır, her ziyaretçide yeniden çalışır.
/// </para>
/// <para>
/// <b>Neden tek çıkış yolu:</b> ham metni HTML'e yazmanın başka bir yolu bırakılmadı; oluşturucu
/// (<see cref="SiteBuilderRenderer"/>) dizgileri doğrudan değil, yalnız bu sınıftan geçirerek
/// ekler. Testler bunu kaynak üzerinden de sabitler.
/// </para>
/// <para>
/// <b>Neden tek tırnak da kaçırılıyor:</b> öznitelikler bu üreticide çift tırnaklı yazılıyor, yani
/// bugün <c>'</c> tehlikeli değil. Ama üreticiye yarın tek tırnaklı bir öznitelik eklendiğinde
/// kaçırmanın hâlâ doğru olmasını istiyoruz; ucuz ve kalıcı bir güvence.
/// </para>
/// </remarks>
public static class SiteBuilderHtml
{
    /// <summary>Metin düğümü ve öznitelik değeri için kaçırma.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&#39;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Çok satırlı gövdeyi paragraflara çevirir: boş satır paragraf ayırır, tek satır sonu
    /// <c>&lt;br /&gt;</c> olur.
    /// </summary>
    /// <remarks>
    /// SIRA ÖNEMLİ: önce kaçırılır, SONRA <c>&lt;br /&gt;</c> eklenir. Ters sırada, kaçırma
    /// bizim eklediğimiz etiketi de kaçırır ve kullanıcı sayfada birebir "&amp;lt;br /&amp;gt;"
    /// görürdü; daha kötüsü, "kaçırmayı sonra yaparız" alışkanlığı bir gün metni ham bırakırdı.
    /// </remarks>
    public static string Paragraphs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        var paragraphs = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        var builder = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim('\n');
            if (trimmed.Trim().Length == 0)
            {
                continue;
            }

            builder.Append("<p>");
            builder.Append(Text(trimmed).Replace("\n", "<br />"));
            builder.Append("</p>");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Görsel adını sayfada kullanılabilir göreli adrese çevirir; ad kalıba uymuyorsa boş döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kaçırma tek başına YETMEZ: <c>src</c>/<c>href</c> gibi URL taşıyan öznitelikler kaçırılmış
    /// olsa bile <c>javascript:</c> şemasıyla kod çalıştırabilir. Bu yüzden adres kullanıcının
    /// verdiği dizgiden KURULMAZ; yalnız panelin ürettiği güvenli dosya adı kabul edilir ve
    /// önek panel tarafından eklenir. Ad kalıba uymuyorsa görsel hiç basılmaz.
    /// </para>
    /// <para>
    /// Kalıp aynı zamanda yol kaçışını da kapatır: <c>..</c>, <c>/</c> ve <c>\</c> kalıba uymaz.
    /// </para>
    /// </remarks>
    public static string ImageSource(string? imageName, string prefix)
    {
        if (string.IsNullOrWhiteSpace(imageName) || !SiteBuilderImageValidator.IsStoredNameValid(imageName))
        {
            return string.Empty;
        }

        return prefix + imageName.Trim();
    }

    // Kasten dar: yaygın adresleri kabul eder, tuhaf olanı reddeder. Bir e-posta adresini
    // reddetmenin bedeli "adres görünmez"; kabul etmenin bedeli bir şema saldırısıdır.
    private static readonly Regex EmailPattern = new(
        @"^[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]{1,63}(\.[A-Za-z0-9\-]{1,63})+$",
        RegexOptions.Compiled);

    /// <summary>E-posta bağlantısı; adres geçerli değilse boş döner (bağlantı hiç basılmaz).</summary>
    public static string MailtoHref(string? email)
    {
        var value = (email ?? string.Empty).Trim();
        return value.Length > 0 && EmailPattern.IsMatch(value) ? "mailto:" + Text(value) : string.Empty;
    }

    // Telefon: yalnız rakam ve numara noktalama işaretleri. Harf içeren bir "numara" bağlantı
    // olmaz — metin olarak yine görünür, ama tıklanabilir adres kurulmaz.
    private static readonly Regex PhonePattern = new(@"^[0-9+()\-.\s]{4,32}$", RegexOptions.Compiled);

    /// <summary>Telefon bağlantısı; biçim tanınmıyorsa boş döner.</summary>
    public static string TelHref(string? phone)
    {
        var value = (phone ?? string.Empty).Trim();
        if (value.Length == 0 || !PhonePattern.IsMatch(value))
        {
            return string.Empty;
        }

        var digits = new string(value.Where(character => char.IsDigit(character) || character == '+').ToArray());
        return digits.Length >= 4 ? "tel:" + Text(digits) : string.Empty;
    }

    /// <summary>Bölüm bağlantıları için güvenli çapa kimliği (yalnız katalogdaki tipler kullanılır).</summary>
    public static string AnchorId(string sectionType) => "bolum-" + Text(sectionType).ToLowerInvariant();
}
