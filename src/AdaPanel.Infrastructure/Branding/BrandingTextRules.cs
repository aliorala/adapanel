using System.Text.RegularExpressions;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Branding;

/// <summary>
/// Marka METİNLERİNİN doğrulanması. Üçünün de ortak özelliği, kullanıcıdan gelip TARAYICIDA bir
/// yorumlayıcıya (CSS, adres çubuğu, HTML) teslim edilmeleridir; bu yüzden hepsi beyaz listeyle
/// karşılanır.
/// </summary>
public static partial class BrandingTextRules
{
    /// <summary>Yalnız <c>#rrggbb</c>. Kısa (#abc) ve isimli renkler bilerek dışarıda — tek biçim, tek denetim.</summary>
    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccentPattern();

    /// <summary>
    /// Vurgu rengini normalleştirir (boş bırakılabilir).
    /// </summary>
    /// <remarks>
    /// Bu değer arayüzde bir CSS ÖZEL DEĞİŞKENİNE yazılıyor. Serbest metin kabul edilseydi
    /// <c>red; background-image: url(https://saldirgan/iz)</c> gibi bir değer bildirimi kapatıp
    /// kendi stilini ekleyebilir, yani panelin görünümünü bir bayi başkasının ekranında
    /// değiştirebilirdi. Desen tam eşleşmedir: içine hiçbir ayraç sığmaz.
    /// </remarks>
    public static string NormalizeAccentColor(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (!AccentPattern().IsMatch(trimmed))
        {
            throw new AppException("branding.invalid_accent_color", 400, 400);
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Logo bağlantısı: yalnız mutlak http/https adresleri (boş bırakılabilir).
    /// </summary>
    /// <remarks>
    /// Bu adres bir <c>&lt;a href&gt;</c> içine giriyor. Şema süzülmeseydi <c>javascript:…</c>
    /// yazan bir bayi, kendi müşterisinin oturumunda kod çalıştırabilirdi — üstelik kurbanı
    /// tıklamaya ikna etmek için elinde panelin kendi logosu olurdu. <c>data:</c> ve
    /// <c>vbscript:</c> aynı sınıfa girer; beyaz liste üçünü de kapsar.
    /// </remarks>
    public static string NormalizeLinkUrl(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AppException("branding.invalid_logo_url", 400, 400);
        }

        return uri.ToString();
    }

    /// <summary>
    /// Panel adı: 64 karaktere kadar, açılı ayraç ve kontrol karakteri içeremez.
    /// </summary>
    /// <remarks>
    /// Arayüz bu metni Vue'nun kaçışlı bağlamasıyla (<c>{{ }}</c>) basar, yani HTML olarak
    /// YORUMLANMAZ. Buradaki denetim ikinci savunma hattıdır: metin ileride bir e-posta gövdesine,
    /// bir pencere başlığına ya da bir günlüğe kopyalanırsa kaçış garantisi oranın koduna bağlı
    /// olur. Ayraçları kaynağında reddetmek, o günü beklemeden riski bitirir. Kontrol
    /// karakterleri de elenir: görünmez karakterler panel adını "sahiplenmenin" ucuz yoludur.
    /// </remarks>
    public static string NormalizePanelName(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.Length > 64)
        {
            throw new AppException("branding.invalid_panel_name", 400, 400);
        }

        foreach (var character in trimmed)
        {
            if (character is '<' or '>' || char.IsControl(character))
            {
                throw new AppException("branding.invalid_panel_name", 400, 400);
            }
        }

        return trimmed;
    }
}
