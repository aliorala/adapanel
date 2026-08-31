using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Scheduler;

public sealed class SchedulerService(
    IIisSiteService siteService,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<SchedulerService> logger,
    // İSTEĞE BAĞLI: adres çözümleyici. Üretimde gerçek DNS; testte sahte, çünkü bir birim testi
    // GERÇEKTEN 169.254.169.254'e çözümleme yapmamalı.
    OutboundAddressGuard.OutboundHostResolver? outboundResolver = null) : ISchedulerService
{
    private const string TaskPath = "\\AdaPanel\\";

    /// <summary>
    /// "script" görevinin çözülmüş hâli: hangi program, hangi argümanlar, hangi klasör ve hangi kimlik.
    /// <paramref name="PrincipalUserId"/> null ise görev bugünkü gibi SYSTEM olarak çalışır.
    /// </summary>
    internal sealed record ScriptPlan(string Execute, string Arguments, string WorkingDirectory, string? PrincipalUserId);

    /// <summary>Görev çıktılarının tutulduğu klasör (panel veri kökü altında).</summary>
    private string TaskLogDirectory => Path.Combine(
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot)),
        "logs",
        "tasks");

    /// <summary>
    /// Görev adından güvenli bir dosya adı üretir.
    /// <para>
    /// Ad kullanıcıdan gelir ve "../" ya da ":" içerebilir; doğrudan yola eklemek panelin veri
    /// kökünün DIŞINA yazmasına yol açardı. Ada ek olarak adın SHA-256 önekini de koyarız: yalnız
    /// temizlenmiş ad kullanılsa, "yedek/gece" ile "yedek-gece" aynı dosyaya yazardı.
    /// </para>
    /// <para>
    /// AYRILMIŞ AYGIT ADI ayrıca etkisizleştirilir. Özet SON EKİ çoğu durumda tuzağı zaten
    /// kırıyordu ("CON" → "CON-a1b2c3d4.log", ayrılmış değil) ama NOKTALI adlarda kırmıyordu:
    /// "CON.txt" → "CON.txt-a1b2c3d4.log" ve ayrılmışlık İLK noktadan önceki parçaya bakıldığı
    /// için o dosya hâlâ aygıta gider — görev günlüğü hiç yazılamazdı. Bu yüzden kontrol
    /// birleştirilmiş ADIN TAMAMI üzerinde, en sonda yapılır.
    /// </para>
    /// </summary>
    internal static string ResolveLogFileName(string taskName)
    {
        var temiz = new string(taskName.Trim()
            .Select(karakter => Path.GetInvalidFileNameChars().Contains(karakter) ? '_' : karakter)
            .ToArray());

        if (temiz.Length > 60)
        {
            temiz = temiz[..60];
        }

        var ozet = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(taskName.Trim())))[..8];

        return WindowsReservedNames.MakeSafe($"{temiz}-{ozet}.log");
    }

    /// <summary>
    /// Eylemi, çıktısı bir dosyaya yazılacak biçimde cmd.exe'ye sarar.
    /// <para>
    /// Windows Görev Zamanlayıcı komut çıktısını SAKLAMAZ; yalnızca çıkış kodunu tutar. Kullanıcı
    /// "görev çalışmıyor" dediğinde elimizde sadece bir sayı oluyordu. Çıktıyı yakalamanın tek
    /// yolu, komutu bir kabuğa sarıp yönlendirmektir.
    /// </para>
    /// <para>
    /// ÇIKIŞ KODU KORUNUR: cmd /c, çalıştırdığı komutun çıkış kodunu döndürür — yani "son sonuç"
    /// sütunu aynı anlamı taşımaya devam eder. Bu olmasaydı çıktı kazanıp teşhisi kaybederdik.
    /// </para>
    /// <para>
    /// ">" kullanılır, ">>" DEĞİL: dosyada yalnızca SON çalışmanın çıktısı durur. Dakikada bir
    /// çalışan bir görevde ekleme yapmak, kimsenin okumadığı ve sınırsız büyüyen bir dosya bırakırdı.
    /// </para>
    /// </summary>
    internal static string BuildLoggedArguments(string execute, string arguments, string logPath)
    {
        // cmd /c "..." ayristirmasi: dis tirnaklar tamamen atilir ve ic kisim oldugu gibi calisir.
        // Bu yuzden bosluklu yollari ic tirnakla sarmak SART, ve komutun tamami ek bir tirnak
        // ciftinin icinde olmali -- yoksa cmd ilk tirnaktan son tirnaga kadarki her seyi program
        // adi sanar. Bu bicim ("" ile baslayip "" ile biten) Microsoft'un belgeledigi calisan bicimdir.
        var komut = $"\"{execute}\"";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            komut += " " + arguments;
        }

        return $"/c \"{komut} > \"{logPath}\" 2>&1\"";
    }

    /// <summary>
    /// Sarmalanmış bir eylemi geri çözer: cmd.exe kaydından asıl program ve argümanları çıkarır.
    /// <para>
    /// Gerekli çünkü panel görev tipini KAYITLI EYLEMDEN türetir (curl.exe → url, php.exe → php).
    /// Sarmalama sonrası her görev "cmd.exe" görünürdü: tipler bozulur, URL sütunu boşalır ve
    /// düzenleme ekranı yanlış dolardı. Yani çıktı kazanıp listeyi kaybederdik.
    /// </para>
    /// </summary>
    internal static bool TryUnwrapLoggedAction(string execute, string arguments, out string realExecute, out string realArguments)
    {
        realExecute = execute;
        realArguments = arguments;

        if (!execute.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // /c "<"program" argumanlar> > "<gunluk>" 2>&1"
        var match = Regex.Match(
            arguments ?? string.Empty,
            "^/c\\s+\"\"(?<exe>[^\"]+)\"(?<args>.*?)\\s*>\\s*\"[^\"]+\"\\s*2>&1\"$",
            RegexOptions.Singleline);

        if (!match.Success)
        {
            return false;
        }

        realExecute = match.Groups["exe"].Value;
        realArguments = match.Groups["args"].Value.Trim();
        return true;
    }

    public async Task<IReadOnlyCollection<ScheduledTaskDto>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        // DIKKAT-1: tarihler ISO ('o') STRING yazilmali — PS 5.1 ConvertTo-Json [datetime]'i "\/Date(...)\/"
        // bicimine cevirir ve System.Text.Json bunu OKUYAMAZ (liste dolunca common.unexpected_error).
        // DIKKAT-2: tetikleyici CIM nesnesinde 'Frequency' OZELLIGI YOKTUR; tip CimClassName + Repetition'dan turetilir.
        // DIKKAT-3: alanlar [string] ile zorlanir ki null gelmesin (C# tarafinda EndsWith NRE atmasin).
        var script = $@"$tasks = Get-ScheduledTask -TaskPath '{TaskPath}' -ErrorAction SilentlyContinue | ForEach-Object {{
  $info = Get-ScheduledTaskInfo -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue
  $trigger = if ($_.Triggers.Count -gt 0) {{ $_.Triggers[0] }} else {{ $null }}
  $scheduleType = ''
  $repetitionInterval = ''
  $timeOfDay = '00:00'
  if ($trigger) {{
    if ($trigger.CimClass.CimClassName -eq 'MSFT_TaskDailyTrigger') {{
      $scheduleType = 'daily'
      # Gunluk gorevde calisma saati StartBoundary'de durur. Duzenlemede geri okunmazsa
      # kullanicinin sectigi saat sessizce 00:00'a duserdi.
      if ($trigger.StartBoundary) {{
        try {{ $timeOfDay = ([datetime]$trigger.StartBoundary).ToString('HH:mm') }} catch {{ }}
      }}
    }}
    elseif ($trigger.Repetition) {{
      # HAM deger gonderilir; cozumleme C# tarafinda yapilir. PowerShell icinde iki desenle
      # ('^PT(\d+)M$' ve '^PT(\d+)H$') ugrasmak, Windows'un urettigi PT1H30M ve P1D gibi
      # bicimleri kaciriyor ve gorev sessizce saatlik-1 varsayilanina dusuyordu.
      $repetitionInterval = [string]$trigger.Repetition.Interval
    }}
  }}
  # Kimlik: sinirli hesapla calisan gorev duzenlemede SYSTEM'e dusmemeli -- bu, kullanicinin
  # istemedigi bir YETKI YUKSELTMESI olurdu.
  $runAs = 'system'
  if ($_.Principal -and $_.Principal.UserId -and $_.Principal.RunLevel -ne 'Highest') {{ $runAs = 'limited' }}
  # Sure siniri de HAM gonderilir; 24 saat P1D olarak saklandigi icin C# tarafinda cozulur.
  $executionLimit = ''
  if ($_.Settings) {{ $executionLimit = [string]$_.Settings.ExecutionTimeLimit }}
  [pscustomobject]@{{
    Name = $_.TaskName
    ScheduleType = $scheduleType
    RepetitionInterval = $repetitionInterval
    TimeOfDay = $timeOfDay
    RunAs = $runAs
    ExecutionTimeLimit = $executionLimit
    State = $_.State.ToString()
    Command = if ($_.Actions.Count -gt 0) {{ [string]$_.Actions[0].Execute }} else {{ '' }}
    Arguments = if ($_.Actions.Count -gt 0) {{ [string]$_.Actions[0].Arguments }} else {{ '' }}
    WorkingDirectory = if ($_.Actions.Count -gt 0) {{ [string]$_.Actions[0].WorkingDirectory }} else {{ '' }}
    NextRunTimeUtc = if ($info -and $info.NextRunTime -and ([datetime]$info.NextRunTime).Year -gt 2000) {{ ([datetime]$info.NextRunTime).ToUniversalTime().ToString('o') }} else {{ $null }}
    LastRunTimeUtc = if ($info -and $info.LastRunTime -and ([datetime]$info.LastRunTime).Year -gt 2000) {{ ([datetime]$info.LastRunTime).ToUniversalTime().ToString('o') }} else {{ $null }}
    LastTaskResult = if ($info) {{ [long]$info.LastTaskResult }} else {{ $null }}
  }}
}}
$tasks | ConvertTo-Json -Depth 5";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.read_failed", 500, 500);
        }

        var tasks = DeserializeArray<ScheduledTaskDto>(result.StandardOutput);
        foreach (var task in tasks)
        {
            // JSON'dan null sizarsa NRE atmasin (PS tarafi [string] zorluyor ama kemer-askı).
            task.Command ??= string.Empty;
            task.Arguments ??= string.Empty;

            // Tekrar araligi HAM haliyle geldi; burada cozulur. Cozulemeyen bir bicimde
            // "saatlik 1" varsayilanina DUSULMEZ -- o varsayilan, kullanicinin 24 saatlik gorevini
            // duzenledigi anda saatte bire cevirip gunde 24 kat daha sik calistiriyordu.
            // Sure siniri: PT0S = sinirsiz (0). 24 saat P1D olarak saklanir, bu yuzden ayni
            // ISO 8601 cozumleyicisi kullanilir -- "PT4H" disindaki her bicimi kacirmamak icin.
            task.TimeLimitHours = ParseRepetitionMinutes(task.ExecutionTimeLimit) is { } limitDakika
                ? limitDakika / 60
                : 0;

            if (string.IsNullOrEmpty(task.ScheduleType) && ParseRepetitionMinutes(task.RepetitionInterval) is { } dakika)
            {
                var (tip, aralik) = DescribeRepetition(dakika);
                task.ScheduleType = tip;
                task.Interval = aralik;
            }
            else if (string.IsNullOrEmpty(task.ScheduleType))
            {
                // Tetikleyici tanınmadı. Panelin uydurması, kullanıcının ayarını yanlış göstermek
                // ve düzenlemede o yanlışı KALICI hâle getirmek olurdu.
                task.ScheduleType = "hourly";
                task.ScheduleUnknown = true;
            }

            // Cikti yakalama sarmalini GERI COZ: tip turetmesi ve ekranda gorunen komut, kullanicinin
            // yazdigi sey olmali -- panelin onu nasil calistirdigi degil.
            if (TryUnwrapLoggedAction(task.Command, task.Arguments, out var gercekKomut, out var gercekArgumanlar))
            {
                task.Command = gercekKomut;
                task.Arguments = gercekArgumanlar;
                task.CapturesOutput = true;
            }

            // Görev tipini kayıtlı eylemden türet (URL curl ile, PHP php.exe ile çalışır).
            if (task.Command.EndsWith("curl.exe", StringComparison.OrdinalIgnoreCase))
            {
                task.TaskType = "url";
                var quoted = Regex.Matches(task.Arguments ?? string.Empty, "\"([^\"]+)\"");
                if (quoted.Count > 0)
                {
                    task.Url = quoted[^1].Groups[1].Value;
                }
            }
            else if (task.Command.EndsWith("php.exe", StringComparison.OrdinalIgnoreCase))
            {
                task.TaskType = "php";
                task.PhpVersion = ResolvePhpVersionFromPath(task.Command);

                // php tipinde form "Command" alaninda BETIGI bekler, php.exe'yi degil: argumanlarin
                // ilk tirnakli parcasi betiktir. Duzenleme ekraninin dogru dolmasi icin ayristirilir.
                var betik = Regex.Match(task.Arguments, "^\"([^\"]+)\"");
                if (betik.Success)
                {
                    task.Command = betik.Groups[1].Value;
                    task.Arguments = task.Arguments[betik.Length..].Trim();
                }
            }
            else if (ScriptTaskPlanner.IsScriptAction(task.Command, task.Arguments))
            {
                task.TaskType = "script";

                // Betik gorevinde form "Komut" alaninda BETIK DOSYASINI bekler. Kayitta ise
                // "cmd.exe /c ..." ya da "powershell.exe -File ..." durur. Geri cozulmezse hem
                // listede hem duzenleme ekraninda "cmd.exe" gorunur; kullanici oyle kaydederse
                // gorev BETIGINI KAYBEDER ve yalnizca cmd.exe calistiran bir goreve doner.
                if (ScriptTaskPlanner.TryResolveScriptPath(task.Command, task.Arguments, out var betikYolu, out var ekArgumanlar))
                {
                    task.Command = betikYolu;
                    task.Arguments = ekArgumanlar;
                }
            }
            else
            {
                task.TaskType = "command";
            }
        }

        return tasks;
    }

    public async Task<int> RemoveTasksUnderRootAsync(string siteRootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteRootPath))
        {
            return 0;
        }

        IReadOnlyCollection<ScheduledTaskDto> gorevler;
        try
        {
            gorevler = await GetTasksAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Görev listesi okunamadıysa site silme akışı DURMAZ; bu bir temizlik adımıdır.
            logger.LogWarning(exception, "Site gorevleri okunamadi ({Root}); temizlik atlandi.", siteRootPath);
            return 0;
        }

        var silinen = 0;
        foreach (var gorev in gorevler)
        {
            // YOL ÜZERİNDEN eşleştirme. Ad benzerliğine bakmak yanlış görev silerdi; kök-adı
            // öneki tuzağı (site1 / site1-evil) ScriptTaskPlanner.IsInsideRoot içinde kapalı.
            var komutIcerde = ScriptTaskPlanner.IsInsideRoot(siteRootPath, gorev.Command);
            var klasorIcerde = ScriptTaskPlanner.IsInsideRoot(siteRootPath, gorev.WorkingDirectory);

            if (!komutIcerde && !klasorIcerde)
            {
                continue;
            }

            try
            {
                await DeleteTaskAsync(gorev.Name, cancellationToken);
                silinen++;
                logger.LogInformation("Silinen sitenin gorevi kaldirildi: {TaskName} ({Root})", gorev.Name, siteRootPath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Tek bir görev silinemezse diğerleri denenmeye devam eder.
                logger.LogWarning(exception, "Site gorevi kaldirilamadi: {TaskName}", gorev.Name);
            }
        }

        return silinen;
    }

    public async Task<int> RepairTaskSettingsAsync(CancellationToken cancellationToken = default)
    {
        // Yalnizca GERCEKTEN yanlis olanlara dokunulur ve KAC TANESI duzeltildigi geri dondurulur.
        // "Hepsini yeniden yaz, tamam de" demek, panelin bu oturumda defalarca ayikladigimiz
        // aliskanligi olurdu: kendi niyetini olcum yerine koymak.
        //
        // Set-ScheduledTask kullanilir, Register degil: tetikleyici, eylem ve principal oldugu gibi
        // kalir -- yalnizca ayarlar degisir. Kullanicinin gorev tanimina dokunulmaz.
        var script = $@"$duzeltilen = 0
foreach ($gorev in (Get-ScheduledTask -TaskPath '{TaskPath}' -ErrorAction SilentlyContinue)) {{
  $s = $gorev.Settings
  # Sure siniri artik KULLANICININ karari; onarim pasi genelde ona DOKUNMAZ.
  #
  # TEK ISTISNA, PANELIN KENDI DAYATTIGI 4 SAAT: 0.2.66-0.2.74 arasinda her gorev sorulmadan
  # PT4H ile kaydedildi ve kullanicinin secme sansi hic olmadi. Yani bir gorevde PT4H gormek,
  # kullanicinin tercihi degil BIZIM birakigimiz izdir; onu geri almak kullanicinin ayarini
  # ezmek olmaz, tersine kendi hatamizi temizlemektir. Kullanicinin bilerek sectigi baska her
  # deger (PT1H, P1D, PT0S...) oldugu gibi kalir.
  $adaSureDuzeltilecek = ($s.ExecutionTimeLimit -eq 'PT4H')
  if ($s.DisallowStartIfOnBatteries -or $s.StopIfGoingOnBatteries -or -not $s.StartWhenAvailable -or $adaSureDuzeltilecek) {{
    $s.DisallowStartIfOnBatteries = $false
    $s.StopIfGoingOnBatteries = $false
    $s.StartWhenAvailable = $true
    if ($adaSureDuzeltilecek) {{ $s.ExecutionTimeLimit = 'PT0S' }}
    try {{
      Set-ScheduledTask -TaskPath $gorev.TaskPath -TaskName $gorev.TaskName -Settings $s -ErrorAction Stop | Out-Null
      $duzeltilen++
    }} catch {{
      [Console]::Out.WriteLine('ADAPANEL-UYARI: ' + $gorev.TaskName + ': ' + $_.Exception.Message)
    }}
  }}
}}
[Console]::Out.WriteLine('ADAPANEL-DUZELTILEN=' + $duzeltilen)";

        try
        {
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            var match = Regex.Match(result.StandardOutput ?? string.Empty, @"ADAPANEL-DUZELTILEN=(\d+)");
            var duzeltilen = match.Success ? int.Parse(match.Groups[1].Value) : 0;
            if (duzeltilen > 0)
            {
                logger.LogInformation("Zamanlanmis gorev ayarlari duzeltildi: {Count} gorev.", duzeltilen);
            }

            return duzeltilen;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Gorev zamanlayici okunamiyorsa panel yine de acilmali; bu bir onarim, bir onkosul degil.
            logger.LogWarning(exception, "Zamanlanmis gorev ayarlari denetlenemedi.");
            return 0;
        }
    }

    public async Task RunTaskAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunPowerShellAsync(BuildRunScript(EnsureUsableName(name)), cancellationToken);
        var cikti = result.StandardOutput ?? string.Empty;

        if (cikti.Contains("ADAPANEL-YOK", StringComparison.Ordinal))
        {
            throw new AppException("cron.not_found", 404, 404);
        }

        if (cikti.Contains("ADAPANEL-DEVREDISI", StringComparison.Ordinal))
        {
            throw new AppException("cron.task_disabled", 409, 409);
        }

        if (!result.Success || !cikti.Contains("ADAPANEL-BASLATILDI", StringComparison.Ordinal))
        {
            throw new AppException("cron.run_failed", 500, 500, result.StandardError);
        }
    }

    /// <summary>
    /// "Şimdi çalıştır" betiği: DEVRE DIŞI GÖREV ÖNCE ÖLÇÜLÜR, sonra tetiklenir.
    /// <para>
    /// <c>Start-ScheduledTask</c> devre dışı bir görevde ham PowerShell yığını döndürür
    /// ("Start-ScheduledTask : Görev devre dışı ... HRESULT 0x80041326") ve panel bunu olduğu gibi
    /// ekrana basıyordu. Kullanıcının yapması gereken şey (önce Etkinleştir) o metinde YOK; hata
    /// teşhis edilebilir olmalı ki çözülebilsin.
    /// </para>
    /// <para>
    /// Başarı POZİTİF BİR İŞARETLE ölçülür: PowerShell 5.1 sonlandırıcı olmayan hatalarda 0 döner,
    /// yani yalnızca çıkış koduna bakmak "tetiklendi" demeye yetmez.
    /// </para>
    /// </summary>
    internal static string BuildRunScript(string taskName) => $@"$gorev = Get-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(taskName)}' -ErrorAction SilentlyContinue
