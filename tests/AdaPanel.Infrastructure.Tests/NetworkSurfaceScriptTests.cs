using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Y10 + ağ yüzeyi + K3 — kurulum betiklerinin GERÇEKTEN koşturulan davranışı.
/// </summary>
/// <remarks>
/// <para>
/// Üç bulgu: (1) <c>enable-iis-features.ps1</c> DNS rolünü koşulsuz kuruyor ve depoda
/// <c>Set-DnsServerRecursion</c> geçen tek satır yoktu → her yeni sunucu ilk günden açık özyinelemeli
/// çözücü. (2) <c>configure-firewall.ps1</c> Stalwart kurulu olmasa bile 6 posta portunu açıyordu.
/// (3) <c>initialize-panel-certificate.ps1</c> her kurulumda <c>icacls /reset /T /C</c> ile K1'i geri
/// alıyordu; ölçüldü: <c>certs\panel.password</c> yükseltilmemiş kullanıcı olarak okunabiliyordu.
/// </para>
/// <para>
/// Testler betiğin bir kopyasını değil, depodaki GERÇEK dosyayı <c>-DefineFunctionsOnly</c> ile
/// dot-source eder; ana akış (rol kurulumu, güvenlik duvarı yazımı) hiç çalışmaz.
/// </para>
/// </remarks>
public sealed class NetworkSurfaceScriptTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-ag-{Guid.NewGuid():N}");

    public NetworkSurfaceScriptTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    private static ScriptTestHost.ScriptResult FonksiyonlariCalistir(string betikAdi, string govde)
    {
        var yol = ScriptTestHost.ScriptPath("scripts", betikAdi);
        return ScriptTestHost.Run($". {ScriptTestHost.PowerShellLiteral(yol)} -DefineFunctionsOnly\n{govde}");
    }

    // ===================================================================================
    // Y10 — DNS özyinelemesi
    // ===================================================================================

    /// <summary>
    /// Özyineleme kapatma fonksiyonu VAR ve çağrılabilir olmalı. Bu makinede (Windows 10, DNS rolü yok)
    /// <c>Set-DnsServerRecursion</c> bulunmadığı için <c>$false</c> döner ve elle yapılacak komutu basar —
    /// sessizce "başarılı" DEMEZ.
    /// </summary>
    [Fact]
    public void Dns_ozyineleme_kapatma_fonksiyonu_vardir_ve_sessiz_yalan_soylemez()
    {
        var sonuc = FonksiyonlariCalistir(
            "enable-iis-features.ps1",
            "$r = Disable-AdaPanelDnsRecursion\nWrite-Output \"SONUC=$r\"");

        Assert.Equal(0, sonuc.ExitCode);
        // Cmdlet varsa True, yoksa False + uyarı; hangisi olursa olsun fonksiyon KOŞTU.
        Assert.True(
            sonuc.Tumu.Contains("SONUC=True", StringComparison.Ordinal) ||
            (sonuc.Tumu.Contains("SONUC=False", StringComparison.Ordinal) &&
             sonuc.Tumu.Contains("Set-DnsServerRecursion", StringComparison.Ordinal)),
            $"Beklenmeyen cikti: {sonuc.Tumu}");
    }

    /// <summary>
    /// DNS rolü kurulan HER dalda özyineleme kapatma çağrılmalı — "sonra yaparız" penceresi kalmamalı.
    /// </summary>
    [Fact]
    public void Dns_rolu_kurulan_her_dalda_ozyineleme_kapatilir()
    {
        var icerik = File.ReadAllText(ScriptTestHost.ScriptPath("scripts", "enable-iis-features.ps1"));

        Assert.Contains("Install-WindowsFeature -Name DNS", icerik, StringComparison.Ordinal);
        // Rol yeni kuruldu dalı + zaten kurulu dalı = en az iki çağrı.
        var cagriSayisi = icerik.Split("Disable-AdaPanelDnsRecursion").Length - 1;
        Assert.True(cagriSayisi >= 3, $"Beklenen: tanim + en az 2 cagri; bulunan toplam gecis: {cagriSayisi}");
    }

    // ===================================================================================
    // Ağ yüzeyi — dinleyicisi olmayan port açılmaz
    // ===================================================================================

    /// <summary>
    /// Stalwart kurulu DEĞİLKEN posta portları açılmamalı. Ölçüm, panelin kendi kurulum düzeni
    /// üzerinden yapılır (<c>runtimes\stalwart\...\stalwart.exe</c>).
    /// </summary>
    [Fact]
    public void Posta_sunucusu_kurulu_degilse_kurulu_degil_denir()
    {
        var dataRoot = Path.Combine(calisma, "bos-veri-koku");
        Directory.CreateDirectory(dataRoot);

        var sonuc = FonksiyonlariCalistir(
            "configure-firewall.ps1",
            $"$r = Test-AdaPanelMailServerInstalled -DataRoot {ScriptTestHost.PowerShellLiteral(dataRoot)}\nWrite-Output \"SONUC=$r\"");

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SONUC=False", sonuc.Tumu, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stalwart GERÇEKTEN kuruluyken posta portları açılmalı — fazla kapatma postayı öldürür.
    /// </summary>
    [Fact]
    public void Posta_sunucusu_kuruluysa_kurulu_denir()
    {
        var dataRoot = Path.Combine(calisma, "dolu-veri-koku");
        var stalwartBin = Path.Combine(dataRoot, "runtimes", "stalwart", "bin");
        Directory.CreateDirectory(stalwartBin);
        File.WriteAllText(Path.Combine(stalwartBin, "stalwart.exe"), "sahte");

        var sonuc = FonksiyonlariCalistir(
            "configure-firewall.ps1",
            $"$r = Test-AdaPanelMailServerInstalled -DataRoot {ScriptTestHost.PowerShellLiteral(dataRoot)}\nWrite-Output \"SONUC=$r\"");

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SONUC=True", sonuc.Tumu, StringComparison.Ordinal);
    }

    /// <summary>
    /// Betik, dinleyicisi olmayan portu artık koşulsuz açmamalı: karar fonksiyonları kullanılıyor olmalı.
    /// </summary>
    [Fact]
    public void Guvenlik_duvari_betigi_portlari_kosulsuz_acmaz()
    {
        var icerik = File.ReadAllText(ScriptTestHost.ScriptPath("scripts", "configure-firewall.ps1"));

        Assert.Contains("if ($mailInstalled)", icerik, StringComparison.Ordinal);
        Assert.Contains("if ($dnsInstalled)", icerik, StringComparison.Ordinal);
    }

    // ===================================================================================
    // K3 — certs ACL'i: /reset yok, doğru ACL yeniden kuruluyor
    // ===================================================================================

    /// <summary>
    /// Sertifika betiği artık <c>icacls /reset</c> ÇALIŞTIRMAMALI: o çağrı mirası geri açıp
    /// K1 sertleştirmesini her kurulumda iptal ediyordu.
    /// </summary>
    [Fact]
    public void Sertifika_betigi_ACL_i_sifirlamaz()
    {
        var icerik = File.ReadAllText(ScriptTestHost.ScriptPath("scripts", "initialize-panel-certificate.ps1"));

        // Yorumlarda "eskiden /reset yapiliyordu" gecebilir; aranan sey CALISTIRILAN komuttur.
        var kodSatirlari = icerik
            .Split('\n')
            .Select(satir => satir.Trim())
            .Where(satir => !satir.StartsWith('#'))
            .ToArray();

        Assert.DoesNotContain(kodSatirlari, satir => satir.Contains("icacls", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Set-AdaPanelSecureAcl", icerik, StringComparison.Ordinal);
    }

    /// <summary>
    /// K3 GERÇEK ÖLÇÜM: betiğin ACL fonksiyonu gerçek bir klasöre uygulanır; öncesinde yazılan
    /// <c>Everyone = FullControl</c> ACE'i sonrasında ARTIK OLMAMALIDIR.
    /// </summary>
    [Fact]
    public void Sertifika_betiginin_ACL_fonksiyonu_kiraciyi_gercekten_dusurur()
    {
        var hedef = Path.Combine(calisma, "certs");
        Directory.CreateDirectory(hedef);

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var info = new DirectoryInfo(hedef);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            everyone,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(acl);

        Assert.Contains(everyone, AcikSidler(hedef));

        var sonuc = FonksiyonlariCalistir(
            "initialize-panel-certificate.ps1",
            $"$r = Set-AdaPanelSecureAcl -Path {ScriptTestHost.PowerShellLiteral(hedef)}\nWrite-Output \"SONUC=$r\"");

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SONUC=True", sonuc.Tumu, StringComparison.Ordinal);
        Assert.DoesNotContain(everyone, AcikSidler(hedef));
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), AcikSidler(hedef));
        // Fazla kapatma kontrolü: Administrators erişimi KALMALI (yoksa installer .pfx yazamaz).
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), AcikSidler(hedef));
    }

    private static IReadOnlyList<SecurityIdentifier> AcikSidler(string dizin) =>
        new DirectoryInfo(dizin)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .Distinct()
            .ToArray();
}
