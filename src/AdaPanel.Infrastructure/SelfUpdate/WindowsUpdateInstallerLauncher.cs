using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.SelfUpdate;

public sealed class WindowsUpdateInstallerLauncher(JsonFileStore fileStore) : IUpdateInstallerLauncher
{
    private const string TaskName = "AdaPanel-SelfUpdate";
    private const string ServiceName = "AdaPanel";

    public async Task ScheduleAsync(
        string packagePath,
        string packageSha256,
        CancellationToken cancellationToken = default)
    {
        var updatesDirectory = Path.GetFullPath(fileStore.EnsureDirectory("updates"));
        var resolvedPackagePath = Path.GetFullPath(packagePath);
        if (!resolvedPackagePath.StartsWith(
                updatesDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(resolvedPackagePath))
        {
            throw new AppException("update.package_not_ready", 409, 409);
        }

        var launcherPath = Path.Combine(updatesDirectory, "apply-update.ps1");

        // IKI AYRI GUNLUK. Onceden calistiricinin kendi izi ile Inno'nun `/LOG=` ciktisi AYNI
        // DOSYAYDI ve Inno dosyayi SIFIRDAN aciyordu: calistiricinin o ana kadar yazdigi her satir
        // (surum karsilastirmasi, servis durdurma, hangi adimda ne oldu) YOK OLUYORDU. Olculdu:
        // apply-update.log'un ilk satiri "Log opened." ve dosyada tek bir [runner] satiri yok.
        // Kok nedenin aylarca bulunamamasinin dogrudan sebebi buydu -- kanit her kosuda siliniyordu.
        //
        // apply-update.log ADI KORUNUR: kullaniciya gosterilen hata metinleri (update.attempt_no_verdict,
        // panel.update.updateTimeout) sunucuda bu dosyaya bakmasini soyluyor ve kurulum arizasinin
        // gercek sebebi ("Cannot create file ... being used by another process") Inno'nun gunlugundedir.
        var logPath = Path.Combine(updatesDirectory, "apply-update.log");
        var runnerLogPath = Path.Combine(updatesDirectory, "update-runner.log");
        await File.WriteAllTextAsync(
            launcherPath,
            BuildLauncherScript(resolvedPackagePath, packageSha256, logPath, runnerLogPath),
            new UTF8Encoding(false),
            cancellationToken);

        var escapedLauncher = EscapePowerShell(launcherPath);
        var registerScript = $$"""
            $ErrorActionPreference = 'Stop'
            $taskName = '{{TaskName}}'
            if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
              Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            }
            $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{{escapedLauncher}}"'
            $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(15)
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            # PIL BAYRAKLARI ACIKCA VERILIYOR. New-ScheduledTaskSettingsSet'in varsayilanlari DIZUSTU
            # BILGISAYAR icin secilmistir ve bu gorev icin uctan uca yanlistir. Bu makinede kayitli
            # AdaPanel-SelfUpdate gorevi okundu, varsayilanlar aynen duruyordu:
            # DisallowStartIfOnBatteries=True, StopIfGoingOnBatteries=True.
            #
            # NEDEN OLDURUCU: sanallastirilmis sunucular (Hyper-V ve bircok VPS) sik sik bir
            # batarya/UPS aygiti bildirir. Oyle bir makinede Gorev Zamanlayici bu gorevi
            # BASLATMAYI REDDEDER (0x800710E0). Zincirin tamami sessizdir: Register-ScheduledTask
            # basarili doner, ScheduleAsync hata atmaz, arayuz "panel yeniden baslatiliyor" der --
            # ve HICBIR SEY OLMAZ. Musteri panelini bir daha ASLA guncelleyemez, yani bu depoda
            # duzeltilen hicbir hatayi da alamaz. Guncelleme yolu oldugu icin kendi kendini
            # onaramaz da: tek cikis yolu elle kurulumdur.
            #
            # StopIfGoingOnBatteries ayrica UPS'li bir makinede sebeke dalgalanmasinin kurulumu
            # TAM ORTASINDA oldurmesine izin verir -> yarim yazilmis panel ikilileri.
            #
            # Ayni olcum cron yolunda daha once yapilmisti (SchedulerService, "AYARLARI ACIKCA VER"
            # blogu); self-update gorevi o duzeltmeden PAYINI ALMAMISTI.
            #
            # SURE SINIRI BILEREK 2 SAAT: bu gorev uzun omurlu bir servis degil, biten bir kurulum
            # kosusudur; takilirsa slotu sonsuza kadar tutmasin.
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2) -StartWhenAvailable
            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
            """;

        var result = await ProcessRunner.RunPowerShellAsync(registerScript, cancellationToken);
        if (!result.Success)
        {
            throw new AppException(
                "update.install_schedule_failed",
                result.ExitCode,
                500,
                $"StandardOutput:\r\n{result.StandardOutput}\r\nStandardError:\r\n{result.StandardError}");
        }
    }

    internal static string BuildLauncherScript(
        string packagePath,
        string packageSha256,
        string logPath,
        string runnerLogPath)
    {
        return $$"""
            $ErrorActionPreference = 'Stop'
            $packagePath = '{{EscapePowerShellLiteral(packagePath)}}'
            $expectedHash = '{{EscapePowerShellLiteral(packageSha256)}}'
            $logPath = '{{EscapePowerShellLiteral(logPath)}}'
            $runnerLogPath = '{{EscapePowerShellLiteral(runnerLogPath)}}'
            $taskName = '{{TaskName}}'
            $serviceName = '{{ServiceName}}'

            # CALISTIRICI KENDI DOSYASINA YAZAR. $logPath'i Inno `/LOG=` ile SIFIRDAN acar; oraya
            # yazilan her [runner] satiri kurulum basladigi anda silinirdi.
            function Write-UpdateLog([string]$message) {
              $timestamp = (Get-Date).ToString('o')
              Add-Content -LiteralPath $runnerLogPath -Value "$timestamp [runner] $message" -ErrorAction SilentlyContinue
            }

            # Her deneme TEMIZ bir izle baslar: dosya surekli buyumesin ve iki denemenin satirlari
            # birbirine karismasin. Onceki denemenin HUKMU zaten last-attempt.json'da duruyor.
            Set-Content -LiteralPath $runnerLogPath -Value "$((Get-Date).ToString('o')) [runner] ---- yeni guncelleme denemesi ----" -Encoding UTF8 -ErrorAction SilentlyContinue

            # DURUM DOSYASI: panel kurulum boyunca kapali oldugu icin ilerlemeyi kendisi bildiremez.
            # Bunun yerine calistirici her adimda buraya yaziyor; panel geri donunce dosyayi okuyup
            # denemenin BITIP BITMEDIGINI ve sonucunu raporluyor.
            #
            # Cozdugu sorun: kurulum iptal olup saniyeler icinde bittiginde bile arayuz "panel yeniden
            # baslatiliyor" diyerek 15 dakika bekliyordu, cunku YALNIZCA surum degisti mi diye bakiyor,
            # denemenin sonucunu hic sormuyordu. Kullaniciya ne hata ne sonuc gorunuyordu.
            $statePath = Join-Path (Split-Path -Parent $logPath) 'last-attempt.json'

            # SAHIPLIK KURALI -- iki yazar, tek dosya, ve ONCEDEN HIC SIRA YOKTU.
            #
            # OLCULEN OLAY: kurulum panelin servisini ONCE baslatir (install-service.ps1 kurulumun
            # icinden cagriliyor), Inno ise ondan ~16 saniye SONRA cikar. O aralikta yeni panel
            # ayaga kalkip "calisan surum = hedef surum" olcumunu yapiyor ve last-attempt.json'a
            # 'succeeded' yaziyor. Calistirici saniyeler sonra kendi (daha az bilgili) hukmunu
            # UZERINE yaziyordu: kullanici BASARILI bir guncellemede "Kurulum tamamlanamadi." goruyordu.
            # Panel de duzeltemiyordu, cunku bekleyen kayit o sirada zaten silinmis oluyor.
            #
            # KURAL: panel kendi OLCUMUYLE karar verdiyse (isDecidedByPanel) calistirici o hukmu
            # EZMEZ. Calistiricinin iddia edebilecegi iki sey vardir -- "kurulum olmadi" ve "servis
            # kalkmadi"; isteklere YENI surumle cevap veren bir panelin varligi ikisini de curutur.
            #
            # TEK ISTISNA: hukmun EKSIK kalan parcasini tamamlamak. Panel karar verdigi anda kurulum
            # cikis kodunu HENUZ kimse bilmiyor (o yuzden yapisal olarak hep null kaliyordu); onu
            # yalnizca calistirici bilir. Faz/sebep DEGISMEZ, yalnizca bos olan cikis kodu doldurulur.
            function Write-UpdateState([string]$phase, [string]$errorText, $exitCode) {
              try {
                $panelHukmu = $null
                if (Test-Path -LiteralPath $statePath) {
                  try { $panelHukmu = Get-Content -LiteralPath $statePath -Raw -ErrorAction Stop | ConvertFrom-Json }
                  catch { $panelHukmu = $null }
                }

                if ($panelHukmu -and $panelHukmu.isDecidedByPanel) {
                  if (($null -ne $exitCode) -and ($null -eq $panelHukmu.installerExitCode)) {
                    # Alanlar TEK TEK yeniden kurulur; panelin nesnesi oldugu gibi yeniden
                    # serilestirilmez. Sebep: ConvertFrom-Json 'updatedAtUtc' alanini DateTime'a
                    # cevirir, ConvertTo-Json onu YEREL SAAT DILIMI ekiyle geri yazar ve panel
                    # tarafinda UTC olmayan bir zaman damgasi olusur (sure hesaplari kayar).
                    $tamamlanmis = [ordered]@{
                      phase = "$($panelHukmu.phase)"
                      error = "$($panelHukmu.error)"
                      errorKey = "$($panelHukmu.errorKey)"
                      installerExitCode = $exitCode
                      isDecidedByPanel = $true
                      updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
                    }
                    ($tamamlanmis | ConvertTo-Json -Compress) | Set-Content -LiteralPath $statePath -Encoding UTF8 -ErrorAction SilentlyContinue
                    Write-UpdateLog "Panel hukmu ('$($panelHukmu.phase)') KORUNDU; yalnizca kurulum cikis kodu ($exitCode) eklendi."
                  }
                  else {
                    Write-UpdateLog "Panel hukmu ('$($panelHukmu.phase)') KORUNDU; '$phase' yazilmadi."
                  }
                  return
                }

                $state = [ordered]@{
                  phase = $phase
                  error = $errorText
                  installerExitCode = $exitCode
                  updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
                }
                ($state | ConvertTo-Json -Compress) | Set-Content -LiteralPath $statePath -Encoding UTF8 -ErrorAction SilentlyContinue
              }
              catch { }
            }

            Write-UpdateState 'starting' '' $null

            # KURULUMDAN ONCEKI SURUM. Basari, sonunda buna gore yargilanir: asil soru "servis
            # kac saniyede kalkti" degil, "panel dosyasi yenilendi mi"dir.
            $serviceImagePath = ''
            $versionBefore = ''
            try {
              $kayit = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name ImagePath -ErrorAction Stop
              if ($kayit.ImagePath -match '^\s*"([^"]+)"') { $serviceImagePath = $Matches[1] }
              elseif ($kayit.ImagePath -match '^\s*(\S+)') { $serviceImagePath = $Matches[1] }
              if ($serviceImagePath -and (Test-Path -LiteralPath $serviceImagePath)) {
                $versionBefore = (Get-Item -LiteralPath $serviceImagePath).VersionInfo.ProductVersion
              }
            }
            catch { }
            Write-UpdateLog "Version before install: '$versionBefore' ($serviceImagePath)"

            $failed = $false
            # SEBEP KORUNUR. Onceden son satir 'Kurulum tamamlanamadi.' yazip gercek hatayi ve
            # kurulum cikis kodunu EZIYORDU; panel kullaniciya yalnizca genel bir cumle
            # gosterebiliyor, sunucuya baglanmadan sebebi ogrenmek mumkun olmuyordu.
            $failReason = ''
            $failExitCode = $null
            try {
              Start-Sleep -Seconds 5
              $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256 -ErrorAction Stop).Hash
              if ($actualHash -ne $expectedHash) {
                throw "Update package checksum changed before installation."
              }

              # Servisi KURULUMDAN ONCE durdur. Sebebi: panel bir Windows servisidir ve kurulum
              # yardimcisinin RestartManager'i onu pencere kapatarak durduramaz. Durduramayinca
              # Abort/Retry/Ignore sorar, /SUPPRESSMSGBOXES altinda bu ABORT'a duser ve kurulum
              # cikis kodu 5 ile iptal olur; panel kendi kendini hic guncelleyemez. Canli sunucuda
              # birebir bu yasandi. Asagidaki "guvence" blogu servisi her halukarda geri baslatir.
              try {
                $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                if ($svc -and $svc.Status -ne 'Stopped') {
                  Write-UpdateState 'stopping-service' '' $null
                  Write-UpdateLog "Stopping service '$serviceName' before install."
                  # Stop-Service waits internally; a hung service would prevent even the
                  # bounded loop below from running. Send the stop request without waiting.
                  if ($svc.Status -ne 'StopPending') { $svc.Stop() }
                  for ($i = 0; $i -lt 60; $i++) {
                    Start-Sleep -Milliseconds 500
                    $svc.Refresh()
                    if ($svc.Status -eq 'Stopped') { break }
                  }
                  Write-UpdateLog "Service status before install: $($svc.Status)."
                }
              }
              catch {
                Write-UpdateLog "Could not stop service before install: $($_.Exception.Message)"
              }

              $arguments = @(
                '/VERYSILENT',
                '/SUPPRESSMSGBOXES',
                '/NORESTART',
                "/LOG=`"$logPath`""
              )
              Write-UpdateState 'installing' '' $null
              $process = Start-Process -FilePath $packagePath -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
              Write-UpdateLog "Installer exited with code $($process.ExitCode)."
              Write-UpdateState 'installer-finished' '' $process.ExitCode
              if ($process.ExitCode -notin @(0, 1641, 3010)) {
                throw "Installer exited with code $($process.ExitCode)."
              }
            }
            catch {
              $failed = $true
              $failReason = $_.Exception.Message
              if ($null -ne $process -and $null -ne $process.ExitCode) { $failExitCode = $process.ExitCode }
              Write-UpdateLog $_.Exception.ToString()
              Write-UpdateState 'failed' $failReason $failExitCode
            }

            # KURULUM OLDU MU? Cevap SUREDEN bagimsiz: kurulumdan hemen sonra disktekI panel
            # dosyasinin surumune bakilir. Eskiden hukum "servis 40 saniyede kalkti mi" sorusuna
            # gore veriliyordu; yuklu bir sunucuda panel acilirken IIS sitelerini, bilesenleri ve
            # sema gocunu tarar, bu sure rahatca asilir ve KURULUM BASARILI OLDUGU HALDE
            # "tamamlanamadi" yaziliyordu. Bir surenin dolmasi bir seyin basarisiz oldugu anlamina
            # gelmez; olcut sonucun kendisidir.
            $kuruldu = $false
            try {
              if ($serviceImagePath -and (Test-Path -LiteralPath $serviceImagePath)) {
                $versionAfter = (Get-Item -LiteralPath $serviceImagePath).VersionInfo.ProductVersion
                Write-UpdateLog "Version after install: '$versionAfter' (before: '$versionBefore')"
                $kuruldu = $versionAfter -and $versionBefore -and ($versionAfter -ne $versionBefore)
              }
            }
            catch { Write-UpdateLog "Could not read version after install: $($_.Exception.Message)" }

            if ($kuruldu) {
              # Kurulum OLDU. Bundan sonra olacaklar (servisin gec kalkmasi gibi) bu gercegi
              # degistirmez; ayri bir durum olarak bildirilir.
              $failed = $false
              $failReason = ''
            }

            # GUVENCE: panel servisi olu birakilamaz. Bu dongu bir HUKUM degil, bir KURTARMA
            # denemesidir; sonucu "kurulum basarili miydi" sorusunu etkilemez.
            $servisCalisiyor = $false
            try {
              $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
              if ($null -eq $service) {
                Write-UpdateLog "Service '$serviceName' not found; nothing to start."
                if (-not $kuruldu) { $failed = $true }
                if ($failReason -eq '') { $failReason = "Panel servisi bulunamadi." }
              }
              else {
                # Servis calisana kadar denenir. Buradaki ust sinir bir "basari suresi" DEGILDIR;
                # yalnizca betigin sonsuza kadar asili kalmamasi icindir ve sonuca etki etmez.
                $sonDeneme = (Get-Date).AddMinutes(10)
                $deneme = 0
                while ((Get-Date) -lt $sonDeneme) {
                  $service.Refresh()
                  if ($service.Status -eq 'Running') { break }
                  $deneme++
                  try { Start-Service -Name $serviceName -ErrorAction Stop }
                  catch { Write-UpdateLog "Service start attempt $deneme failed: $($_.Exception.Message)" }
                  Start-Sleep -Seconds 5
                }

                $service.Refresh()
                $servisCalisiyor = $service.Status -eq 'Running'
                if ($servisCalisiyor) {
                  Write-UpdateLog "Service '$serviceName' is running."
                }
                else {
                  Write-UpdateLog "Service '$serviceName' is NOT running (status: $($service.Status))."
                  # Panel ayakta degilse kullanici bunu MUTLAKA bilmeli -- kurulum olmus olsa bile.
                  $failed = $true
                  $failReason = "Kurulum tamamlandi ancak panel servisi baslatilamadi (durum: $($service.Status))."
                }
              }
            }
            catch {
              $failed = $true
              if ($failReason -eq '') { $failReason = "Servis kurtarma adimi basarisiz: $($_.Exception.Message)" }
              Write-UpdateLog "Service recovery failed: $($_.Exception.ToString())"
            }

            # SON DURUM: panel geri donunce bunu okuyup kullaniciya sonucu soyleyecek.
            if ($failed) {
              if ($failReason -eq '') { $failReason = 'Kurulum tamamlanamadi.' }

              # KURULUM GUNLUGUNUN SONU da tasiniyor. Asil sebep cogu zaman burada yaziyor
              # ("Cannot create file ... being used by another process" gibi) ve kullanicinin
              # bunu gormek icin sunucuya baglanip dosya aramasi gerekiyordu.
              try {
                $kuyruk = Get-Content -LiteralPath $logPath -Tail 25 -ErrorAction SilentlyContinue
                if ($kuyruk) { $failReason = $failReason + "`n---- kurulum gunlugu (son satirlar) ----`n" + ($kuyruk -join "`n") }
              }
              catch { }

              Write-UpdateState 'failed' $failReason $failExitCode
            }
            else { Write-UpdateState 'succeeded' '' 0 }

            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            if ($failed) { exit 1 }
            """;
    }

    private static string EscapePowerShell(string value) => value.Replace("\"", "`\"");

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");
}
