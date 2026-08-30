using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Sır sızıntısı denetiminde ÖLÇÜLEN iki açığın kapalı kaldığını sınar.
/// </summary>
/// <remarks>
/// <para>
/// <b>1) Geçici dosya, kiracıya açıktı.</b> Panel LocalSystem koşar,
/// <c>Path.GetTempPath()</c> onun için <c>C:\Windows\TEMP\</c>'dir. O klasörün varsayılan DACL'i
/// ÖLÇÜLDÜ ve içinde <c>BUILTIN\IIS_IUSRS | ReadData | ContainerInherit, ObjectInherit</c> ACE'i
/// var. <c>ObjectInherit</c> yüzünden oraya yazılan her dosya bunu miras alır; ACE
/// <c>InheritOnly</c> olmadığı için dizinin kendisinde de geçerlidir ve dizin nesnesinde
/// <c>ReadData</c> = <c>FILE_LIST_DIRECTORY</c> demektir. Yani IIS_IUSRS klasörü listeleyip
/// dosyaları okuyabiliyordu; GUID'li ad bir koruma değildi. Oraya posta hesabı parolası
/// (MailEnableMailService), Stalwart TLS özel anahtarı (MailService) ve keyfi PowerShell scripti
/// (ProcessRunner) yazılıyordu.
/// </para>
/// <para>
/// <b>2) Parola HTTP yanıtına dönüyordu.</b> <c>ComponentBootstrapper.BuildProcessDetails</c>
/// argüman dizgisini olduğu gibi <c>AppException.Details</c>'e koyuyor,
/// <c>ApiExceptionHandlingMiddleware</c> ise <c>Details</c>'i ortam ayrımı YAPMADAN yanıt
/// gövdesine yazıyor. Argümanlar arasında <c>--password="&lt;MariaDB root&gt;"</c> geçiyor.
/// </para>
/// <para>
/// Testler ACL'i TAKLİT ETMEZ: gerçek bir klasöre gerçek bir miras ACE'i yazar, dosyayı gerçekten
/// oluşturur ve DACL'i diskten GERİ OKUR. Yükseltilmiş yetki gerekmez — test kendi oluşturduğu
/// nesnenin sahibidir.
/// </para>
/// </remarks>
public sealed class SecretLeakSurfaceTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-sir-{Guid.NewGuid():N}");

    public SecretLeakSurfaceTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    /// <summary>
    /// C:\Windows\TEMP'in ölçülen ACE'inin AYNISI: okuma hakkı, hem klasöre hem de içindeki
    /// dosyalara miras kalacak şekilde. Kiracıyı temsilen Users kullanılır (IIS_IUSRS her makinede
    /// çözülemeyebilir; miras davranışı SID'den bağımsızdır).
    /// </summary>
    private void KiraciyaOkumaMirasiVer()
    {
        var info = new DirectoryInfo(calisma);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static bool KiraciOkuyabiliyorMu(FileSecurity security)
    {
        var kiraci = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        return security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(kiraci) &&
                (rule.FileSystemRights & FileSystemRights.ReadData) != 0);
    }

    /// <summary>
    /// KONTROL ÖLÇÜMÜ: düz <see cref="File.WriteAllTextAsync(string,string,CancellationToken)"/>
    /// gerçekten sızdırıyor mu? Bu test geçmezse ortam açığı üretmiyordur ve asıl test hiçbir şey
    /// kanıtlamaz (koruma "doğuştan ölü" olur, kimse fark etmez).
    /// </summary>
    [Fact]
    public async Task DuzYazma_kiraci_okuma_hakkini_MIRAS_ALIR()
    {
        KiraciyaOkumaMirasiVer();
        var yol = Path.Combine(calisma, "duz.ps1");

        await File.WriteAllTextAsync(yol, "$p = 'gizli'");

        Assert.True(
            KiraciOkuyabiliyorMu(new FileInfo(yol).GetAccessControl()),
            "Ortam beklendiği gibi miras vermiyor; asıl test bu durumda hiçbir şey kanıtlamaz.");
    }

    [Fact]
    public async Task SecureTempFile_kiraci_okuma_mirasini_KESER()
    {
        KiraciyaOkumaMirasiVer();
        var yol = Path.Combine(calisma, "gizli.ps1");

        await SecureTempFile.WriteAsync(
            yol,
            "$lg.Password = 'gizli'",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var security = new FileInfo(yol).GetAccessControl();

        Assert.True(
            security.AreAccessRulesProtected,
            "Miras KAPATILMAMIŞ: C:\\Windows\\TEMP'in IIS_IUSRS:ReadData ACE'i dosyaya düşer.");
        Assert.False(
            KiraciOkuyabiliyorMu(security),
            "Kiracı sır dosyasını hâlâ okuyabiliyor.");
    }

    /// <summary>
    /// İçerik hiçbir an geniş izinle diskte durmamalı: DACL dosya AÇILIRKEN verilir. Dosya
    /// oluştuktan SONRA ACL uygulansaydı bu test yine geçerdi; bu yüzden asıl güvence
    /// <see cref="SecureTempFile.Create"/>'ın <c>FileSecurity</c>'yi oluşturma çağrısına vermesidir.
    /// Burada içeriğin doğru yazıldığı da ayrıca doğrulanır (kilit, dosyayı bozmamalı).
    /// </summary>
    [Fact]
    public async Task SecureTempFile_icerigi_bozmadan_yazar()
    {
        var yol = Path.Combine(calisma, "icerik.json");
        const string icerik = "{\"secret\":\"değer-ÜÇ\"}";

        await SecureTempFile.WriteAsync(
            yol,
            icerik,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Equal(icerik, await File.ReadAllTextAsync(yol));
    }

    [Fact]
    public void BuildSecurity_kiraci_gruplarini_ICERMEZ()
    {
        var security = SecureTempFile.BuildSecurity();

        Assert.True(security.AreAccessRulesProtected);

        var kimlikler = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToArray();

        Assert.NotEmpty(kimlikler);
        foreach (var yasak in new[]
                 {
                     WellKnownSidType.WorldSid,
                     WellKnownSidType.BuiltinUsersSid,
                     WellKnownSidType.AuthenticatedUserSid,
                     WellKnownSidType.BuiltinGuestsSid
                 })
        {
            var sid = new SecurityIdentifier(yasak, null);
            Assert.DoesNotContain(kimlikler, kimlik => kimlik.Equals(sid));
        }

        // IIS_IUSRS'in WellKnownSidType karşılığı yok; sabit SID ile bakılır.
        Assert.DoesNotContain(kimlikler, kimlik => kimlik.Value == "S-1-5-32-568");
    }

    /// <summary>
    /// BAĞLANTI TESTİ — korumanın DOĞUŞTAN ÖLÜ olmadığını kanıtlar.
    /// <para>
    /// Yukarıdaki testler <see cref="SecureTempFile"/>'ın kendisini sınar; bu test onun ÇAĞRI
    /// YERİNDE gerçekten devrede olduğunu sınar. <c>ProcessRunner.RunPowerShellAsync</c> scripti
    /// yalnız 8000 karakteri aşınca geçici dosyaya yazar, bu yüzden script bilerek şişirilir.
    /// Script kendi dosyasının (<c>$PSCommandPath</c>) DACL'ini okur: miras kapalıysa koruma
    /// canlıdır. Yardımcıyı kullanmayı bırakırsak burası kırmızıya döner.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ProcessRunner_uzun_scripti_KILITLI_dosyaya_yazar()
    {
        var sisirme = new string('#', 9000);
        var sonuc = await ProcessRunner.RunPowerShellAsync(
            $"# {sisirme}\n(Get-Acl $PSCommandPath).AreAccessRulesProtected");

        // PowerShell bulunamazsa ölçüm YAPILAMAMIŞTIR; "geçti" demek yalan olurdu.
        Assert.True(
            sonuc.ExitCode == 0,
            $"PowerShell ölçümü çalışmadı (çıkış {sonuc.ExitCode}): {sonuc.StandardError}");
        Assert.Equal("True", sonuc.StandardOutput.Trim());
    }

    /// <summary>
    /// ÖLÇÜLEN çağrı: mariadb kurulumu <c>--password="&lt;root&gt;"</c> ile çalıştırılıyor ve
    /// başarısızlıkta bu dizge HTTP yanıt gövdesine kadar gidiyordu.
    /// </summary>
    [Fact]
    public void BuildProcessDetails_parolayi_MASKELER()
    {
        const string parola = "R00t-Parola-Gizli!";

        var details = ComponentBootstrapper.BuildProcessDetails(
            "mariadb-install-db.exe",
            $"--datadir=\"C:\\data\" --service=\"AdaMariaDB\" --password=\"{parola}\" --port=3306",
            exitCode: 1,
            standardOutput: string.Empty,
            errorOutput: string.Empty);

        Assert.DoesNotContain(parola, details, StringComparison.Ordinal);
        // Kayıt teşhis değerini KAYBETMEMELİ: hangi alanın gizlendiği görünmeye devam etsin.
        Assert.Contains("--port=3306", details, StringComparison.Ordinal);
        Assert.Contains("mariadb-install-db.exe", details, StringComparison.Ordinal);
    }

    /// <summary>
    /// Zaman aşımı yolu AYRI bir dize kurar; mutasyon denemesi bu yolun hiçbir testle
    /// kapsanmadığını ortaya çıkardı. Aynı parola aynı şekilde sızardı.
    /// </summary>
    [Fact]
    public void BuildTimeoutDetails_parolayi_MASKELER()
    {
        const string parola = "Zaman-Asimi-Parolasi!";

        var details = ComponentBootstrapper.BuildTimeoutDetails(
            "mariadb-install-db.exe",
            $"--service=\"AdaMariaDB\" --password=\"{parola}\" --port=3306",
            TimeSpan.FromMinutes(10));

        Assert.DoesNotContain(parola, details, StringComparison.Ordinal);
        Assert.Contains("--port=3306", details, StringComparison.Ordinal);
    }

    /// <summary>
    /// Süreç çıktısı da maskelenmeli: dış kurulumcular kendi komut satırlarını hata metnine basar.
    /// </summary>
    [Fact]
    public void BuildProcessDetails_surec_ciktisindaki_parolayi_da_MASKELER()
    {
        const string parola = "Std-Out-Parola!";

        var details = ComponentBootstrapper.BuildProcessDetails(
            "setup.exe",
            "--quiet",
            exitCode: 2,
            standardOutput: $"komut: setup --password={parola}",
            errorOutput: $"reddedildi: token={parola}");

        Assert.DoesNotContain(parola, details, StringComparison.Ordinal);
    }
}
