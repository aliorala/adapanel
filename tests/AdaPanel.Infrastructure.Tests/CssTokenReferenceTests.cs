using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// TANIMSIZ BİR CSS BELİRTECİNE ATIF YAPILAMAZ.
///
/// <para>
/// <b>Neden var — ölçülmüş, yıllarca sessiz kalmış hata:</b> panelde renkler tasarım
/// belirteçlerine bağlı görünüyordu ama bağlı DEĞİLDİ. On ayrı ad — <c>--danger</c>,
/// <c>--warning</c>, <c>--warn-border</c>, <c>--border</c>, <c>--border-1</c>,
/// <c>--border-soft</c>, <c>--danger-soft</c>, <c>--success</c>, <c>--accent</c>,
/// <c>--fail-border</c> — depoda HİÇBİR YERDE tanımlı değildi. Yazan kişi belirtece
/// bağladığını sandı; CSS sessizce yedek değere (<c>var(--danger, #e5484d)</c>) düştü.
/// </para>
/// <para>
/// Sonucu iki katmanlıydı. Birincisi: o renkler tema değiştirmiyordu. Panelin varsayılan teması
/// AÇIK (<c>main.ts</c>: kayıtlı tercih "dark" değilse "light"), ve koyu tema için seçilmiş
/// sabit renkler beyaz zeminde ölçüldüğünde <b>2.12:1, 1.96:1</b> gibi değerler veriyordu —
/// yani uyarı metinleri okunmuyordu. İkincisi: yedeği OLMAYAN iki atıf
/// (<c>ClusterView</c> <c>--border-1</c>, <c>BillingView</c> <c>--text-2</c>) özelliği tümüyle
/// geçersiz kılıyordu; o kenarlık ve o metin rengi hiç uygulanmıyordu.
/// </para>
/// <para>
/// <b>Neden yalnız düzeltmek yetmez:</b> <c>--danger</c>/<c>--warning</c>/<c>--border</c>,
/// insanların yazmayı DOĞAL bulduğu adlar. Tarayıcı bunu hata saymaz, derleme uyarmaz, testler
/// görmez — hata tam olarak bu yüzden yıllarca durdu. Bu dosya, o sessizliği bozan nöbetçidir.
/// </para>
/// </summary>
public class CssTokenReferenceTests
{
    /// <summary>
    /// Çalışma anında JS ile atanan belirteçler — statik kaynakta tanımı OLMAZ.
    /// </summary>
    /// <remarks>
    /// Liste bir MUAFİYET DEĞİL, bir İDDİADIR: aşağıdaki test her birinin gerçekten
    /// <c>setProperty</c> ya da satır içi <c>:style</c> ile atandığını KANITLAR. Böylece
    /// listeye bir ad eklemek, hatayı susturmanın kolay yolu olmaktan çıkar.
    /// </remarks>
    private static readonly string[] CalismaAnindaAtananlar = ["--brand-accent", "--fill"];

    [Fact]
    public void Her_var_atfinin_bir_TANIMI_var()
    {
        var tanimlar = Tanimlar();
        var eksikler = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (yol, metin) in Dosyalar([".css", ".vue", ".ts"]))
        {
            var temiz = YorumlariSil(metin);
            foreach (Match eslesme in Regex.Matches(temiz, @"var\(\s*(--[a-z0-9-]+)"))
            {
                var ad = eslesme.Groups[1].Value;
                if (tanimlar.Contains(ad) || CalismaAnindaAtananlar.Contains(ad))
                {
                    continue;
                }

                eksikler.TryAdd(ad, []);
                eksikler[ad].Add(Path.GetFileName(yol));
            }
        }

        Assert.True(
            eksikler.Count == 0,
            "Tanimsiz CSS belirtecine atif var; CSS bunu HATA SAYMAZ, sessizce yedege duser "
            + "(yedek yoksa ozellik tumuyle gecersiz olur):\n"
            + string.Join("\n", eksikler.Select(x => $"  {x.Key} -> {string.Join(", ", x.Value.Distinct())}")));
    }

    /// <summary>
    /// Muafiyet listesindeki her ad GERÇEKTEN çalışma anında atanıyor olmalı.
    /// </summary>
    /// <remarks>
    /// Bu test olmadan liste, yukarıdaki nöbetçiyi susturmanın en kolay yolu olurdu: adı ekle,
    /// kırmızı gitsin. Burada her ad için atamanın kaynakta bulunduğu KANITLANIR.
    /// </remarks>
    [Theory]
    [InlineData("--brand-accent")]
    [InlineData("--fill")]
    public void Muaf_tutulan_belirtec_calisma_aninda_GERCEKTEN_ataniyor(string ad)
    {
        var atandi = Dosyalar([".vue", ".ts"]).Any(d =>
            d.Metin.Contains($"setProperty(\"{ad}\"", StringComparison.Ordinal) ||
            d.Metin.Contains($"setProperty('{ad}'", StringComparison.Ordinal) ||
            d.Metin.Contains($"'{ad}':", StringComparison.Ordinal) ||
            d.Metin.Contains($"\"{ad}\":", StringComparison.Ordinal));

        Assert.True(atandi, $"'{ad}' muaf tutulmus ama kaynakta calisma aninda ATANDIGI gorulmuyor.");
    }

    private static HashSet<string> Tanimlar()
    {
        var tanimlar = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, metin) in Dosyalar([".css", ".vue"]))
        {
            foreach (Match eslesme in Regex.Matches(YorumlariSil(metin), @"^\s*(--[a-z0-9-]+)\s*:", RegexOptions.Multiline))
            {
                tanimlar.Add(eslesme.Groups[1].Value);
            }
        }

        // Kendi kendini denetleyen alt sinir: belirtec sistemi bir gun silinirse bu test
        // "hic atif yok, demek ki temiz" diyerek YESIL kalmasin.
        Assert.True(tanimlar.Count > 30, $"Belirtec tanimi beklenenden az bulundu ({tanimlar.Count}); tarama bozulmus olabilir.");
        return tanimlar;
    }

    /// <summary>
    /// Yorumlar ATILIR: bir hatayı ANLATAN yorum ("`var(--border, #d8dee9)` yazıyordu")
    /// nöbetçiyi tetiklerse, doğru olan davranış cezalandırılmış olur.
    /// </summary>
    private static string YorumlariSil(string metin)
        => Regex.Replace(metin, @"/\*.*?\*/|//[^\r\n]*", " ", RegexOptions.Singleline);

    private static List<(string Yol, string Metin)> Dosyalar(string[] uzantilar)
    {
        var kaynak = Path.Combine(KaynakKoku(), "frontend", "src");
        return Directory.EnumerateFiles(kaynak, "*", SearchOption.AllDirectories)
            .Where(y => uzantilar.Contains(Path.GetExtension(y), StringComparer.OrdinalIgnoreCase))
            .Select(y => (y, File.ReadAllText(y)))
            .ToList();
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
