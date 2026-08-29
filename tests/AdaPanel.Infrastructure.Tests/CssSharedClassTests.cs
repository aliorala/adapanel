using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PAYLASILAN BIR CSS SINIFI, KULLANAN HER BILESENDE TANIMLI OLMAK ZORUNDA.
///
/// <para>
/// <b>Neden var — olculmus, sessiz duran hata:</b> Vue'da <c>&lt;style scoped&gt;</c>
/// <b>bilesenler arasi GECMEZ</b>. Buna ragmen bir dizi ad "ortak" gibi kullaniliyordu:
/// tanim tek bir ekranin kapsamli stilinde yasiyor, ayni adi yazan komsu ekran ise
/// HICBIR SEY almiyordu. CSS tanimsiz sinifi hata saymaz, derleme uyarmaz, tarayici
/// susar — oge yalnizca <b>bicimsiz</b> cizilir.
/// </para>
/// <para>
/// Ilk taramada bulunanlar (sayilar o andaki durumdur): <c>section-head</c> 31 kopya ve
/// 8 bilesende hic tanim yok; <c>compact-select</c> 8 cagri yeri, 2 tanim — kalan 6
/// ekranda "compact" secim kutusu global <c>select { width: 100% }</c> ile baslik satirinin
/// TAMAMINI kapliyordu; <c>section-kicker</c> 5 cagri, 1 tanim; <c>toggle-field</c>
/// 6 cagri, 4 tanim — tanimsiz iki ekranda etiket onay kutusunun ALT SATIRINA dusuyordu;
/// <c>warn-banner</c> 3 cagri, 1 tanim — kalan iki ekranda UYARI seridi MAVI bilgi seridi
/// olarak ciziliyordu; <c>wide</c> 8 cagri, <b>SIFIR</b> tanim.
/// </para>
/// <para>
/// <b>Kural:</b> bir sinif adi birden cok bilesende geciyorsa artik ortak bir SOZLESMEDIR;
/// o zaman ya <c>styles/base.css</c>'te tanimlidir (tek kaynak), ya da kullanan HER bilesen
/// onu kendi kapsamli stilinde tanimlar. Tek bir bilesende gecen ad o bilesenin ozel isidir
/// ve bu testin konusu degildir — bu sinir bilinclidir, muafiyet listesi tutmak zorunda
/// kalmamanin bedelidir (bkz. <see cref="Tek_bilesende_gecen_tanimsiz_siniflar_RAPOR_EDILIR"/>).
/// </para>
/// </summary>
public class CssSharedClassTests
{
    [Fact]
    public void Birden_cok_bilesende_kullanilan_sinif_HER_KULLANANDA_tanimli()
    {
        var bilesenler = Bilesenler();
        var ortakTanimlar = SiniflariTopla(File.ReadAllText(BaseCssYolu()));

        // sinif -> kullanan bilesenler
        var kullanim = new SortedDictionary<string, List<Bilesen>>(StringComparer.Ordinal);
        foreach (var bilesen in bilesenler)
        {
            foreach (var sinif in bilesen.Kullanilan)
            {
                kullanim.TryAdd(sinif, []);
                kullanim[sinif].Add(bilesen);
            }
        }

        // Kendi kendini denetleyen alt sinir: tarayici bozulup hicbir sinif bulamazsa
        // test "sorun yok" diyerek YESIL kalmasin.
        Assert.True(bilesenler.Count > 50, $"Beklenenden az bilesen tarandi ({bilesenler.Count}).");
        Assert.True(kullanim.Count > 200, $"Beklenenden az sinif adi bulundu ({kullanim.Count}).");

        var eksikler = new List<string>();
        foreach (var (sinif, kullananlar) in kullanim)
        {
            if (kullananlar.Count < 2 || ortakTanimlar.Contains(sinif))
            {
                continue;
            }

            var tanimsizlar = kullananlar.Where(b => !b.Tanimli.Contains(sinif)).Select(b => b.Ad).ToList();
            if (tanimsizlar.Count > 0)
            {
                eksikler.Add(
                    $"  .{sinif} -> {kullananlar.Count} bilesende kullaniliyor, "
                    + $"su {tanimsizlar.Count} tanesinde TANIM YOK: {string.Join(", ", tanimsizlar)}");
            }
        }

        Assert.True(
            eksikler.Count == 0,
            "Paylasilan sinif, kullanan bilesende tanimsiz. `<style scoped>` BILESENLER ARASI GECMEZ: "
            + "sinif orada sessizce BICIMSIZ cizilir. Cozum ya adi styles/base.css'e tasimak (tek kaynak) "
            + "ya da o ekrana ozgu bir varyantsa AYRI BIR AD vermektir:\n"
            + string.Join("\n", eksikler));
    }

