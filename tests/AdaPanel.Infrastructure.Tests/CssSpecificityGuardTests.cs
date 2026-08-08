using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Arayüz stillerinde ÖZGÜLLÜK TUZAĞINI kalıcı olarak engeller.
/// </summary>
/// <remarks>
/// <para>
/// Bu test gerçek bir regresyondan doğdu. Global bir kuralın onay kutusu satırlarını kapsamaması
/// için <c>label:not(.check-field)</c> yazılmıştı. Ama <c>:not()</c>, argümanının özgüllüğünü
/// kurala <b>ekler</b>: <c>.form-grid label</c> (0,1,1) iken <c>.form-grid label:not(.check-field)</c>
/// (0,2,1) olur. Bu da bileşenlerin kendi kapsamlı <c>.checkbox-field[data-v-…]</c> (0,2,0)
/// kurallarını ezer — takma ad bileşenindeki onay kutuları tam olarak böyle bozuldu: kutu hücrenin
/// tamamına gerildi, etiketi alt satıra düştü.
/// </para>
/// <para>
/// Doğrusu <c>:where()</c>'dir: özgüllüğü <b>her zaman sıfırdır</b>, yani dışlama yapar ama kuralı
/// güçlendirmez. Aşağıdaki test, yayınlanan CSS'te sınıf/kimlik argümanlı çıplak <c>:not()</c>
/// kalmadığını doğrular. Gözle fark edilmesi çok zor bir hata sınıfı olduğu için otomatik korunur.
/// </para>
/// </remarks>
public sealed class CssSpecificityGuardTests
{
    /// <summary>
    /// Aranan tam desen: bir ELEMENT adına (label/input/button…) doğrudan iliştirilmiş, sınıf ya da
    /// kimlik argümanlı çıplak <c>:not()</c>.
    /// </summary>
    /// <remarks>
    /// Yalnız element düzeyi kurallar hedeflenir. <c>.data-row:not(.table-head):hover</c> gibi
    /// sınıfa dayalı DURUM seçicilerinde özgüllüğün yükselmesi zaten istenen davranıştır;
    /// tuzak, geniş bir element kuralından istisna oyarken kuralın farkında olmadan
    /// güçlenmesidir. Element adı, kapsam niteliği (<c>[data-v-…]</c>) alabilir.
    /// </remarks>
    private static readonly Regex CiplakNot = new(
        @"\b(?:label|input|button|select|textarea|a|p|span|div|li|td|th)(?:\[[^\]]*\])*:not\(\s*[.#]",
        RegexOptions.Compiled);

    [Fact]
    public void Yayinlanan_CSS_te_ozgullugu_yukselten_ciplak_not_kalmamali()
    {
        var suclular = new List<string>();

        foreach (var dosya in StilDosyalari())
        {
            var icerik = File.ReadAllText(dosya);
            foreach (var secici in Seciciler(icerik))
            {
                if (!CiplakNot.IsMatch(secici))
                {
                    continue;
                }

                // `:not(:where(...))` doğru kullanımdır: `:where` sıfır özgüllüklü olduğu için
                // dıştaki `:not` da hiçbir şey eklemez.
                var temizlenmis = Regex.Replace(secici, @":where\([^)]*\)", ":where()");
                if (CiplakNot.IsMatch(temizlenmis))
                {
                    suclular.Add($"{Path.GetFileName(dosya)}: {secici.Trim()}");
                }
            }
        }

        Assert.True(
            suclular.Count == 0,
            "Özgüllüğü yükselten çıplak :not() bulundu. `:not(.x)` yerine `:not(:where(.x))` kullanın; " +
            "aksi halde bileşenlerin kendi kapsamlı kuralları ezilir.\n  " + string.Join("\n  ", suclular));
    }

    /// <summary>
    /// Testin GERÇEKTEN bir şey taradığını doğrular: dosya bulunamazsa yukarıdaki test
    /// sessizce yeşil kalır ve hiçbir şeyi korumaz.
    /// </summary>
    [Fact]
    public void Taranacak_stil_dosyasi_bulunmali()
    {
        var dosyalar = StilDosyalari();
        Assert.NotEmpty(dosyalar);
        Assert.Contains(dosyalar, d => new FileInfo(d).Length > 1000);
    }

    /// <summary>Kaba ama yeterli seçici ayıklama: her kural bloğunun `{` öncesi kısmı.</summary>
    private static IEnumerable<string> Seciciler(string css)
    {
        // YORUMLAR ÖNCE SİLİNİR: bu dosyadaki açıklamalar tuzağın kendisini ÖRNEK olarak yazıyor
        // (`:not(.x)` yerine `:not(:where(.x))` kullanın). Yorum metni seçici sanılırsa test
        // kendi belgelerini ihlal sayar ve kalıcı olarak kırmızı kalır.
        var yorumsuz = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        // @media/@supports gibi at-kurallarının başlığı seçici değildir; elenir.
        foreach (Match eslesme in Regex.Matches(yorumsuz, @"(^|[}])([^{}@]+)\{"))
        {
            yield return eslesme.Groups[2].Value;
        }
    }

    private static IReadOnlyList<string> StilDosyalari()
    {
        var kok = BulKok();
        var liste = new List<string>();

        // 1) Kaynak stiller — derleme yapılmamış olsa da korunur.
        var stilKlasoru = Path.Combine(kok, "frontend", "src", "styles");
        if (Directory.Exists(stilKlasoru))
        {
            liste.AddRange(Directory.GetFiles(stilKlasoru, "*.css", SearchOption.AllDirectories));
        }

        // 2) Yayınlanan paket — kapsamlı (scoped) bileşen stilleri ancak burada birleşir,
        //    ve tuzağın etkisi asıl orada ortaya çıkar.
        var wwwroot = Path.Combine(kok, "src", "AdaPanel.Api", "wwwroot", "assets");
        if (Directory.Exists(wwwroot))
        {
            liste.AddRange(Directory.GetFiles(wwwroot, "*.css", SearchOption.TopDirectoryOnly));
        }

        return liste;
    }

    private static string BulKok()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
