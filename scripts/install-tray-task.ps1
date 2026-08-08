<#
.SYNOPSIS
  Tepsi simgesini AYAKTA TUTAN zamanlanmis gorevi kurar.

.DESCRIPTION
  Sorun: panel kendi kendini guncellerken kurulumu SERVIS kimligiyle (LocalSystem, oturum 0)
  calistiriyor. Bu baglamdan kullanicinin masaustu oturumunda program baslatilamaz. Guncelleme
  sirasinda calisan tepsi sureci de exe degistirilebilsin diye sonlandiriliyor. Sonuc: her
  guncellemeden sonra simge kayboluyor ve kullanici Baslat menusunden ELLE baslatmak zorunda
  kaliyor -- oysa bu program tam da "panel acilmadiginda" ise yarayacak.

  Cozum: oturum acilisinda baslayan VE belirli arayla tekrarlanan bir gorev. "--tray" anahtari
  zaten calisiyorsa sessizce cikar (pencere ACMAZ), dolayisiyla tekrarlanmasi zararsizdir.

  Neden Baslangic klasoru yetmiyor: orasi yalnizca OTURUM ACILISINDA calisir. Oturum acikken
  surec olurse (guncelleme, cokme, kullanicinin yanlislikla kapatmasi) bir daha gelmez.

.NOTES
  Gorev BUILTIN\Users olarak ve INTERAKTIF belirtecle calisir: her kullanicinin kendi
  oturumunda, kendi yetkisiyle. Yonetici yetkisi ISTEMEZ -- yoksa her tetiklenmede UAC cikardi.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [string]$TaskName = 'AdaPanel Tepsi',
    [int]$RepeatMinutes = 5
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $InstallDir 'AdaPanel.Api.exe'
if (-not (Test-Path $exe)) {
    throw "AdaPanel.Api.exe bulunamadi: $exe"
}

# XML kullaniliyor cunku "oturum acilisi" tetikleyicisine TEKRARLAMA eklemek yalnizca boyle
# mumkun; schtasks'in /SC ONLOGON bicimi /RI kabul etmiyor.
#
# ExecutionTimeLimit=PT0S SART: varsayilan 3 gundur ve Gorev Zamanlayici o sure sonunda
# tepsi surecini OLDURURDU. Simge birkac gun sonra sebepsiz kayboluyor gibi gorunurdu.
#
# MultipleInstances=IgnoreNew: tepsi calisirken gorev "calisiyor" sayilir ve yeni tetikleme
# atlanir. Surec oldugunde gorev de biter, bir sonraki tekrar onu geri getirir.
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>AdaPanel bildirim alani simgesini calisir durumda tutar.</Description>
    <URI>\$TaskName</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Repetition>
        <Interval>PT${RepeatMinutes}M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-545</GroupId>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"$exe"</Command>
      <Arguments>--tray</Arguments>
      <WorkingDirectory>$InstallDir</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

$gecici = Join-Path $env:TEMP 'adapanel-tepsi-gorev.xml'
# Gorev Zamanlayici XML'i UTF-16 bekler; UTF-8 verilirse "gecersiz gorev XML" hatasi doner.
[System.IO.File]::WriteAllText($gecici, $xml, [System.Text.Encoding]::Unicode)

try {
    # /F : varsa uzerine yaz (guncellemede yol degismis olabilir).
    $cikti = & schtasks.exe /Create /TN $TaskName /XML $gecici /F 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Gorev olusturulamadi (kod $LASTEXITCODE): $cikti"
    }

    # DOGRULA: schtasks bazen 0 dondurup gorevi olusturmayabiliyor (yol/izin durumlarinda).
    # "Olusturuldu" demek yetmez, GERCEKTEN var mi diye sorulur.
    $kontrol = & schtasks.exe /Query /TN $TaskName 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Gorev olusturuldu goruldu ama sorgulanamadi: $kontrol"
    }

    Write-Output "Zamanlanmis gorev hazir: $TaskName (her $RepeatMinutes dakikada bir denetlenir)"
}
finally {
    Remove-Item $gecici -Force -ErrorAction SilentlyContinue
}
