using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// UZUN İŞLERİN "sürüyor" bilgisinin UÇTAN UCA ayakta kalması.
/// <para>
/// Zincir dört dosyada yaşıyor (denetleyici → uç → istemci sarmalayıcısı → ekran) ve halkalardan
/// biri geri alınırsa hata SESSİZCE döner: iş yine başlar, ekran yine "çalışıyor" görünür, ama
/// sayfa yenilendiğinde yine kaybolur. Bu yüzden zincirin kendisi kaynaktan sabitleniyor —
/// antivirüs tarafında aynı gerekçeyle yapıldığı gibi
/// (bkz. <see cref="AntivirusScanLifetimeWiringTests"/>).
/// </para>
/// </summary>
public sealed class RunningJobWiringTests
{
    /// <summary>
    /// Uzun süren HER uç çalışan-iş kaydı AÇMALI.
    /// <para>
    /// Kaydı açmayı unutmak, tam da kapatılan hatanın geri gelmesidir: iş sunucuda sürer, panel
    /// hiçbir şey bilmez, kullanıcı ikinci kez tetikler. Liste ELLE yazılı ama DAHİL ETME listesi
    /// değil: her satır var olan bir eyleme bakar ve eylem kaydı bırakırsa test yeşil kalır.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("BackupController.cs", "BackupSiteAsync")]
    [InlineData("BackupController.cs", "BackupDatabaseAsync")]
    [InlineData("BackupController.cs", "BackupMailAsync")]
    [InlineData("BackupController.cs", "BackupPanelAsync")]
    [InlineData("BackupController.cs", "BackupSubscriptionAsync")]
    [InlineData("BackupController.cs", "RunScheduleNowAsync")]
    [InlineData("BackupController.cs", "RestoreAsync")]
    [InlineData("BackupController.cs", "RestoreItemsAsync")]
    [InlineData("MigrationController.cs", "MigrateSiteAsync")]
    [InlineData("MigrationController.cs", "MigrateSiteStreamAsync")]
    [InlineData("SoftwareController.cs", "InstallAsync")]
    [InlineData("SoftwareController.cs", "InstallDefaultsAsync")]
    public void Uzun_Uclar_Calisan_Is_Kaydi_Aciyor(string dosya, string eylem)
    {
        var govde = EylemGovdesi(Oku($"src/AdaPanel.Api/Controllers/{dosya}"), eylem);

        // `await using`: iş nasıl biterse bitsin (başarı, hata, iptal) kayıt temizlenir. Düz
        // `await runningJobs.BeginAsync(...)` yazılsaydı kayıt asla düşmez ve panel o türden bir
        // daha iş başlatamazdı (409) -- yani düzeltme kendi kendini bozardı.
        Assert.Contains("await using var job = await runningJobs.BeginAsync(", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Üç modülün de "sürüyor" ucu olmalı; sayfa yenilendikten sonra geri okunacak TEK kaynak
    /// budur.
    /// </summary>
    [Theory]
    [InlineData("BackupController.cs")]
    [InlineData("MigrationController.cs")]
    [InlineData("SoftwareController.cs")]
    public void Her_Modulun_Suren_Is_Ucu_Var(string dosya)
    {
        var kaynak = Oku($"src/AdaPanel.Api/Controllers/{dosya}");

        Assert.Contains("[HttpGet(\"running\")]", kaynak, StringComparison.Ordinal);
        Assert.Contains("runningJobs.GetStatusAsync(", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// KİRACI İZOLASYONU: yedek ucu kapsamlı kullanıcıya AÇIK olduğu için süzgeç ZORUNLU. İş
    /// etiketi site/yedek adı taşır; süzgeçsiz bir müşteri, başka bir müşterinin sitesinin
    /// yedeklendiğini şeritten okurdu.
    /// </summary>
    [Fact]
    public void Yedek_Suren_Is_Ucu_Kiraci_Suzgecini_Uyguluyor()
    {
        var govde = EylemGovdesi(Oku("src/AdaPanel.Api/Controllers/BackupController.cs"), "GetRunningJobsAsync");

        Assert.Contains("GetAllowedSubscriptionIdsAsync(", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sözleşme sunucudan istemciye BOZULMADAN taşınmalı; alan adı kayarsa ekran sessizce boş
    /// şerit gösterir.
    /// </summary>
    [Fact]
    public void Suren_Is_Sozlesmesi_Istemciye_Aynen_Tasiniyor()
    {
        var dto = Oku("src/AdaPanel.Core/Dtos/RunningJobs/RunningJobDtos.cs");
        var composable = Oku("frontend/src/composables/useRunningTask.ts");

        Assert.Contains("DateTime ServerTimeUtc", dto, StringComparison.Ordinal);
        Assert.Contains("serverTimeUtc: string", composable, StringComparison.Ordinal);
        Assert.Contains("startedAtUtc: string", composable, StringComparison.Ordinal);
        Assert.Contains("detectedAtUtc: string", composable, StringComparison.Ordinal);
    }

    /// <summary>
    /// GEÇEN SÜRE SUNUCUNUN BAŞLANGIÇ ANINDAN hesaplanmalı.
    /// <para>
    /// Composable eskiden süreyi yalnız kendi <c>begin()</c> anından ölçüyordu; yenilenmiş bir
    /// sayfada 40 dakikadır süren bir iş için "0:05" yazmak, işi YENİ BAŞLAMIŞ göstermekti.
    /// Ayrıca sunucunun kendi "şimdi"si kullanılmazsa saati sapmış bir istemcide süre yanlış
    /// (hatta negatif) çıkardı.
    /// </para>
    /// </summary>
    [Fact]
    public void Gecen_Sure_Sunucunun_Baslangic_Anindan_Hesaplaniyor()
    {
        var composable = Oku("frontend/src/composables/useRunningTask.ts");

        Assert.Contains("serverStart?: RunningTaskStart", composable, StringComparison.Ordinal);
        Assert.Contains("serverNowMs - startedAtMs", composable, StringComparison.Ordinal);

        // Negatif süre YASAK: saat sapması "-3:12" gibi bir metin üretmemeli.
        Assert.Contains("Math.max(0,", composable, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dört ekranın da şeridi SUNUCUDAN beslemesi ve yoklamayı bırakmaması gerekir; yalnız kendi
    /// belleğine bakan bir şerit sayfa yenilenince boşalır.
    /// </summary>
    [Theory]
    [InlineData("BackupView.vue")]
    [InlineData("SiteBackupView.vue")]
    [InlineData("MigrationView.vue")]
    [InlineData("SoftwareView.vue")]
    public void Ekranlar_Suren_Isi_Sunucudan_Okuyor(string ekranDosyasi)
    {
        var ekran = Oku($"frontend/src/views/{ekranDosyasi}");

        Assert.Contains("useServerRunningTask(", ekran, StringComparison.Ordinal);
        Assert.Contains("interrupted-label", ekran, StringComparison.Ordinal);
        // Ekran açılır açılmaz bir kez okunmalı; yoksa yenilenmiş sayfa şeridi hiç doldurmaz.
        Assert.Contains(".start", ekran, StringComparison.Ordinal);
    }

    /// <summary>
    /// SAHTE YÜZDE YASAĞI metin düzeyinde de korunur: "sürüyor, X dakikadır" dürüsttür, "%63"
    /// uydurmadır. Yeni metinlerin hiçbiri yüzde yer tutucusu taşımamalı.
    /// </summary>
    [Fact]
    public void Suren_Is_Metinleri_Yuzde_Uydurmuyor()
    {
        var tr = Sozluk("frontend/src/i18n/tr.json");
        var en = Sozluk("frontend/src/i18n/en.json");

        var anahtarlar = new[]
        {
            "panel.runningJob.backup",
            "panel.runningJob.restore",
            "panel.runningJob.migration",
            "panel.runningJob.software",
            "panel.runningJob.resumedHint",
            "panel.runningJob.interrupted",
            "panel.running.interruptedHint",
            "panel.running.dismiss"
        };

        foreach (var anahtar in anahtarlar)
        {
            Assert.True(tr.ContainsKey(anahtar), $"TR karşılığı yok: {anahtar}");
            Assert.True(en.ContainsKey(anahtar), $"EN karşılığı yok: {anahtar}");

            foreach (var metin in new[] { tr[anahtar], en[anahtar] })
            {
                Assert.DoesNotContain("{percent", metin, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("{eta", metin, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("{remaining", metin, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("%", metin, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Aynı anda ikinci iş 409 döndürüyor; kullanıcı ekranda HAM anahtar görmemeli, yani dört
    /// çakışma anahtarının da iki dilde karşılığı olmalı.
    /// </summary>
    [Fact]
    public void Cakisma_Anahtarlarinin_Iki_Dilde_Karsiligi_Var()
    {
        var tr = Sozluk("frontend/src/i18n/tr.json");
        var en = Sozluk("frontend/src/i18n/en.json");

        foreach (var anahtar in new[]
                 {
                     "runningjob.backup_in_progress",
                     "runningjob.restore_in_progress",
                     "runningjob.migration_in_progress",
                     "runningjob.software_in_progress"
                 })
        {
            Assert.True(tr.ContainsKey(anahtar), $"TR karşılığı yok: {anahtar}");
            Assert.True(en.ContainsKey(anahtar), $"EN karşılığı yok: {anahtar}");
        }
    }

    /// <summary>
    /// "Kesildi" durumu ekranda AYRI ve UYARI olarak görünmeli: yarıda kalmış bir yedeği sessizce
    /// yok saymak, kullanıcının olmayan bir yedeğe güvenmesi demektir.
    /// </summary>
    [Fact]
    public void Kesildi_Durumu_Ekranda_Uyari_Olarak_Gosteriliyor()
    {
        var serit = Oku("frontend/src/components/RunningTaskBanner.vue");

        Assert.Contains("interruptedLabel", serit, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", serit, StringComparison.Ordinal);
        // Kesilme şeridinde döner halka OLMAMALI: hiçbir şey sürmüyor.
        Assert.DoesNotContain("rt-interrupted\" role=\"status\"", serit, StringComparison.Ordinal);
    }

    // ===============================================================================================
    // Yardimcilar
    // ===============================================================================================

    private static Dictionary<string, string> Sozluk(string goreliYol) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(Oku(goreliYol))!;

    private static string EylemGovdesi(string kaynak, string eylemAdi)
    {
        var baslangic = kaynak.IndexOf($"> {eylemAdi}(", StringComparison.Ordinal);
        if (baslangic < 0)
        {
            baslangic = kaynak.IndexOf($" {eylemAdi}(", StringComparison.Ordinal);
        }

        Assert.True(baslangic >= 0, $"Eylem bulunamadı: {eylemAdi}");

        // İmza + gövde: bir sonraki HTTP niteliğine, ilk özel yardımcıya, XML belgesine ya da
        // dosya sonuna kadar. Yardımcıları içeri almak YANLIŞ POZİTİF üretirdi.
        var sonraki = Regex.Match(kaynak[baslangic..], @"\n\s*(\[Http|private |/// )");
        var uzunluk = sonraki.Success ? sonraki.Index : kaynak.Length - baslangic;
        return kaynak.Substring(baslangic, uzunluk);
    }

    private static string Oku(string goreliYol)
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        var kok = dizin?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
        return File.ReadAllText(Path.Combine(kok, goreliYol.Replace('/', Path.DirectorySeparatorChar)));
    }
}
