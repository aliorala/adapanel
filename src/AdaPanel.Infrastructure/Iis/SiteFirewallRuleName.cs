using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// Bir sitenin güvenlik duvarı kuralının adı — TEK KAYNAK.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı sınıf (canlı denemede ölçüldü):</b> kural adı yalnız EKLEME yolunda üretiliyordu
/// ve silme yolu yoktu. Silme eklendiğinde adın ikinci kez yazılması gerekecekti — ve bu depoda
/// ikinci nüshanın bedeli defalarca ölçüldü: biri değişir, öteki sessizce eşleşmez olur ve kural
/// silinemez hâle gelir. Ad tek yerde üretilir; iki yol da buradan alır.
/// </para>
/// <para>
/// <b>Ölçülen iki açık:</b> kurulu panelde
/// <c>AdaPanel Site AdaPanel.WebDb.PhpMyAdmin TCP 8896</c> kuralı Windows'ta İKİ KEZ kayıtlıydı
/// (yükseltilmiş <c>Get-NetFirewallRule</c> ile doğrulandı), ve silinmiş <c>alioral.xyz</c>
/// sitesinin kuralı hâlâ duruyordu.
/// </para>
/// </remarks>
internal static partial class SiteFirewallRuleName
{
    /// <summary>
    /// Kural adı. Site adındaki güvensiz karakterler tireye çevrilir.
    /// </summary>
    /// <remarks>
    /// Aynı temizleme deseni uygulama havuzu adında da kullanılıyor; buradaki amaç kural adının
    /// <c>netsh</c> argümanında tırnak/karakter sorunu çıkarmaması.
    /// </remarks>
    public static string For(string siteName, int port) =>
        $"AdaPanel Site {GuvenliAd().Replace(siteName, "-")} TCP {port}";

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex GuvenliAd();
}
