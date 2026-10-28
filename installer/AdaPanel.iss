#define MyAppName "AdaPanel"
#ifndef MyAppVersion
  #define MyAppVersion "0.8.126"
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
UninstallDisplayIcon={app}\adapanel.ico
SetupIconFile=adapanel.ico
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
; Extracted before installation; keep before the solid-compressed application payload.
Source: "..\scripts\update-service-guard.ps1"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "adapanel.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\components.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\enable-iis-features.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\initialize-panel-certificate.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\install-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\configure-firewall.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\harden-adapanel.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\install-tray-task.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
; .htaccess IIS modulunun kurulum/kaldirma betigi. AYRI bir betik cunku iki yerden cagriliyor:
; kurulumda (ConfigureInstallation) ve kaldirmada ([UninstallRun], -Remove ile).
Source: "..\scripts\install-htaccess-module.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
; AfterInstall BU GIRDIDE, cunku [Files] bolumunun SON satiri: ConfigureInstallation ancak butun
; dosyalar kopyalandiktan sonra calisir. Yeni bir [Files] girdisi eklerken BU SATIRIN ALTINA degil
; USTUNE eklenmeli; yoksa servis, henuz kopyalanmamis dosyalarla baslatilir.
;
; ONEMLI (K11a -- olculdu): ConfigureInstallation -> install-service.ps1 servisi BASLATIR, ama
; kurulum sureci bundan SONRA da devam eder ([Icons], [Run], kaldirici kaydi, gunlugun kapanmasi).
; Canli olcum: panel istekleri yanitlamaya 00:49:07'de basladi, "Installation process succeeded"
; 00:49:19, "Log closed" 00:49:23 -- yani yeni panel, kurulum bitmeden ~16 saniye once AYAKTAYDI.
;
; Bu ORTUSME KALDIRILAMAZ ve kaldirilmaya calisilmamalidir: servis baslatmayi kurulumun sonuna
; (ssPostInstall / [Run]) tasimak, install-service.ps1'in "KRITIK" olma ozelligini yok ederdi --
; bugun servis kurulamazsa RaiseException ile kurulum IPTAL EDILIP geri aliniyor (wwwroot yedegi
; dahil). O guvenceyi kaybetmek, kazanilan seyden cok daha degerlidir. Kaldi ki kurulum sureci her
; halukarda servisten sonra bir sure daha yasar; yaris tamamen kapatilamaz.
;
; DOGRU COZUM YARISI KAZANMAK DEGIL, HUKME SAHIPLIK KOYMAKTIR ve panel tarafinda yapildi:
; panel kendi olcumuyle karar verdiyse (isDecidedByPanel) calistirici o hukmu ezemez, yalnizca
; eksik kurulum cikis kodunu tamamlayabilir. Bkz. WindowsUpdateInstallerLauncher.Write-UpdateState
; ve SelfUpdateService.ShouldRestorePanelVerdict.
Source: "..\scripts\uninstall-cleanup.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion; AfterInstall: ConfigureInstallation

[InstallDelete]
; DIKKAT: {app}\wwwroot BURADA SILINMEZ.
;
; [InstallDelete] dosyalar kopyalanmadan ONCE calisir ve Inno'nun geri alma (rollback) mekanizmasi
; [InstallDelete] ile silinenleri GERI GETIRMEZ. ConfigureInstallation icindeki install-service.ps1
; "KRITIK" isaretlidir ve RaiseException ile kurulumu iptal eder. Eski dizi suydu:
;   wwwroot silinir -> dosyalar kopyalanir -> install-service patlar -> rollback eski exe'yi geri
;   getirir ama wwwroot BOSTUR -> servis "Running" gorunur, tarayicida BOS SAYFA cikar.
; Teshisi zor, sessiz bir bozulma. Artik wwwroot .bak'e TASINIR (PrepareToInstall), basarida
; silinir, basarisizlikta GERI KONUR -- bkz. [Code] bolumundeki BackupWwwRoot/RestoreWwwRoot.
;
; components.json girdisi de AYNI SEBEPLE kaldirildi ve GEREKSIZDI: [Files] bolumundeki iki girdi
; de "ignoreversion" ile zaten uzerine yaziyor. Silinip sonra kurulum patlarsa rollback onu geri
; getirmiyordu -> panel bilesen listesi olmadan aciliyordu.

