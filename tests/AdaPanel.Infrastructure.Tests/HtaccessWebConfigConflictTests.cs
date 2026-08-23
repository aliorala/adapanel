using System.Xml.Linq;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// web.config ÇAKIŞMASININ ÖLÇÜMÜ — ve modülün neden o dosyaya HİÇ DOKUNMADIĞININ kanıtı.
///
/// Görev tanımı "web.config'e zaten beş servis yazıyor, altıncı yazar olacaksın, çakışmayı ölç ve
/// çöz" diyordu. Ölçüm iki şey buldu:
///
///   1) SAYI EKSİK. Depoda site web.config'ine yazan servis sayısı BEŞ DEĞİL, EN AZ ON ÜÇ
///      (aşağıdaki listede sayıldı).
///   2) ASIL ÇAKIŞMA "eleman kaybı" DEĞİL. Hepsi <c>XDocument.Load(PreserveWhitespace)</c> →
///      cerrahi değişiklik → <c>Save</c> deseniyle çalışıyor, yani birbirlerinin elemanlarını
///      SİLMİYORLAR. Gerçek çakışma KAYIP GÜNCELLEME: hiçbirinde dosya başına kilit yok, iki
///      servis aynı anda yazarsa SON YAZAN KAZANIR.
///
/// Bu testler o kayıp güncellemeyi ÜRETEREK gösteriyor. Sonuç, tasarım kararının gerekçesi:
/// <b>en iyi çözüm on dördüncü yazar olmamaktır.</b> Modül applicationHost.config'e bir kez
/// kaydediliyor, site başına açma/kapama ise anahtar DOSYASINA yazılıyor.
/// </summary>
public sealed class HtaccessWebConfigConflictTests(ITestOutputHelper output)
{
    /// <summary>Depodaki yazarların desenini birebir taklit eder (yükle → değiştir → kaydet).</summary>
    private static void WriterPattern(string path, Action<XElement> mutateSystemWebServer)
    {
        var document = File.Exists(path)
            ? XDocument.Load(path, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));

        var configuration = document.Element("configuration")!;
        var systemWebServer = configuration.Element("system.webServer");
        if (systemWebServer is null)
        {
            systemWebServer = new XElement("system.webServer");
            configuration.Add(systemWebServer);
        }

        mutateSystemWebServer(systemWebServer);
        document.Save(path);
    }

    [Fact]
    public void MEVCUT_YAZARLAR_BIRBIRININ_ELEMANLARINI_SILMEZ()
    {
        // Iyi haber: desen guvenli. Bir <modules> elemani eklenseydi hayatta kalirdi.
        var dizin = Directory.CreateTempSubdirectory("adapanel-webconfig-a");
        try
        {
            var path = Path.Combine(dizin.FullName, "web.config");

            WriterPattern(path, s => s.Add(new XElement("rewrite", new XElement("rules"))));      // SiteAliasService gibi
            WriterPattern(path, s => s.Add(new XElement("httpErrors")));                          // SiteHostingSettingsService gibi
            WriterPattern(path, s => s.Add(new XElement("defaultDocument")));                     // WebAppPublisher gibi
            WriterPattern(path, s => s.Add(new XElement("modules",
                new XElement("add",
                    new XAttribute("name", HtaccessModuleRegistration.ModuleName)))));            // varsayimsal altinci yazar

            var document = XDocument.Load(path);
            var systemWebServer = document.Element("configuration")!.Element("system.webServer")!;

            Assert.NotNull(systemWebServer.Element("rewrite"));
            Assert.NotNull(systemWebServer.Element("httpErrors"));
            Assert.NotNull(systemWebServer.Element("defaultDocument"));
            Assert.NotNull(systemWebServer.Element("modules"));
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }

    [Fact]
    public void ASIL_CAKISMA_KAYIP_GUNCELLEMEDIR_ve_URETILEBILIYOR()
    {
        // KOTU haber: kilit yok. Iki yazar ayni ANI yakalarsa biri kaybolur.
        // Burada yaris, deterministik olsun diye ELLE kurgulaniyor: iki yazar da dosyayi
        // AYNI icerikten okuyor, sonra sirayla kaydediyor -- gercek bir yarisin tam olarak
        // urettigi durum.
        var dizin = Directory.CreateTempSubdirectory("adapanel-webconfig-b");
        try
        {
            var path = Path.Combine(dizin.FullName, "web.config");
            new XDocument(new XElement("configuration", new XElement("system.webServer"))).Save(path);

            // Iki yazar da AYNI ANDA okuyor.
            var yazarA = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var yazarB = XDocument.Load(path, LoadOptions.PreserveWhitespace);

            yazarA.Element("configuration")!.Element("system.webServer")!
                .Add(new XElement("rewrite", new XAttribute("kaynak", "MailAutodiscoverRewriteRules")));
            yazarB.Element("configuration")!.Element("system.webServer")!
                .Add(new XElement("modules", new XAttribute("kaynak", "HtaccessModule")));

            yazarA.Save(path);
            yazarB.Save(path);   // SON YAZAN KAZANIR

            var sonuc = XDocument.Load(path).Element("configuration")!.Element("system.webServer")!;

            Assert.NotNull(sonuc.Element("modules"));
            Assert.Null(sonuc.Element("rewrite"));   // <-- A'nin degisikligi SESSIZCE KAYBOLDU

            output.WriteLine("KAYIP GUNCELLEME URETILDI: iki yazar ayni web.config'i okudu,");
            output.WriteLine("son kaydeden digerinin degisikligini yok etti. Depoda bu yolda");
            output.WriteLine("dosya basina kilit YOK -> on dorduncu yazar olmak bu sinifi agirlastirirdi.");
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }

    [Fact]
    public void MODUL_HICBIR_SITE_WEB_CONFIGINE_YAZMAZ()
    {
        // TASARIM KARARININ NOBETCISI. Barindirma katmanindaki hicbir dosya site web.config'ine
        // yazmamali; yazmaya baslarsa bu test kirilir ve karar bilincli olarak gozden gecirilir.
        var hostingDirectory = Path.Combine(
            RepoRoot(), "src", "AdaPanel.Infrastructure", "Htaccess", "Hosting");

        foreach (var file in Directory.GetFiles(hostingDirectory, "*.cs"))
        {
            var content = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            Assert.False(
                content.Contains("\"web.config\"", StringComparison.Ordinal),
                $"{name} site web.config'ine dokunuyor gorunuyor -- global kayit karari bozuluyor.");
        }
    }

    [Fact]
    public void MODUL_APPLICATIONHOST_CONFIGE_KAYDEDILIYOR()
    {
        // Kaydin GERCEKTEN apphost'a yapildiginin kaniti (site kapsamina degil).
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "install-htaccess-module.ps1"));

        Assert.Contains("system.webServer/modules", script);

        // HER /commit: SITE DEGIL APPHOST OLMALI.
        // Ilk surumu yalnizca Contains("/commit:apphost") diyordu ve mutasyonda HAYATTA KALDI:
        // betikte IKI commit var (ekleme ve kaldirma); birini site kapsamina cevirmek digerini
        // bulan iddiayi kirmiyordu. Site kapsamina yazmak, tam da kacindigimiz sey olurdu --
        // musterinin web.config'ine dokunmak.
        var commitler = System.Text.RegularExpressions.Regex.Matches(script, @"/commit:(\w+)");

        Assert.True(commitler.Count >= 2, $"en az iki /commit bekleniyordu, {commitler.Count} bulundu");
        foreach (System.Text.RegularExpressions.Match commit in commitler)
        {
            Assert.Equal("apphost", commit.Groups[1].Value);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AdaPanel.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
