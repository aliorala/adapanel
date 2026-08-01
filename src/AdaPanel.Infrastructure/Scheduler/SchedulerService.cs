using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Scheduler;

public sealed class SchedulerService(IIisSiteService siteService) : ISchedulerService
{
    private const string TaskPath = "\\AdaPanel\\";

    /// <summary>
    /// "script" görevinin çözülmüş hâli: hangi program, hangi argümanlar, hangi klasör ve hangi kimlik.
    /// <paramref name="PrincipalUserId"/> null ise görev bugünkü gibi SYSTEM olarak çalışır.
    /// </summary>
    private sealed record ScriptPlan(string Execute, string Arguments, string WorkingDirectory, string? PrincipalUserId);

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

    public async Task<ScheduledTaskDto> CreateTaskAsync(CreateScheduledTaskRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        // Betik görevi IIS'e (site kökü + havuz adı) bakmak zorunda olduğundan çözüm ASENKRON;
        // saf yol/komut mantığı ScriptTaskPlanner'da, IIS'e bağımlı kısmı burada duruyor.
        var plan = await ResolveScriptPlanAsync(request, cancellationToken);

        var result = await ProcessRunner.RunPowerShellAsync(BuildCreateScript(request, plan), cancellationToken);
        if (!result.Success)
        {
            // Uygulama havuzu kimliğiyle kayıt başarısızsa SYSTEM'e SESSİZCE DÜŞME — bu, kullanıcının
            // seçmediği bir yetki yükseltmesi olurdu. Ayrı hata koduyla nedeni açıkça söyle.
            if (plan?.PrincipalUserId is not null)
            {
                throw new AppException("cron.app_pool_principal_failed", 500, 500, result.StandardError);
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

    private static string BuildCreateScript(CreateScheduledTaskRequest request, ScriptPlan? plan)
    {
        var scheduleType = request.ScheduleType.Trim().ToLowerInvariant();
        var interval = Math.Max(1, request.Interval);
        var (execute, arguments, workingDirectory) = plan is null
            ? ResolveAction(request)
            : (plan.Execute, plan.Arguments, plan.WorkingDirectory);

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
        // Uygulama havuzu kimligi seciliyse: "IIS APPPOOL\<havuz>" bir SANAL hesaptir; parolasi yoktur
        // ama LSA kimlik bilgisini kendisi turetir. Bu yuzden -LogonType Password (parola PARAMETRESI
        // VERILMEDEN) kullaniliyor — gMSA/sanal hesaplar icin Microsoft'un belgeledigi kalip budur.
        // ServiceAccount yalnizca LocalSystem/LocalService/NetworkService icin gecerlidir, S4U ise
        // sanal hesaplarda "hesap adi-SID eslemesi yok" hatasi verir. RunLevel bilerek Limited:
        // havuz hesabina yonetici yukseltmesi vermek modun tum amacini bozardi.
        var principal = plan?.PrincipalUserId is { Length: > 0 } appPoolUserId
            ? $"New-ScheduledTaskPrincipal -UserId '{EscapeSingleQuoted(appPoolUserId)}' -LogonType Password -RunLevel Limited"
            : "New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest";

        return $@"$ErrorActionPreference = 'Stop'
if (Get-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -ErrorAction SilentlyContinue) {{
  Unregister-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
}}
$action = {action}
$trigger = {trigger}
$principal = {principal}
Register-ScheduledTask -TaskPath '{TaskPath}' -TaskName ""{EscapePowerShell(request.Name.Trim())}"" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null";
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
        var wantsAppPool = string.Equals((request.RunAs ?? string.Empty).Trim(), "apppool", StringComparison.OrdinalIgnoreCase);
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
            if (!ScriptTaskPlanner.IsInsideRoot(siteRoot, scriptPath) ||
                !ScriptTaskPlanner.IsInsideRoot(siteRoot, workingDirectory))
            {
                throw new AppException("cron.script_outside_site", 403, 403);
            }

            if (wantsAppPool)
            {
                if (string.IsNullOrWhiteSpace(site.ApplicationPoolName))
                {
                    throw new AppException("cron.app_pool_unknown", 409, 409);
                }

                principalUserId = ScriptTaskPlanner.BuildAppPoolUserId(site.ApplicationPoolName);
            }
        }
        else if (wantsAppPool)
        {
            // Havuz kimliği bir SİTEYE aittir; site seçilmeden istenirse sessizce SYSTEM'e düşmek
            // kullanıcının seçmediği bir yetki verirdi — hata dön.
            throw new AppException("cron.app_pool_requires_site", 400, 400);
        }

        var (execute, arguments) = ScriptTaskPlanner.BuildAction(scriptPath, request.Arguments);
        return new ScriptPlan(execute, arguments, workingDirectory, principalUserId);
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
                return (request.Command.Trim(), request.Arguments?.Trim() ?? string.Empty, workingDirectory);
        }
    }

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
