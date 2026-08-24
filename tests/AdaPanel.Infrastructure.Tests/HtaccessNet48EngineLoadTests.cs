using System.Diagnostics;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// ÖZELLİĞİN ASIL KANITI: çekirdek motor <b>.NET Framework 4.8 içinde yükleniyor</b> ve bir
/// <c>RewriteRule</c> gerçekten SONUCU DEĞİŞTİRİYOR.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu test var:</b> <c>.htaccess</c> desteğinin üç katmanı da yazılmıştı (motor, IIS
/// modülü, panel ekranı) ama özellik ÇALIŞMIYORDU ve kimse bunu ölçmemişti. Sebep yapısaldı:
/// motor <c>net8.0-windows</c>, IIS modülü ise <c>net48</c> olmak zorunda (<c>System.Web</c> yalnız
/// orada var) ve bir net8 derlemesi CLR 4 içinde yüklenemez. Bu depoda en pahalı hata sınıfı budur:
/// "kod var, ekranı var, hiç çalışmıyor". Bu yüzden bağlantı bir daha KANITSIZ bırakılamaz.
/// </para>
/// <para>
/// <b>Neden ayrı bir SÜREÇ:</b> bu test projesi <c>net8.0-windows</c>'tur. Modülün DLL'ini bu
/// süreçte yüklemek hiçbir şey kanıtlamazdı — .NET 8 çalışma zamanının bir net48 derlemesini
/// açabilmesi, o derlemenin <b>.NET Framework'te</b> çalıştığını göstermez. İddia tam olarak
/// "CLR 4 içinde yükleniyor" olduğu için gerçek bir CLR 4 süreci gerekiyor.
/// </para>
/// <para>
/// <b>Neden Windows PowerShell 5.1:</b> bu makinede kurulu, garanti edilen ve fazladan hiçbir
/// artefakt üretmeyen tek .NET Framework 4.x barındırıcısıdır (<c>powershell.exe</c>, PowerShell 7
/// DEĞİL — o .NET Core'dur). Alternatifi, yalnız bu testi koşturmak için depoya ikinci bir net48
/// test projesi eklemekti; aynı kanıtı üretirdi, bedeli daha büyüktü.
/// </para>
/// <para>
/// <b>DÜRÜST SINIR:</b> bu test <b>IIS'i kanıtlamaz</b>. Modülün <c>w3wp</c> içinde GAC'tan
/// yüklendiği, <c>applicationHost.config</c> kaydının tuttuğu ve gerçek bir HTTP isteğinin
/// yeniden yazıldığı BU MAKİNEDE ölçülemedi (IIS sitesi ve yükseltilmiş yetki yok).
/// Kanıtlanan şey şudur ve azımsanacak bir şey değildir: <b>motor net48'de yükleniyor, kayıt
/// yerine oturuyor ve bir kural sonucu değiştiriyor</b> — yani özelliği ölü tutan engel kalktı.
/// </para>
/// </remarks>
public sealed class HtaccessNet48EngineLoadTests
{
    /// <summary>
    /// Motor net48'de yükleniyor ve <c>RewriteRule</c> UYGULANIYOR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Betik, modülün <c>HtaccessHttpModule.Apply</c> içinde izlediği YOLUN AYNISINI izler:
    /// <c>Install()</c> → <c>HtaccessEngineRegistry.Current</c> → <c>Compile(...)</c> →
    /// <c>Evaluate(...)</c>. Farklı bir yol kullanılsaydı test, üretimde koşmayan bir şeyi
    /// ölçerdi.
    /// </para>
    /// <para>
    /// Üç şey birden denetleniyor, çünkü üçü ayrı ayrı yanlış olabilir:
    /// (1) sürecin GERÇEKTEN CLR 4 olduğu ve derlemenin net48 damgası taşıdığı,
    /// (2) motorun kayıt yerine OTURDUĞU (eskiden burası hep <c>null</c>'dı),
    /// (3) kuralın SONUCU DEĞİŞTİRDİĞİ.
    /// </para>
    /// <para>
    /// Eşleşmeyen istek de ölçülüyor: onsuz "her zaman Rewrite dönen" bozuk bir motor da testi
    /// geçerdi.
    /// </para>
    /// </remarks>
    [Fact]
    public void MOTOR_NET48_ICINDE_YUKLENIYOR_ve_REWRITERULE_SONUCU_DEGISTIRIYOR()
    {
        var modulDll = ModulCiktisi();
        Assert.True(
            File.Exists(modulDll),
            $"net48 modul ciktisi bulunamadi: {modulDll}. Cozum icin projeyi derleyin "
            + "(AdaPanel.sln icinde AdaPanel.HtaccessModule projesi var).");

        using var site = new GeciciSite();
        site.Yaz(
            ".htaccess",
            """
            RewriteEngine On
            RewriteRule ^urun/([0-9]+)$ /index.php?id=$1 [L]
            """);

        var cikti = Net48Kos(modulDll, site.Kok);

        // (1) Gercekten .NET Framework mi?
        Assert.Contains("HOST_CLR=v4.0.30319", cikti);
        Assert.Contains("DERLEME_CALISMA_ZAMANI=v4.0.30319", cikti);

        // (2) Motor kayit yerine OTURDU mu? Eskiden burasi HER ZAMAN null'di ve modul her istegi
        //     dokunmadan geciriyordu.
        Assert.Contains("MOTOR=AdaPanel.HtaccessModule.HtaccessModuleEngineAdapter", cikti);

        // (3) Kural SONUCU DEGISTIRDI mi?
        Assert.Contains("ESLESEN=Rewrite|/index.php?id=5|0", cikti);

        // Eslesmeyen istek dokunulmadan gecmeli; yoksa "her seye Rewrite diyen" bir motor da
        // yukaridaki satiri gecerdi.
        Assert.Contains("ESLESMEYEN=None||0", cikti);
    }

