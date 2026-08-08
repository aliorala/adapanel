namespace AdaPanel.Core.Common;

/// <summary>
/// Alan adı ilişkileri için TEK kaynak.
/// </summary>
/// <remarks>
/// Aynı kural iki yerde kullanılıyor: <b>güvenlik denetimi</b> ("başkasının alanının altına site
/// açılamaz") ve <b>site akışı</b> ("alt alan adına üst bölgede A kaydı düş"). İki ayrı kopya
/// yazılsaydı, biri düzeltilip diğeri unutulduğunda denetimin reddettiği bir adı akışın kabul
/// etmesi ya da tersi mümkün olurdu — sessiz ve bulunması zor bir tutarsızlık.
/// </remarks>
public static class DomainNames
{
    /// <summary>Karşılaştırma için normalleştirir: kırpar, sondaki noktayı atar, küçük harfe çevirir.</summary>
    public static string Normalize(string? domainName)
        => (domainName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// <paramref name="child"/>, <paramref name="parent"/> alan adının ALTINDA mı?
    /// (<c>blog.ornek.com</c> → <c>ornek.com</c> için doğru.)
    /// </summary>
    /// <remarks>
    /// <para>
    /// Karşılaştırma ETİKET SINIRINDA yapılır (baştaki nokta şart). Düz <c>EndsWith</c> ile
    /// "notornek.com" da "ornek.com"un altı sayılırdı; güvenlik denetiminde bu, meşru müşterilerin
    /// kendi alan adlarını bağlayamaması demekti.
    /// </para>
    /// <para>
    /// Tek etiketli (nokta içermeyen) adlar üst alan SAYILMAZ. Aksi halde adı "com" ya da
    /// "localhost" olan tek bir site, o uzantının tamamını kilitlerdi.
    /// </para>
    /// </remarks>
    public static bool IsSubdomainOf(string? child, string? parent)
    {
        var alt = Normalize(child);
        var ust = Normalize(parent);

        if (ust.Length == 0 || !ust.Contains('.'))
        {
            return false;
        }

        return alt.Length > ust.Length + 1
            && alt.EndsWith('.' + ust, StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="child"/>'ın <paramref name="parent"/>'a göre etiketi
    /// (<c>blog.ornek.com</c> + <c>ornek.com</c> → <c>blog</c>). Alt alan değilse boş döner.
    /// </summary>
    public static string LabelUnder(string? child, string? parent)
    {
        if (!IsSubdomainOf(child, parent))
        {
            return string.Empty;
        }

        var alt = Normalize(child);
        var ust = Normalize(parent);
        return alt[..(alt.Length - ust.Length - 1)];
    }
}
