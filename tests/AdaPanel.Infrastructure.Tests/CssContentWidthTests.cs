using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// İÇERİK GENİŞLİĞİ TEK KAYNAKTAN GELİR.
///
/// <para>
/// <b>Neden var — ölçülmüş hata:</b> panelin düzen kabuğunda (<c>MainLayout.vue</c>) içerik
/// sütununun HİÇBİR üst sınırı yoktu. Yayınlanan 0.8.1 derlemesinde doğrulandı:
/// <c>.content-column[data-v-…]{gap:10px;min-width:0;display:grid}</c> — <c>max-width</c> yok.
/// 2560px'lik bir ekranda ölçülen sonuç: içerik sütunu 2306px, üç sütunlu formun her alanı
/// <b>738px</b>, beş sütunlu bir tablo satırında satırın ADI ile o satırın işlem düğmesi
/// arasında <b>2184px</b>, açıklama paragrafı satır başına <b>310 karakter</b> (okunur aralık
/// 45-90). Yani sorun bir ekranın değil, altmış ekranın ortak kabuğunun sorunuydu.
/// </para>
/// <para>
/// <b>Kural:</b> genişlik kararı iki belirteçte yaşar — <c>--content-max</c> (dış sınır:
/// tablo/ızgara/grafik) ve <c>--reading-max</c> (okuma metni). İkisi de YALNIZ
/// <c>styles/base.css</c>'te tanımlanır ve sınır YALNIZ <c>MainLayout.vue</c>'daki
/// <c>.main-column</c> kuralında uygulanır. Bir ekran kendi başına dört haneli bir
/// <c>max-width</c> yazarsa karar ikiye bölünür; bu testin engellediği tam olarak odur.
/// </para>
/// <para>
/// <b><c>.wide</c> neden burada:</b> 0.8.0'da ölçülmüştü — 8 dosyada kullanılıyor, <b>sıfır</b>
/// tanım (bkz. <see cref="CssSharedClassTests"/>), yani o ekranlar "geniş" değildi, sınıf
/// hiçbir şey yapmıyordu. Silindi. Yeni düzende sınır tek bir dış kabukta durduğu ve panel
/// panel daraltma yapılmadığı için kaçılacak bir daraltma da yok — sınıfın yapacağı iş yok.
/// Aşağıdaki test onu ÖLÜ olarak geri gelmekten korur: geri getirmek isteyen önce daraltmanın
/// nerede olduğunu yazmak zorunda kalsın.
/// </para>
/// </summary>
public sealed class CssContentWidthTests
{
    private const string BaseCss = "base.css";
    private const string Kabuk = "MainLayout.vue";

    private static readonly string[] Belirtecler = ["--content-max", "--reading-max"];

    /// <summary>Düzen kabuğunun sütun adları: bunlara genişlik sınırı KABUKTAN başka yerde konmaz.</summary>
    private static readonly string[] KabukSutunlari = [".app-shell", ".main-column", ".content-column"];

    [Fact]
    public void Genislik_belirtecleri_YALNIZ_base_css_te_bir_kez_tanimli()
    {
        foreach (var belirtec in Belirtecler)
        {
            var tanimYerleri = new List<string>();
            foreach (var (yol, metin) in Dosyalar())
            {
                var sayi = Regex.Matches(YorumsuzHali(metin), Regex.Escape(belirtec) + @"\s*:").Count;
                for (var i = 0; i < sayi; i++)
                {
                    tanimYerleri.Add(Path.GetFileName(yol));
                }
            }

            Assert.True(
                tanimYerleri.Count == 1 && tanimYerleri[0] == BaseCss,
                $"`{belirtec}` tam olarak BIR kez ve {BaseCss} icinde tanimli olmali. " +
                $"Bulunan: [{string.Join(", ", tanimYerleri)}]. Ayni degeri ikinci bir yere yazmak " +
                "genislik kararini ikiye boler; ikisi zamanla ayrisir ve hangisinin gecerli oldugu " +
                "ancak tarayicida olculerek anlasilir.");
        }
    }

    [Fact]
    public void Icerik_sinirinin_KENDISI_kabukta_ve_belirtece_bagli()
    {
        var kabuk = YorumsuzHali(File.ReadAllText(DosyaYolu(Path.Combine("frontend", "src", "layouts"), Kabuk)));

        Assert.Matches(@"\.main-column\s*\{[^}]*max-width:\s*var\(--content-max\)", kabuk);
    }

    [Fact]
    public void Kabuk_sutunlarina_BASKA_dosyada_genislik_siniri_konmaz()
    {
        var suclular = new List<string>();

        foreach (var (yol, metin) in Dosyalar())
        {
            if (Path.GetFileName(yol) == Kabuk)
            {
                continue;
            }

            foreach (var (secici, govde) in Kurallar(metin))
            {
                if (!KabukSutunlari.Any(secici.Contains) || !Regex.IsMatch(govde, @"(?<!\()max-width\s*:"))
                {
                    continue;
                }

                suclular.Add($"{Path.GetFileName(yol)}: {secici.Trim()}");
            }
        }

        Assert.True(
            suclular.Count == 0,
            $"Duzen kabugunun sutunlarina {Kabuk} DISINDA genislik siniri konmus. Sinir tek yerde " +
            "durmali, yoksa hangi kuralin kazandigi ekrandan ekrana degisir.\n  " + string.Join("\n  ", suclular));
    }