if (-not $gorev) {{
  [Console]::Out.WriteLine('ADAPANEL-YOK')
}} elseif ($gorev.State -eq 'Disabled') {{
  [Console]::Out.WriteLine('ADAPANEL-DEVREDISI')
}} else {{
  Start-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(taskName)}' -ErrorAction Stop
  [Console]::Out.WriteLine('ADAPANEL-BASLATILDI')
}}";

    /// <summary>
    /// Günlük dosyasının yolunu hazırlar; hazırlanamazsa null döner ve görev çıktı yakalamadan
    /// kaydedilir.
    /// <para>
    /// SESSİZCE VAZGEÇMEK burada DOĞRU: çıktı kaydı bir kolaylıktır, görevin ön koşulu değildir.
    /// Klasör oluşturulamadı diye kullanıcının görevini hiç kurmamak, kolaylık uğruna asıl işi
    /// engellemek olurdu.
    /// </para>
    /// </summary>
    private async Task<string?> PrepareLogPathAsync(string taskName, string? principalUserId, CancellationToken cancellationToken)
    {
        try
        {
            var klasor = TaskLogDirectory;
            Directory.CreateDirectory(klasor);

            // Sinirli kimlikle calisan gorev de kendi ciktisini yazabilmeli. Yazamazsa cmd
            // yonlendirmesi basarisiz olur ve gorev HIC calismaz -- yani kolaylik, isi bozar.
            if (!string.IsNullOrWhiteSpace(principalUserId))
            {
                var script = $@"& icacls.exe '{EscapeSingleQuoted(klasor)}' /grant '{EscapeSingleQuoted(principalUserId)}:(OI)(CI)M' | Out-Null";
                await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            }

            return Path.Combine(klasor, ResolveLogFileName(taskName));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Gorev cikti gunlugu hazirlanamadi ({TaskName}); gorev cikti kaydi olmadan kurulacak.", taskName);
            return null;
        }
    }

    public async Task<ScheduledTaskOutputDto> GetTaskOutputAsync(string name, CancellationToken cancellationToken = default)
    {
        name = EnsureUsableName(name);

        var dosya = Path.Combine(TaskLogDirectory, ResolveLogFileName(name));
        if (!File.Exists(dosya))
        {
            return new ScheduledTaskOutputDto { Name = name, Available = false };
        }

        // SON kisim okunur, tamami degil: bir gorev megabaytlarca cikti uretebilir ve tamamini
        // tarayiciya tasimak hem sunucuyu hem sayfayi bogar. Kullanicinin aradigi hata mesaji da
        // neredeyse her zaman SONDADIR.
        const int EnFazlaBayt = 64 * 1024;
        await using var akis = new FileStream(dosya, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var kirpildi = akis.Length > EnFazlaBayt;
        if (kirpildi)
        {
            akis.Seek(-EnFazlaBayt, SeekOrigin.End);
        }

        using var okuyucu = new StreamReader(akis);
        var icerik = await okuyucu.ReadToEndAsync(cancellationToken);

        return new ScheduledTaskOutputDto
        {
            Name = name,
            Available = true,
            Truncated = kirpildi,
            WrittenAtUtc = File.GetLastWriteTimeUtc(dosya),
            Content = icerik.TrimEnd()
        };
    }

    public async Task<ScheduledTaskStatusDto> GetTaskStatusAsync(string name, CancellationToken cancellationToken = default)
    {
        name = EnsureUsableName(name);

        // "Calisiyor mu" durum METNINDEN degil, Task Scheduler'in kendi durum sabitinden okunur:
        // metin sunucunun diline gore degisir ve 'Running' kelimesini aramak Turkce bir Windows'ta
        // sessizce yanlis cevap verir. State=4 (TASK_STATE_RUNNING) her dilde aynidir.
        var script = $@"$gorev = Get-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(name.Trim())}' -ErrorAction SilentlyContinue
if (-not $gorev) {{ '{{}}'; exit 0 }}
$info = Get-ScheduledTaskInfo -TaskName $gorev.TaskName -TaskPath $gorev.TaskPath -ErrorAction SilentlyContinue
[pscustomobject]@{{
  Name = $gorev.TaskName
  State = $gorev.State.ToString()
  IsRunning = ([int]$gorev.State -eq 4)
  LastTaskResult = if ($info) {{ [long]$info.LastTaskResult }} else {{ $null }}
  LastRunTimeUtc = if ($info -and $info.LastRunTime -and ([datetime]$info.LastRunTime).Year -gt 2000) {{ ([datetime]$info.LastRunTime).ToUniversalTime().ToString('o') }} else {{ $null }}
}} | ConvertTo-Json -Depth 3";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.read_failed", 500, 500, result.StandardError);
        }

        var durum = JsonSerializer.Deserialize<ScheduledTaskStatusDto>(
            result.StandardOutput.Trim(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (durum is null || string.IsNullOrWhiteSpace(durum.Name))
        {
            throw new AppException("cron.not_found", 404, 404);
        }

        return durum;
    }

    public async Task<TaskEnvironmentDto> GetEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunPowerShellAsync(RuntimeLocator.DiscoveryScript, cancellationToken);
            var (yorumlayicilar, pathte) = RuntimeLocator.ParseDiscovery(result.StandardOutput);

            return new TaskEnvironmentDto
            {
                PythonInterpreters = yorumlayicilar,
                PythonOnPath = pathte
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Olcemedik. BOS bir sonuc "Python yok" DEMEK DEGILDIR; DTO'da ikisi de bos kalir ve
            // CanRepairPython false doner -- yani panel var olmayan bir sorunu onarmayi onermez.
            logger.LogWarning(exception, "Gorev ortami olculemedi.");
            return new TaskEnvironmentDto();
        }
    }

    public async Task<int> AddPythonToMachinePathAsync(CancellationToken cancellationToken = default)
    {
        var ortam = await GetEnvironmentAsync(cancellationToken);
        if (ortam.PythonInterpreters.Count == 0)
        {
            throw new AppException("cron.python_not_found", 404, 404);
        }

        // EN YENI surum secilir (liste zaten sirali). Eski bir yorumlayiciyi PATH'in basina koymak,
        // kullanicinin yeni kurdugu surumu golgeleyerek yeni bir sorun uretirdi.
        var script = RuntimeLocator.BuildAddToMachinePathScript(ortam.PythonInterpreters[0]);
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.path_update_failed", 500, 500, result.StandardError);
        }

        var eslesme = Regex.Match(result.StandardOutput ?? string.Empty, @"ADAPANEL-DEGISIKLIK=(\d+)");
        var eklenen = eslesme.Success ? int.Parse(eslesme.Groups[1].Value) : 0;
        logger.LogInformation("Python makine PATH'ine eklendi: {Path} ({Count} klasor).", ortam.PythonInterpreters[0], eklenen);
        return eklenen;
    }

    /// <summary>
    /// Süren çalışmayı durdurur — SÜREÇ AĞACIYLA BİRLİKTE.
    /// <para>
    /// <c>Stop-ScheduledTask</c> tek başına YETMİYOR. Kullanıcının canlı sunucusunda Chrome/Selenium
    /// açan bir .bat görevi vardı: Zamanlayıcı yalnızca kendi başlattığı süreci sonlandırıyor, o
    /// sürecin doğurduğu altı kademelik zincir (cmd → python → chromedriver → chrome → ...) hayatta
    /// kalıyordu. Panel "durduruldu" diyor, iş sürüyordu.
    /// </para>
    /// <para>
    /// İkinci vaka: görev sonradan yeniden kaydedilince Zamanlayıcı çalışan turun izini kaybediyor.
    /// Süreç yaşamaya devam ediyor ama <c>Stop-ScheduledTask</c> onu artık tanımıyor. Bu ÖKSÜZ süreç
    /// yalnızca tek bir işaretten bulunabilir: panelin sardığı komut satırında görevin kendi günlük
    /// dosyasının yolu geçer, ve o yol göreve özeldir.
    /// </para>
    /// <para>
    /// Sonuç ÖLÇÜLÜR: durdurma sonrası görev hâlâ "Running" ise arayüz bunu söyler. "Durduruldu"
    /// demek, gerçekten durduğunu görmeden verilebilecek bir söz değildir.
    /// </para>
    /// </summary>
    public async Task<StopScheduledTaskResultDto> StopTaskAsync(string name, CancellationToken cancellationToken = default)
    {
        var temizAd = EnsureUsableName(name);
        var gunlukYolu = Path.Combine(TaskLogDirectory, ResolveLogFileName(temizAd));

        var result = await ProcessRunner.RunPowerShellAsync(BuildStopScript(temizAd, gunlukYolu), cancellationToken);
        var cikti = result.StandardOutput ?? string.Empty;
        var oldurulenEslesme = Regex.Match(cikti, @"ADAPANEL-OLDURULEN=(\d+)");
        var calisiyorEslesme = Regex.Match(cikti, @"ADAPANEL-CALISIYOR=(\d+)");

        // Betik hiç bir işaret bırakmadıysa ölçüm yapılmamıştır; "durduruldu" demek yalan olur.
        if (!oldurulenEslesme.Success || !calisiyorEslesme.Success)
        {
            throw new AppException("cron.stop_failed", 500, 500, result.StandardError);
        }

        var durum = new StopScheduledTaskResultDto
        {
            KilledProcesses = int.Parse(oldurulenEslesme.Groups[1].Value, CultureInfo.InvariantCulture),
            StillRunning = calisiyorEslesme.Groups[1].Value == "1"
        };

        logger.LogInformation(
            "Gorev calismasi durduruldu ({TaskName}): {Killed} surec agaci sonlandirildi, hala calisiyor = {StillRunning}.",
            temizAd,
            durum.KilledProcesses,
            durum.StillRunning);

        return durum;
    }

    /// <summary>
    /// Durdurma betiği. PID'ler ÖNCE toplanır: <c>Stop-ScheduledTask</c>'tan sonra görev, çalışanlar
    /// listesinden düşer ve hayatta kalan alt süreçleri o göreve bağlayacak hiçbir iz kalmaz.
    /// </summary>
    internal static string BuildStopScript(string taskName, string logPath) => $@"$hedefYol = '{TaskPath}{EscapeSingleQuoted(taskName)}'
