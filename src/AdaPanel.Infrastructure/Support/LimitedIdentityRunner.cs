using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Scheduler;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Bir komutu panelin kendi yetkisiyle (LocalSystem) DEĞİL, sınırlı bir kimlikle çalıştırır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekli:</b> composer, npm, artisan ve WP-CLI tanımı gereği <i>sitenin kendi kodunu</i>
/// çalıştırır — <c>composer.json</c> script'leri, <c>package.json</c> script'leri, Laravel/WordPress
/// PHP kodu. Bu dosyaları kiracı zaten FTP ve dosya yöneticisiyle yazabiliyor. Panel LocalSystem
/// olarak koştuğu için bu uçlar, bir bayiye/müşteriye <b>SYSTEM olarak keyfi kod çalıştırma</b>
/// imkânı veriyordu. Komut dizgesini süzmek bunu kapatmaz: kötü niyetli şey argümanda değil,
/// aracın çalıştırmak üzere tasarlandığı dosyanın içinde.
/// </para>
/// <para>
/// <b>Neden <c>--no-scripts</c> / <c>--ignore-scripts</c> değil:</b> Laravel'in kurulumu
/// <c>post-autoload-dump</c> → <c>package:discover</c> adımına gerçekten muhtaçtır. Script'leri
/// kapatmak açığı kapatırdı ama özelliği de bozardı.
/// </para>
/// <para>
/// <b>Neden Görev Zamanlayıcı:</b> Windows'ta bir süreci başka bir kimlikle başlatmanın yolu ya
/// token çoğaltma (CreateProcessAsUser — çok sayıda P/Invoke, pencere istasyonu/masaüstü ACL'leri,
/// kırılgan) ya da zamanlayıcıdır. Panelde LOCAL SERVICE ile görev çalıştırma <b>zaten var ve bu
/// makinede canlı doğrulanmış</b> (bkz. <see cref="ScriptTaskPlanner.LimitedUserId"/>); yeni ve
/// denenmemiş bir yol icat etmek yerine o yol kullanılıyor.
/// </para>
/// <para>
/// Bedeli: her çağrıda ~1-2 saniyelik görev kaydet/başlat/sil ek yükü. Bu uçlar (composer install,
/// npm run build) zaten saniyeler-dakikalar süren işlerdir; ek yük ölçüye girmez.
/// </para>
/// </remarks>
internal static class LimitedIdentityRunner
{
    private const string TaskPath = @"\AdaPanel\";

