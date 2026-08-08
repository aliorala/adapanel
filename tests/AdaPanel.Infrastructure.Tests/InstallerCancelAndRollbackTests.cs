using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Kurulum yarida kesilirse panel OLU KALMAMALI, arayuz BOS KALMAMALI.
/// </summary>
/// <remarks>
/// <para>
/// IKI SESSIZ BOZULMA vardi:
/// </para>
/// <list type="number">
/// <item>
/// <c>InitializeSetup</c> sihirbaz acilmadan servisi durduruyor (gerekcesi dogru: RestartManager
/// taramasi daha once calisir). Ama dosyada <c>DeinitializeSetup</c> YOKTU ve
/// <c>StartPanelService</c> diye bir sey HIC YOKTU. Kullanici ilk ekranda Iptal'e basarsa servis
/// DURMUS kaliyordu -- siteler IIS'te calismaya devam ettigi icin kimse fark etmiyordu.
/// </item>
/// <item>
/// <c>[InstallDelete]</c> <c>{app}\wwwroot</c>'u dosyalar kopyalanmadan ONCE siliyordu ve
/// Inno'nun rollback'i <c>[InstallDelete]</c> ile silinenleri GERI GETIRMEZ. <c>install-service.ps1</c>
/// "KRITIK" isaretli ve <c>RaiseException</c> ile kurulumu iptal ediyor: eski exe geri geliyor ama
/// <c>wwwroot</c> BOS kaliyordu -- servis "Running" gorunuyor, tarayicida bos sayfa aciliyordu.
/// </item>
/// </list>
/// <para>
/// <b>SINIR -- durust olmak gerekir:</b> bir Inno Setup kurulumunun IPTAL akisi burada
/// kosturulamaz (kurulum yapmak, servis durdurmak ve makineyi degistirmek gerekirdi). Bu yuzden
/// iki katman kullanilir: (a) betigin GERCEKTEN DERLENDIGI bir ISCC derlemesi -- Pascal kodu
/// derlenmezse kurulum hicbir sey yapamaz; (b) yapinin metin denetimi. Metin denetimi kirilgandir
/// ve bu BILEREK boyledir; alternatifi hicbir denetim olmamasiydi.
/// </para>
/// </remarks>
public sealed class InstallerCancelAndRollbackTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-iss-{Guid.NewGuid():N}");

    public InstallerCancelAndRollbackTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    /// <summary>
    /// Servisi geri baslatan bir kod yolu VAR olmali.
    /// </summary>
    [Fact]
    public void Iptal_edilen_kurulum_servisi_geri_baslatir()
    {
        var betik = IssMetni();

        Assert.Contains("procedure StartPanelService;", betik);
        Assert.Contains("procedure DeinitializeSetup;", betik);

        // Bayrak olmadan "biz mi durdurduk" bilinemez ve zaten durmus bir servis yanlislikla
        // baslatilirdi.
        Assert.Contains("ServiceWasStoppedByUs", betik);

        var deinit = BolumuAl(betik, "procedure DeinitializeSetup;");
        Assert.Contains("StartPanelService", deinit);
        Assert.Contains("InstallCompleted", deinit);
    }

    /// <summary>
    /// Servisi durduran her yol bayragi KURMALI; yoksa geri baslatma calismaz.
    /// </summary>
    [Fact]
    public void Servisi_durduran_her_yol_bayragi_kurar()
    {
        var betik = IssMetni();

        var init = BolumuAl(betik, "function InitializeSetup: Boolean;");
        Assert.Contains("StopPanelService", init);
        Assert.Contains("ServiceWasStoppedByUs := True", init);

        var prepare = BolumuAl(betik, "function PrepareToInstall(var NeedsRestart: Boolean): string;");
        Assert.Contains("StopPanelService", prepare);
        Assert.Contains("ServiceWasStoppedByUs := True", prepare);
    }

    /// <summary>
    /// wwwroot ARTIK [InstallDelete] ile silinmiyor.
    /// </summary>
    /// <remarks>
    /// Rollback <c>[InstallDelete]</c> silmelerini geri getirmez; bu yuzden geri alinabilir tek
    /// yontem tasima (.bak) + basarisizlikta geri koymadir.
    /// </remarks>
    [Fact]
    public void InstallDelete_wwwrootu_artik_silmiyor()
    {
        var betik = IssMetni();
        var installDelete = BolumuAl(betik, "[InstallDelete]");

        // Yorum satirlari haric GERCEK bir girdi kalmamali.
        var girdiler = installDelete
            .Split('\n')
            .Select(satir => satir.Trim())
            .Where(satir => satir.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.DoesNotContain(girdiler, girdi => girdi.Contains("wwwroot", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// wwwroot yedeklenir, basarida atilir, BASARISIZLIKTA GERI KONUR.
    /// </summary>
    [Fact]
    public void Basarisiz_yukseltme_eski_arayuzu_geri_koyar()
    {
        var betik = IssMetni();

        Assert.Contains("function BackupWwwRoot: Boolean;", betik);
        Assert.Contains("procedure RestoreWwwRoot;", betik);
        Assert.Contains("procedure DiscardWwwRootBackup;", betik);

        // Yedek, dosyalar KOPYALANMADAN once alinmali.
        var prepare = BolumuAl(betik, "function PrepareToInstall(var NeedsRestart: Boolean): string;");
        Assert.Contains("BackupWwwRoot", prepare);

        // Basarida atilir (yoksa {app} altinda kalici bir wwwroot.bak birikirdi).
        var curStep = BolumuAl(betik, "procedure CurStepChanged(CurStep: TSetupStep);");
        Assert.Contains("ssPostInstall", curStep);
        Assert.Contains("InstallCompleted := True", curStep);
        Assert.Contains("DiscardWwwRootBackup", curStep);

        // Basarisizlikta geri konur.
        var deinit = BolumuAl(betik, "procedure DeinitializeSetup;");
        Assert.Contains("RestoreWwwRoot", deinit);
    }

    /// <summary>
    /// Installer'daki yedek surum tanimi Directory.Build.props ile AYNI olmali.
    /// </summary>
    /// <remarks>
    /// Scriptler <c>/DMyAppVersion</c> ile bu degeri ezer; ama .iss elle derlenirse damgalanan
    /// deger budur.
    /// </remarks>
    [Fact]
    public void Installer_yedek_surumu_kaynak_surumle_ayni()
    {
        var iss = Regex.Match(IssMetni(), "#define\\s+MyAppVersion\\s+\"([^\"]+)\"");
        Assert.True(iss.Success, "installer/AdaPanel.iss icinde MyAppVersion tanimi yok.");

        var props = File.ReadAllText(ScriptTestHost.ScriptPath("Directory.Build.props"));
        var kaynak = Regex.Match(props, "<Version>([^<]+)</Version>");
        Assert.True(kaynak.Success, "Directory.Build.props icinde <Version> yok.");

        Assert.Equal(kaynak.Groups[1].Value.Trim(), iss.Groups[1].Value.Trim());
    }

    /// <summary>
    /// [Code] bolumu GERCEKTEN DERLENIYOR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Metin denetimleri "yazdim" der, "calisir" demez. Inno'nun Pascal derleyicisi ise
    /// derlenmeyen bir proseduru kabul etmez -- ve derlenmeyen bir <c>[Code]</c> bolumu
    /// kurulumun HIC olmamasi demektir.
    /// </para>
    /// <para>
    /// OLCULDU: <c>[Files]</c> hatasi Pascal derlemesinden ONCE patliyor, yani publish klasoru
    /// olmadan yapilan bir derleme <c>[Code]</c>'u HIC denetlemiyor. Bu yuzden gecici bir kok
    /// kurulur. Derleme ciktisi da gecici klasore gider: depoya ya da artifacts'e hicbir sey
    /// yazilmaz.
    /// </para>
    /// </remarks>
    [Fact]
    public void Kod_bolumu_gercekten_derleniyor()
    {
        var iscc = IsccYolu();
        if (iscc is null)
        {
            // Inno Setup kurulu degil: derleme denetimi yapilamaz. Diger testler yapiyi korur.
            return;
        }

        var kok = GeciciDerlemeKokuKur();
        var cikti = Path.Combine(kok, "out");
        Directory.CreateDirectory(cikti);

        var sonuc = IsccCalistir(iscc, cikti, Path.Combine(kok, "installer", "AdaPanel.iss"));

        Assert.True(sonuc.ExitCode == 0, $"installer/AdaPanel.iss derlenmedi:\n{sonuc.Cikti}");
        Assert.Contains("Compiling [Code] section", sonuc.Cikti);
    }

    // --- duzenek ---

    private static string IssMetni() => File.ReadAllText(ScriptTestHost.ScriptPath("installer", "AdaPanel.iss"));

    /// <summary>Bir bolumun/prosedurun metnini, bir sonraki ust duzey basliga kadar dondurur.</summary>
    private static string BolumuAl(string betik, string baslik)
    {
        var baslangic = betik.IndexOf(baslik, StringComparison.Ordinal);
        Assert.True(baslangic >= 0, $"Bulunamadi: {baslik}");

        var govde = betik[(baslangic + baslik.Length)..];

        // Sonraki ust duzey tanim ya da bolum basligi.
        var sonraki = Regex.Match(govde, @"^(?:procedure |function |\[)", RegexOptions.Multiline);
        return sonraki.Success ? govde[..sonraki.Index] : govde;
    }

    private static string? IsccYolu() =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe")
        }.FirstOrDefault(File.Exists);

    /// <summary>
    /// ISCC'nin <c>[Files]</c> asamasini gecebilmesi icin en kucuk sahte depo agaci.
    /// </summary>
    private string GeciciDerlemeKokuKur()
    {
        var kok = Path.Combine(calisma, "derleme");
        var depo = ScriptTestHost.RepoRoot;

        Directory.CreateDirectory(Path.Combine(kok, "installer"));
        Directory.CreateDirectory(Path.Combine(kok, "artifacts", "publish", "win-x64"));
        Directory.CreateDirectory(Path.Combine(kok, "scripts"));

        File.Copy(Path.Combine(depo, "installer", "AdaPanel.iss"), Path.Combine(kok, "installer", "AdaPanel.iss"));
        File.Copy(Path.Combine(depo, "installer", "adapanel.ico"), Path.Combine(kok, "installer", "adapanel.ico"));
        File.Copy(Path.Combine(depo, "components.json"), Path.Combine(kok, "components.json"));

        foreach (var betik in Directory.GetFiles(Path.Combine(depo, "scripts"), "*.ps1"))
        {
            File.Copy(betik, Path.Combine(kok, "scripts", Path.GetFileName(betik)));
        }

        File.WriteAllText(Path.Combine(kok, "artifacts", "publish", "win-x64", "AdaPanel.Api.exe"), "stub");
        return kok;
    }

    private static (int ExitCode, string Cikti) IsccCalistir(string iscc, string ciktiKlasoru, string issYolu)
    {
        using var surec = Process.Start(new ProcessStartInfo
        {
            FileName = iscc,
            // VersionInfoVersion SAYISAL olmak zorunda ("0.0.0-test" gecersizdir).
            Arguments = $"\"/O{ciktiKlasoru}\" \"/DMyAppVersion=0.0.0.1\" \"{issYolu}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        var cikti = surec.StandardOutput.ReadToEnd() + surec.StandardError.ReadToEnd();
        surec.WaitForExit(180_000);
        return (surec.ExitCode, cikti);
    }
}
