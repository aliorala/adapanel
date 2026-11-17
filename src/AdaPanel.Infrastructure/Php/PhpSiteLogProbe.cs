using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Php;

/// <summary>Runs through the site's real IIS handler. A CLI success or error_log(true) is insufficient.</summary>
internal sealed class PhpSiteLogProbe
{
    internal sealed record Reply(int StatusCode, string Body);
    internal Func<SiteDto, string, string, CancellationToken, Task<Reply>> Send { get; init; } = SendLocalAsync;

    internal async Task<PhpErrorLoggingDto> VerifyAsync(SiteDto site, PhpSiteLogPlan plan, CancellationToken token)
    {
        var callerToken = token;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        token = budget.Token;
        var result = Describe(plan);
        result.CheckedAtUtc = DateTime.UtcNow;
        if (plan.ConfigurationError is not null) return result;
        if (plan.LogPath is null || plan.DirectoryPath is null) return result;
        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? createdDirectory = null;
        try
        {
            PhpSiteLogConfiguration.EnsureSafePath(site.PhysicalPath);
            PhpSiteLogConfiguration.EnsureSafePath(plan.LogPath);
            var directories = new List<string> { site.PhysicalPath };
            var api = Path.Combine(site.PhysicalPath, "api");
            if (Directory.Exists(api)) directories.Add(api);
            // A newly created child tests per-directory inheritance even on a fresh site.
            createdDirectory = Path.Combine(site.PhysicalPath, "adapanel-log-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(createdDirectory);
            directories.Add(createdDirectory);
            foreach (var directory in directories)
            {
                token.ThrowIfCancellationRequested();
                PhpSiteLogConfiguration.EnsureSafePath(directory);
                var secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                var marker = "AdaPanel-PHP-log-check-" + Guid.NewGuid().ToString("N");
                var script = Path.Combine(directory, "adapanel-log-check-" + Guid.NewGuid().ToString("N") + ".php");
                var body = BuildScript(secret, marker);
                using (var stream = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(body), token);
                created.Add(script, body);
                var relative = "/" + Path.GetRelativePath(site.PhysicalPath, script).Replace('\\', '/');
                var response = await Send(site, relative, secret, token);
                if (response.StatusCode != 200)
                    return Fail(result, "unverified", $"PHP isteği {response.StatusCode} döndürdü ({DisplayDirectory(site, directory)}). Günlük yazımı doğrulanamadı.");
                using var json = JsonDocument.Parse(response.Body);
                var data = json.RootElement;
                if (data.GetProperty("marker").GetString() != marker || data.GetProperty("sapi").GetString() != "cgi-fcgi")
                    return Fail(result, "failed", "Yanıt beklenen PHP FastCGI işleminden gelmedi.");
                result.CacheTtlSeconds = Math.Clamp(data.GetProperty("cacheTtl").GetInt32(), 0, 86400);
                var effectivePath = data.GetProperty("errorLog").GetString() ?? "";
                var actualPool = data.GetProperty("pool").GetString() ?? "";
                if (!actualPool.Equals(site.ApplicationPoolName, StringComparison.OrdinalIgnoreCase))
                    return Fail(result, "unverified", "PHP isteğinin uygulama havuzu kimliği doğrulanamadı.");
                if (!SamePath(effectivePath, plan.LogPath) || !data.GetProperty("logErrors").GetBoolean() || data.GetProperty("displayErrors").GetBoolean())
                {
                    var recent = new[] { Path.Combine(site.PhysicalPath, ".user.ini"), Path.Combine(directory, ".user.ini") }
                        .Any(path => File.Exists(path) && File.GetLastWriteTimeUtc(path).AddSeconds(result.CacheTtlSeconds) > DateTime.UtcNow);
                    return Fail(result, recent ? "pending" : "failed",
                        recent ? $"{DisplayDirectory(site, directory)} etkin PHP ayarları henüz eşleşmiyor. .user.ini önbelleği en fazla {result.CacheTtlSeconds} saniye; süreden sonra yeniden doğrulayın."
                               : $"{DisplayDirectory(site, directory)} etkin error_log/log_errors/display_errors ayarları eşleşmiyor. Alt dizin ve PHP yapılandırmasını inceleyin.");
                }
                // Read the bounded tail from the exact configured file, as a separate observer.
                // The PHP return value is deliberately ignored.
                if (!await ContainsMarkerAsync(plan.LogPath, marker, token))
                    return Fail(result, "failed", $"PHP error_log çağrısına rağmen işaret doğru günlükten geri okunamadı ({DisplayDirectory(site, directory)}). İzin ve open_basedir ayarlarını kontrol edin.");
                // PHP itself must also be able to read its own log (not just the panel's SYSTEM account).
                if (!data.GetProperty("readBack").GetBoolean())
                    return Fail(result, "failed", "PHP işlemi kendi günlüğünden yazdığı işareti geri okuyamadı.");
            }

            if (PhpSiteLogHttpProtection.Inside(site.PhysicalPath, plan.LogPath))
            {
                var url = "/" + Path.GetRelativePath(site.PhysicalPath, plan.LogPath).Replace('\\', '/');
                var response = await Send(site, url, "", token);
                if (response.StatusCode is not (403 or 404))
                    return Fail(result, "failed", $"Günlük dosyasının HTTP erişim engeli doğrulanamadı (HTTP {response.StatusCode}).");
            }
            result.Status = "verified";
            result.Detail = "Gerçek IIS PHP isteğiyle kök, alt dizin ve varsa api/ yazımı; dosyadan geri okuma, etkin ayarlar, siteye özel izinler ve HTTP koruması doğrulandı.";
            if (!result.Managed) result.Detail += " Bu günlük panel tarafından arşivlenmiyor; mevcut kayıtlar korunur.";
            return result;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or ArgumentException)
        {
            return Fail(result, "unverified", "Gerçek PHP günlük kontrolü tamamlanamadı: " + ex.GetType().Name + ". Ayar kaydı çalışma kanıtı değildir.");
        }
        finally
        {
            foreach (var entry in created)
            {
                try
                {
                    PhpSiteLogConfiguration.EnsureSafePath(entry.Key);
                    if (File.Exists(entry.Key) && File.ReadAllText(entry.Key) == entry.Value) File.Delete(entry.Key);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { result.Status = "unverified"; result.Detail += " Geçici PHP kontrol dosyası temizlenemedi: " + Path.GetFileName(entry.Key); }
            }
            if (createdDirectory is not null)
            {
                try { PhpSiteLogConfiguration.EnsureSafePath(createdDirectory); Directory.Delete(createdDirectory, recursive: false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { result.Status = "unverified"; result.Detail += " Geçici kontrol klasörü temizlenemedi."; }
            }
        }
    }

    internal static PhpErrorLoggingDto Describe(PhpSiteLogPlan plan) => new()
    {
        Status = plan.ConfigurationError is null ? "unverified" : "failed",
        LogPath = plan.LogPath ?? "", Managed = PhpSiteLogRotation.HasTrustedMarker(plan),
        Detail = plan.ConfigurationError ?? plan.Warning ?? "Yapılandırma okundu; gerçek PHP yazımı henüz doğrulanmadı."
    };
    private static PhpErrorLoggingDto Fail(PhpErrorLoggingDto result, string status, string detail)
    { result.Status = status; result.Detail = detail; return result; }
    private static string DisplayDirectory(SiteDto site, string path) => Path.GetFullPath(path).Equals(Path.GetFullPath(site.PhysicalPath), StringComparison.OrdinalIgnoreCase) ? "Kök dizin" : Path.GetFileName(path) == "api" ? "api/" : "Alt dizin";
    private static bool SamePath(string a, string b)
    { try { return Path.IsPathFullyQualified(a) && Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); } catch (ArgumentException) { return false; } }
    private static async Task<bool> ContainsMarkerAsync(string path, string marker, CancellationToken token)
    {
        PhpSiteLogConfiguration.EnsureSafePath(path);
        if (!File.Exists(path)) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        file.Seek(Math.Max(0, file.Length - 65536), SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(65536, file.Length)];
        var count = await file.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, token);
        return Encoding.UTF8.GetString(buffer, 0, count).Contains(marker, StringComparison.Ordinal);
    }

    internal static string BuildScript(string secret, string marker) => """
        <?php
        if (!hash_equals('__SECRET__', $_SERVER['HTTP_X_ADAPANEL_PROBE'] ?? '')) { http_response_code(404); exit; }
        header('Content-Type: application/json'); header('Cache-Control: no-store');
        $marker = '__MARKER__'; $path = ini_get('error_log');
        $on = static function($v) { return !in_array(strtolower((string)$v), ['', '0', 'off', 'false', 'none'], true); };
        $settings = ['marker'=>$marker, 'sapi'=>php_sapi_name(), 'pool'=>getenv('APP_POOL_ID') ?: '',
          'errorLog'=>$path, 'logErrors'=>$on(ini_get('log_errors')), 'displayErrors'=>$on(ini_get('display_errors')),
          'cacheTtl'=>(int)ini_get('user_ini.cache_ttl'), 'openBasedir'=>ini_get('open_basedir')];
        error_log($marker); clearstatcache(true, $path);
        $tail = ''; $f = @fopen($path, 'rb');
        if ($f) { $size = fstat($f)['size']; fseek($f, max(0, $size - 65536)); $tail = fread($f, 65536); fclose($f); }
        $settings['readBack'] = strpos($tail, $marker) !== false;
        echo json_encode($settings);
        """.Replace("__SECRET__", secret, StringComparison.Ordinal).Replace("__MARKER__", marker, StringComparison.Ordinal);

    private static async Task<Reply> SendLocalAsync(SiteDto site, string path, string secret, CancellationToken token)
    {
        // Never resolve public DNS for this probe, use a proxy, follow redirects to another host,
        // or disable TLS validation. Only locally bound IIS addresses may receive the token.
        var localIps = NetworkInterface.GetAllNetworkInterfaces().SelectMany(x => x.GetIPProperties().UnicastAddresses).Select(x => x.Address).ToHashSet();
        Exception? lastError = null;
        Reply? lastReply = null;
        foreach (var binding in site.Bindings.Where(b => b.Protocol is "http" or "https").OrderBy(b => b.Protocol == "https" ? 1 : 0).Take(16))
        {
            var ipText = binding.IpAddress;
            var address = string.IsNullOrWhiteSpace(ipText) || ipText is "*" or "0.0.0.0" or "::" ? IPAddress.Loopback : IPAddress.Parse(ipText.Trim('[', ']'));
            if (!IPAddress.IsLoopback(address) && !localIps.Contains(address)) continue;
            if (binding.Port is <= 0 or > 65535) continue;
            var host = string.IsNullOrWhiteSpace(binding.HostName) ? address.ToString() : binding.HostName;
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5), ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(new IPEndPoint(address, binding.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                } };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                var requestToken = deadline.Token;
                var uri = new UriBuilder(binding.Protocol, host, binding.Port) { Path = path }.Uri;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (secret.Length > 0) request.Headers.Add("X-AdaPanel-Probe", secret);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
                await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
                var buffer = new byte[65537];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), requestToken);
                    if (read == 0) break;
                    count += read;
                }
                if (count == buffer.Length) throw new IOException("Probe response exceeded limit.");
                lastReply = new Reply((int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, count));
                if ((int)response.StatusCode is >= 300 and <= 399) continue; // Try configured HTTPS binding, never the redirect URL.
                return lastReply;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { lastError = ex; }
        }
        if (lastReply is not null) return lastReply;
        throw new HttpRequestException("No verified local IIS binding could be reached.", lastError);
    }
}