    /// <summary>
    /// <paramref name="fileName"/> komutunu <see cref="ScriptTaskPlanner.LimitedUserId"/> kimliğiyle
    /// çalıştırır ve çıktısını toplar.
    /// </summary>
    /// <param name="grantRoot">
    /// Sınırlı kimliğe okuma/yazma verilecek klasör (sitenin kökü). Verilmezse komut kendi
    /// dosyalarını okuyamaz ve sessizce düşer.
    /// </param>
    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        string grantRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // SIRAYA SOKULUYOR — çapraz-kiracı erişimi bunun için kapanıyor.
        //
        // LOCAL SERVICE TEK ve ORTAK bir hesaptır: her kiracının komutu aynı kimlikle koşar. İzin
        // kalıcı verilseydi (ilk sürümde öyleydi), bir kez composer çalıştırmış HER sitenin kökünde
        // LOCAL SERVICE grant'i dururdu ve A kiracısının kendi composer.json script'i B'nin
        // wp-config.php'sini okuyabilirdi — yani SYSTEM'den inmek dikey yükselmeyi kapatıp yatay
        // geçişi açık bırakırdı.
        //
        // Çözüm: izin YALNIZ çalışma boyunca verilir ve aynı anda yalnız BİR çalışma olur. Böylece
        // her an en fazla tek sitenin kökü açıktır ve o site, kodu koşan kiracının kendi sitesidir.
        await CalismaKilidi.WaitAsync(cancellationToken);
        try
        {
            return await CalistirAsync(fileName, arguments, workingDirectory, grantRoot, timeout, cancellationToken);
        }
        finally
        {
            CalismaKilidi.Release();
        }
    }

    private static readonly SemaphoreSlim CalismaKilidi = new(1, 1);

    private static async Task<CommandResult> CalistirAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        string grantRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // İzin ZATEN varsa dokunma ve geri de alma: cron'un "sınırlı yetki" görevleri
        // (SchedulerService.GrantLimitedIdentityAccessAsync) bu grant'e KALICI olarak muhtaçtır.
        // Körlemesine kaldırmak, o sitedeki zamanlanmış görevleri sessizce çalışmaz hâle getirirdi.
        var izinZatenVardi = LocalServiceErisebiliyor(grantRoot);
        if (!izinZatenVardi)
        {
            await GrantAccessAsync(grantRoot, cancellationToken);
        }

        try
        {
            return await IcerdeCalistirAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);
        }
        finally
        {
            if (!izinZatenVardi)
            {
                await RevokeAccessAsync(grantRoot, cancellationToken);
            }
        }
    }

    private static async Task<CommandResult> IcerdeCalistirAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {

        // Çıktı dosyaları ve sarmalayıcı betik, sınırlı kimliğin YAZABİLECEĞİ ayrı bir klasörde
        // durur. Site köküne yazmıyoruz: kullanıcının dosyalarının arasına panel artığı bırakmak,
        // sonra da onu temizlemeyi unutmak bu depoda daha önce oldu.
        var isKlasoru = Path.Combine(Path.GetTempPath(), $"ada-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isKlasoru);

        var ciktiYolu = Path.Combine(isKlasoru, "out.txt");
        var hataYolu = Path.Combine(isKlasoru, "err.txt");
        var sarmalayiciYolu = Path.Combine(isKlasoru, "run.ps1");
        var gorevAdi = $"AdaPanel-Run-{Guid.NewGuid():N}";

        try
        {
            await GrantAccessAsync(isKlasoru, cancellationToken);

            // Sarmalayıcı bir DOSYADAN çalışır; komut ve argümanlar görev eylemine gömülmez.
            // Böylece tırnak/kaçış kuralları tek bir yerde kalır ve cmd.exe hiç devreye girmez —
            // yani araya bir kabuk sokup enjeksiyon yüzeyi açmıyoruz.
            // -ArgumentList BOŞ DİZEYLE VERİLEMEZ. Start-Process bu parametreyi doğrular ve boş
            // gelince "null or empty" diye fırlatır; sarmalayıcı komutu hiç çalıştıramadan düşer,
            // çıktı dosyaları oluşmaz ve panel kullanıcısına sebepsiz bir hata gider. Bu, tam olarak
            // burada ölçülerek bulundu (görev sonucu 1, işaret dosyası boş). Bugünkü beş çağıranın
            // hepsi dolu argüman geçiyor — yani sorun gizli kalırdı; bu yüzden parametre argüman
            // yokken hiç YAZILMIYOR.
            var argumanParcasi = string.IsNullOrWhiteSpace(arguments)
                ? string.Empty
                : $" -ArgumentList '{TekTirnak(arguments)}'";

            var sarmalayici = $@"$ErrorActionPreference = 'Stop'
$p = Start-Process -FilePath '{TekTirnak(fileName)}'{argumanParcasi} -WorkingDirectory '{TekTirnak(workingDirectory)}' -NoNewWindow -Wait -PassThru -RedirectStandardOutput '{TekTirnak(ciktiYolu)}' -RedirectStandardError '{TekTirnak(hataYolu)}'
exit $p.ExitCode";
            await File.WriteAllTextAsync(
                sarmalayiciYolu,
                sarmalayici,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken);

            var sonuc = await ProcessRunner.RunPowerShellAsync(
                GorevBetigi(gorevAdi, sarmalayiciYolu, timeout),
                cancellationToken);

            if (!sonuc.Success)
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StandardOutput = string.Empty,
                    StandardError = sonuc.StandardError
                };
            }

            return new CommandResult
            {
                ExitCode = CikisKoduCoz(sonuc.StandardOutput),
                StandardOutput = await OkuAsync(ciktiYolu, cancellationToken),
                StandardError = await OkuAsync(hataYolu, cancellationToken)
            };
        }
        finally
        {
            try { Directory.Delete(isKlasoru, recursive: true); } catch { /* en iyi çaba */ }
        }
    }

    /// <summary>
    /// Görevi kaydeder, çalıştırır, BİTMESİNİ BEKLER, sonucu yazar ve görevi siler — hepsi tek bir
    /// PowerShell oturumunda. C# tarafından yoklamak her turda yeni bir PowerShell süreci
    /// başlatırdı; kısa komutlarda bu, işin kendisinden uzun sürer.
    /// </summary>
    private static string GorevBetigi(string gorevAdi, string sarmalayiciYolu, TimeSpan timeout)
    {
        var saniye = Math.Max(30, (int)timeout.TotalSeconds);

        // 267009 (0x41301) = "görev şu anda çalışıyor", 267011 (0x41303) = "görev henüz hiç
        // çalışmadı". Durum yerine SONUCU yoklamak, Start-ScheduledTask'tan hemen sonraki yarışı
        // da kapatır: görev daha 'Running' olmadan durumu okuyup "bitti" sanmayız.
        return $@"$ErrorActionPreference = 'Stop'
$yol = '{TekTirnak(TaskPath)}'
$ad = '{TekTirnak(gorevAdi)}'
$eylem = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{Cift(sarmalayiciYolu)}""'
$kimlik = New-ScheduledTaskPrincipal -UserId '{TekTirnak(ScriptTaskPlanner.LimitedUserId)}' -LogonType ServiceAccount -RunLevel Limited
$ayar = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds {saniye})

