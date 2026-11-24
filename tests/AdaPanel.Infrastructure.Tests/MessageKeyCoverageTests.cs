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

        var anahtarlar = ToplaAnahtarlar(kok);

        // OLCEMEDIK ile SORUN YOK ayrimi: tarama bozulup hicbir anahtar bulamazsa test sessizce
        // gecerdi ve bir daha hicbir seyi korumazdi. Bugun 300'un uzerinde anahtar var.
        Assert.True(
            anahtarlar.Count > 200,
            $"Mesaj anahtari taramasi bozulmus olabilir: yalnizca {anahtarlar.Count} anahtar bulundu. Kok: {Path.GetFullPath(kok)}");

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
    /// <summary>
    /// Tarayıcı, <c>MessageKey</c>/<c>ErrorKey</c> DIŞINDAKİ anahtar alanlarını da GÖRÜYOR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir test:</b> desen genişletildiğinde ana test kızarmadı — çünkü ortaya çıkan
    /// <c>advisor.*</c> anahtarlarının çevirisi zaten vardı. Yani genişletmeyi geri almak da hiçbir
    /// testi kızartmazdı; koruma sessizce daralır ve bir sonraki çevirisiz <c>TitleKey</c> yine
    /// kaçardı. Kapsamın kendisi ölçülmeli.
    /// </para>
    /// <para>
    /// <c>advisor.disk.critical</c> seçildi çünkü kaynakta YALNIZCA <c>TitleKey = "..."</c> olarak
    /// geçiyor (ölçüldü: tek eşleşme, <c>AdvisorService.cs</c>). Eski desen onu bulamıyordu.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("advisor.disk.critical")]   // yalnizca TitleKey olarak geciyor
    [InlineData("advisor.update.detail")]   // yalnizca DetailKey olarak geciyor
    public void Tarayici_TitleKey_ve_DetailKey_atamalarini_da_GORUR(string anahtar)
    {
        var bulunanlar = ToplaAnahtarlar(BulKok());

        Assert.Contains(anahtar, bulunanlar);
    }

    [Fact]
    public void UretimTaramasi_SrcAnahtarlariniKorur_RepoKopyalariniVeFrontendTestleriniDislar()
    {
        var root = Path.Combine(Path.GetTempPath(), "ada-message-key-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Write("src/Production.cs", "new AppException(\"scope.error\"); TitleKey = \"scope.title\"; DetailKey = \"scope.detail\"; StatusMessage = \"scope.status\";");
            Write(".tmp/private-copy/src/OldProduction.cs", "TitleKey = \"outside.private_copy\";");
            Write("tests/TestFixture.cs", "new AppException(\"outside.test_fixture\");");
            Write("frontend/src/Fixture.cs", "DetailKey = \"outside.frontend\";");
            Write("frontend/node_modules/example/Fixture.cs", "StatusMessage = \"outside.node_modules\";");
            Write("src/obj/Generated.cs", "TitleKey = \"outside.generated_obj\";");
            Write("src/bin/Generated.cs", "TitleKey = \"outside.generated_bin\";");
            var keys = ToplaAnahtarlar(root);
            Assert.Equal(new[] { "scope.detail", "scope.error", "scope.status", "scope.title" }, keys.Order(StringComparer.Ordinal).ToArray());

            void Write(string relative, string source)
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, source);
            }
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UretimTaramasi_EksikSrcYerineRepoFixturelariniTaramaz()
    {
        var root = Path.Combine(Path.GetTempPath(), "ada-message-key-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        try
        {
            File.WriteAllText(Path.Combine(root, "tests", "Fixture.cs"), "TitleKey = \"outside.only_fixture\";");
            Assert.Throws<DirectoryNotFoundException>(() => ToplaAnahtarlar(root));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyCollection<string> ToplaAnahtarlar(string depoKoku)
    {
        // Every caller supplies the repository root. Fixture copies, tests and node_modules
        // are outside the production contract and must never be traversed.
        var kaynakKoku = Path.Combine(Path.GetFullPath(depoKoku), "src");
        // Anahtar bicimi: kucuk harfle baslar ve en az bir nokta icerir ("cron.run_failed").
        // Dosya adlari, yollar ve SQL parcalari bu bicime uymaz.
        //
        // YALNIZCA HATA YOLLARI taranir (AppException, Fail, middleware'in yazdigi kodlar,
        // bir de DTO'lara ATANAN anahtarlar: "MessageKey = ..." / "ErrorKey = ...").
        //
        // ATAMA BICIMI SONRADAN EKLENDI ve HEMEN BIR ACIK YAKALADI: BackupService yedek
        // sikistirilamadiginda BackupRestoreWarningDto.MessageKey = "backup.compression_failed"
        // uretiyordu, ama bu anahtarin hicbir dilde karsiligi yoktu -- kullanici uyari kutusunda
        // birebir "backup.compression_failed" yazisini goruyordu. Desen yalnizca "new AppException("
        // ve ".Fail(" cagrilarina baktigi icin sinifin bu yarisi korumasizdi.
        // Sebep: ekranda ham anahtar goren yol budur -- her ekran hatayi
        // "resolveApiMessage(error) -> t(anahtar)" ile basar ve ceviri yoksa anahtarin KENDISI
        // gorunur. Basari mesajlarinda ise arayuz sunucunun anahtarini kullanmaz, kendi metnini
        // gosterir ("panel.tasks.saved" gibi); onlari da zorunlu tutmak, hicbir yerde
        // gorunmeyen 80'den fazla anahtar icin ceviri istemek olurdu.
        // ACILIS PARANTEZINDEN SONRA `\s*` ZORUNLU -- ve bu bir suslemedir sanilmasin, OLCULMUS
        // bir korluktu: desen `new AppException(` ile anahtarin arasinda satir sonu KABUL
        // ETMIYORDU, yani cok satirli cagrilar
        //     throw new AppException(
        //         "migration.archive_too_large", ...
        // hic taranmiyordu. Nobetci tam da bu yuzden `migration.archive_too_large`'i kacirdi;
        // anahtarin hicbir dilde karsiligi yoktu ve kullanici hata kutusunda ham anahtari
        // gorecekti. Uzun anahtar + uzun details metni satiri tasirdi, yani cok satirli yazim
        // istisna degil NORMAL olan bicimdir.
        //
        // Yanlis eslesme riski yok: tirnakli dize hemen bosluktan SONRA gelmek zorunda; ilk
        // argumani degisken olan cagrilar eslesmez.
        // ATAMA DALI `Message|Error` ILE SINIRLI DEGIL, `...Key` ile BITEN HER ADI tarar.
        //
        // OLCULDU: eski desen yalnizca `MessageKey`/`ErrorKey` ariyordu. Oysa kullaniciya cikan
        // metinler baska adlarla da tasiniyor -- `AdvisorService` uyari kartlarini `TitleKey` ve
        // `DetailKey` ile uretiyor (advisor.update.title, advisor.disk.critical, ...) ve bunlarin
        // TAMAMI nobetcinin gormedigi yerdeydi. Cevirisi unutulsa kullanici uyari kartinda ham
        // anahtari gorurdu -- korumanin var olma sebebinin ta kendisi.
        //
        // `StatusMessage` AYRICA taranir: "...Key" ile bitmedigi icin yukaridaki dala girmiyordu,
        // ama kullaniciya AYNEN gosterilen bir anahtar tasiyor. FTP sunucu durumu bu alanla
        // bildiriliyor (`ftp.control_port_conflict` gibi) ve arayuz onu `t()` ile basiyor --
        // cevirisi unutulsa ekranda ham anahtar gorunurdu.
        //
        // OLCULDU (genisletmeden ONCE): depoda `StatusMessage = "..."` bicimindeki anahtar
        // sayisi 4 ve dordunun de cevirisi VAR. Yani bu genisletme mevcut bir aciği kapatmiyor,
        // ILERIDE acilacak olani kapatiyor -- bedeli sifir oldugu icin simdi yapildi.
        //
        // Yanlis eslesme riski dusuk: yakalanan dize ZATEN anahtar bicimine uymak zorunda
        // (kucuk harfle baslar, en az bir nokta, yalniz harf/rakam/alt cizgi). Dosya adlari,
        // yollar ve SQL parcalari bu bicime uymaz.
        var desen = new Regex(
            @"(?:new AppException\(\s*|\.Fail\(\s*|WriteResponseAsync\([^,]+,\s*[^,]+,\s*|\b(?:[A-Za-z]*Key|StatusMessage)\s*=\s*)""([a-z][a-zA-Z0-9_]*(?:\.[a-zA-Z0-9_]+)+)""",
            RegexOptions.Compiled);

        var anahtarlar = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dosya in UretimKaynakDosyalari(kaynakKoku))
        {
            if (dosya.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                dosya.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            // KONSOL KURTARMA ARACI HARIC -- ve bu bir muafiyet degil, KAPSAM tanimi.
            //
            // Bu testin sordugu soru "kullanici EKRANDA ham anahtar gorur mu". Konsol parola
            // kurtarma araci tarayiciya HIC cikmaz: anahtarlarini kendi metin tablosuyla
            // karsilar (ParolaBelirlemeKomutu.HataMetni -> "HATA: Panelde hic yonetici hesabi
            // yok..."), yani ceviri sozlugune hic bakmaz.
            //
            // OLCULDU: desen `...Key` atamalarini da tarayacak sekilde genisletilince bu uc
            // anahtar (recovery.*) ortaya cikti. Onlari sozluge EKLEMEK yanlis olurdu -- hicbir
            // ekranda kullanilmadiklari icin bu kez "olu anahtar" denetimi kizardi. Dogru cevap,
            // kapsamin disinda olduklarini SOYLEMEK.
            if (dosya.Contains($"{Path.DirectorySeparatorChar}Recovery{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                dosya.EndsWith("ConsolePasswordRecovery.cs", StringComparison.Ordinal))
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

    private static IEnumerable<string> UretimKaynakDosyalari(string kaynakKoku)
    {
        var pending = new Stack<string>(); pending.Push(kaynakKoku);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                // Prune generated output before walking it, rather than rejecting only its files.
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }
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