$gunluk = '{EscapeSingleQuoted(logPath)}'
$pidler = New-Object System.Collections.ArrayList

# 1) Zamanlayicinin BILDIGI calisan tur. EnginePID, Windows 8'den beri gorevin eylemini calistiran
#    surecin kendisidir (eski taskeng.exe araciligi kalkti).
try {{
  $servis = New-Object -ComObject Schedule.Service
  $servis.Connect()
  foreach ($calisan in $servis.GetRunningTasks(1)) {{
    if ($calisan.Path -eq $hedefYol -and $calisan.EnginePID) {{ [void]$pidler.Add([int]$calisan.EnginePID) }}
  }}
}} catch {{ }}

# 2) Zamanlayicinin IZINI KAYBETTIGI oksuz surecler: panelin sarmaladigi komut satirinda bu gorevin
#    gunluk dosyasinin TAM yolu gecer. Eslesme tam metin uzerinden yapilir (joker yok).
try {{
  foreach ($surec in (Get-CimInstance Win32_Process -Filter ""Name='cmd.exe'"" -ErrorAction SilentlyContinue)) {{
    if ($surec.CommandLine -and $surec.CommandLine.Contains($gunluk)) {{ [void]$pidler.Add([int]$surec.ProcessId) }}
  }}
}} catch {{ }}