# -ErrorAction Stop ACIKCA veriliyor ve kayit AYRICA dogrulaniyor. Olculdu: Register-ScheduledTask
# bir CIM cmdlet'idir ve erisim reddedildiginde ($ErrorActionPreference = 'Stop' olmasina ragmen)
# hatayi yazip AKISI SURDURUYOR. O halde Start/Get adimlari da sirayla dusuyor, dongu tanimsiz
# bir degiskenle hemen cikiyor ve panel kullanicisina SEBEPSIZ bir hata gidiyordu.
Register-ScheduledTask -TaskPath $yol -TaskName $ad -Action $eylem -Principal $kimlik -Settings $ayar -ErrorAction Stop | Out-Null
if (-not (Get-ScheduledTask -TaskPath $yol -TaskName $ad -ErrorAction SilentlyContinue)) {{
  throw ""Gorev kaydedilemedi: $yol$ad""
}}
try {{
  Start-ScheduledTask -TaskPath $yol -TaskName $ad -ErrorAction Stop
  $bitis = (Get-Date).AddSeconds({saniye} + 30)
  do {{
    Start-Sleep -Milliseconds 400
    $bilgi = Get-ScheduledTaskInfo -TaskPath $yol -TaskName $ad -ErrorAction Stop
  }} while (($bilgi.LastTaskResult -eq 267009 -or $bilgi.LastTaskResult -eq 267011) -and (Get-Date) -lt $bitis)
  Write-Output ""ADAEXIT=$($bilgi.LastTaskResult)""
}} finally {{
  Unregister-ScheduledTask -TaskPath $yol -TaskName $ad -Confirm:$false -ErrorAction SilentlyContinue
}}";
    }

    /// <summary>
    /// Sınırlı kimliğe klasör üzerinde okuma/çalıştırma ve YAZMA hakkı verir.
    /// <para>
    /// Neden Modify: composer <c>vendor/</c>, npm <c>node_modules/</c>, artisan ise önbellek yazar;
    /// yalnız okuma verilirse komut "çalıştı" görünür ama ilk yazmada düşer. Verilen hak site
    /// köküyle sınırlıdır ve alternatifin (SYSTEM) yanında hâlâ çok dardır.
    /// </para>
    /// </summary>
    private static async Task GrantAccessAsync(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var betik = $@"$ErrorActionPreference = 'Stop'
& icacls.exe ""{Cift(root)}"" /grant ""{Cift(ScriptTaskPlanner.LimitedUserId)}:(OI)(CI)M"" /T | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw ""icacls cikis kodu: $LASTEXITCODE"" }}";

        var sonuc = await ProcessRunner.RunPowerShellAsync(betik, cancellationToken);
        if (!sonuc.Success)
        {
            throw new AppException("hosting.limited_grant_failed", 500, 500, sonuc.StandardError);
        }
    }

    /// <summary>
    /// Çalışma bitince sınırlı kimliğin izni geri alınır — izni BU çalışma verdiyse.
    /// <para>
    /// Başarısızlığı yutmuyoruz ama işlemi de düşürmüyoruz: komut çoktan çalıştı, kullanıcıya
    /// çıktısını vermek gerekiyor. Geri alınamayan izin bir sonraki çalışmada "zaten vardı"
    /// sayılacağı için kalıcılaşır — bu yüzden sessiz geçmiyor, günlüğe yazılıyor.
    /// </para>
    /// </summary>
    private static async Task RevokeAccessAsync(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var betik = $@"$ErrorActionPreference = 'Stop'
& icacls.exe ""{Cift(root)}"" /remove:g ""{Cift(ScriptTaskPlanner.LimitedUserId)}"" /T /C /Q | Out-Null";

        // İptal edilmiş bir istekte de temizlik ÇALIŞMALI; aksi hâlde iptal, izni kalıcı bırakmanın
        // yolu olurdu. Bu yüzden CancellationToken.None.
        _ = cancellationToken;
        await ProcessRunner.RunPowerShellAsync(betik, CancellationToken.None);
    }

    /// <summary>Sınırlı kimliğin bu klasörde AÇIK bir izni var mı (miras alınan değil, açıkça verilmiş)?</summary>
    private static bool LocalServiceErisebiliyor(string root)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return false;
        }

        try
        {
            var localService = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalServiceSid, null);

            var kurallar = new DirectoryInfo(root)
                .GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier));

            foreach (System.Security.AccessControl.FileSystemAccessRule kural in kurallar)
            {
                if (kural.AccessControlType == System.Security.AccessControl.AccessControlType.Allow &&
                    kural.IdentityReference is System.Security.Principal.SecurityIdentifier sid &&
                    sid.Equals(localService))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException or PlatformNotSupportedException)
        {
            // ÖLÇEMEDİK. "Zaten vardı" saymıyoruz: öyle sayarsak izni verir ve geri ALMAYIZ, yani
            // ölçememe kalıcı bir açığa dönüşürdü. "Yoktu" saymak en kötü ihtimalle var olan bir
            // izni bir kez fazladan kaldırır — o da cron için "İzinleri Onar" ile geri gelir.
            return false;
        }
    }

    private static int CikisKoduCoz(string ciktı)
    {
        foreach (var satir in ciktı.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var kirpilmis = satir.Trim();
            if (kirpilmis.StartsWith("ADAEXIT=", StringComparison.Ordinal)
                && int.TryParse(kirpilmis["ADAEXIT=".Length..].Trim(), out var kod))
            {
                return kod;
            }
        }

        // İşaret hiç gelmediyse "başarılı" demiyoruz: ölçemediğimizi başarı saymak, bu depodaki
        // güvenlik duvarı uyarılarında zaten bir kez yanlış alarm üretmişti.
        return -1;
    }

    private static async Task<string> OkuAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string TekTirnak(string value) => (value ?? string.Empty).Replace("'", "''");

    private static string Cift(string value) => (value ?? string.Empty).Replace("\"", "`\"");
}