    /// <summary>
    /// Motor bağlanmış olsa BİLE bozuk bir <c>.htaccess</c> isteği BOZMAZ (fail-open).
    /// </summary>
    /// <remarks>
    /// Pazarlık dışı kural: motor ne yaparsa yapsın istek sürmelidir. Bu, net48 tarafında da
    /// ölçülüyor çünkü fail-open'ın net8 testleri motorun KENDİSİNİ ölçüyor; buradaki soru,
    /// bağlantı kurulduktan sonra gerçek çalışma zamanında da aynı sözün tutulup tutulmadığı.
    /// </remarks>
    [Fact]
    public void BOZUK_HTACCESS_net48_de_de_KARAR_URETMEZ_ve_SUREC_COKMEZ()
    {
        var modulDll = ModulCiktisi();
        Assert.True(File.Exists(modulDll), $"net48 modul ciktisi bulunamadi: {modulDll}");

        using var site = new GeciciSite();

        // Kapanmayan blok + tanimsiz direktif: motorun "olumcul soz dizimi hatasi" dali.
        site.Yaz(
            ".htaccess",
            """
            RewriteEngine On
            <IfModule mod_rewrite.c>
            RewriteRule ^urun/([0-9]+)$ /index.php?id=$1 [L]
            """);

        var cikti = Net48Kos(modulDll, site.Kok);

        Assert.Contains("MOTOR=AdaPanel.HtaccessModule.HtaccessModuleEngineAdapter", cikti);
        Assert.Contains("ESLESEN=None||0", cikti);
        Assert.DoesNotContain("PATLADI=", cikti);
    }

    // ---------------------------------------------------------------- kosum altyapisi

    /// <summary>
    /// Betiği Windows PowerShell 5.1 (CLR 4) içinde koşturur ve çıktısını döner.
    /// </summary>
    /// <remarks>
    /// <c>-NoProfile</c>: kullanıcının profili betiği etkilemesin (bu makinede profil var ve
    /// testin sonucu ona bağlı olmamalı). <c>-ExecutionPolicy Bypass</c>: politika kısıtlı
    /// makinelerde testin sessizce atlanmasını değil, koşmasını sağlar.
    /// </remarks>
    private static string Net48Kos(string modulDll, string siteKoku)
    {
        var kabuk = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(
            File.Exists(kabuk),
            $"Windows PowerShell 5.1 bulunamadi ({kabuk}). Bu test .NET Framework barindiricisi olmadan "
            + "kosamaz ve ATLANMAZ: atlamak, kanitlanmamis bir seyi kanitlanmis gostermek olurdu.");

        var betikYolu = Path.Combine(Path.GetTempPath(), $"adapanel-net48-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(betikYolu, Betik(modulDll, siteKoku));

        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = kabuk,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{betikYolu}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            var cikti = surec.StandardOutput.ReadToEnd();
            var hata = surec.StandardError.ReadToEnd();

            // 60 sn: makine yuk altindayken surec baslatma yavaslayabilir; sonsuz bekleme ise
            // test kosumunu ASAR (kilitlenen bir testin bedeli, kirmizi bir testten agirdir).
            Assert.True(surec.WaitForExit(60_000), "net48 kanit sureci 60 saniyede bitmedi.");

            Assert.True(
                surec.ExitCode == 0,
                $"net48 kanit sureci {surec.ExitCode} ile bitti.{Environment.NewLine}"
                + $"CIKTI:{Environment.NewLine}{cikti}{Environment.NewLine}HATA:{Environment.NewLine}{hata}");

            return cikti;
        }
        finally
        {
            try
            {
                File.Delete(betikYolu);
            }
            catch (IOException)
            {
                // Gecici betigin silinememesi testin sonucunu degistirmez.
            }
        }
    }