    /// <summary>
    /// Dört haneli sabit bir <c>max-width</c>, belirteci baypas eden ikinci bir içerik sınırıdır.
    /// </summary>
    /// <remarks>
    /// Eşik 1000px: bunun altındaki değerler bir kutunun/şeridin kendi ölçüsüdür (ör. sayfa
    /// bildirimi <c>min(420px, …)</c>), İÇERİK sınırı değildir. Ortam sorgularının başlığı
    /// (<c>@media (max-width: 1080px)</c>) elenir — orada <c>max-width</c> bir kırılma noktasıdır,
    /// bir ölçü değil.
    /// </remarks>
    [Fact]
    public void Sabit_dort_haneli_max_width_yazilmaz()
    {
        var suclular = new List<string>();

        foreach (var (yol, metin) in Dosyalar())
        {
            foreach (Match eslesme in Regex.Matches(YorumsuzHali(metin), @"(?<!\()max-width\s*:\s*(\d+)px"))
            {
                if (int.Parse(eslesme.Groups[1].Value) >= 1000)
                {
                    suclular.Add($"{Path.GetFileName(yol)}: {eslesme.Value}");
                }
            }
        }

        Assert.True(
            suclular.Count == 0,
            "Sabit icerik genisligi yazilmis. Deger `--content-max` / `--reading-max` " +
            "belirtecinden gelmeli.\n  " + string.Join("\n  ", suclular));
    }

    [Fact]
    public void Olu_wide_sinifi_geri_gelmemis()
    {
        var kullananlar = new List<string>();
        var tanimlayanlar = new List<string>();

        foreach (var (yol, metin) in Dosyalar())
        {
            var temiz = YorumsuzHali(metin);

            // Sablon tarafi: `class="... wide ..."` icinde TAM sozcuk olarak. `settings-wide-field`
            // gibi adlar bilincli olarak eslesmez — onlarin tanimi var ve isleri belli.
            foreach (Match eslesme in Regex.Matches(temiz, @"class=""([^""]*)"""))
            {
                if (eslesme.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("wide"))
                {
                    kullananlar.Add(Path.GetFileName(yol));
                }
            }

            // Stil tarafi: `.wide` ama `.wide-button` DEGIL.
            foreach (var (secici, _) in Kurallar(temiz))
            {
                if (Regex.IsMatch(secici, @"\.wide(?![\w-])"))
                {
                    tanimlayanlar.Add($"{Path.GetFileName(yol)}: {secici.Trim()}");
                }
            }
        }

        Assert.True(
            kullananlar.Count == 0 && tanimlayanlar.Count == 0,
            "`.wide` geri gelmis. 0.8.0'da OLCULDU: 8 cagri yeri, SIFIR tanim — sinif hicbir sey " +
            "yapmiyordu ve o ekranlar 'genis' degildi. Yeni duzende sinir tek bir dis kabukta; panel " +
            "panel daraltma olmadigi icin kacilacak bir daraltma da yok. Geri getirmek gerekiyorsa " +
            "ONCE daraltmanin nerede oldugu yazilmali ve sinif tanimlanmali.\n" +
            $"  kullanan: [{string.Join(", ", kullananlar)}]\n  tanimlayan: [{string.Join(", ", tanimlayanlar)}]");
    }

    /// <summary>
    /// Kendi kendini denetleyen alt sınır: tarayıcı bozulup dosya bulamazsa yukarıdaki testler
    /// "sorun yok" diyerek sessizce yeşil kalmasın.
    /// </summary>
    [Fact]
    public void Taranan_dosya_sayisi_anlamli()
    {
        var dosyalar = Dosyalar();

        Assert.True(dosyalar.Count > 50, $"Beklenenden az dosya tarandi ({dosyalar.Count}).");
        Assert.Contains(dosyalar, d => Path.GetFileName(d.Yol) == BaseCss);
        Assert.Contains(dosyalar, d => Path.GetFileName(d.Yol) == Kabuk);
    }

    private static string YorumsuzHali(string metin) =>
        Regex.Replace(metin, @"/\*.*?\*/", " ", RegexOptions.Singleline);

    /// <summary>Kaba ama yeterli kural ayıklama: seçici + gövde çifti.</summary>
    private static IEnumerable<(string Secici, string Govde)> Kurallar(string css)
    {
        foreach (Match eslesme in Regex.Matches(YorumsuzHali(css), @"(^|[}])([^{}@]+)\{([^{}]*)\}", RegexOptions.Multiline))
        {
            yield return (eslesme.Groups[2].Value, eslesme.Groups[3].Value);
        }
    }

    private static IReadOnlyList<(string Yol, string Metin)> Dosyalar()
    {
        var kok = Path.Combine(BulKok(), "frontend", "src");
        var liste = new List<(string, string)>();

        foreach (var uzanti in new[] { "*.css", "*.vue" })
        {
            foreach (var yol in Directory.GetFiles(kok, uzanti, SearchOption.AllDirectories))
            {
                liste.Add((yol, File.ReadAllText(yol)));
            }
        }

        return liste;
    }

    private static string DosyaYolu(string gorecelKlasor, string ad) =>
        Path.Combine(BulKok(), gorecelKlasor, ad);

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