    /// <summary>
    /// Tek bilesende gecen tanimsiz siniflar: kirmizi yapmaz, ama SAYISI sabittir.
    /// </summary>
    /// <remarks>
    /// Bunlar bir ekranin kendi ic isi oldugu icin yukaridaki kuralin kapsami disinda; yine de
    /// her biri "yazildi ama hicbir sey yapmiyor" demektir. Sayiyi bir tavana baglamak, listenin
    /// sessizce buyumesini engeller: yeni bir olu sinif eklemek testi kirmiziya cevirir.
    /// Tavan DUSURULEBILIR, yukseltilemez — yukseltmek icin once sebebi yazilmalidir.
    /// </remarks>
    [Fact]
    public void Tek_bilesende_gecen_tanimsiz_siniflar_RAPOR_EDILIR()
    {
        var bilesenler = Bilesenler();
        var ortakTanimlar = SiniflariTopla(File.ReadAllText(BaseCssYolu()));

        var sayim = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var bilesen in bilesenler)
        {
            foreach (var sinif in bilesen.Kullanilan)
            {
                sayim.TryAdd(sinif, []);
                sayim[sinif].Add(bilesen.Ad);
            }
        }

        var oluSiniflar = sayim
            .Where(x => x.Value.Count == 1)
            .Where(x => !ortakTanimlar.Contains(x.Key))
            .Where(x => !bilesenler.Single(b => b.Ad == x.Value[0]).Tanimli.Contains(x.Key))
            .Select(x => $"{x.Key} ({x.Value[0]})")
            .ToList();