[Run]
; TEPSIYI AYAKTA TUTAN ZAMANLANMIS GOREV.
; Baslangic klasoru yalnizca OTURUM ACILISINDA calisir. Panel kendi kendini guncellerken kurulumu
; SERVIS kimligiyle (oturum 0) calistirdigi icin kullanicinin oturumunda hicbir sey baslatamiyor,
; calisan tepsi sureci de exe degistirilebilsin diye sonlandiriliyordu: her guncellemeden sonra
; simge kayboluyor ve kullanici Baslat menusunden ELLE baslatmak zorunda kaliyordu. Gorev belirli
; arayla denetler; "--tray" zaten calisiyorsa sessizce cikar, tekrarlanmasi zararsizdir.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install-tray-task.ps1"" -InstallDir ""{app}"""; Flags: runhidden waituntilterminated; Tasks: trayicon

; Kurulumdan HEMEN sonra baslat: kullanici bir sonraki oturum acilisini beklemesin.
; `skipifsilent` KALDIRILDI: panelin kendi kendini guncellemesi sessiz kurulum yapiyor ve bu bayrak
; yuzunden tepsi hic baslatilmiyordu. Etkilesimli oturum yoksa calistirma zaten sessizce basarisiz
; olur; zarari yok, faydasi var.
Filename: "{app}\AdaPanel.Api.exe"; Parameters: "--tray"; Flags: nowait runasoriginaluser; Tasks: trayicon
Filename: "{code:GetPanelUrl}"; Description: "AdaPanel arayuzunu ac"; Flags: postinstall shellexec skipifsilent

[Tasks]
Name: "desktopicon"; Description: "Masaustune AdaPanel kisayolu olustur"; GroupDescription: "Kisayollar:"
; Tepsi programi: panel HIC acilmadiginda servisleri gorup yeniden baslatmanin tek yolu.
; Varsayilan ACIK -- kurtarma araci, ihtiyac duyulmadan once kurulu olmali.
Name: "trayicon"; Description: "Saat yanindaki bildirim alaninda AdaPanel simgesi (servis durumu ve yeniden baslatma)"; GroupDescription: "Kisayollar:"

[Icons]
Name: "{group}\AdaPanel"; Filename: "{code:GetPanelUrl}"; IconFilename: "{app}\adapanel.ico"
Name: "{group}\AdaPanel'i Kaldir"; Filename: "{uninstallexe}"
Name: "{autodesktop}\AdaPanel"; Filename: "{code:GetPanelUrl}"; IconFilename: "{app}\adapanel.ico"; Tasks: desktopicon
; OTURUM ACILISINDA baslar. Yonetici ISTEMEZ (manifest asInvoker): her acilista UAC penceresi
; cikarsa kullanici programi kapatir ve tam ihtiyac aninda elinde bir sey kalmaz.
;
; {commonstartup} -- TUM KULLANICILAR icin. Onceden {userstartup} yaziyordu ve SUNUCUDA HIC
; GORUNMUYORDU: panel kendi kendini guncellerken kurulumu SERVIS kimligiyle (LocalSystem)
; calistiriyor, dolayisiyla "kurulumu yapan kullanici" SYSTEM oluyor ve kisayol SYSTEM'in
; Baslangic klasorune dusuyordu. Yonetici RDP ile baglandiginda oradan hicbir sey acilmiyordu.
Name: "{commonstartup}\AdaPanel Tepsi"; Filename: "{app}\AdaPanel.Api.exe"; Parameters: "--tray"; Tasks: trayicon
; "--tray-goster": pencereyi ACAR. Duz "--tray" zaten calisiyorsa sessizce cikar (dogru davranis,
; ama kisayola basan kullanici bir sey gormek ister).
Name: "{group}\AdaPanel Servis Durumu"; Filename: "{app}\AdaPanel.Api.exe"; Parameters: "--tray-goster"; IconFilename: "{app}\adapanel.ico"

[UninstallRun]
; .HTACCESS MODULU EN BASTA KALDIRILIR ve bu SIRA ONEMLIDIR.
; Modul GAC'ta ve applicationHost.config'te yasiyor; ikisi de {app} klasorunun DISINDA.
; [UninstallDelete] {app}'i silse bile bu iki kayit KALIRDI ve sonuc su olurdu: IIS her istekte
; artik var olmayan bir derlemeyi yuklemeye calisir -> sunucudaki TUM siteler 500 verir.
; Yani bu satir olmadan "AdaPanel'i kaldirmak" musterinin butun sitelerini dusururdu.
; Once kayit gider, sonra dosyalar.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install-htaccess-module.ps1"" -Remove -ModulePath ""{app}\AdaPanel.HtaccessModule.dll"""; Flags: runhidden waituntilterminated; RunOnceId: "AdaPanelHtaccessModulKaldir"
; Gorev kaldirilmazsa, program silindikten sonra her birkac dakikada bir var olmayan bir exe
; calistirilmaya calisilir ve Gorev Zamanlayici hata biriktirir.
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""AdaPanel Tepsi"" /F"; Flags: runhidden; RunOnceId: "AdaPanelTepsiGorevSil"
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\scripts\uninstall-cleanup.ps1"" -InstallDir ""{app}"" -DataRoot ""{commonappdata}\AdaPanel"" -KeepData {code:GetUninstallKeepData}"; Flags: runhidden waituntilterminated; RunOnceId: "AdaPanelCleanUninstall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  PortPage: TInputQueryWizardPage;
  IsUpdateInstall: Boolean;
  WipeAllData: Boolean;
  { InitializeSetup sihirbaz acilmadan ONCE servisi durdurur (bkz. oradaki aciklama). Kullanici
    ilk ekranda Iptal'e basarsa servis DURMUS kalirdi: siteler IIS'te calismaya devam ettigi icin
    kimse fark etmez, panel sessizce olur. Bu bayrak "biz durdurduk mu" bilgisini tasir. }
  ServiceWasStoppedByUs: Boolean;
  InstallCompleted: Boolean;
  { wwwroot yedegi alindi mi (basarisiz yukseltmede geri koymak icin). }
  WwwRootBackedUp: Boolean;

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
  { .htaccess IIS modulu = best-effort. IKI SEBEPLE kurulumu ASLA bozmamali:
    1) Panel bu modul olmadan da TAM calisir; .htaccess yalnizca bir ozelliktir.
    2) Adim GAC'a yaziyor ve applicationHost.config'e dokunuyor -- yani IIS'i etkileyen en riskli
       adim. Basarisiz olursa DOGRU davranis, kurulumu iptal etmek degil, ozelligi ATLAMAKTIR.
    Betik zaten IIS yoksa / DLL yoksa sessizce cikiyor.

    KRITIK: bu adim modulu ACMAZ. Genel anahtar dosyasini OLUSTURMAZ, dolayisiyla kurulumdan
    sonra sunucudaki HICBIR sitenin davranisi degismez. Modul kayitli ama OLUDUR; acmak
    panelden, site site yapilir. Urunun en yuksek etki alanli degisikligi icin kabul edilebilir
    tek varsayilan budur. }
  TryRunPowerShellScript(
    'install-htaccess-module.ps1',
    '-ModulePath "' + ExpandConstant('{app}\AdaPanel.HtaccessModule.dll') +
    '" -DataRoot "' + ExpandConstant('{commonappdata}\AdaPanel') + '"');
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