# SIRA ONEMLI: agac ONCE oldurulur, Stop-ScheduledTask SONRA cagrilir.
#
# BU MAKINEDE OLCULDU: Stop-ScheduledTask once cagrilirsa ust surec aninda olur, Get-Process artik
# onu bulamaz ve oldurme dongusu HICBIR SEY YAPMAZ (olculen cikti: OLDURULEN=0). Zamanlayici
# gorevi bir is nesnesi (job object) icinde calistirdigi icin normal cocuklar yine de olur --
# ama is nesnesinden KOPAN surecler (Chrome/chromedriver tam olarak bunu yapar) hayatta kalir ve
# artik onlari o goreve baglayan hicbir iz kalmamistir. Kullanicinin canli sunucusunda yasanan
# durum buydu. Once oldurmek, agaci HENUZ BUTUNKEN yakalar.
$oldurulen = 0
foreach ($surecId in ($pidler | Select-Object -Unique)) {{
  # Sistem sureclerine ASLA dokunma: yanlis bir PID ile /T /F sunucuyu dusurur.
  if ($surecId -le 4) {{ continue }}
  $surec = Get-Process -Id $surecId -ErrorAction SilentlyContinue
  if (-not $surec) {{ continue }}
  # taskhostw PAYLASILAN bir konaktir: COM isleyicili gorevlerde EnginePID ayni taskhostw'u
  # gosterir ve orada AYNI ANDA baska Windows gorevleri de barinir (bu makinede olculdu: tek bir
  # taskhostw altinda iki ayri sistem gorevi). Onu /T ile oldurmek ilgisiz gorevleri de dusururdu.
  # Panelin kendi gorevleri her zaman dogrudan calistirilabilir bir eylemdir, taskhostw'da
  # barinmazlar; yani bu atlama hicbir mesru durdurmayi engellemez.
  if ('svchost','services','lsass','wininit','csrss','smss','System','taskhostw','taskeng' -contains $surec.ProcessName) {{ continue }}
  & taskkill.exe /PID $surecId /T /F 2>&1 | Out-Null
  $oldurulen++
}}

