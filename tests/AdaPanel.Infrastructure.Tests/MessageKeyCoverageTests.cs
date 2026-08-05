using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Sunucunun URETEBILDIGI her mesaj anahtarinin arayuzde bir karsiligi olmali.
/// <para>
/// BULUNAN HATA: <c>common.unexpected_error</c> hicbir dilde yoktu. ApiExceptionHandlingMiddleware
/// yakalanmayan HER istisnayi bu kodla donduruyor, yani panelin EN SIK hata yolu buydu; kullanici
/// kirmizi kutuda birebir "common.unexpected_error" yazisini goruyordu. Ayni durumda
/// <c>common.forbidden</c>, <c>not_found</c> ve iki API anahtari hatasi vardi.
/// </para>
/// <para>
/// Bu test tek tek anahtarlari degil, SINIFI kapatir: yeni bir hata anahtari eklendiginde ceviri
/// unutulursa burada yakalanir. Boylece "ham anahtar ekranda" hatasi geri gelemez.
/// </para>
/// </summary>
public sealed class MessageKeyCoverageTests
{
    [Fact]
    public void EveryMessageKeyTheServerCanReturn_HasATurkishAndEnglishTranslation()
    {
        var kok = BulKok();
        var tr = OkuSozluk(Path.Combine(kok, "frontend", "src", "i18n", "tr.json"));
        var en = OkuSozluk(Path.Combine(kok, "frontend", "src", "i18n", "en.json"));

        var anahtarlar = ToplaAnahtarlar(Path.Combine(kok, "src"));

        // OLCEMEDIK ile SORUN YOK ayrimi: tarama bozulup hicbir anahtar bulamazsa test sessizce
        // gecerdi ve bir daha hicbir seyi korumazdi. Bugun 300'un uzerinde anahtar var.
        Assert.True(
            anahtarlar.Count > 200,
            $"Mesaj anahtari taramasi bozulmus olabilir: yalnizca {anahtarlar.Count} anahtar bulundu. Kok: {Path.GetFullPath(kok)}, dosya: {Directory.EnumerateFiles(Path.Combine(kok, "src"), "*.cs", SearchOption.AllDirectories).Count()}");

        var eksik = new List<string>();
        foreach (var anahtar in anahtarlar)
        {
            var trVar = tr.Contains(anahtar);
            var enVar = en.Contains(anahtar);
            if (!trVar || !enVar)
            {
                eksik.Add($"{anahtar} (tr={(trVar ? "var" : "YOK")}, en={(enVar ? "var" : "YOK")})");
            }
        }

        Assert.True(
            eksik.Count == 0,
            "Cevirisi olmayan mesaj anahtarlari var; kullanici ekranda ham anahtari gorur:\n  " + string.Join("\n  ", eksik));
    }

    /// <summary>
    /// Sunucu kaynagindaki mesaj anahtarlarini toplar: AppException, ApiResponse.Fail/Ok ve
    /// middleware'in dogrudan yazdigi kodlar.
    /// </summary>
    private static IReadOnlyCollection<string> ToplaAnahtarlar(string kaynakKoku)
    {
        // Anahtar bicimi: kucuk harfle baslar ve en az bir nokta icerir ("cron.run_failed").
        // Dosya adlari, yollar ve SQL parcalari bu bicime uymaz.
        //
        // YALNIZCA HATA YOLLARI taranir (AppException, Fail, middleware'in yazdigi kodlar).
        // Sebep: ekranda ham anahtar goren yol budur -- her ekran hatayi
        // "resolveApiMessage(error) -> t(anahtar)" ile basar ve ceviri yoksa anahtarin KENDISI
        // gorunur. Basari mesajlarinda ise arayuz sunucunun anahtarini kullanmaz, kendi metnini
        // gosterir ("panel.tasks.saved" gibi); onlari da zorunlu tutmak, hicbir yerde
        // gorunmeyen 80'den fazla anahtar icin ceviri istemek olurdu.
        var desen = new Regex(
            @"(?:new AppException\(|\.Fail\(|WriteResponseAsync\([^,]+,\s*[^,]+,\s*)""([a-z][a-zA-Z0-9_]*(?:\.[a-zA-Z0-9_]+)+)""",
            RegexOptions.Compiled);

        var anahtarlar = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dosya in Directory.EnumerateFiles(kaynakKoku, "*.cs", SearchOption.AllDirectories))
        {
            if (dosya.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                dosya.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match eslesme in desen.Matches(File.ReadAllText(dosya)))
            {
                var anahtar = eslesme.Groups[1].Value;

                // Dosya uzantilari ve alan adlari anahtar DEGILDIR; bicim onlari da yakalar.
                if (Regex.IsMatch(anahtar, @"\.(json|php|exe|dll|log|zip|txt|xml|cs|ps1|sql|conf|ini|pem|crt|key)$"))
                {
                    continue;
                }

                anahtarlar.Add(anahtar);
            }
        }

        return anahtarlar;
    }

    private static HashSet<string> OkuSozluk(string yol)
    {
        using var belge = JsonDocument.Parse(File.ReadAllText(yol));
        var kume = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ozellik in belge.RootElement.EnumerateObject())
        {
            kume.Add(ozellik.Name);
        }

        return kume;
    }

    private static string BulKok()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                // NORMALIZE EDILIR. Aksi halde yol "...\bin\Debug\net8.0-windows\..\..\..\.." gibi
                // birikir ve icinde "\bin\" gecer; bin/obj eleyen filtre butun kaynak dosyalarini
                // atlar, tarama sessizce 0 anahtar bulur ve test hicbir seyi korumaz.
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