function RunServiceGuard(const Action: string): Boolean;
var
  ResultCode: Integer;
begin
  if not FileExists(ExpandConstant('{tmp}\update-service-guard.ps1')) then
    ExtractTemporaryFile('update-service-guard.ps1');
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\update-service-guard.ps1') + '" -Action ' + Action +
    ' -ServiceName "{#MyAppServiceName}" -StatePath "' +
    ExpandConstant('{tmp}\adapanel-service-state.json') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then Result := ResultCode = 0;
  if Result then Log('AdaPanel service guard completed: ' + Action)
  else Log('AdaPanel service guard failed: ' + Action);
end;

procedure StopPanelService;
begin
  if not RunServiceGuard('Stop') then
    RaiseException('AdaPanel servisi guvenle durdurulamadi. Kurulum dosyalari degistirilmedi.');
end;

{ Servisi GERI BASLATIR. Boyle bir prosedur HIC YOKTU: kurulum yarida kesildiginde servisi
  yeniden baslatan tek bir kod yolu bile bulunmuyordu. }
procedure StartPanelService;
var
  ResultCode: Integer;
begin
  if not RunServiceGuard('Restore') then
  begin
    Log('AdaPanel: servis baslangic ayari geri yuklenemedi.');
    Exit;
  end;
  Exec(
    'powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "$svc = Get-Service -Name ''{#MyAppServiceName}'' -ErrorAction SilentlyContinue; if ($svc -and $svc.Status -ne ''Running'') { Start-Service -Name ''{#MyAppServiceName}'' -ErrorAction SilentlyContinue }"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

{ wwwroot'u .bak'e TASIR (kopyalamaz: tasima atomiktir ve disk yeri istemez). }
function BackupWwwRoot: Boolean;
var
  Source: string;
  Backup: string;
begin
  Result := False;
  Source := ExpandConstant('{app}\wwwroot');
  Backup := ExpandConstant('{app}\wwwroot.bak');

  if not DirExists(Source) then
    Exit;

  if DirExists(Backup) then
    DelTree(Backup, True, True, True);

  Result := RenameFile(Source, Backup);
  if not Result then
    Log('AdaPanel: wwwroot yedeklenemedi; eski icerik yerinde birakiliyor.');
end;

procedure RestoreWwwRoot;
var
  Source: string;
  Backup: string;
begin
  Source := ExpandConstant('{app}\wwwroot');
  Backup := ExpandConstant('{app}\wwwroot.bak');

  if not DirExists(Backup) then
    Exit;

  { Yarim kopyalanmis yeni icerik varsa once o gider; yoksa geri koyma basarisiz olur. }
  if DirExists(Source) then
    DelTree(Source, True, True, True);

  if RenameFile(Backup, Source) then
    Log('AdaPanel: wwwroot yedekten geri konuldu.')
  else
    Log('AdaPanel: wwwroot GERI KONULAMADI: ' + Backup);
end;

procedure DiscardWwwRootBackup;
var
  Backup: string;
begin
  Backup := ExpandConstant('{app}\wwwroot.bak');
  if DirExists(Backup) then
    DelTree(Backup, True, True, True);
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
  ServiceWasStoppedByUs := False;
  InstallCompleted := False;
  WwwRootBackedUp := False;

  if ExistingInstallDetected then
  begin
    ServiceWasStoppedByUs := True;
    try
      StopPanelService;
    except
      StartPanelService;
      RaiseException(GetExceptionMessage);
    end;
  end;

  Result := True;
end;

{ KURULUM TAMAMLANMADIYSA SERVISI GERI BASLAT.
  Bu prosedur dosyada HIC YOKTU. InitializeSetup servisi sihirbaz acilmadan durdurdugu icin,
  kullanicinin ilk ekranda Iptal'e basmasi paneli OLU birakiyordu -- ve siteler IIS'te calismaya
  devam ettigi icin bunu kimse fark etmiyordu. Ayni sey wwwroot yedegi icin de gecerli:
  kurulum yarida kaldiysa eski arayuz geri konur, yoksa panel bos sayfa acar. }
procedure DeinitializeSetup;
begin
  if InstallCompleted then
  begin
    RunServiceGuard('Restore');
    DiscardWwwRootBackup;
    Exit;
  end;

  if WwwRootBackedUp then
    RestoreWwwRoot;

  if ServiceWasStoppedByUs then
    StartPanelService;
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
  begin
    ServiceWasStoppedByUs := True;
    try
      StopPanelService;
    except
      Result := GetExceptionMessage;
      Exit;
    end;
  end;

  // Eski arayuz dosyalarini kopyalama BASLAMADAN once kenara al. Yerinde silmek (eski
  // [InstallDelete] girdisi) geri alinamazdi; tasima geri alinabilir.
  WwwRootBackedUp := BackupWwwRoot;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Buraya ulasildiysa dosyalar kopyalandi VE ConfigureInstallation (install-service.ps1 dahil)
    // hatasiz bitti. Kurulum basarilidir: yedek artik gereksiz, servisi de install-service basladi.
    InstallCompleted := True;
    DiscardWwwRootBackup;
    WwwRootBackedUp := False;
  end;
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