# Agac oldukten SONRA Zamanlayici durumu temizlenir: gorev hala calisiyor isaretli kalabilir.
try {{ Stop-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(taskName)}' -ErrorAction Stop | Out-Null }} catch {{ }}

Start-Sleep -Milliseconds 500
$gorev = Get-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(taskName)}' -ErrorAction SilentlyContinue
$halaCalisiyor = if ($gorev -and $gorev.State -eq 'Running') {{ 1 }} else {{ 0 }}
[Console]::Out.WriteLine('ADAPANEL-OLDURULEN=' + $oldurulen)
[Console]::Out.WriteLine('ADAPANEL-CALISIYOR=' + $halaCalisiyor)";

    public async Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await EnsureTaskUrlTargetAllowedAsync(request, cancellationToken);

        // Betik görevi IIS'e (site kökü + havuz adı) bakmak zorunda olduğundan çözüm ASENKRON;
        // saf yol/komut mantığı ScriptTaskPlanner'da, IIS'e bağımlı kısmı burada duruyor.
        var plan = await ResolveScriptPlanAsync(request, cancellationToken);

        var logPath = await PrepareLogPathAsync(request.Name, plan?.PrincipalUserId, cancellationToken);
        var result = await ProcessRunner.RunPowerShellAsync(BuildCreateScript(request, plan, logPath), cancellationToken);
        if (!result.Success)
        {
            // Uygulama havuzu kimliğiyle kayıt başarısızsa SYSTEM'e SESSİZCE DÜŞME — bu, kullanıcının
            // seçmediği bir yetki yükseltmesi olurdu. Ayrı hata koduyla nedeni açıkça söyle.
            if (plan?.PrincipalUserId is not null)
            {
                throw new AppException("cron.limited_principal_failed", 500, 500, result.StandardError);
            }

            // Gercek PowerShell hatasini detaya koy: "kaydedilemedi" tek basina teshis edilemiyor.
            throw new AppException("cron.write_failed", 500, 500, result.StandardError);
        }

        /*
         * GERİ OKUMA VARDI, AMA SONUCU YUTULUYORDU.
         *
         * Refleks doğruydu: kayıttan sonra görev listesi yeniden okunuyor. Sorun, BULUNAMADIĞINDA
         * ne yapıldığıydı — panel elindeki istek nesnesinden bir DTO uydurup <c>State = "Ready"</c>
         * yazıyordu. Yani ölçüm yapılıyor, ölçümün cevabı beğenilmeyince ATILIYORDU.
         *
         * NEDEN GERÇEK BİR RİSK: `Register-ScheduledTask` her başarısızlıkta çıkış kodunu
         * düşürmüyor — bu deponun kendi ölçümü (`LimitedIdentityRunner`) erişim reddinde bile
         * akışın sürdüğünü yazıyor. O durumda yukarıdaki `result.Success` denetimi geçiyor, geri
         * okuma görevi bulamıyor ve kullanıcı listede "Hazır" görüyor. Görev Zamanlayıcı'da ise
         * hiçbir şey yok: yedek alınmıyor, cron çalışmıyor, kimse haberdar olmuyor.
         *
         * "Kaydedilemedi" DEMİYORUZ, çünkü bilmiyoruz — komut başarılı dönmüştü. Söylenen şey
         * ölçülen şeydir: kayıt DOĞRULANAMADI. Ayrı anahtar, ayrı sebep; kullanıcı bunu Görev
         * Zamanlayıcı'da arayabilir.
         */
        var tasks = await GetTasksAsync(cancellationToken);
        var kaydedilen = tasks.FirstOrDefault(task =>
            string.Equals(task.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase));

        if (kaydedilen is null)
        {
            throw new AppException(
                "cron.write_unverified",
                500,
                500,
                $"'{request.Name.Trim()}' kaydi basarili dondu ama gorev listesinde bulunamadi.");
        }

        return kaydedilen;
    }

    public async Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default)
    {
        var script = $@"Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(EnsureUsableName(name))}' -Confirm:$false -ErrorAction Stop";
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.write_failed", 500, 500, result.StandardError);
        }
    }

    public async Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        var verb = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        var script = $@"{verb} -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(EnsureUsableName(name))}' -ErrorAction Stop | Out-Null";
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.write_failed", 500, 500, result.StandardError);
        }
    }

    internal static string BuildCreateScript(CreateScheduledTaskRequest request, ScriptPlan? plan, string? logPath = null)
    {
        var scheduleType = request.ScheduleType.Trim().ToLowerInvariant();
        var interval = Math.Max(1, request.Interval);
        var (execute, arguments, workingDirectory) = plan is null
            ? ResolveAction(request)
            : (plan.Execute, plan.Arguments, plan.WorkingDirectory);

        // Cikti yakalama: komut cmd.exe'ye sarilir ve ciktisi gunluk dosyasina yonlendirilir.
        // Cikis kodu korunur (cmd /c calistirdigi komutun kodunu dondurur), bu yuzden "son sonuc"
        // sutunu ayni anlami tasimaya devam eder.
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            arguments = BuildLoggedArguments(execute, arguments, logPath);
            execute = "cmd.exe";
        }

        // Tek tırnak + '' kaçışı: URL/komut içindeki $, ", & karakterleri PowerShell tarafından yorumlanmasın.
        var action = $"New-ScheduledTaskAction -Execute '{EscapeSingleQuoted(execute)}'";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            action += $" -Argument '{EscapeSingleQuoted(arguments)}'";
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            action += $" -WorkingDirectory '{EscapeSingleQuoted(workingDirectory)}'";
        }

        // DIKKAT: [TimeSpan]::MaxValue KULLANMA — Task Scheduler XML dogrulamasi "P99999999DT23H59M59S"
        // suresini REDDEDIYOR ("aralik disi deger") ve kayit patliyor. 3650 gun (10 yil) = fiilen suresiz, her surumde gecerli.
        var trigger = scheduleType switch
        {
            "minute" or "minutely" => $"New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes {interval}) -RepetitionDuration (New-TimeSpan -Days 3650)",
            "daily" => $"New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact('{NormalizeTime(request.TimeOfDay)}','HH:mm',[System.Globalization.CultureInfo]::InvariantCulture))",
            _ => $"New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Hours {interval}) -RepetitionDuration (New-TimeSpan -Days 3650)"
        };

        // Principal'i ACIKCA ver: panel servisi LocalSystem'dir ve principal'siz "-RunLevel Highest"
        // SYSTEM baglaminda hesap eslemesinde patlayabiliyor. Ayni desen self-update gorevinde
        // (WindowsUpdateInstallerLauncher) bu sunucuda defalarca KANITLANDI - birebir onu kullaniyoruz.
        //
        // Sinirli kimlik seciliyse LOCAL SERVICE kullanilir: ServiceAccount + RunLevel Limited. Bu
        // birlesim bu makinede CANLI dogrulandi (gorev sonucu 0x0, betik icinden okunan kimlik
        // "NT AUTHORITY\Local Service"). Uygulama havuzu sanal hesabi neden KULLANILAMIYOR ve neden
        // schtasks'in "SUCCESS" yaniti guvenilmez: bkz. ScriptTaskPlanner.LimitedUserId.
        var principal = plan?.PrincipalUserId is { Length: > 0 } limitedUserId
            ? $"New-ScheduledTaskPrincipal -UserId '{EscapeSingleQuoted(limitedUserId)}' -LogonType ServiceAccount -RunLevel Limited"
            : "New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest";

        // AYARLARI ACIKCA VER. -Settings atlanirsa Windows'un DIZUSTU BILGISAYAR icin secilmis
        // varsayilanlari uygulanir ve bir sunucu zamanlayicisi icin uctan uca yanlistir. Olculdu
        // (New-ScheduledTaskSettingsSet, varsayilan hali):
        //
        //   DisallowStartIfOnBatteries = True   -> makine batarya bildiriyorsa gorev REDDEDILIR
        //   StopIfGoingOnBatteries     = True   -> calisirken batarya gorulurse ORTASINDA kesilir
        //   StartWhenAvailable         = False  -> sunucu kapali/yeniden baslarken kacan tur TELAFI EDILMEZ
        //   ExecutionTimeLimit         = PT72H  -> takilan bir tur slotu UC GUN tutar
        //
        // Ilk ikisi kullanicinin gordugu 0x800710E0'i (ERROR_OPERATOR_OR_ADMINISTRATOR_HAS_REFUSED_
        // THE_REQUEST) dogrudan uretir. Sanallastirilmis sunucular (Hyper-V, cesitli VPS'ler) sik sik
        // bir batarya/UPS aygiti bildirir; panel bunu hic denetlemiyordu.
        //
        // MultipleInstances = IgnoreNew BILEREK korunur: bir onceki tur hala suruyorken ikincisini
        // baslatmak, yavas bir gorevde surekli buyuyen bir surec yigini olusturur.
        //
        // SURE SINIRI ARTIK KULLANICININ: 0.2.66'da sabit 4 saat koymustum ve bu, uzun suren
        // MESRU bir gorevi (buyuk bir veri cekme, tam yedek) her seferinde ORTASINDA olduruyordu --
        // kullanicinin gordugu tek sey "sure sinirina takildi" oluyordu. Sinir koymamanin riski
        // takilan bir calismanin sonraki turlari engellemesi; ama panel artik takilan calismayi
        // GORUP tek dugmeyle durdurabiliyor, yani o risk gorunur ve geri alinabilir. Yarida
        // kesilen bir is ise geri alinamaz. Bu yuzden varsayilan SINIRSIZ.
        //
        // Windows'ta sinirsiz PT0S ile ifade edilir (olculdu).
        var sureSiniri = Math.Max(0, request.TimeLimitHours) is var saat && saat > 0
            ? $"(New-TimeSpan -Hours {saat})"
            : "([TimeSpan]::Zero)";

        var settings =
            "New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            $"-StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit {sureSiniri}";

        return $@"$ErrorActionPreference = 'Stop'
if (Get-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(request.Name.Trim())}' -ErrorAction SilentlyContinue) {{
  Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(request.Name.Trim())}' -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}}
