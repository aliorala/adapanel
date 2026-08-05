using System.Text.RegularExpressions;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// <c>icacls</c>'e gidecek hesap adı ve izin değerlerinin TEK doğrulama yeri.
/// <para>
/// Bu değerler bir komut satırına gömülüyor. Doğrulanmazlarsa içine tırnak koyan biri
/// <c>icacls</c>'e istediği bayrağı (<c>/inheritance:r</c>, <c>/deny</c>, <c>/remove</c>)
/// geçirebilir; panel LocalSystem olarak koştuğu için sonucun ağırlığı SYSTEM seviyesindedir.
/// Üstelik izin değişiklikleri geri alınamaz: yanlış bir <c>/deny</c> siteyi erişilemez bırakır.
/// </para>
/// <para>
/// Kural TEK YERDE durur. Aynı doğrulama iki ayrı serviste kopyalanmıştı; kopyalanmış bir kural,
/// birini sıkılaştırıp diğerini unutmanın kestirme yoludur.
/// </para>
/// </summary>
internal static class AclInputValidator
{
    /// <summary>
    /// İzin verilen haklar: okuma, okuma+çalıştırma, yazma, değiştirme, tam denetim.
    /// <para>
    /// <c>icacls</c> bunlardan çok daha fazlasını kabul eder (<c>(OI)(CI)(DE,WD,AD)</c> gibi
    /// birleşik biçimler dahil), ama panelin arayüzü yalnızca bu beşini sunar. Serbest metin
    /// kabul etmek, sunulmayan bir yeteneği doğrulanmamış biçimde açık bırakmak olurdu.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> AllowedRights =
        new(StringComparer.OrdinalIgnoreCase) { "R", "RX", "W", "M", "F" };

    /// <summary>
    /// Hesap/grup adı: harf, rakam, boşluk, nokta, tire, alt çizgi; isteğe bağlı tek bir alan
    /// ayıracı (<c>SUNUCU\kullanici</c> ya da <c>kullanici@alan</c>).
    /// <para>
    /// Tırnak, noktalı virgül, iki nokta ve eğik çizgi YOKTUR: bunlar <c>icacls</c> söz dizimini
    /// kırar ve argüman sınırından çıkmayı mümkün kılar.
    /// </para>
    /// </summary>
    private static readonly Regex PrincipalPattern =
        new(@"^[\w .\-]+(?:[\\@][\w .\-]+)?$", RegexOptions.Compiled);

    /// <summary>
    /// Hesap adını doğrular ve kırpılmış hâlini döner. Geçersizse <see cref="AppException"/> atar.
    /// </summary>
    internal static string EnsurePrincipal(string? principal)
    {
        var temiz = (principal ?? string.Empty).Trim();

        if (temiz.Length is 0 or > 256 || !PrincipalPattern.IsMatch(temiz))
        {
            throw new AppException("files.invalid_principal", 400, 400);
        }

        return temiz;
    }

    /// <summary>
    /// İzin değerini doğrular ve büyük harfe çevrilmiş hâlini döner. Geçersizse
    /// <see cref="AppException"/> atar.
    /// </summary>
    internal static string EnsureRights(string? rights)
    {
        var temiz = (rights ?? string.Empty).Trim();

        if (!AllowedRights.Contains(temiz))
        {
            throw new AppException("files.invalid_rights", 400, 400);
        }

        return temiz.ToUpperInvariant();
    }
}
