using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Nginx;

internal static partial class NginxInstallationSupport
{
    internal const string StartupTaskName = "AdaPanel Nginx";
    internal const int DefaultPort = 8088;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static bool HasConfigurationToPreserve(string installDirectory)
    {
        if (File.Exists(Path.Combine(installDirectory, "conf", "nginx.conf"))) return true;
        if (File.Exists(Path.Combine(installDirectory, "conf", "adapanel.json")))
            throw new AppException("nginx.config_invalid", 400, 400, "The existing Nginx configuration is missing; its metadata was preserved.");
        return false;
    }

    internal static Task<NginxConfigurationRequest> PrepareAsync(
        string installDirectory,
        CancellationToken cancellationToken,
        bool preserveExistingConfiguration = false)
        => PrepareAsync(installDirectory, preserveExistingConfiguration, () => new NginxConfigurationRequest
        {
            ListenAddress = "127.0.0.1",
            ListenPort = FindAvailablePort(DefaultPort),
            ServerName = "_",
            UpstreamUrl = "http://127.0.0.1:80",
            CacheEnabled = false,
            CacheSizeMb = 256
        }, (path, token) => TestAsync(FindExecutable(installDirectory), installDirectory, path, token),
            token => RegisterStartupTaskAsync(installDirectory, token),
            token => StartAsync(installDirectory, token), cancellationToken);

