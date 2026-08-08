using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Paketlenecek ICERIGIN denetimi.
/// </summary>
/// <remarks>
/// <para>
/// <c>package-installer.ps1</c> yalnizca uretilen exe'nin boyutunu ve SHA256'sini YAZDIRIYORDU.
/// Bu bir KAYITTIR, dogrulama degil. Bu makinede bir EXE bulastiricisi (Synaptics) uretilen
/// kurulum paketlerini bulastirmisti ve "yayindaki dosyayi indir, yereldekiyle hash karsilastir"
/// yontemi bu sinifa TAMAMEN KORDUR: yereldeki dosya zaten bulasikti, iki hash de birebir
/// tutuyordu.
/// </para>
/// <para>
/// Hash karsilastirmasi "aktarim sirasinda bozulma" yakalar. "Kaynakta bulasma" yakalamaz.
/// Yakalayan sey, paketin ICINDE NE OLDUGUNU beklenen listeyle karsilastirmaktir -- ve o denetim
/// burada test edilir.
/// </para>
/// </remarks>
public sealed class PublishOutputVerificationTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-publish-{Guid.NewGuid():N}");

    public PublishOutputVerificationTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    /// <summary>
    /// Beklenen icerikte denetim GECER ve her dosyanin SHA256'si manifest'e yazilir.
    /// </summary>
    [Fact]
    public void Beklenen_icerikte_manifest_uretilir()
    {
        var publish = Path.Combine(calisma, "publish");
        Directory.CreateDirectory(Path.Combine(publish, "wwwroot"));
        PeDosyasiYaz(Path.Combine(publish, "AdaPanel.Api.exe"));
        File.WriteAllText(Path.Combine(publish, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(publish, "wwwroot", "index.html"), "<html></html>");

        var manifest = Path.Combine(calisma, "publish-manifest.json");
        var sonuc = DenetimiCalistir(publish, manifest, imzasizMi: true);

        Assert.True(sonuc.ExitCode == 0, sonuc.Tumu);
        Assert.True(File.Exists(manifest), "Manifest yazilmadi.");

        var icerik = File.ReadAllText(manifest);
        Assert.Contains("AdaPanel.Api.exe", icerik);
        Assert.Contains("wwwroot/index.html", icerik);
        Assert.Contains("\"sha256\"", icerik);
    }

    /// <summary>
    /// BEKLENMEYEN bir calistirilabilir varsa paket URETILMEZ.
    /// </summary>
    /// <remarks>
    /// Bulastirici yuku publish klasorune fazladan bir exe/dll birakir. Boyut/hash yazdirmak bunu
    /// gormez; beklenen listeyle karsilastirmak gorur.
    /// </remarks>
    [Fact]
    public void Beklenmeyen_calistirilabilir_paketlemeyi_durdurur()
    {
        var publish = Path.Combine(calisma, "publish");
        Directory.CreateDirectory(publish);
        PeDosyasiYaz(Path.Combine(publish, "AdaPanel.Api.exe"));
        PeDosyasiYaz(Path.Combine(publish, "SynTPEnh.exe"));

        var sonuc = DenetimiCalistir(publish, Path.Combine(calisma, "m.json"), imzasizMi: true);

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("BEKLENMEYEN CALISTIRILABILIR", sonuc.Tumu);
        Assert.Contains("SynTPEnh.exe", sonuc.Tumu);
    }

    /// <summary>
    /// UZANTIYA DEGIL ICERIGE bakar: PE dosyasi masum bir adla gizlenemez.
    /// </summary>
    /// <remarks>
    /// Uzanti suzgeci (".exe/.dll/.scr") yuku ".dat" adiyla birakan bir bulastiriciyi kacirirdi.
    /// Denetim MZ + PE basligini okur.
    /// </remarks>
    [Fact]
    public void Uzantisi_degistirilmis_calistirilabilir_de_yakalanir()
    {
        var publish = Path.Combine(calisma, "publish");
        Directory.CreateDirectory(Path.Combine(publish, "wwwroot", "assets"));
        PeDosyasiYaz(Path.Combine(publish, "AdaPanel.Api.exe"));
        PeDosyasiYaz(Path.Combine(publish, "wwwroot", "assets", "logo.png"));

        var sonuc = DenetimiCalistir(publish, Path.Combine(calisma, "m.json"), imzasizMi: true);

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("logo.png", sonuc.Tumu);
    }

    /// <summary>
    /// BULASMA IMZASI (CompanyName='Synaptics') paketlemeyi durdurur.
    /// </summary>
    /// <remarks>
    /// Bu durum bu makinede bilerek uretilemez (bir dosyaya sahte VersionInfo yazmak icin gercek
    /// bir PE kaynak bolumu gerekir). Bu yuzden yalnizca UST VERI OKUYUCU enjekte edilir; karar
    /// mantigi ve gezme betigin GERCEK kodudur.
    /// </remarks>
    [Fact]
    public void Bulasma_imzasi_tasiyan_dosya_paketlemeyi_durdurur()
    {
        var publish = Path.Combine(calisma, "publish");
        Directory.CreateDirectory(publish);
        PeDosyasiYaz(Path.Combine(publish, "AdaPanel.Api.exe"));

        var sonuc = UstVeriIleCalistir(publish, imzaDurumu: "NotSigned", yayinci: "", sirket: "Synaptics Incorporated");

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("BULASMA IMZASI", sonuc.Tumu);
    }

    /// <summary>
    /// Beklenen ama BEKLENMEDIK BIR YAYINCI tarafindan imzalanmis dosya paketlemeyi durdurur.
    /// </summary>
    /// <remarks>
    /// Yalnizca "imzali mi" diye sormak yetmez: saldirgan kendi sertifikasiyla imzalayabilir.
    /// Yayincinin KIM oldugu sorulur.
    /// </remarks>
    [Fact]
    public void Beklenmedik_yayinci_paketlemeyi_durdurur()
    {
        var publish = Path.Combine(calisma, "publish");
        Directory.CreateDirectory(publish);
        PeDosyasiYaz(Path.Combine(publish, "e_sqlite3.dll"));

        var sonuc = UstVeriIleCalistir(publish, imzaDurumu: "Valid", yayinci: "Kotu Adam Ltd", sirket: "Kotu Adam");

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("BEKLENMEDIK YAYINCI", sonuc.Tumu);
    }

    /// <summary>
    /// Imzali gelmesi gereken bir dosya imzasini kaybetmisse paketlemeyi durdurur.
    /// </summary>
    [Fact]
    public void Imzasini_kaybetmis_dosya_paketlemeyi_durdurur()
    {
        var publish = Path.Combine(calisma, "publish");
        Directory.CreateDirectory(publish);
        PeDosyasiYaz(Path.Combine(publish, "wpfgfx_cor3.dll"));

        var sonuc = UstVeriIleCalistir(publish, imzaDurumu: "NotSigned", yayinci: "", sirket: "");

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("IMZA GECERSIZ", sonuc.Tumu);
    }

    // --- duzenek ---

    /// <summary>
    /// Sahte PE dosyalari gercek bir Authenticode imzasi tasiyamaz; bu yuzden yalnizca UST VERI
    /// OKUYUCU degistirilir. Dolasma, PE tanima, karar ve manifest yazma betigin GERCEK kodudur.
    /// </summary>
    private static ScriptTestHost.ScriptResult UstVeriIleCalistir(
        string publishDir,
        string imzaDurumu,
        string yayinci,
        string sirket,
        string? manifestPath = null)
    {
        var betik = ScriptTestHost.ScriptPath("scripts", "verify-publish-output.ps1");
        Assert.True(File.Exists(betik), $"Denetim betigi bulunamadi: {betik}");

        var saglayici =
            "{ param($yol) [pscustomobject]@{ ImzaDurumu = " + ScriptTestHost.PowerShellLiteral(imzaDurumu) +
            "; Yayinci = " + ScriptTestHost.PowerShellLiteral(yayinci) +
            "; Sirket = " + ScriptTestHost.PowerShellLiteral(sirket) + " } }";

        var manifestArg = manifestPath is null
            ? string.Empty
            : " -ManifestPath " + ScriptTestHost.PowerShellLiteral(manifestPath);

        return ScriptTestHost.Run(
            ". " + ScriptTestHost.PowerShellLiteral(betik) + " -DefineFunctionsOnly\n" +
            "$saglayici = " + saglayici + "\n" +
            "Invoke-AdaPublishVerification -PublishDir " + ScriptTestHost.PowerShellLiteral(publishDir) +
            manifestArg + " -MetadataProvider $saglayici | Out-Null\n" +
            "Write-Output \"DENETIM=gecti\"\n");
    }

    private static ScriptTestHost.ScriptResult DenetimiCalistir(string publishDir, string manifestPath, bool imzasizMi)
    {
        Assert.True(imzasizMi, "Bu duzenek yalnizca imzasiz sahte PE dosyalari icin kullanilir.");
        return UstVeriIleCalistir(publishDir, "NotSigned", string.Empty, string.Empty, manifestPath);
    }

    /// <summary>Gecerli MZ + PE basligi tasiyan en kucuk dosya.</summary>
    private static void PeDosyasiYaz(string yol)
    {
        var bayt = new byte[128];
        bayt[0] = 0x4D; // M
        bayt[1] = 0x5A; // Z
        BitConverter.GetBytes(0x40).CopyTo(bayt, 0x3C);
        bayt[0x40] = 0x50; // P
        bayt[0x41] = 0x45; // E
        bayt[0x42] = 0x00;
        bayt[0x43] = 0x00;

        Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
        File.WriteAllBytes(yol, bayt);
    }
}
