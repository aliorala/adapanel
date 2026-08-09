using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Denetim kaydı KAPSAMINI kaynak üzerinden korur: durum değiştiren her ucun iz bıraktığını ve
/// hiçbir kayda parola/sır GEÇMEDİĞİNİ sabitler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kaynak taraması:</b> burada korunan şey tek bir davranış değil, bir SINIF — "yeni bir
/// durum değiştiren uç eklendiğinde denetim kaydı yazmayı unutmak". Tek tek uçları çalıştıran
/// testler yalnız bugün var olanları korur; yarın eklenen uç sessizce izsiz kalırdı. Panelde bu
/// hata sınıfı zaten gerçekleşmişti: FilesController, MailController ve FtpController'da
/// <c>IAuditService</c> bağımlılığı bile yoktu.
/// </para>
/// <para>
/// <b>Neden "parola" taraması:</b> parolayı kayda yazmamak, çalışma anında ölçülmesi zor ama
/// kaynakta apaçık görülen bir kuraldır. Bir çağrı yeri <c>request.Password</c>'ü denetim
/// çağrısına eklediği anda test kırılır — kayda düşüp yedeklere yayılmadan ÖNCE.
/// (İkinci savunma hattı <c>AuditSecretRedactor</c>; o da AuditLogReadTests'te doğrulanıyor.)
/// </para>
/// </remarks>
public sealed class AuditCoverageTests
{
    /// <summary>Denetim kaydı zorunlu tutulan denetleyiciler.</summary>
    private static readonly string[] Denetleyiciler =
    [
        "FilesController.cs",
        "MailController.cs",
        "FtpController.cs",
        "DatabasesController.cs",
        "TerminalController.cs"
    ];

    /// <summary>
    /// Durum DEĞİŞTİRMEYEN POST uçları. Salt-okunur bir aramayı her çağrıda kayda yazmak, kaydı
    /// gürültüye boğup asıl değişiklikleri görünmez kılardı.
    /// </summary>
    private static readonly string[] SaltOkunurUclar =
    [
        "[HttpPost(\"search\")]"
    ];

    [Fact]
    public void Durum_degistiren_HER_uc_denetim_kaydi_yazar()
    {
        var kok = KaynakKoku();
        var eksik = new List<string>();
        var incelenen = 0;

        foreach (var dosyaAdi in Denetleyiciler)
        {
            var yol = Path.Combine(kok, "src", "AdaPanel.Api", "Controllers", dosyaAdi);
            var metin = File.ReadAllText(yol);

            Assert.True(
                metin.Contains("IAuditService", StringComparison.Ordinal),
                $"{dosyaAdi}: IAuditService bagimliligi YOK — bu denetleyici hic iz birakmiyor.");

            foreach (var (imza, govde) in UcGovdeleri(metin))
            {
                if (SaltOkunurUclar.Any(muaf => imza.StartsWith(muaf, StringComparison.Ordinal)))
                {
                    continue;
                }

                incelenen++;
                if (!govde.Contains("AuditAsync(", StringComparison.Ordinal))
                {
                    eksik.Add($"{dosyaAdi} → {imza}");
                }
            }
        }

        // ÖLÇEMEDİK ile SORUN YOK ayrımı: ayrıştırma bozulup hiçbir uç bulunamazsa test sessizce
        // geçer ve bir daha hiçbir şeyi korumazdı.
        Assert.True(incelenen >= 40, $"Uc taramasi bozulmus olabilir: yalnizca {incelenen} durum degistiren uc bulundu.");

        Assert.True(
            eksik.Count == 0,
            "Denetim kaydi YAZMAYAN durum degistiren uclar var (kim/ne zaman/neye/ne yapti cevapsiz kalir):\n  "
            + string.Join("\n  ", eksik));
    }

    [Fact]
    public void Denetim_cagrilarina_PAROLA_gecirilmez()
    {
        var kok = KaynakKoku();
        var ihlaller = new List<string>();

        // Denetim çağrısının argüman listesi: "AuditAsync(" ile açılan parantezin kapanışına kadar.
        // "WriteAuditAsync(" de kasten eşleşir (DatabasesController'ın modüle özgü sarmalayıcısı).
        var cagri = new Regex(@"AuditAsync\(", RegexOptions.Compiled);

        foreach (var dosyaAdi in Denetleyiciler)
        {
            var yol = Path.Combine(kok, "src", "AdaPanel.Api", "Controllers", dosyaAdi);
            var metin = File.ReadAllText(yol);

            foreach (Match eslesme in cagri.Matches(metin))
            {
                var argumanlar = ArgumanListesi(metin, eslesme.Index + eslesme.Length - 1);
                var kod = MetinDisiKod(argumanlar);

                if (kod.Contains("Password", StringComparison.OrdinalIgnoreCase)
                    || kod.Contains("Parola", StringComparison.OrdinalIgnoreCase)
                    || kod.Contains("Secret", StringComparison.OrdinalIgnoreCase))
                {
                    ihlaller.Add($"{dosyaAdi}: {Kisalt(argumanlar)}");
                }
            }
        }

        Assert.True(
            ihlaller.Count == 0,
            "Denetim kaydina PAROLA/SIR gecirilen cagri(lar) var — kayit, korumaya calistigi sirrin ikinci deposu olur:\n  "
            + string.Join("\n  ", ihlaller));
    }

