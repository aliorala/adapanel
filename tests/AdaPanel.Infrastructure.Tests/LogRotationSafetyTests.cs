using System.Diagnostics;
using AdaPanel.Infrastructure.Logs;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Log döndürmenin SİLME KAPISI — gerçek dosyalar, gerçek kilitler, gerçek junction'lar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu testler var:</b> bu depoda özyinelemeli bir silme bir MÜŞTERİ SİTESİNİ silebilecek
/// hataya yol açtı. Log döndürme de silen bir özelliktir ve gece, kimse bakmazken çalışır. Silme
/// yolunun her kuralı burada gerçek dosya sistemi üzerinde sınanır.
/// </para>
/// <para>
/// <b>ÖLÇÜM (bu makinede, testten bağımsız olarak da doğrulandı):</b> bir dosyayı
/// <c>FILE_SHARE_DELETE</c> ile açmış yazan varken düz <c>File.Delete</c> <b>BAŞARILI olur ve
/// dosyayı yok eder</b>; <c>FileShare.None + FileOptions.DeleteOnClose</c> ise paylaşım ihlaliyle
/// reddedilir. Yani "sil, IOException'ı yut" kalıbı bu iş için yetersizdir — sessiz veri kaybı
/// üretir. Aşağıdaki <see cref="Baskasinin_actigi_dosya_SILINMEZ"/> tam olarak bunu sabitler.
/// </para>
/// </remarks>
public sealed class LogRotationSafetyTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-logrot-guvenlik-{Guid.NewGuid():N}");
    private readonly string kok;

    public LogRotationSafetyTests()
    {
        kok = Path.Combine(calisma, "loglar");
        Directory.CreateDirectory(kok);
    }

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi çaba */ }
    }

    /// <summary>Meşru silme ENGELLENMİYOR: kapının aşırı sıkı olması da bir arıza olurdu.</summary>
    [Fact]
    public void Kurallara_uyan_dosya_silinir()
    {
        var yol = DosyaYaz(kok, "u_ex260101.log.zst", "arsiv");

        var sonuc = LogFileGuard.SafeDelete(kok, yol, [".zst"]);

        Assert.Equal(LogFileGuardOutcome.Done, sonuc);
        Assert.False(File.Exists(yol));
    }

    /// <summary>
    /// BAŞKASININ AÇIK TUTTUĞU DOSYA SİLİNMEZ — hem de yazan taraf silmeye izin veren paylaşım
    /// kipini seçmiş olsa bile.
    /// </summary>
    /// <remarks>
    /// Tutucu burada bilerek <c>FileShare.Delete</c> ile açılıyor: düz <c>File.Delete</c> bu
    /// dosyayı SİLERDİ. Yani bu test, kapının IOException yakalayan bir sarmalayıcıya
    /// indirgenmediğini kanıtlar.
    /// </remarks>
    [Fact]
    public void Baskasinin_actigi_dosya_SILINMEZ()
    {
        var yol = DosyaYaz(kok, "canli.log.zst", "canli-veri");

        using (var tutucu = new FileStream(
                   yol, FileMode.Open, FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            var sonuc = LogFileGuard.SafeDelete(kok, yol, [".zst"]);

            Assert.Equal(LogFileGuardOutcome.InUse, sonuc);
        }

        Assert.True(File.Exists(yol), "Canli olarak tutulan dosya SILINDI.");
        Assert.Equal("canli-veri", File.ReadAllText(yol));
    }

    /// <summary>Kilit kalktığında aynı dosya normal şekilde silinebilmeli (kapı kalıcı engel değil).</summary>
    [Fact]
    public void Kilit_kalkinca_dosya_silinebilir()
    {
        var yol = DosyaYaz(kok, "gecici.log.zst", "veri");

        using (var _ = new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(LogFileGuardOutcome.InUse, LogFileGuard.SafeDelete(kok, yol, [".zst"]));
        }

        Assert.Equal(LogFileGuardOutcome.Done, LogFileGuard.SafeDelete(kok, yol, [".zst"]));
        Assert.False(File.Exists(yol));
    }

    /// <summary>Kökün DIŞINDAKİ bir yol hiçbir koşulda silinmez.</summary>
    [Fact]
    public void Kok_disindaki_dosya_silinmez()
    {
        var disarida = DosyaYaz(calisma, "musteri.log", "musterinin-dosyasi");

        var sonuc = LogFileGuard.SafeDelete(kok, disarida, [".log"]);

        Assert.Equal(LogFileGuardOutcome.OutsideRoot, sonuc);
        Assert.True(File.Exists(disarida));
    }

    /// <summary>
    /// ".." ile kökten kaçan yol silinmez. <c>PathContainment</c> normalleştirmesi bunu kapatır;
    /// düz dizge birleştirme yapılsaydı burası bir dizin geçişi açığı olurdu.
    /// </summary>
    [Fact]
    public void Ust_dizine_kacan_yol_silinmez()
    {
        var hedef = DosyaYaz(calisma, "kurban.log", "veri");
        var kacan = Path.Combine(kok, "..", "kurban.log");

        var sonuc = LogFileGuard.SafeDelete(kok, kacan, [".log"]);

        Assert.Equal(LogFileGuardOutcome.OutsideRoot, sonuc);
        Assert.True(File.Exists(hedef));
    }

    /// <summary>
    /// ALT KLASÖRDEKİ dosya silinmez: kural "kökün altında" değil, "kökün DOĞRUDAN altında"dır.
    /// Alt klasöre inmediğimiz için bir kavşağın içine yürüme ihtimali de kalmaz.
    /// </summary>
    [Fact]
    public void Alt_klasordeki_dosya_silinmez()
    {
        var alt = Path.Combine(kok, "arsiv");
        Directory.CreateDirectory(alt);
        var yol = DosyaYaz(alt, "derin.log.zst", "veri");

        var sonuc = LogFileGuard.SafeDelete(kok, yol, [".zst"]);

        Assert.Equal(LogFileGuardOutcome.OutsideRoot, sonuc);
        Assert.True(File.Exists(yol));
    }

    /// <summary>Beklenmeyen uzantı silinmez: klasöre düşmüş başka bir dosya panelin işi değildir.</summary>
    [Theory]
    [InlineData("wp-config.php")]
    [InlineData("veritabani.bak")]
    [InlineData("notlar.txt")]
    [InlineData("adsiz")]
    public void Yanlis_uzantili_dosya_silinmez(string ad)
    {
        var yol = DosyaYaz(kok, ad, "onemli");

        var sonuc = LogFileGuard.SafeDelete(kok, yol, [".log", ".zst"]);

        Assert.Equal(LogFileGuardOutcome.WrongExtension, sonuc);
        Assert.True(File.Exists(yol));
    }

    /// <summary>Klasör, dosya sanılıp silinmeye kalkışılmaz.</summary>
    [Fact]
    public void Klasor_dosya_sanilip_silinmez()
    {
        var klasor = Path.Combine(kok, "gunluk.log");
        Directory.CreateDirectory(klasor);
        File.WriteAllText(Path.Combine(klasor, "icerik.txt"), "veri");

        var sonuc = LogFileGuard.SafeDelete(kok, klasor, [".log"]);

        Assert.NotEqual(LogFileGuardOutcome.Done, sonuc);
        Assert.True(Directory.Exists(klasor));
        Assert.True(File.Exists(Path.Combine(klasor, "icerik.txt")));
    }

    /// <summary>
    /// KLASÖRÜN KENDİSİ BİR KAVŞAKSA YÖNETİLMEZ.
    /// </summary>
    /// <remarks>
    /// Bir sitenin <c>W3SVC&lt;id&gt;</c> klasörü müşterinin site köküne kavşak olarak kurulmuşsa,
    /// içindeki "*.log" dosyaları aslında müşterinin dosyalarıdır. Panelin orada silme yapması, bu
    /// depoda bir kez yaşanmış hata sınıfının aynısı olurdu.
    /// </remarks>
    [Fact]
    public void Kavsak_olan_klasor_YONETILMEZ()
    {
        var musteriSitesi = Path.Combine(calisma, "musteri-sitesi");
        Directory.CreateDirectory(musteriSitesi);
        File.WriteAllText(Path.Combine(musteriSitesi, "index.php"), "site");
        File.WriteAllText(Path.Combine(musteriSitesi, "erisim.log"), "musteri verisi");

        var baglanti = Path.Combine(calisma, "W3SVC7");
        JunctionOlustur(baglanti, musteriSitesi);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");

        Assert.False(LogFileGuard.IsManageableDirectory(baglanti));

        // Hedefteki hiçbir şeye dokunulmamış olmalı.
        Assert.True(File.Exists(Path.Combine(musteriSitesi, "index.php")));
        Assert.True(File.Exists(Path.Combine(musteriSitesi, "erisim.log")));
    }

    /// <summary>Gerçek klasör yönetilir — koruma, meşru işi durdurmamalı.</summary>
    [Fact]
    public void Gercek_klasor_yonetilir()
    {
        Assert.True(LogFileGuard.IsManageableDirectory(kok));
        Assert.False(LogFileGuard.IsManageableDirectory(Path.Combine(calisma, "hic-olmayan")));
        Assert.False(LogFileGuard.IsManageableDirectory("   "));
    }

    /// <summary>
    /// Salt-okunur dosya silinmez. Bayrağı KALDIRMIYORUZ: bir yöneticinin bilerek koyduğu koruma,
    /// temizlik işinin kaldıracağı bir şey değildir.
    /// </summary>
    [Fact]
    public void Salt_okunur_dosya_silinmez()
    {
        var yol = DosyaYaz(kok, "korumali.log.zst", "veri");
        File.SetAttributes(yol, FileAttributes.ReadOnly);

        try
        {
            var sonuc = LogFileGuard.SafeDelete(kok, yol, [".zst"]);

            Assert.Equal(LogFileGuardOutcome.AccessDenied, sonuc);
            Assert.True(File.Exists(yol));
        }
        finally
        {
            File.SetAttributes(yol, FileAttributes.Normal);
        }
    }

    /// <summary>Olmayan dosya sessizce "yok" döner: tur idempotent olmalı, ikinci çağrı patlamamalı.</summary>
    [Fact]
    public void Olmayan_dosya_hata_uretmez()
    {
        var sonuc = LogFileGuard.SafeDelete(kok, Path.Combine(kok, "yok.log.zst"), [".zst"]);

        Assert.Equal(LogFileGuardOutcome.Missing, sonuc);
    }

    /// <summary><see cref="LogFileGuard.IsInUse"/> canlı tutulan dosyayı tanır, serbest dosyayı tanımaz.</summary>
    [Fact]
    public void Kullanimda_mi_sorusu_dogru_cevaplanir()
    {
        var yol = DosyaYaz(kok, "u_ex260101.log", "veri");

        Assert.False(LogFileGuard.IsInUse(yol));

        using var tutucu = new FileStream(yol, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(LogFileGuard.IsInUse(yol));
    }

    private static string DosyaYaz(string klasor, string ad, string icerik)
    {
        var yol = Path.Combine(klasor, ad);
        File.WriteAllText(yol, icerik);
        return yol;
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        var yol = Path.Combine(Path.GetTempPath(), $"ada-junction-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            yol,
            $"$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null");
        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{yol}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            surec.WaitForExit(60_000);
        }
        finally
        {
            try { File.Delete(yol); } catch { /* en iyi çaba */ }
        }
    }
}
