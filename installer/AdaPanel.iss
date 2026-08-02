#define MyAppName "AdaPanel"
#ifndef MyAppVersion
  #define MyAppVersion "0.2.33"
#endif
#define MyAppPublisher "AdaPanel"
#define MyAppExeName "AdaPanel.Api.exe"
#define MyAppServiceName "AdaPanel"
#define PublishDir "..\artifacts\publish\win-x64"
#define InstallerOutDir "..\artifacts\installer"

[Setup]
AppId={{A9D88A4B-5B8D-4429-BD93-A08D0C7F9846}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\AdaPanel
DefaultGroupName=AdaPanel
DisableProgramGroupPage=yes
OutputDir={#InstallerOutDir}
OutputBaseFilename=AdaPanelSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
AppContact=AdaPanel
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
; CloseApplications=no OLMAK ZORUNDA. Panel bir WINDOWS SERVISIDIR; RestartManager onu pencere
; kapatarak durduramaz. CloseApplications=yes iken sira sudur: RestartManager dosyalari tutan
; AdaPanel.Api.exe'yi bulur, kapatmayi dener, BASARAMAZ ve Abort/Retry/Ignore sorar. Sessiz
; guncellemede (/SUPPRESSMSGBOXES) bu soru ABORT'a duser -> kurulum iptal, geri alma, cikis kodu 5.
; Yani panel kendi kendini HIC guncelleyemiyordu. Canli sunucuda birebir bu yasandi (apply-update.log).
; Servisi durdurma isini zaten PrepareToInstall dogru sekilde yapiyor (Stop-Service + taskkill yedegi)
; ve o adim dosyalar kopyalanmadan ONCE calisir; RestartManager'a gerek yok.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Messages]
tr.WelcomeLabel1=AdaPanel Kurulum Sihirbazina Hos Geldiniz
tr.WelcomeLabel2=Bu sihirbaz AdaPanel'i (Windows barindirma kontrol paneli) bilgisayariniza kuracak.%n%nDevam etmeden once tum diger uygulamalari kapatmaniz onerilir.
tr.SetupWindowTitle=AdaPanel Kurulumu
tr.FinishedHeadingLabel=AdaPanel kurulumu tamamlandi

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\components.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\enable-iis-features.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\initialize-panel-certificate.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\install-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\configure-firewall.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\harden-adapanel.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\uninstall-cleanup.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion; AfterInstall: ConfigureInstallation

[InstallDelete]
Type: filesandordirs; Name: "{app}\wwwroot"
Type: files; Name: "{app}\components.json"

[Run]
Filename: "{code:GetPanelUrl}"; Description: "AdaPanel arayuzunu ac"; Flags: postinstall shellexec skipifsilent

[Tasks]
Name: "desktopicon"; Description: "Masaustune AdaPanel kisayolu olustur"; GroupDescription: "Kisayollar:"

[Icons]
Name: "{group}\AdaPanel"; Filename: "{code:GetPanelUrl}"
Name: "{group}\AdaPanel'i Kaldir"; Filename: "{uninstallexe}"
Name: "{autodesktop}\AdaPanel"; Filename: "{code:GetPanelUrl}"; Tasks: desktopicon

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\scripts\uninstall-cleanup.ps1"" -InstallDir ""{app}"" -DataRoot ""{commonappdata}\AdaPanel"" -KeepData {code:GetUninstallKeepData}"; Flags: runhidden waituntilterminated; RunOnceId: "AdaPanelCleanUninstall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  PortPage: TInputQueryWizardPage;
  IsUpdateInstall: Boolean;
  WipeAllData: Boolean;

function GetPanelPort(Param: string): string;
forward;

procedure RunPowerShellScript(const ScriptName: string; const Arguments: string);
var
  ResultCode: Integer;
  ScriptPath: string;
  Parameters: string;
begin
  ScriptPath := ExpandConstant('{app}\scripts\') + ScriptName;
  Parameters := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  if Arguments <> '' then
    Parameters := Parameters + ' ' + Arguments;

  if not Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    RaiseException('AdaPanel kurulum adimi baslatilamadi: ' + ScriptName);
  end;

  if ResultCode <> 0 then
  begin
    RaiseException(
      'AdaPanel kurulum adimi basarisiz oldu: ' + ScriptName +
      ' (cikis kodu ' + IntToStr(ResultCode) + ')');
  end;
end;

{ Best-effort: IIS ozellikleri / firewall gibi adimlar panel SERVISINI kurmayi engellememeli.
  Panel (Kestrel Windows servisi) bunlarsiz da acilir; bu yuzden hatayi yutup loglariz. }
procedure TryRunPowerShellScript(const ScriptName: string; const Arguments: string);
begin
  try
    RunPowerShellScript(ScriptName, Arguments);
  except
    Log('AdaPanel: best-effort kurulum adimi atlandi (' + ScriptName + '): ' + GetExceptionMessage);
  end;
end;

procedure ConfigureInstallation;
begin
  { IIS = site barindirma; panel IIS olmadan da acilir -> best-effort, kurulumu BOZMAZ. }
  TryRunPowerShellScript('enable-iis-features.ps1', '');
  { Sertifika = best-effort: hata olsa bile install-service'i ENGELLEME (eksikse panel HTTP'ye duser ya da
    mevcut sertifikayi kullanir; servis acilinca HardenAsync certs ACL'ini onarir -> sonraki sefer duzgun olur). }
  TryRunPowerShellScript('initialize-panel-certificate.ps1', '');
  { Servis = KRITIK: panelin calismasi icin sart. }
  RunPowerShellScript(
    'install-service.ps1',
    '-ExecutablePath "' + ExpandConstant('{app}\{#MyAppExeName}') +
    '" -ServiceName "{#MyAppServiceName}" -Urls "https://0.0.0.0:' +
    GetPanelPort('') + '" -PreferredHttpsPort ' + GetPanelPort(''));
  { Firewall = uzaktan erisim; panel local acilir -> best-effort. }
  TryRunPowerShellScript('configure-firewall.ps1', '-Port ' + GetPanelPort(''));
end;

function ExtractPortFromMarker(ImagePath: string; Marker: string): string;
var
  Position: Integer;
  Index: Integer;
  Character: string;
begin
  Result := '';
  Position := Pos(Marker, ImagePath);
  if Position = 0 then
    Exit;

  Index := Position + Length(Marker);
  while Index <= Length(ImagePath) do
  begin
    Character := Copy(ImagePath, Index, 1);
    if Pos(Character, '0123456789') = 0 then
      Break;

    Result := Result + Character;
    Index := Index + 1;
  end;
end;

function ReadExistingPanelPort: string;
var
  ImagePath: string;
begin
  Result := '';
  if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\{#MyAppServiceName}', 'ImagePath', ImagePath) then
  begin
    Result := ExtractPortFromMarker(ImagePath, 'https://0.0.0.0:');
    if Result = '' then
      Result := ExtractPortFromMarker(ImagePath, 'http://0.0.0.0:');
    if Result = '' then
      Result := ExtractPortFromMarker(ImagePath, 'https://localhost:');
    if Result = '' then
      Result := ExtractPortFromMarker(ImagePath, 'http://localhost:');
  end;
end;

function ExistingInstallDetected: Boolean;
var
  ImagePath: string;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\{#MyAppServiceName}', 'ImagePath', ImagePath) or
    FileExists(ExpandConstant('{commonappdata}\AdaPanel\data\adapanel.db')) or
    DirExists(ExpandConstant('{commonappdata}\AdaPanel\data')) or
    FileExists(ExpandConstant('{autopf}\AdaPanel\{#MyAppExeName}'));
end;

procedure StopPanelService;
var
  ResultCode: Integer;
begin
  Exec(
    'powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "if (Get-Service -Name ''{#MyAppServiceName}'' -ErrorAction SilentlyContinue) { Stop-Service -Name ''{#MyAppServiceName}'' -Force -ErrorAction SilentlyContinue; for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 500; $svc = Get-Service -Name ''{#MyAppServiceName}'' -ErrorAction SilentlyContinue; if (-not $svc -or $svc.Status -eq ''Stopped'') { break } } }; if (Get-Process -Name ''AdaPanel.Api'' -ErrorAction SilentlyContinue) { taskkill /F /IM AdaPanel.Api.exe | Out-Null; Start-Sleep -Seconds 2 }"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

// Servis EN BASTA durdurulur; RestartManager taramasindan da once.
//
// Neden burada: panel bir Windows servisidir ve RestartManager onu pencere kapatarak durduramaz.
// Duramayinca Abort/Retry/Ignore sorar; sessiz guncellemede (/SUPPRESSMSGBOXES) bu soru ABORT'a
// duser, kurulum iptal olur ve cikis kodu 5 doner. Canli sunucuda birebir bu yasandi.
//
// PrepareToInstall'da durdurmak YETMIYOR: o adim RestartManager taramasindan SONRA calisir.
// CloseApplications=no ayari da tek basina yetmiyor: eski surumlerin guncelleme calistiricisi
// komut satirindan /CLOSEAPPLICATIONS geciriyor ve KOMUT SATIRI betikteki ayari EZER. Servisi
// burada durdurunca dosyalari tutan sure kalmiyor ve tarama zaten bir sey bulamiyor.
function InitializeSetup: Boolean;
begin
  if ExistingInstallDetected then
    StopPanelService;
  Result := True;
end;

procedure InitializeWizard;
var
  ExistingPort: string;
begin
  IsUpdateInstall := ExistingInstallDetected;
  ExistingPort := ReadExistingPanelPort;

  PortPage := CreateInputQueryPage(
    wpSelectDir,
    'AdaPanel port',
    'AdaPanel dinleme portunu secin',
    'AdaPanel arayuzu icin bu makinede bos bir TCP portu girin (varsayilan 8890).');
  PortPage.Add('AdaPanel portu:', False);
  if ExistingPort <> '' then
    PortPage.Values[0] := ExistingPort
  else
    PortPage.Values[0] := '8890';
end;

function GetPanelPort(Param: string): string;
begin
  Result := Trim(PortPage.Values[0]);
  if Result = '' then
    Result := '8890';
end;

function GetPanelUrl(Param: string): string;
begin
  Result := 'https://localhost:' + GetPanelPort('') + '/panel';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PortValue: Integer;
begin
  Result := True;

  if CurPageID = PortPage.ID then
  begin
    PortValue := StrToIntDef(GetPanelPort(''), 0);
    if (PortValue < 1) or (PortValue > 65535) then
    begin
      MsgBox('Lutfen 1 ile 65535 arasinda gecerli bir TCP portu girin.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := IsUpdateInstall and (ReadExistingPanelPort <> '') and (PageID = PortPage.ID);
end;

{ Kaldirma: veriler VARSAYILAN OLARAK KORUNUR (Plesk gibi once sorulur). Yalnizca kullanici
  acikca EVET derse musteri veritabanlari/siteler/yedekler silinir. Sessiz kaldirma = koru. }
function InitializeUninstall(): Boolean;
begin
  Result := True;
  WipeAllData := False;
  if not UninstallSilent then
    WipeAllData := MsgBox(
      'AdaPanel kaldiriliyor.' + #13#10 + #13#10 +
      'BARINDIRMA VERILERI DE SILINSIN MI?' + #13#10 +
      '(musteri veritabanlari, web sitesi kayitlari, yedekler, panel ayarlari)' + #13#10 + #13#10 +
      'Hayir = yalnizca panel kaldirilir; siteler ve veritabanlari CALISMAYA DEVAM EDER (onerilen).' + #13#10 +
      'Evet = HER SEY kalici olarak silinir.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

function GetUninstallKeepData(Param: string): string;
begin
  if WipeAllData then
    Result := '0'
  else
    Result := '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  Result := '';
  // InitializeSetup zaten durdurdu; burada tekrar denemek ucuz bir guvence (kullanici sihirbazda
  // beklerken servis elle baslatilmis olabilir).
  if ExistingInstallDetected then
    StopPanelService;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption :=
      'AdaPanel kuruldu ve servis baslatildi.' + #13#10 + #13#10 +
      'Bu bilgisayardan erisim:' + #13#10 +
      '    ' + GetPanelUrl('') + #13#10 + #13#10 +
      'Ag uzerinden / uzaktan erisim:' + #13#10 +
      '    https://<SUNUCU-IP>:' + GetPanelPort('') + '/panel' + #13#10 + #13#10 +
      'Sunucunun IP adreslerini, panel acildiktan sonra Genel Bakis ekranindaki ' +
      '"Panele Erisim Adresleri" kartinda gorebilirsiniz.';
end;