        // 2026-08 olcumu: 42 adet. Tasima turunde bunlarin hicbiri paylasilan degil; her biri
        // tek bir ekranda yazilmis ama hic biciminlenmemis ad.
        const int Tavan = 42;
        Assert.True(
            oluSiniflar.Count <= Tavan,
            $"Tek bilesende gecen TANIMSIZ sinif sayisi {Tavan} tavanini asti ({oluSiniflar.Count}). "
            + "Yeni eklenen ad hicbir sey yapmiyor demektir:\n  " + string.Join("\n  ", oluSiniflar));
    }

    private sealed record Bilesen(string Ad, HashSet<string> Kullanilan, HashSet<string> Tanimli);

    private static List<Bilesen> Bilesenler()
    {
        var kaynak = Path.Combine(KaynakKoku(), "frontend", "src");
        var liste = new List<Bilesen>();
        foreach (var yol in Directory.EnumerateFiles(kaynak, "*.vue", SearchOption.AllDirectories))
        {
            var metin = File.ReadAllText(yol);
            liste.Add(new Bilesen(
                Path.GetFileName(yol),
                SablondanSiniflar(BlokBirlestir(metin, "template")),
                SiniflariTopla(BlokBirlestir(metin, "style"))));
        }

        return liste;
    }

    private static string BaseCssYolu()
        => Path.Combine(KaynakKoku(), "frontend", "src", "styles", "base.css");

    private static string BlokBirlestir(string metin, string etiket)
    {
        var kalip = $"<{etiket}(?:\\s[^>]*)?>(.*?)</{etiket}>";
        return string.Join(
            "\n",
            Regex.Matches(metin, kalip, RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .Select(e => e.Groups[1].Value));
    }

    /// <summary>
    /// Bir stil blogundaki SECICILERDE gecen sinif adlari.
    /// </summary>
    /// <remarks>
    /// Bildirim govdeleri (<c>: ... ;</c>) once silinir; aksi halde <c>font: 0.8rem/1.4 ...</c>
    /// gibi degerler yanlis pozitif uretirdi. Yorumlar da silinir: bir hatayi ANLATAN yorum
    /// ("`.section-head` tanimsizdi") tanim sayilmamalidir.
    /// </remarks>
    private static HashSet<string> SiniflariTopla(string css)
    {
        var temiz = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        temiz = Regex.Replace(temiz, @":[^;{}]*[;}]", m => m.Value.EndsWith('}') ? "}" : ";");
        return Regex.Matches(temiz, @"\.(-?[_a-zA-Z][\w-]*)")
            .Select(e => e.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Sablondaki STATIK sinif adlari.
    /// </summary>
    /// <remarks>
    /// <para><c>class="a b"</c> dogrudan alinir.</para>
    /// <para>
    /// <c>:class</c> ifadelerinde YALNIZCA nesne ANAHTARLARI (<c>{ 'ad': kosul }</c>) ve dizi
    /// ELEMANLARI (<c>[ 'ad', ... ]</c>) alinir. Bu ayrim OLCULEREK eklendi: kaba bir "tirnak
    /// icindeki her sey siniftir" taramasi <c>{ active: tab === 'allow' }</c> ifadesindeki
    /// <c>allow</c> gibi KOSUL DEGERLERINI sinif sanip 69 yanlis pozitif uretti.
    /// </para>
    /// </remarks>
    private static HashSet<string> SablondanSiniflar(string sablon)
    {
        var t = Regex.Replace(sablon, "<!--.*?-->", " ", RegexOptions.Singleline);
        var sonuc = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match e in Regex.Matches(t, @"(?<![\w:.\-])class\s*=\s*""([^""]*)""|(?<![\w:.\-])class\s*=\s*'([^']*)'"))
        {
            Ekle(sonuc, e.Groups[1].Success ? e.Groups[1].Value : e.Groups[2].Value);
        }

        foreach (Match e in Regex.Matches(t, @"(?::|v-bind:)class\s*=\s*""([^""]*)"""))
        {
            var ifade = e.Groups[1].Value;
            foreach (Match nesne in Regex.Matches(ifade, @"\{([^{}]*)\}"))
            {
                foreach (Match anahtar in Regex.Matches(
                    nesne.Groups[1].Value,
                    @"(?:^|,)\s*(?:'([^']+)'|""([^""]+)""|([A-Za-z_][\w-]*))\s*:"))
                {
                    Ekle(sonuc, anahtar.Groups[1].Value + anahtar.Groups[2].Value + anahtar.Groups[3].Value);
                }
            }

            foreach (Match dizi in Regex.Matches(ifade, @"\[([^\][]*)\]"))
            {
                var govde = Regex.Replace(dizi.Groups[1].Value, @"\{[^{}]*\}", " ");
                foreach (var parca in govde.Split(','))
                {
                    var eleman = Regex.Match(parca.Trim(), @"^'([^']+)'$|^""([^""]+)""$");
                    if (eleman.Success)
                    {
                        Ekle(sonuc, eleman.Groups[1].Value + eleman.Groups[2].Value);
                    }
                }
            }
        }

        return sonuc;

        static void Ekle(HashSet<string> hedef, string ham)
        {
            foreach (var ad in ham.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (Regex.IsMatch(ad, @"^-?[_a-zA-Z][\w-]*$"))
                {
                    hedef.Add(ad);
                }
            }
        }
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