    /// <summary>
    /// Kanıt betiği. Modülün üretimdeki çağrı sırasını birebir izler.
    /// </summary>
    private static string Betik(string modulDll, string siteKoku) =>
        $$"""
        $ErrorActionPreference = 'Stop'
        try {
            $asm = [Reflection.Assembly]::LoadFrom('{{modulDll}}')

            # Derlemenin net48 damgasi ve BARINDIRAN surecin CLR surumu AYRI iki olcumdur:
            # birincisi "ne derlendi", ikincisi "nerede kosuyor".
            'DERLEME_CALISMA_ZAMANI=' + $asm.ImageRuntimeVersion
            'HOST_CLR=' + [System.Runtime.InteropServices.RuntimeEnvironment]::GetSystemVersion()

            # HtaccessHttpModule.EnsureInitialized bunu cagirir.
            [AdaPanel.HtaccessModule.HtaccessModuleEngineAdapter]::Install()

            $motor = [AdaPanel.HtaccessModule.HtaccessEngineRegistry]::Current
            if ($null -eq $motor) { 'MOTOR=YOK'; exit 2 }
            'MOTOR=' + $motor.GetType().FullName

            # HtaccessHttpModule.Apply: once Compile, sonra Evaluate.
            $derlenmis = $motor.Compile((Join-Path '{{siteKoku}}' '.htaccess'))
            if ($null -eq $derlenmis) { 'DERLEME=YOK'; exit 3 }

            function Karar([string]$yol) {
                $istek = New-Object AdaPanel.HtaccessModule.HtaccessRequestInfo
                $istek.VirtualPath = $yol
                $istek.DocumentRoot = '{{siteKoku}}'
                $istek.HttpMethod = 'GET'
                $istek.QueryString = ''
                $istek.RemoteAddress = '203.0.113.10'
                $istek.Host = 'ornek.com'
                $k = $motor.Evaluate($istek, $derlenmis)
                return ('' + $k.Kind + '|' + $k.Target + '|' + $k.StatusCode)
            }

            'ESLESEN=' + (Karar '/urun/5')
            'ESLESMEYEN=' + (Karar '/hakkinda.html')
            exit 0
        }
        catch {
            'PATLADI=' + $_.Exception.GetType().FullName + ': ' + $_.Exception.Message
            exit 1
        }
        """;

    /// <summary>
    /// Modülün net48 çıktısı. Yol TAHMİN EDİLMİYOR: yapılandırma (Debug/Release) test derlemesinin
    /// kendi yolundan okunuyor, kök ise yukarı doğru <c>AdaPanel.sln</c> aranarak bulunuyor.
    /// </summary>
    private static string ModulCiktisi()
    {
        var ciktiKlasoru = new DirectoryInfo(AppContext.BaseDirectory);

        // .../bin/<Yapilandirma>/net8.0-windows -> bir ust klasorun adi yapilandirmadir.
        var yapilandirma = ciktiKlasoru.Parent?.Name ?? "Debug";

        return Path.Combine(
            DepoKoku(), "src", "AdaPanel.HtaccessModule", "bin", yapilandirma, "net48",
            "AdaPanel.HtaccessModule.dll");
    }

    private static string DepoKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.True(dizin is not null, "AdaPanel.sln bulunamadi; modul ciktisinin yeri belirlenemiyor.");
        return dizin!.FullName;
    }

    /// <summary>
    /// Gerçek diskte geçici bir site kökü. Sahte dosya sistemi KULLANILMIYOR: kanıtın tamamı
    /// "gerçek dosyayı gerçek çalışma zamanında okuyup uyguladı" iddiasına dayanıyor.
    /// </summary>
    private sealed class GeciciSite : IDisposable
    {
        public GeciciSite()
        {
            Kok = Path.Combine(Path.GetTempPath(), $"adapanel-net48-site-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Kok);
        }

        public string Kok { get; }

        public void Yaz(string goreliYol, string icerik) =>
            File.WriteAllText(Path.Combine(Kok, goreliYol), icerik);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Kok, recursive: true);
            }
            catch (IOException)
            {
                // Gecici klasorun silinememesi testin sonucunu degistirmez.
            }
        }
    }
}