$action = {action}
$trigger = {trigger}
$principal = {principal}
$settings = {settings}
Register-ScheduledTask -TaskPath '{TaskPath}' -TaskName '{EscapeSingleQuoted(request.Name.Trim())}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null";
    }

    /// <summary>
    /// Yola yapışan GÖRÜNMEZ karakterleri temizler.
    /// <para>
    /// CANLI OLAY: kullanıcının yazdığı yol ekranda kusursuz görünüyordu ama panel "böyle bir
    /// klasör yok" diyordu. Sebep, yolun başındaki U+202A (LEFT-TO-RIGHT EMBEDDING) karakteriydi:
    /// Windows Gezgini'nin "Yol olarak kopyala" komutu bunu kendiliğinden ekler. Karakterin hiçbir
    /// görüntüsü yoktur, dolayısıyla kullanıcı ne yazdığına baksa da farkı GÖREMEZ; panel ise
    /// "‪C:\..." diye bir yol arar ve bulamaz.
    /// </para>
    /// <para>
    /// Bu, teşhis edilmesi en zor hata türüdür: her iki taraf da kendince haklıdır. Kullanıcıyı
    /// görünmez bir karakteri kovalamaya bırakmak yerine panel sessizce temizler — çünkü bu
    /// karakterlerin bir dosya yolunda hiçbir meşru işlevi yoktur.
    /// </para>
    /// </summary>
    internal static string StripInvisibleCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Karakterler KOD NOKTASIYLA yazılır, harfiyle değil: kaynak dosyada görünmez bir karakter
        // tutmak, ilerideki bir kopyala-yapıştırda ya da kodlama dönüşümünde sessizce kaybolur ve
        // düzeltme kimse fark etmeden ortadan kalkardı. Ayrıca okuyan kişi ne filtrelendiğini
        // ancak böyle görebilir.
        return new string(value.Where(karakter => (int)karakter switch
        {
            0x200B => false, // ZERO WIDTH SPACE
            0x200E => false, // LEFT-TO-RIGHT MARK
            0x200F => false, // RIGHT-TO-LEFT MARK
            0x2060 => false, // WORD JOINER
            0xFEFF => false, // BYTE ORDER MARK
            >= 0x202A and <= 0x202E => false, // yön gömme/geçersiz kılma — "Yol olarak kopyala" bunu ekler
            >= 0x2066 and <= 0x2069 => false, // yön yalıtma
            _ => true
        }).ToArray()).Trim();
    }

    /// <summary>
    /// İstekteki bütün yol/adres alanlarını görünmez karakterlerden arındırır.
    /// Doğrulamadan ÖNCE çalışır ki denetimler kullanıcının gerçekte kastettiği değeri görsün.
    /// </summary>
    private static void SanitizeRequest(CreateScheduledTaskRequest request)
    {
        request.Command = StripInvisibleCharacters(request.Command);
        request.Arguments = StripInvisibleCharacters(request.Arguments);
        request.WorkingDirectory = StripInvisibleCharacters(request.WorkingDirectory);
        request.Url = StripInvisibleCharacters(request.Url);

        // KULLANILMAYAN alanlar temizlenir.
        //
        // Çalışma klasörü alanı yalnızca "komut" ve "betik" tiplerinde EKRANDA GÖRÜNÜR. Kullanıcı
        // bir .bat görevini düzenlerken tipi PHP'ye çevirdiğinde alan gizlenir ama formdaki eski
        // değeri taşımaya devam eder; panel de görünmeyen bir alan yüzünden kaydı reddeder.
        // Kullanıcının göremediği bir değere göre hata vermek, çözülemez bir hatadır.
        var taskType = (request.TaskType ?? "command").Trim().ToLowerInvariant();
        if (taskType is "url" or "php")
        {
            request.WorkingDirectory = string.Empty;
        }
    }

    /// <summary>Testlerin temizleme kurallarini dogrudan olcebilmesi icin.</summary>
    internal static void SanitizeForTests(CreateScheduledTaskRequest request) => SanitizeRequest(request);

    /// <summary>
    /// Testlerin GERÇEKTEN kurulan komutu ölçebilmesi için (<see cref="ResolveAction"/>).
    /// </summary>
    /// <remarks>
    /// Gerekli: "curl'e hangi bayraklar veriliyor" bir GÜVENLİK kararıdır ve kaynakta metin
    /// aramakla doğrulanamaz — kararın yazıldığını değil, komutun o hâlde KURULDUĞUNU ölçmek
    /// gerekir. Yeni bir HTTP yüzeyi açmaz; <c>internal</c>'dır.
    /// </remarks>
    internal static (string Execute, string Arguments, string WorkingDirectory) ResolveActionForTests(
        CreateScheduledTaskRequest request) => ResolveAction(request);

    /// <summary>
    /// URL görevinin adresi panelin ULAŞMAMASI gereken bir yere mi bakıyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yüzey neden var:</b> <see cref="ResolveAction"/> bu adresten
    /// <c>curl.exe -fsSL "&lt;adres&gt;"</c> kurar ve görev SYSTEM olarak koşar.
    /// <see cref="IsAcceptableTaskUrl"/> yalnız ŞEMAYA bakar — <c>http://169.254.169.254/…</c>
    /// onun için kusursuz bir adrestir.
    /// </para>
    /// <para>
    /// <b>Neden burada, <c>CronController</c>'da değil:</b> orada da bir denetim var
    /// (<c>EnsureCustomerUrlAllowedAsync</c>) ama YALNIZCA müşteri/bayi dalında çalışır; yönetici
    /// dalı ve servisi çağıran her yol (otomasyon, WHMCS tümleşimi, ileride eklenecek uçlar)
    /// denetimsizdi. Panelin "Administrator" rolü bir WINDOWS yöneticisi DEĞİLDİR — aynı gerekçe
    /// <c>UpdatePackageTrust</c>'ta yazılı — yani "admin zaten güvenilir" demek burada geçerli bir
    /// savunma değil. Denetim, komutu GERÇEKTEN kuran katmana kondu.
    /// </para>
    /// <para>
    /// <b>DÜRÜST SINIR — İKİ tanesi:</b> (1) <c>curl</c>'e <c>-L</c> veriliyor, yani YÖNLENDİRME
    /// İZLENİYOR; dış bir adres <c>302</c> ile iç ağa atlarsa bu kapı görmez. <c>-L</c>
    /// kaldırılmadı çünkü meşru cron adreslerinin çoğu (http→https, www yönlendirmesi) ona
    /// dayanır ve kaldırmak çalışan görevleri sessizce kırardı. (2) Denetim görev KURULURKEN
    /// yapılır, curl ise HER ÇALIŞMADA adı yeniden çözer; arada değişen bir DNS kaydı (rebinding)
    /// bu kapının arkasından geçer. İkisi de <c>--resolve</c> ya da .NET'e taşıma gerektirir;
    /// ayrı iş olarak raporlandı.
    /// </para>
    /// </remarks>
    private async Task EnsureTaskUrlTargetAllowedAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals((request.TaskType ?? "command").Trim(), "url", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Uri.TryCreate((request.Url ?? string.Empty).Trim(), UriKind.Absolute, out var adres))
        {
            // Biçim kararı ValidateRequest'in; burada sessiz kalmak onun hatasını ezmemek içindir.
            return;
        }

        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            adres.DnsSafeHost,
            outboundResolver ?? OutboundAddressGuard.SystemResolver,
            OutboundTargetPolicy.Connecting(runtimeOptions.Value.AllowInternalOutboundTargets),
            cancellationToken);

        if (engellenen is not null)
        {
            throw new AppException("cron.url_forbidden", 403, 403, engellenen);
        }
    }

    private static void ValidateRequest(CreateScheduledTaskRequest request)
    {
        SanitizeRequest(request);

        // GÖREV ADI BİR GÜVENLİK SINIRIDIR, bir etiket değil.
        //
        // Ad, Görev Zamanlayıcı'yı süren PowerShell betiklerine gömülür ve panel servisi LocalSystem
        // olarak koşar. Kaçış (tek tırnaklı dize) artık doğru, ama TEK savunma olarak bırakılmıyor:
        // ileride biri yine çift tırnaklı bir dize yazarsa beyaz liste o hatayı da tutar.
        //
        // Doğrulama İKİ yerde: burada ve DTO'nun RegularExpression özniteliğinde. Aynı deseni
        // kopyalamıyoruz — ikisi de TaskNameRules'tan okuyor.
        //
        // Panelin müşteri görevlerine eklediği "u<guid>--" öneki AYRILARAK ölçülür: önek desene
        // uyar (u + onaltılık + tire) ama 35 karakter tutar; ayrılmasaydı kullanıcının 60 karakterlik
        // hakkı sessizce 25'e düşerdi.
        if (!TaskNameRules.IsCreatable(request.Name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        var taskType = (request.TaskType ?? "command").Trim().ToLowerInvariant();
        switch (taskType)
        {
            case "url":
                if (!IsAcceptableTaskUrl(request.Url, out var urlHataKodu))
                {
                    throw new AppException(urlHataKodu, 400, 400);
                }
                break;
            case "script":
                // Betik yolu boşsa "invalid_name" yanıltıcı olur; kullanıcı yol alanını göremez.
                if (string.IsNullOrWhiteSpace(request.Command))
                {
                    throw new AppException("cron.invalid_script", 400, 400);
                }
                break;
            case "php":
            default:
                if (string.IsNullOrWhiteSpace(request.Command))
                {
                    throw new AppException("cron.invalid_name", 400, 400);
                }

                if (IsBareInteractiveShell(request.Command, request.Arguments))
                {
                    throw new AppException("cron.bare_shell", 400, 400);
                }

                // Kullanicinin ELLE yazdigi calisma klasoru yoksa SESSIZCE atilmaz, soylenir:
                // turetilmis bir klasoru dusurmek yardimdir, kullanicinin yazdigini yok saymak
                // ise onu neyin bozuldugunu bilmeden birakmak olurdu.
                if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) &&
                    !Directory.Exists(request.WorkingDirectory.Trim()))
                {
                    throw new AppException("cron.working_directory_missing", 400, 400);
                }
                break;
        }
    }

    /// <summary>
    /// Tekrar aralığını (ISO 8601 süresi) DAKİKAYA çevirir. Çözülemezse null.
    /// <para>
    /// KANITLANMIŞ HATA: panel yalnızca <c>PT&lt;n&gt;M</c> ve <c>PT&lt;n&gt;H</c> biçimlerini
    /// tanıyordu. Oysa Windows aynı süreyi kendi normalleştirdiği biçimde saklıyor — ölçüldü:
    /// </para>
    /// <list type="bullet">
    /// <item>60 dakika → <c>PT1H</c></item>
    /// <item>90 dakika → <c>PT1H30M</c></item>
    /// <item>24 saat → <c>P1D</c></item>
    /// </list>
    /// <para>
    /// Tanınmayan biçim "saatlik, 1" varsayılanına düşüyordu. Sonuç sessiz ve ağır: 24 saatte bir
    /// çalışan bir görevi düzenleyip kaydeden kullanıcı, hiçbir uyarı almadan onu SAATTE BİR
    /// çalışır hâle getiriyordu — yani görev günde 24 kat daha sık çalışmaya başlıyordu.
    /// </para>
    /// </summary>
    internal static int? ParseRepetitionMinutes(string? isoDuration)
    {
        var deger = (isoDuration ?? string.Empty).Trim();
        var eslesme = Regex.Match(
            deger,
            @"^P(?:(?<gun>\d+)D)?(?:T(?:(?<saat>\d+)H)?(?:(?<dakika>\d+)M)?(?:(?<saniye>\d+)S)?)?$",
            RegexOptions.IgnoreCase);

        if (!eslesme.Success || deger.Length < 2)
        {
            return null;
        }

        var toplam =
            (eslesme.Groups["gun"].Success ? int.Parse(eslesme.Groups["gun"].Value) * 1440 : 0) +
            (eslesme.Groups["saat"].Success ? int.Parse(eslesme.Groups["saat"].Value) * 60 : 0) +
            (eslesme.Groups["dakika"].Success ? int.Parse(eslesme.Groups["dakika"].Value) : 0);

        return toplam > 0 ? toplam : null;
    }

    /// <summary>
    /// Dakika cinsinden aralığı, panelin formundaki (tip, sayı) ikilisine çevirir.
    /// <para>
    /// Tam saat katları SAAT olarak gösterilir: kullanıcı "1440 dakika" değil "24 saat" okumak
    /// ister ve ikisi aynı zamanlamadır — kaydedildiğinde birebir aynı aralığı üretir.
    /// </para>
    /// </summary>
    internal static (string ScheduleType, int Interval) DescribeRepetition(int minutes) =>
        minutes >= 60 && minutes % 60 == 0
            ? ("hourly", minutes / 60)
            : ("minute", minutes);

    /// <summary>
    /// URL görevinin adresi kabul edilebilir mi; değilse HANGİ nedenle olmadığı.
    /// <para>
    /// SIRA ÖNEMLİ ve bunu bir kez yanlış yaptım: önce "dosya yolu mu" diye bakıyordum, oysa her
    /// URL eğik çizgi içerir ve eğik çizgi Windows'ta da bir dizin ayırıcısıdır. Sonuç: geçerli
    /// <c>https://…</c> adresleri de "bu bir dosya yolu" diye reddedildi — yani yardım etmek için
    /// eklediğim mesaj, çalışan özelliği bozdu. Doğrusu ÖNCE gerçek ölçüm (bu, http/https bir
    /// adres mi), yalnızca değilse tahmin (dosya yoluna mı benziyor).
    /// </para>
    /// </summary>
    internal static bool IsAcceptableTaskUrl(string? url, out string errorCode)
    {
        var deger = (url ?? string.Empty).Trim();

        if (Uri.TryCreate(deger, UriKind.Absolute, out var adres) &&
            (adres.Scheme == Uri.UriSchemeHttp || adres.Scheme == Uri.UriSchemeHttps))
        {
            errorCode = string.Empty;
            return true;
        }

        // Adres değil. Kullanıcı buraya sunucudaki bir dosyanın yolunu yazmış olabilir; o durumda
        // "geçersiz adres" demek yardımcı olmuyor — dosya orada, yol da doğru, eksik olan tek şey
        // dosyanın yanlış TİPTE aranması. Doğru seçeneğe yönlendiriyoruz.
        errorCode = LooksLikeWindowsPath(deger) ? "cron.url_is_a_file_path" : "cron.invalid_url";
        return false;
    }

    /// <summary>
    /// Metin bir Windows dosya yoluna mı benziyor: "C:\…" ya da "\\sunucu\paylasim".
    /// <para>
    /// Yalnızca eğik çizgi aramak YETMEZ; her URL eğik çizgi içerir. Ayırt edici olan sürücü
    /// harfi ya da UNC önekidir.
    /// </para>
    /// </summary>
    internal static bool LooksLikeWindowsPath(string value) =>
        Regex.IsMatch(value, @"^[a-zA-Z]:[\\/]") || value.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// Komut, argümansız bir kabuk mu (cmd.exe / powershell.exe)?
    /// <para>
    /// Böyle bir görev HİÇBİR ZAMAN BİTMEZ: argümansız cmd.exe etkileşimli bir kabuk açar ve girdi
    /// bekler; görevin altında konsol olmadığı için sonsuza kadar bekler. Çalışma süresi sınırı
    /// dolana dek gelen her tetikleme "önceki tur sürüyor" diye reddedilir (0x800710E0) ve görev
    /// fiilen ölür.
    /// </para>
    /// <para>
    /// Neden GERÇEKTEN oluyor, teorik değil: betik görevleri Windows'a <c>cmd.exe /c "...bat"</c>
    /// olarak kaydedilir ve panel bir süre bunu geri çözmüyordu (0.2.70'te düzeltildi) — düzenleme
    /// ekranı komut alanına <c>cmd.exe</c> yazıyordu. Kullanıcı hiçbir şeyi bilerek değiştirmeden
    /// kaydettiğinde görev, betiği yerine argümansız bir kabuk çalıştıran bir göreve dönüşüyordu.
    /// Sebebi ortadan kaldırdık; bu denetim aynı sonucu başka bir yoldan üretmeyi de engeller.
    /// </para>
    /// </summary>
    internal static bool IsBareInteractiveShell(string? command, string? arguments)
    {
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        var dosyaAdi = Path.GetFileName((command ?? string.Empty).Trim().Trim('"'));
        return string.Equals(dosyaAdi, "cmd.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(dosyaAdi, "cmd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(dosyaAdi, "powershell.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(dosyaAdi, "powershell", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(dosyaAdi, "pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "script" görevini çalıştırılabilir bir plana çevirir; başka tiplerde null döner.
    /// Sıra bilinçli: önce ucuz ve kesin denetimler (uzantı, varlık), sonra IIS'e gidenler.
    /// </summary>
    private async Task<ScriptPlan?> ResolveScriptPlanAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals((request.TaskType ?? string.Empty).Trim(), "script", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var scriptPath = ScriptTaskPlanner.NormalizePath(request.Command);
        if (scriptPath is null || !ScriptTaskPlanner.IsAllowedExtension(scriptPath))
        {
            throw new AppException("cron.invalid_script", 400, 400);
        }

        if (!File.Exists(scriptPath))
        {
            throw new AppException("cron.script_not_found", 404, 404);
        }

        // Çalışma dizini verilmemişse betiğin bulunduğu klasör: .bat dosyaları neredeyse her zaman
        // kendi klasörlerine göreli yol kullanır, aksi hâlde görev "dosya bulunamadı" ile düşer.
        var workingDirectory = ScriptTaskPlanner.NormalizePath(request.WorkingDirectory)
                               ?? Path.GetDirectoryName(scriptPath)
                               ?? string.Empty;

        var siteName = (request.SiteName ?? string.Empty).Trim();
        // "apppool" eski istemcilerden gelebilir; artık sınırlı yetki anlamına gelir (bkz.
        // ScriptTaskPlanner.LimitedUserId — havuz kimliği Task Scheduler'da hiç çalışmıyordu).
        var runAs = (request.RunAs ?? string.Empty).Trim();
        var wantsLimited = string.Equals(runAs, "limited", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(runAs, "apppool", StringComparison.OrdinalIgnoreCase);
        string? principalUserId = null;

        if (siteName.Length > 0)
        {
            var site = await siteService.GetSiteAsync(siteName, cancellationToken);
            var siteRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
            if (string.IsNullOrWhiteSpace(siteRoot))
            {
                throw new AppException("cron.site_root_unknown", 409, 409);
            }

            // Hem betik hem çalışma dizini site kökünün İÇİNDE kalmalı. Çalışma dizinini de denetliyoruz
            // çünkü "cd C:\Windows\System32" ile başlayan bir görev, göreli yollarla site dışına taşar.
            // Betik için kökün KENDİSİ geçersiz (klasör çalıştırılamaz), çalışma dizini için geçerli:
            // site kökünde duran bir .bat'ın doğal çalışma dizini kökün ta kendisidir.
            if (!ScriptTaskPlanner.IsInsideRoot(siteRoot, scriptPath) ||
                !ScriptTaskPlanner.IsRootOrInsideRoot(siteRoot, workingDirectory))
            {
                throw new AppException("cron.script_outside_site", 403, 403);
            }

            if (wantsLimited)
            {
                // LOCAL SERVICE'in site klasörünü okuyup çalıştırabilmesi gerekir; erişim verilmezse
                // görev kaydolur ama her çalışmada sessizce düşer. Bu yüzden erişim BURADA verilir ve
                // verilemezse görev hiç oluşturulmaz — çalışmayacağı baştan belli bir görev bırakmak,
                // "SUCCESS" deyip bozuk görev kaydeden schtasks davranışının aynısı olurdu.
                await GrantLimitedIdentityAccessAsync(siteRoot, cancellationToken);
                principalUserId = ScriptTaskPlanner.LimitedUserId;
            }
        }
        else if (wantsLimited)
        {
            // Sınırlı kimlik bir SİTE kökü içindir; site seçilmeden istenirse sessizce SYSTEM'e düşmek
            // kullanıcının seçmediği bir yetki verirdi — hata dön.
            throw new AppException("cron.limited_requires_site", 400, 400);
        }

        var (execute, arguments) = ScriptTaskPlanner.BuildAction(scriptPath, request.Arguments);
        return new ScriptPlan(execute, arguments, workingDirectory, principalUserId);
    }

    /// <summary>
    /// Sınırlı kimliğe site kökü üzerinde okuma/çalıştırma ve yazma hakkı verir.
    /// <para>
    /// Neden Modify: site betikleri hemen her zaman kendi klasörlerine günlük/çıktı yazar; yalnızca
    /// okuma verilirse görev "çalıştı" görünür ama betik ilk yazma denemesinde düşer. Verilen hak
    /// SİTE KÖKÜYLE sınırlıdır ve alternatifin (SYSTEM) yanında hâlâ çok dardır.
    /// </para>
    /// </summary>
    private static async Task GrantLimitedIdentityAccessAsync(string siteRoot, CancellationToken cancellationToken)
    {
        var script = $@"$ErrorActionPreference = 'Stop'
& icacls.exe '{EscapeSingleQuoted(siteRoot)}' /grant '{EscapeSingleQuoted(ScriptTaskPlanner.LimitedUserId)}:(OI)(CI)M' /T | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw ""icacls cikis kodu: $LASTEXITCODE"" }}";

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.limited_grant_failed", 500, 500, result.StandardError);
        }
    }

    // Görev tipini yürütülebilir eyleme çevirir (Plesk görev tipleri):
    //  url     -> curl.exe -fsS ... "<adres>" (Windows Server 2019+/Win10 ile birlikte gelir)
    //  php     -> <runtimes\php\...\php.exe> "<betik>" <argümanlar>
    //  command -> kullanıcı komutu aynen
    // ("script" tipi buraya UĞRAMAZ; onu ResolveScriptPlanAsync çözer — IIS'e bakması gerekir.)
    private static (string Execute, string Arguments, string WorkingDirectory) ResolveAction(CreateScheduledTaskRequest request)
    {
        var taskType = (request.TaskType ?? "command").Trim().ToLowerInvariant();
        var workingDirectory = request.WorkingDirectory?.Trim() ?? string.Empty;

        switch (taskType)
        {
            case "url":
                // -f: HTTP hata kodunda başarısız say (Task Scheduler "son sonuç"a yansır), -sS: sessiz ama hatayı yaz,
                // -L: yönlendirmeyi izle, 5 dk üst sınır + 2 tekrar: geçici ağ sorunlarında görev boşa düşmesin.
                //
                // --proto / --proto-redir / --max-redirs: ÖLÇÜLEREK eklendi, bezeme değil.
                // Bu makinede curl 8.19.0 ile ölçüldü (sahte yerel sunucu, 302 döndürüldü):
                //   * VARSAYILAN hâlde curl yönlendirmeyi http/https'in YANINDA ftp/ftps'e de izler:
                //     "Location: ftp://127.0.0.1/x" verildiğinde curl bu makinedeki FTP servisine
                //     GERÇEKTEN bağlandı (curl: (67) Access denied: 530 — yani bağlantı kuruldu,
                //     yalnız kimlik reddedildi). Görev SYSTEM olarak koştuğu için bu, müşterinin
                //     verdiği bir adresin panelin iç servislerine soket açtırması demekti.
                //     "=http,https" ile aynı yönlendirme "Protocol \"ftp\" disabled (in redirect)"
                //     ile REDDEDİLDİ.
                //   * file/gopher/smb/dict yönlendirmeleri bu curl sürümünde ZATEN kapalı çıktı
                //     (üçü de ayrıca ölçüldü); bayrak onları da açıkça kapatır, yani ileride
                //     varsayılanı değişen bir curl sürümü sessizce yeni bir kapı açamaz.
                //   * --max-redirs 5: .NET tarafındaki denetlenen istemciyle AYNI tavan
                //     (OutboundConnectGuard.CreateHandler). curl varsayılanı 50'dir ve bir
                //     yönlendirme halkası görevi 5 dakikalık üst sınıra kadar döndürürdü.
                //
                // -L NEDEN ATILMADI (ölçüldü): -L olmadan curl 302'yi BAŞARI sayıp gövdeyi hiç
                // istemiyor — çıkış kodu 0, çıktı BOŞ. Yani "http→https" ya da "www" yönlendirmesi
                // olan her meşru cron adresi görev listesinde "başarılı" görünürken hiçbir şey
                // çalıştırmayacaktı. Sessizce çalışmayan bir cron, hata veren bir cron'dan pahalıdır.
                //
                // KALAN AÇIK — DÜRÜSTÇE: adres denetimi (OutboundAddressGuard) görev KURULURKEN
                // yapılır; curl'ün çalışma anında http/https yönlendirmesiyle 127.0.0.1'e ya da
                // 169.254.169.254'e sapmasını hiçbir curl bayrağı engelleyemez (curl'de adres
                // engel listesi YOKTUR — ölçüldü/arandı). Bunu kapatmanın tek yolu görevi curl
                // yerine panelin DENETLENEN istemcisiyle çalıştırmaktır (yeni bir CLI fiili);
                // o iş, Görev Zamanlayıcı üzerinde SYSTEM olarak sınanabildiği yerde AYRI olarak
                // yapılmalı — burada sınanamayan bir değişikliği her müşterinin cron yoluna
                // sokmak, kapattığından fazlasını kırardı.
                return (
                    "curl.exe",
                    $"-fsSL --proto \"=http,https\" --proto-redir \"=http,https\" --max-redirs 5 --max-time 300 --retry 2 \"{request.Url.Trim()}\"",
                    workingDirectory);
            case "php":
            {
                var scriptPath = request.Command.Trim();
                var arguments = $"\"{scriptPath}\"";
                if (!string.IsNullOrWhiteSpace(request.Arguments))
                {
                    arguments += " " + request.Arguments.Trim();
                }

                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    workingDirectory = Path.GetDirectoryName(scriptPath) ?? string.Empty;
                }

                return (ResolvePhpExecutable(request.PhpVersion), arguments, workingDirectory);
            }
            default:
            {
                var command = request.Command.Trim();

                // Calisma klasoru verilmemisse DOSYANIN KENDI KLASORU kullanilir -- "php" tipinde
                // zaten boyle yapiliyordu, "command" tipinde unutulmustu.
                //
                // Neden onemli: gorev SYSTEM olarak calisir ve SYSTEM'in calisma klasoru
                // C:\Windows\system32'dir. Bir .bat/.cmd dosyasi neredeyse her zaman kendi
                // klasorunde oldugunu varsayar (yanindaki betigi cagirir, ciktisini yanina yazar).
                // Klasor verilmezse bu varsayimlarin hepsi bozulur; gorev "calisti" gorunur ama
                // hicbir sey yapmaz ya da anlasilmaz bir cikis koduyla duser. Kullanicinin cift
                // tiklayarak elde ettigi davranis budur; zamanlanmis hâli de ayni olmali.
                if (string.IsNullOrWhiteSpace(workingDirectory) && LooksLikeFilePath(command))
                {
                    workingDirectory = Path.GetDirectoryName(command) ?? string.Empty;
                }

                // VAR OLMAYAN bir klasor ASLA yazilmaz.
                //
                // Task Scheduler, calisma klasoru gecersizse gorevi HIC BASLATMAZ ve 0x8007010B
                // ("Dizin adi gecersiz") dondurur -- komut dogru olsa bile. Canlida boyle bir gorev
                // uc saniyede bu kodla dustu ve hicbir cikti uretmedi, cunku surec hic olusmadi.
                // Turetilmis bir klasor ugruna gorevi calisamaz hale getirmek, kolaylik adina asil
                // isi bozmak olurdu; boyle bir durumda klasoru hic vermeyip Windows'un varsayilanina
                // birakmak her zaman daha iyidir.
                if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
                {
                    workingDirectory = string.Empty;
                }

                return (command, request.Arguments?.Trim() ?? string.Empty, workingDirectory);
            }
        }
    }

    /// <summary>
    /// Komut, bir klasoru olan gercek bir dosya yolu mu? "python", "ipconfig" gibi PATH uzerinden
    /// cozulen komutlarda klasor turetmek yanlis olur -- bu yuzden ayri ayri bakilir.
    /// </summary>
    internal static bool LooksLikeFilePath(string command) =>
        !string.IsNullOrWhiteSpace(command) &&
        command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 &&
        !string.IsNullOrWhiteSpace(Path.GetDirectoryName(command));

    /// <summary>
    /// Kayıtlı php.exe yolundan sürümü çözer (ör. ...\runtimes\php\8.3\php.exe → "8.3").
    /// <para>
    /// Düzenleme için gerekli: sürüm geri okunmazsa, kullanıcı yalnızca betiği değiştirdiğinde
    /// görev sessizce "kurulu en yeni PHP"ye geçerdi. 7.4 isteyen eski bir uygulama 8.3'e taşınır
    /// ve bir daha çalışmazdı.
    /// </para>
    /// </summary>
    internal static string ResolvePhpVersionFromPath(string phpExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(phpExecutablePath))
        {
            return string.Empty;
        }

        // Yoldaki SON sürüm benzeri parça alınır: kök klasörde de sayı geçebilir.
        var eslesmeler = Regex.Matches(phpExecutablePath, @"(?<![\d.])(\d+\.\d+)(?![\d.])");
        return eslesmeler.Count > 0 ? eslesmeler[^1].Groups[1].Value : string.Empty;
    }

    private static string ResolvePhpExecutable(string? version)
    {
        // SUNUCUNUN TAMAMINA bakilir, yalnizca panelin kendi klasorune degil.
        //
        // Onceden yalnizca %ProgramData%\AdaPanel\runtimes\php taraniyordu. Sunucuda PHP zaten
        // kuruluysa (IIS ile gelen, elle kurulan ya da baska bir panelden kalan) gorev "PHP kurulu
        // degil" diye REDDEDILIYORDU -- kullanici sitesinin PHP ile calistigini gozuyle gorurken.
        // Python'daki hatanin aynisi: panel kendi bildigi tek yeri gercegin tamami saniyordu.
        var candidates = RuntimeLocator.FindPhpExecutables();

        if (candidates.Count == 0)
        {
            throw new AppException("cron.php_not_installed", 404, 404);
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            var match = candidates.FirstOrDefault(path => path.Contains(version.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return candidates[0];
    }

    private static string NormalizeTime(string timeOfDay)
    {
        return DateTime.TryParseExact(timeOfDay?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "00:00";
    }

    /// <summary>
    /// PowerShell TEK TIRNAKLI dizesi için kaçış: tek tırnak ikilenir.
    /// <para>
    /// Bu dosyada başka bir kaçış YOKTUR ve olmamalı. Eskiden bir de <c>EscapePowerShell</c> vardı
    /// (yalnızca <c>"</c> karakterini kaçırıyordu) ve çıktısı ÇİFT TIRNAKLI dizelerin içine
    /// giriyordu. PowerShell'de çift tırnaklı dize <c>$(...)</c> alt-ifadesini ÇALIŞTIRIR; panel
    /// servisi de LocalSystem olarak koştuğu için görev adına <c>$(...)</c> yazan bir hosting
    /// müşterisi sunucuda SYSTEM olarak kod çalıştırabiliyordu. Tek tırnaklı dizede <c>$</c>
    /// genişlemesi ve alt-ifade YOKTUR; tek gereken kaçış budur.
    /// </para>
    /// <para>
    /// Kaçış tek başına savunma sayılmıyor: adın kendisi ayrıca <see cref="TaskNameRules"/> beyaz
    /// listesinden geçer (bkz. <see cref="EnsureUsableName"/> ve <see cref="ValidateRequest"/>).
    /// </para>
    /// </summary>
    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    /// <summary>
    /// Var olan bir göreve dokunmadan önce adın kabuk için zararsız olduğunu doğrular.
    /// <para>
    /// Bütün işlem uçları (çalıştır, durdur, sil, etkinleştir, durum, çıktı) buradan geçer —
    /// tek tek yazılsaydı biri unutulurdu ve bu dosyada tam olarak o olmuştu.
    /// </para>
    /// </summary>
    private static string EnsureUsableName(string? name)
    {
        var temiz = (name ?? string.Empty).Trim();
        if (!TaskNameRules.IsUsable(temiz))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        return temiz;
    }

    private static T[] DeserializeArray<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.Deserialize<T[]>() ?? [],
            JsonValueKind.Object => [document.RootElement.Deserialize<T>()!],
            _ => []
        };
    }
}