    [Fact]
    public void Terminal_ucu_CALISTIRILAN_KOMUTU_kayda_yazar()
    {
        var yol = Path.Combine(KaynakKoku(), "src", "AdaPanel.Api", "Controllers", "TerminalController.cs");
        var metin = File.ReadAllText(yol);

        var cagriBasi = metin.IndexOf("AuditAsync(", StringComparison.Ordinal);
        Assert.True(cagriBasi > 0, "TerminalController denetim kaydi yazmiyor.");

        var argumanlar = ArgumanListesi(metin, metin.IndexOf('(', cagriBasi));

        // Panel LocalSystem olarak kosar: bu uc, sunucuda SYSTEM yetkisiyle keyfi kod calistirmanin
        // ta kendisidir. Komut kaydedilmezse bir olaydan sonra "sunucuda ne yapildi" sorusu
        // CEVAPSIZ kalir — denetim kaydinin var olma sebebi tam olarak budur.
        Assert.True(
            argumanlar.Contains("request.Command", StringComparison.Ordinal),
            "Terminal denetim kaydi calistirilan KOMUTU icermiyor; yalniz cikis kodu yazmak yetmez.");
    }

    /// <summary>
    /// Dosyayı uç uç ayırır: her <c>[Http...]</c> özniteliğinden bir sonrakine (ya da sınıf sonuna)
    /// kadar olan metin o ucun gövdesi sayılır. Tam bir C# ayrıştırıcısı değil ama korumak istediği
    /// şey için yeterli ve kırılgan olmayan bir yaklaşım.
    /// </summary>
    private static IEnumerable<(string Imza, string Govde)> UcGovdeleri(string metin)
    {
        var http = new Regex(@"\[Http(Get|Post|Put|Delete|Patch)[^\]]*\]", RegexOptions.Compiled);
        var eslesmeler = http.Matches(metin);

        for (var i = 0; i < eslesmeler.Count; i++)
        {
            var mevcut = eslesmeler[i];
            var son = i + 1 < eslesmeler.Count ? eslesmeler[i + 1].Index : metin.Length;
            var imza = mevcut.Value;

            // Yalnız durum DEĞİŞTİREN fiiller.
            if (!imza.StartsWith("[HttpPost", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpPut", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpDelete", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpPatch", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (imza, metin[mevcut.Index..son]);
        }
    }

    /// <summary>
    /// Argüman metninden DÜZ METİN kısımlarını atar, KOD kısımlarını bırakır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gerekçe: denetim kaydının insan tarafından okunan cümleleri zaten "password was changed"
    /// gibi ifadeler içerir — bunları ihlal saymak testi kullanılamaz hâle getirirdi. Tehlikeli
    /// olan, bir DEĞERİN geçirilmesidir: <c>request.Password</c>. Bu yüzden tırnak içindeki düz
    /// metin atılır ama enterpolasyon delikleri (<c>{...}</c>) KOD sayılıp korunur — sızıntının en
    /// olası biçimi tam olarak <c>$"... {request.Password}"</c>dır.
    /// </para>
    /// </remarks>
    private static string MetinDisiKod(string argumanlar)
    {
        var kod = new System.Text.StringBuilder();
        var metinIcinde = false;
        var delikDerinligi = 0;

        for (var i = 0; i < argumanlar.Length; i++)
        {
            var karakter = argumanlar[i];

            if (metinIcinde)
            {
                if (karakter == '\\')
                {
                    i++; // kaçırılmış karakter (ör. \") atlanır
                    continue;
                }

                if (karakter == '{' && delikDerinligi == 0)
                {
                    delikDerinligi = 1;
                    continue;
                }

                if (delikDerinligi > 0)
                {
                    if (karakter == '{')
                    {
                        delikDerinligi++;
                    }
                    else if (karakter == '}')
                    {
                        delikDerinligi--;
                        continue;
                    }

                    kod.Append(karakter);
                    continue;
                }

                if (karakter == '"')
                {
                    metinIcinde = false;
                }

                continue;
            }

            if (karakter == '"')
            {
                metinIcinde = true;
                continue;
            }

            kod.Append(karakter);
        }

        return kod.ToString();
    }

    /// <summary>Verilen açılış parantezinden dengeli kapanışa kadar olan argüman metni.</summary>
    private static string ArgumanListesi(string metin, int acilisIndeksi)
    {
        var derinlik = 0;
        for (var i = acilisIndeksi; i < metin.Length; i++)
        {
            if (metin[i] == '(')
            {
                derinlik++;
            }
            else if (metin[i] == ')')
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return metin[(acilisIndeksi + 1)..i];
                }
            }
        }

        return metin[acilisIndeksi..];
    }

    private static string Kisalt(string deger) =>
        deger.Length <= 160 ? deger : deger[..160] + "…";

    /// <summary>Depo kökünü test çıktısı klasöründen yukarı çıkarak bulur.</summary>
    private static string KaynakKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        return dizin!.FullName;
    }
}