    internal static async Task<NginxConfigurationRequest> PrepareAsync(
        string installDirectory,
        bool preserveExistingConfiguration,
        Func<NginxConfigurationRequest> createConfiguration,
        Func<string, CancellationToken, Task<CommandResult>> testConfiguration,
        Func<CancellationToken, Task> registerStartup,
        Func<CancellationToken, Task> start,
        CancellationToken cancellationToken)
    {
        NginxConfigurationRequest configuration;
        if (preserveExistingConfiguration)
        {
            var configPath = Path.Combine("conf", "nginx.conf");
            if (!File.Exists(Path.Combine(installDirectory, configPath)))
                throw new AppException("nginx.config_invalid", 400, 400, "The preserved Nginx configuration is missing.");
            configuration = await ReadConfigurationAsync(installDirectory, cancellationToken);
            EnsureRuntimeDirectories(installDirectory);
            var result = await testConfiguration(configPath, cancellationToken);
            if (!result.Success) throw new AppException("nginx.config_invalid", 400, 400, FormatResult(result));
            await registerStartup(cancellationToken);
            await start(cancellationToken);
        }
        else
        {
            try
            {
                configuration = createConfiguration();
                await WriteConfigurationAsync(installDirectory, configuration, testConfiguration, async token =>
                {
                    await registerStartup(token);
                    await start(token);
                }, cancellationToken);
            }
            catch (Exception failure)
            {
                try { DiscardUninitializedConfiguration(installDirectory); }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Nginx first-install configuration cleanup failed.", failure, cleanupFailure);
                }
                throw;
            }
        }
        return configuration;
    }

    // Only call when no configuration existed before extraction. A packaged default
    // must not make a failed first install look like a user-configured installation
    // on retry. Successfully generated configuration remains recoverable with its metadata.
    internal static void DiscardUninitializedConfiguration(string installDirectory)
    {
        var root = Path.GetFullPath(installDirectory);
        var configPath = Path.Combine(root, "conf", "nginx.conf");
        if (File.Exists(Path.Combine(root, "conf", "adapanel.json")) || !File.Exists(configPath)) return;
        SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(root)!, configPath);
        File.Delete(configPath);
    }

    internal static Task ConfigureAsync(string installDirectory, NginxConfigurationRequest configuration, CancellationToken cancellationToken)
        => WriteConfigurationAsync(installDirectory, configuration,
            (path, token) => TestAsync(FindExecutable(installDirectory), installDirectory, path, token), async token =>
            {
                await RegisterStartupTaskAsync(installDirectory, token);
                await ReloadAsync(installDirectory, token);
            }, cancellationToken);

    internal static Task WriteConfigurationAsync(
        string installDirectory,
        NginxConfigurationRequest configuration,
        CancellationToken cancellationToken)
        => WriteConfigurationAsync(installDirectory, configuration,
            (configPath, token) => TestAsync(FindExecutable(installDirectory), installDirectory, configPath, token),
            cancellationToken);

    internal static Task WriteConfigurationAsync(
        string installDirectory,
        NginxConfigurationRequest configuration,
        Func<string, CancellationToken, Task<CommandResult>> testConfiguration,
        CancellationToken cancellationToken)
        => WriteConfigurationAsync(installDirectory, configuration, testConfiguration, _ => Task.CompletedTask, cancellationToken);

    internal static async Task WriteConfigurationAsync(
        string installDirectory,
        NginxConfigurationRequest configuration,
        Func<string, CancellationToken, Task<CommandResult>> testConfiguration,
        Func<CancellationToken, Task> activate,
        CancellationToken cancellationToken)
    {
        Validate(configuration);
        var configDirectory = Path.Combine(installDirectory, "conf");
        var configPath = Path.Combine(configDirectory, "nginx.conf");
        var statePath = Path.Combine(configDirectory, "adapanel.json");
        var operation = Guid.NewGuid().ToString("N");
        var pendingConfigPath = configPath + "." + operation + ".pending";
        var pendingStatePath = statePath + "." + operation + ".pending";
        var backupConfigPath = configPath + "." + operation + ".backup";
        var backupStatePath = statePath + "." + operation + ".backup";

        EnsureRuntimeDirectories(installDirectory);

        var hadConfiguration = File.Exists(configPath);
        var hadState = File.Exists(statePath);
        var configPublished = false;
        var statePublished = false;
        var completed = false;
        try
        {
            // Her iki dosya da hazırlanıp doğrulanmadan canlı yapılandırma değişmez.
            await File.WriteAllTextAsync(pendingConfigPath, BuildConfiguration(configuration), cancellationToken);
            await File.WriteAllTextAsync(pendingStatePath, JsonSerializer.Serialize(configuration, JsonOptions), cancellationToken);
            var testResult = await testConfiguration(Path.Combine("conf", Path.GetFileName(pendingConfigPath)), cancellationToken);
            if (!testResult.Success)
            {
                throw new AppException("nginx.config_invalid", 400, 400, FormatResult(testResult));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (hadConfiguration)
                File.Replace(pendingConfigPath, configPath, backupConfigPath);
            else
                File.Move(pendingConfigPath, configPath);
            configPublished = true;

            if (hadState) File.Replace(pendingStatePath, statePath, backupStatePath);
            else File.Move(pendingStatePath, statePath);
            statePublished = true;

            // Keep both originals until startup/reload completes. This restores disk
            // state on failure; it does not claim to undo a delivered runtime signal.
            await activate(cancellationToken);
            completed = true;
        }
        catch (Exception failure)
        {
            // Attempt each restore independently and without the cancelled request token.
            // A failed restore keeps its backup and must not hide the activation error.
            var errors = new List<Exception>();
            RestorePublishedFile(configPublished, hadConfiguration, configPath, backupConfigPath, errors);
            RestorePublishedFile(statePublished, hadState, statePath, backupStatePath, errors);
            if (errors.Count > 0)
                throw new AggregateException("Nginx configuration rollback failed; recovery copies were retained.", new[] { failure }.Concat(errors));
            throw;
        }
        finally
        {
            DeletePreparedFile(pendingConfigPath);
            DeletePreparedFile(pendingStatePath);
            // Geri alma da başarısızsa kurtarma kopyasını silme.
            if (completed)
            {
                DeletePreparedFile(backupConfigPath);
                DeletePreparedFile(backupStatePath);
            }
        }
    }

    private static void EnsureRuntimeDirectories(string installDirectory)
    {
        Directory.CreateDirectory(Path.Combine(installDirectory, "conf"));
        Directory.CreateDirectory(Path.Combine(installDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(installDirectory, "temp", "cache"));
        Directory.CreateDirectory(Path.Combine(installDirectory, "temp", "proxy_temp"));
    }

    private static void RestorePublishedFile(bool published, bool existed, string target, string backup, List<Exception> errors)
    {
        if (!published) return;
        try
        {
            if (existed) ReplacePreparedFile(backup, target);
            else File.Delete(target);
        }
        catch (Exception exception)
        {
            errors.Add(new IOException($"Nginx configuration could not be restored. Recovery path: {backup}", exception));
        }
    }

    private static void ReplacePreparedFile(string preparedPath, string targetPath)
    {
        if (File.Exists(targetPath)) File.Replace(preparedPath, targetPath, null);
        else File.Move(preparedPath, targetPath);
    }

    private static void DeletePreparedFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static async Task<NginxConfigurationRequest> ReadConfigurationAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(installDirectory, "conf", "adapanel.json");
        if (!File.Exists(statePath))
        {
            return new NginxConfigurationRequest();
        }

        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<NginxConfigurationRequest>(
            stream,
            JsonOptions,
            cancellationToken) ?? new NginxConfigurationRequest();
    }

    internal static async Task<CommandResult> TestCurrentConfigurationAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        return await TestAsync(
            FindExecutable(installDirectory),
            installDirectory,
            Path.Combine("conf", "nginx.conf"),
            cancellationToken);
    }

    internal static async Task StartAsync(string installDirectory, CancellationToken cancellationToken)
    {
        if (IsRunning(installDirectory))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = FindExecutable(installDirectory),
                Arguments = $"-p \"{EnsureTrailingSeparator(installDirectory)}\" -c conf/nginx.conf",
                WorkingDirectory = installDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
            {
                throw new AppException("nginx.start_failed", 500, 500);
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AppException("nginx.start_failed", 500, 500, exception.ToString());
        }

        await Task.Delay(500, cancellationToken);
        if (!IsRunning(installDirectory))
        {
            throw new AppException("nginx.start_failed", 500, 500, ReadErrorLog(installDirectory));
        }
    }

    internal static async Task StopAsync(string installDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(installDirectory) || !File.Exists(Path.Combine(installDirectory, "conf", "nginx.conf")))
        {
            if (IsRunning(installDirectory))
                throw new AppException("nginx.stop_failed", 500, 500, "Nginx is still running, but its configuration file is missing.");
            return;
        }

        if (IsRunning(installDirectory))
        {
            var result = await SignalAsync(installDirectory, "quit", cancellationToken);
            if (!result.Success)
            {
                throw new AppException("nginx.stop_failed", 500, 500, FormatResult(result));
            }
        }

        await WaitForStopAsync(() => IsRunning(installDirectory), () => Task.Delay(250, cancellationToken));
    }

    internal static async Task WaitForStopAsync(Func<bool> isRunning, Func<Task> wait)
    {
        for (var attempt = 0; attempt < 20 && isRunning(); attempt++)
        {
            await wait();
        }
        if (isRunning())
            throw new AppException("nginx.stop_failed", 500, 500, "Nginx is still running after the shutdown grace period.");
    }

    internal static async Task ReloadAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var testResult = await TestCurrentConfigurationAsync(installDirectory, cancellationToken);
        if (!testResult.Success)
        {
            throw new AppException("nginx.config_invalid", 400, 400, FormatResult(testResult));
        }

        if (!IsRunning(installDirectory))
        {
            await StartAsync(installDirectory, cancellationToken);
            return;
        }

        var result = await SignalAsync(installDirectory, "reload", cancellationToken);
        if (!result.Success)
        {
            throw new AppException("nginx.reload_failed", 500, 500, FormatResult(result));
        }
    }

    internal static Task RegisterStartupTaskAsync(
        string installDirectory,
        CancellationToken cancellationToken)
        => RegisterStartupTaskAsync(installDirectory, ProcessRunner.RunPowerShellAsync, cancellationToken);

    internal static async Task RegisterStartupTaskAsync(
        string installDirectory,
        Func<string, CancellationToken, Task<CommandResult>> runPowerShell,
        CancellationToken cancellationToken)
    {
        var executablePath = FindExecutable(installDirectory);
        var arguments = $"-p \"{EnsureTrailingSeparator(installDirectory)}\" -c conf/nginx.conf";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $action = New-ScheduledTaskAction -Execute '{{EscapePowerShell(executablePath)}}' -Argument '{{EscapePowerShell(arguments)}}'
            $trigger = New-ScheduledTaskTrigger -AtStartup
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
            Register-ScheduledTask -TaskName '{{StartupTaskName}}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
            """;

        var result = await runPowerShell(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("nginx.startup_task_failed", 500, 500, FormatResult(result));
        }
    }

    internal static async Task RemoveAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var durdurmaHatasi = string.Empty;
        try
        {
            await StopAsync(installDirectory, cancellationToken);
        }
        catch (Exception exception)
        {
            durdurmaHatasi = exception.Message;
        }

        // ÖNCE SÜREÇ, SONRA GÖREV. Eskiden süreç öldürme YALNIZCA StopAsync istisna atarsa
        // deneniyordu; oysa "nginx -s quit" hata vermeden dönüp süreci ayakta bırakabilir
        // (bozuk conf, kapanmayan işçi, PID dosyası eski). O yolda kaldırma "başarılı" görünür,
        // dizin kilitli kalır ve bir sonraki kurulum "dosya kullanımda" ile patlar. Bu yüzden
        // sonuç HER DURUMDA ölçülür.
        var kalanSurec = await DrainManagedProcessesAsync(installDirectory, cancellationToken);

        var result = await ProcessRunner.RunPowerShellAsync(
            $"Unregister-ScheduledTask -TaskName '{StartupTaskName}' -Confirm:$false -ErrorAction SilentlyContinue",
            cancellationToken);
        if (!result.Success)
        {
            throw new AppException("nginx.remove_failed", 500, 500, FormatResult(result));
        }

        if (kalanSurec > 0)
        {
            // Kullanıcıya YALAN söylemektense gerçeği söyle: dizin hâlâ kilitli, silme/yeniden
            // kurma bu yüzden başarısız olacak.
            throw new AppException(
                "nginx.remove_failed",
                500,
                500,
                string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        $"{kalanSurec} adet nginx.exe hala '{installDirectory}' dizininden calisiyor; dizin kilitli kaldi.",
                        durdurmaHatasi
                    }.Where(value => !string.IsNullOrWhiteSpace(value))));
        }
    }

    internal static bool IsRunning(string installDirectory)
    {
        var pidPath = Path.Combine(installDirectory, "logs", "nginx.pid");
        if (!File.Exists(pidPath) ||
            !int.TryParse(File.ReadAllText(pidPath).Trim(), out var processId))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited &&
                   string.Equals(process.ProcessName, "nginx", StringComparison.OrdinalIgnoreCase) &&
                   ProcessRunner.IsUnderDirectory(process.MainModule?.FileName, installDirectory);
        }
        catch
        {
            return false;
        }
    }

    internal static async Task<bool> IsStartupTaskRegisteredAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            $"if (Get-ScheduledTask -TaskName '{StartupTaskName}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}",
            cancellationToken);
        return result.Success && bool.TryParse(result.StandardOutput, out var registered) && registered;
    }

    internal static string FindExecutable(string installDirectory)
    {
        var executablePath = Directory.Exists(installDirectory)
            ? Directory.EnumerateFiles(installDirectory, "nginx.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new AppException("nginx.executable_not_found", 404, 404);
        }

        return executablePath;
    }

    internal static string BuildConfiguration(NginxConfigurationRequest configuration)
    {
        Validate(configuration);
        var listenAddress = FormatListenAddress(configuration.ListenAddress);
        var cachePath = configuration.CacheEnabled
            ? $"    proxy_cache_path temp/cache levels=1:2 keys_zone=adapanel_cache:16m max_size={configuration.CacheSizeMb}m inactive=60m use_temp_path=off;{Environment.NewLine}"
            : string.Empty;
        var cacheDirectives = configuration.CacheEnabled
            ? """
                    proxy_cache adapanel_cache;
                    proxy_cache_methods GET HEAD;
                    proxy_cache_valid 200 10m;
                    proxy_cache_bypass $http_upgrade;
                    proxy_no_cache $http_upgrade;
                    add_header X-AdaPanel-Cache $upstream_cache_status always;
              """
            : string.Empty;

        return $$"""
            worker_processes 1;
            error_log logs/error.log warn;
            pid logs/nginx.pid;

            events {
                worker_connections 1024;
            }

            http {
                include mime.types;
                default_type application/octet-stream;
                access_log logs/access.log;
                sendfile on;
                keepalive_timeout 65;
                server_tokens off;
                gzip on;
                proxy_temp_path temp/proxy_temp;
            {{cachePath}}    map $http_upgrade $connection_upgrade {
                    default upgrade;
                    '' close;
                }

                server {
                    listen {{listenAddress}}:{{configuration.ListenPort}};
                    server_name {{configuration.ServerName}};

                    location / {
                        proxy_http_version 1.1;
                        proxy_set_header Host $host;
                        proxy_set_header X-Real-IP $remote_addr;
                        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                        proxy_set_header X-Forwarded-Proto $scheme;
                        proxy_set_header Upgrade $http_upgrade;
                        proxy_set_header Connection $connection_upgrade;
                        proxy_pass {{FormatUpstreamUrl(configuration.UpstreamUrl)}};
            {{cacheDirectives}}        }
                }
            }
            """;
    }

    internal static void Validate(NginxConfigurationRequest configuration)
    {
        if (!IPAddress.TryParse(configuration.ListenAddress, out _))
        {
            throw new AppException("nginx.listen_address_invalid", 400, 400);
        }

        if (configuration.ListenPort is < 1 or > 65535)
        {
            throw new AppException("nginx.port_invalid", 400, 400);
        }

        if (!ServerNamePattern().IsMatch(configuration.ServerName))
        {
            throw new AppException("nginx.server_name_invalid", 400, 400);
        }

        if (string.IsNullOrWhiteSpace(configuration.UpstreamUrl) ||
            configuration.UpstreamUrl.Any(char.IsControl) ||
            !Uri.TryCreate(configuration.UpstreamUrl, UriKind.Absolute, out var upstream) ||
            (upstream.Scheme != Uri.UriSchemeHttp && upstream.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(upstream.UserInfo) ||
            !string.IsNullOrEmpty(upstream.Fragment))
        {
            throw new AppException("nginx.upstream_invalid", 400, 400);
        }

        if (configuration.CacheSizeMb is < 16 or > 8192)
        {
            throw new AppException("nginx.cache_size_invalid", 400, 400);
        }
    }

    private static async Task<CommandResult> TestAsync(
        string executablePath,
        string installDirectory,
        string configPath,
        CancellationToken cancellationToken)
    {
        return await ProcessRunner.RunAsync(
            executablePath,
            $"-t -p \"{EnsureTrailingSeparator(installDirectory)}\" -c \"{configPath.Replace('\\', '/')}\"",
            installDirectory,
            cancellationToken);
    }

    private static Task<CommandResult> SignalAsync(
        string installDirectory,
        string signal,
        CancellationToken cancellationToken)
    {
        return ProcessRunner.RunAsync(
            FindExecutable(installDirectory),
            $"-p \"{EnsureTrailingSeparator(installDirectory)}\" -c conf/nginx.conf -s {signal}",
            installDirectory,
            cancellationToken);
    }

    private static int FindAvailablePort(int preferredPort)
    {
        for (var port = preferredPort; port <= preferredPort + 50; port++)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new AppException("nginx.port_unavailable", 409, 409);
    }

    private static string FormatListenAddress(string address)
        => address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address;

    private static string FormatUpstreamUrl(string value)
    {
        var input = value.Trim();
        var upstream = new Uri(input, UriKind.Absolute);
        var escaped = upstream.AbsoluteUri;

        // URI'siz proxy_pass, gelen isteğin özgün URI'sini korur. Uri sınıfının boş
        // yola eklediği '/' bu davranışı değiştirmesin; açıkça girilmiş yollar ve
        // sorguların son eğik çizgileri ise yönlendirme için anlamlıdır.
        var authorityStart = input.IndexOf("://", StringComparison.Ordinal) + 3;
        if (input.IndexOfAny(['/', '\\', '?'], authorityStart) < 0)
            escaped = escaped.TrimEnd('/');

        // Nginx URL'deki '$' karakterlerini tırnak içinde de değişken sayar. Burada
        // kullanıcı bir literal URL girer; dolar URI kodlamasıyla literal kalır.
        // Tek bir çift tırnaklı argüman, URL içindeki ';' gibi ayırıcıların yeni
        // nginx yönergelerine dönüşmesini de engeller.
        return "\"" + escaped.Replace("$", "%24", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string EnsureTrailingSeparator(string path)
    {
        // nginx -p prefix'i argüman olarak tırnak içinde verilir. Windows yolu ters eğik
        // çizgiyle bittiğinde ('...\nginx\') kapanış tırnağı \" olarak KAÇIŞ alır ve nginx
        // prefix'i yanlış (sonraki tırnağa kadar her şeyi yutarak) okur -> "syntax hatalı".
        // Forward-slash + sonda '/' kullanmak hem bu kaçışı önler hem de nginx tarafından
        // Windows'ta sorunsuz kabul edilir.
        var fullPath = Path.GetFullPath(path).Replace('\\', '/');
        return fullPath.EndsWith('/') ? fullPath : fullPath + '/';
    }

    private static string FormatResult(CommandResult result)
        => string.Join(
            Environment.NewLine,
            new[] { result.StandardError, result.StandardOutput }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();

    private static string ReadErrorLog(string installDirectory)
    {
        var errorLog = Path.Combine(installDirectory, "logs", "error.log");
        if (!File.Exists(errorLog))
        {
            return string.Empty;
        }

        // ESKİ HÂLİ: File.ReadAllText ile TAMAMI belleğe, sonra son 4000 karakter. error.log
        // DÖNMEZ; aylarca çalışan bir sunucuda gigabaytlara çıkar. Üstelik bu çağrı
        // `throw new AppException("nginx.start_failed", …, ReadErrorLog(…))` ifadesinin İÇİNDE ve
        // etrafında try/catch yok: nginx dosyayı yazmak için açık tuttuğunda okuma IOException
        // atar ve DOSYA HATASI, "nginx başlatılamadı" hatasının YERİNE geçerdi. FileTail hem
        // seek eder hem FileShare.ReadWrite ile açar hem de kendi arızasını yutar.
        return FileTail.ReadLastBytes(errorLog, 4000);
    }

    /// <summary>
    /// Bu kurulum dizininden çalışan nginx süreçlerini öldürür.
    /// <para>
    /// ESKİ HÂLİ HİÇ ÇALIŞMIYORDU: karşılaştırmanın bir tarafı <see cref="EnsureTrailingSeparator"/>
    /// ile İLERİ eğik çizgiye çevriliyordu ("C:/.../nginx/"), diğer taraf ise
    /// <c>Path.GetFullPath</c> çıktısıydı ("C:\...\nginx\nginx.exe" — TERS eğik çizgi). Bu iki
    /// dizge asla eşleşmez; <c>Kill</c> satırına hiç girilmiyordu. Sonuç: nginx kaldırma
    /// "başarılı" raporlanıyor ama nginx.exe dizini kilitli tutmaya devam ediyordu.
    /// </para>
    /// <para>
    /// Kural artık TEK kaynakta: <see cref="ProcessRunner.KillProcessesUnder"/> karşılaştırmayı
    /// <c>Path.GetFullPath</c> üzerinden (iki ayırıcıyı da normalleştirerek) yapar.
    /// </para>
    /// </summary>
    private static int KillManagedProcesses(string installDirectory)
        => ProcessRunner.KillProcessesUnder(installDirectory, "nginx");

    /// <summary>
    /// Kurulum dizininden çalışan nginx kalmayana kadar bekler; gerekirse öldürür ve SONUCU ÖLÇER.
    /// Geriye HÂLÂ ayakta kalan süreç sayısını döner (0 = dizin serbest).
    /// <para>
    /// NEDEN BEKLEME: <c>nginx -s quit</c> ZARİF kapanmadır — komut hemen döner, işçi süreçler
    /// açık bağlantıları bitirene kadar yaşamaya devam eder. Hemen ölçen kod "hâlâ çalışıyor"
    /// sanıp gereksiz yere öldürür; hiç ölçmeyen kod ise kilitli dizini "temizlendi" sayar.
    /// </para>
    /// </summary>
    private static async Task<int> DrainManagedProcessesAsync(string installDirectory, CancellationToken cancellationToken)
    {
        for (var deneme = 0; deneme < 15; deneme++)
        {
            if (ProcessRunner.CountProcessesUnder(installDirectory, "nginx") == 0)
            {
                return 0;
            }

            await Task.Delay(200, cancellationToken);
        }

        KillManagedProcesses(installDirectory);

        // Öldürme de anında değildir: çekirdek tanıtıcıları kapatana kadar süreç listede kalır.
        for (var deneme = 0; deneme < 15; deneme++)
        {
            var kalan = ProcessRunner.CountProcessesUnder(installDirectory, "nginx");
            if (kalan == 0)
            {
                return 0;
            }

            await Task.Delay(200, cancellationToken);
        }

        return ProcessRunner.CountProcessesUnder(installDirectory, "nginx");
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    [GeneratedRegex(@"^(_|[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?)$")]
    private static partial Regex ServerNamePattern();
}
