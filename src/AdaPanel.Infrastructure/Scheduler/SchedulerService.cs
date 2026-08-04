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
    ILogger<SchedulerService> logger) : ISchedulerService
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

        return $"{temiz}-{ozet}.log";
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
  if ($trigger) {{
    if ($trigger.CimClass.CimClassName -eq 'MSFT_TaskDailyTrigger') {{ $scheduleType = 'daily' }}
    elseif ($trigger.Repetition -and $trigger.Repetition.Interval -match '^PT\d+M$') {{ $scheduleType = 'minute' }}
    else {{ $scheduleType = 'hourly' }}
  }}
  [pscustomobject]@{{
    Name = $_.TaskName
    ScheduleType = $scheduleType
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
            }
            else if (ScriptTaskPlanner.IsScriptAction(task.Command, task.Arguments))
            {
                task.TaskType = "script";
            }
            else
            {
                task.TaskType = "command";
            }
        }

        return tasks;
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
  if ($s.DisallowStartIfOnBatteries -or $s.StopIfGoingOnBatteries -or -not $s.StartWhenAvailable -or $s.ExecutionTimeLimit -eq 'PT72H') {{
    $s.DisallowStartIfOnBatteries = $false
    $s.StopIfGoingOnBatteries = $false
    $s.StartWhenAvailable = $true
    if ($s.ExecutionTimeLimit -eq 'PT72H') {{ $s.ExecutionTimeLimit = 'PT4H' }}
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
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        var script = $@"Start-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(name.Trim())}"" -ErrorAction Stop";
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.run_failed", 500, 500, result.StandardError);
        }
    }

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
                var script = $@"& icacls.exe ""{EscapePowerShell(klasor)}"" /grant ""{EscapePowerShell(principalUserId)}:(OI)(CI)M"" | Out-Null";
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
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

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
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        // "Calisiyor mu" durum METNINDEN degil, Task Scheduler'in kendi durum sabitinden okunur:
        // metin sunucunun diline gore degisir ve 'Running' kelimesini aramak Turkce bir Windows'ta
        // sessizce yanlis cevap verir. State=4 (TASK_STATE_RUNNING) her dilde aynidir.
        var script = $@"$gorev = Get-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(name.Trim())}"" -ErrorAction SilentlyContinue
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

    public async Task StopTaskAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        var script = $@"Stop-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(name.Trim())}"" -ErrorAction Stop";
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.stop_failed", 500, 500, result.StandardError);
        }
    }

    public async Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

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

        var tasks = await GetTasksAsync(cancellationToken);
        return tasks.FirstOrDefault(task => string.Equals(task.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? new ScheduledTaskDto
            {
                Name = request.Name.Trim(),
                TaskType = (request.TaskType ?? "command").Trim().ToLowerInvariant(),
                Url = request.Url?.Trim() ?? string.Empty,
                ScheduleType = request.ScheduleType.Trim(),
                Command = request.Command.Trim(),
                Arguments = request.Arguments?.Trim() ?? string.Empty,
                WorkingDirectory = request.WorkingDirectory?.Trim() ?? string.Empty,
                State = "Ready"
            };
    }

    public async Task DeleteTaskAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        var script = $@"Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(name.Trim())}"" -Confirm:$false -ErrorAction Stop";
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("cron.write_failed", 500, 500, result.StandardError);
        }
    }

    public async Task SetTaskEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        var verb = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        var script = $@"{verb} -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(name.Trim())}"" -ErrorAction Stop | Out-Null";
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
        // baslatmak, yavas bir gorevde surekli buyuyen bir surec yigini olusturur. Ama bu secim ancak
        // ExecutionTimeLimit ile birlikte guvenlidir -- yoksa takilan TEK bir tur zamanlamanin
        // tamamini uc gun boyunca sessizce oldururdu. 4 saat: gercekci her panel gorevine yeter,
        // takilan bir tur ise ayni gun icinde kendiliginden temizlenir.
        const string settings =
            "New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            "-StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4)";

        return $@"$ErrorActionPreference = 'Stop'
if (Get-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -ErrorAction SilentlyContinue) {{
  Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}}
$action = {action}
$trigger = {trigger}
$principal = {principal}
$settings = {settings}
Register-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null";
    }

    private static void ValidateRequest(CreateScheduledTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new AppException("cron.invalid_name", 400, 400);
        }

        var taskType = (request.TaskType ?? "command").Trim().ToLowerInvariant();
        switch (taskType)
        {
            case "url":
                if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new AppException("cron.invalid_url", 400, 400);
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
                break;
        }
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
& icacls.exe ""{EscapePowerShell(siteRoot)}"" /grant ""{EscapePowerShell(ScriptTaskPlanner.LimitedUserId)}:(OI)(CI)M"" /T | Out-Null
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
                return ("curl.exe", $"-fsSL --max-time 300 --retry 2 \"{request.Url.Trim()}\"", workingDirectory);
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

    private static string ResolvePhpExecutable(string? version)
    {
        var phpRoot = Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\php");
        var candidates = Directory.Exists(phpRoot)
            ? Directory.EnumerateFiles(phpRoot, "php.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

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

    private static string EscapePowerShell(string value) => value.Replace("\"", "`\"");

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

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
