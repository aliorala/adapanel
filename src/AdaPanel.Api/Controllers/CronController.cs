using System.Net;
using System.Net.Sockets;
using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("tasks")]
[Route("api/v1/cron")]
public sealed class CronController(
    ISchedulerService schedulerService,
    ICustomerScopeService customerScopeService,
    IAuditService auditService) : ControllerBase
{
    private bool IsAdmin => User.IsInRole("Administrator");

    // Müşteri/bayi görevleri abonelik ad alanında yaşar (Plesk aboneliğe bağlar): "u<id>--<ad>".
    // Alt-kullanıcıda kimlik PARENT müşteriye çözülür → parent ve tüm alt-kullanıcılar aynı görev
    // listesini ve kotayı paylaşır; admin hepsini görür.
    private async Task<string> GetOwnerPrefixAsync(CancellationToken cancellationToken) =>
        $"u{await CustomerAccess.GetSubscriptionUserIdAsync(customerScopeService, User, cancellationToken):N}--";

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ScheduledTaskDto>>>> GetTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = await schedulerService.GetTasksAsync(cancellationToken);
        if (!IsAdmin)
        {
            var prefix = await GetOwnerPrefixAsync(cancellationToken);
            tasks = tasks
                .Where(task => task.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(task =>
                {
                    task.Name = task.Name[prefix.Length..];
                    return task;
                })
                .ToList();
        }

        return Ok(ApiResponse<IReadOnlyCollection<ScheduledTaskDto>>.Ok(tasks));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<ScheduledTaskDto>>> CreateTaskAsync([FromBody] CreateScheduledTaskRequest request, CancellationToken cancellationToken)
    {
        var prefix = IsAdmin ? string.Empty : await GetOwnerPrefixAsync(cancellationToken);
        if (!IsAdmin)
        {
            // Müşteri/bayi yalnızca URL görevi kurabilir (komut/PHP/betik = sunucuda kod çalıştırmak,
            // admin yetkisi). "script" tipi de bilinçli olarak BU sınırın dışında: müşteriye planlı
            // betik açmak, kimlik ne olursa olsun yeni bir saldırı yüzeyidir.
            request.TaskType = "url";
            request.Command = string.Empty;
            request.Arguments = string.Empty;
            request.WorkingDirectory = string.Empty;
            request.PhpVersion = string.Empty;
            request.SiteName = string.Empty;
            request.RunAs = "system";
            await EnsureCustomerUrlAllowedAsync(request.Url);

            // Plan kotası (MaxScheduledTasks; 0 = sınırsız): aynı adla güncelleme kotayı doldurmaz.
            // GetScopeAsync alt-kullanıcıda parent'ın kapsamına çözülür; prefix de parent'a çözüldüğünden
            // kota ABONELİK genelinde tektir (alt-kullanıcı sayısıyla çarpılamaz).
            var scope = await customerScopeService.GetScopeAsync(User.GetRequiredUserId(), cancellationToken);
            if (scope.MaxScheduledTasks > 0)
            {
                var requestedName = prefix + (request.Name ?? string.Empty).Trim();
                var ownedCount = (await schedulerService.GetTasksAsync(cancellationToken))
                    .Count(task => task.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(task.Name, requestedName, StringComparison.OrdinalIgnoreCase));
                if (ownedCount >= scope.MaxScheduledTasks)
                {
                    throw new AppException("cron.quota_exceeded", 409, 409);
                }
            }

            request.Name = prefix + (request.Name ?? string.Empty).Trim();
        }

        var task = await schedulerService.CreateTaskAsync(request, cancellationToken);
        if (!IsAdmin && task.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            task.Name = task.Name[prefix.Length..];
        }

        var target = string.IsNullOrWhiteSpace(request.Url) ? request.Command : request.Url;
        // Betik görevlerinde denetim kaydı tek başına "script" demekle yetinemez: hangi dosya, hangi mod
        // (site/sunucu) ve hangi kimlik çalıştı sorusu olay sonrası incelemenin tamamıdır.
        var scriptContext = string.Equals(request.TaskType, "script", StringComparison.OrdinalIgnoreCase)
            ? string.IsNullOrWhiteSpace(request.SiteName)
                ? " [mode=server, identity=SYSTEM]"
                : $" [mode=site:{request.SiteName}, identity={(IsLimitedRunAs(request.RunAs) ? "LOCAL SERVICE" : "SYSTEM")}]"
            : string.Empty;
        await WriteAuditAsync("cron.created", $"Scheduled task '{task.Name}' ({request.TaskType}) -> {target}{scriptContext}", cancellationToken);
        return Ok(ApiResponse<ScheduledTaskDto>.Ok(task, "cron.saved"));
    }

    // "apppool" eski istemcilerin gonderdigi degerdir ve artik sinirli kimlik anlamina gelir;
    // denetim kaydi da o gorevin GERCEKTE hangi hesapla kostugunu yazmali.
    private static bool IsLimitedRunAs(string? runAs) =>
        string.Equals(runAs, "limited", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(runAs, "apppool", StringComparison.OrdinalIgnoreCase);

    [HttpPost("{name}/run")]
    public async Task<ActionResult<ApiResponse<object>>> RunTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.RunTaskAsync(await ScopeNameAsync(name, cancellationToken), cancellationToken);
        await WriteAuditAsync("cron.run", $"Scheduled task '{name}' triggered manually.", cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.run_started"));
    }

    /// <summary>
    /// Tek görevin anlık durumu. "Şimdi çalıştır"dan sonra sayfa bunu kısa aralıklarla okuyup
    /// kullanıcıya "çalışıyor / bitti / şu sonuçla bitti" der.
    /// </summary>
    [HttpGet("{name}/status")]
    public async Task<ActionResult<ApiResponse<ScheduledTaskStatusDto>>> GetTaskStatusAsync(string name, CancellationToken cancellationToken)
    {
        var durum = await schedulerService.GetTaskStatusAsync(await ScopeNameAsync(name, cancellationToken), cancellationToken);
        return Ok(ApiResponse<ScheduledTaskStatusDto>.Ok(durum, "cron.status"));
    }

    /// <summary>
    /// Görevin son çalışmasında ürettiği çıktı (stdout + stderr).
    /// </summary>
    [HttpGet("{name}/output")]
    public async Task<ActionResult<ApiResponse<ScheduledTaskOutputDto>>> GetTaskOutputAsync(string name, CancellationToken cancellationToken)
    {
        var cikti = await schedulerService.GetTaskOutputAsync(await ScopeNameAsync(name, cancellationToken), cancellationToken);
        return Ok(ApiResponse<ScheduledTaskOutputDto>.Ok(cikti, "cron.output"));
    }

    /// <summary>
    /// Takılı kalmış bir çalışmayı durdurur. Görev SİLİNMEZ, devre dışı da bırakılmaz; yalnızca
    /// sürmekte olan tur sonlandırılır ve görev bir sonraki zamanında normal çalışmaya döner.
    /// </summary>
    [HttpPost("{name}/stop")]
    public async Task<ActionResult<ApiResponse<object>>> StopTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.StopTaskAsync(await ScopeNameAsync(name, cancellationToken), cancellationToken);
        await WriteAuditAsync("cron.stopped", $"Scheduled task '{name}' run stopped.", cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.stopped"));
    }

    [HttpPost("{name}/enable")]
    public async Task<ActionResult<ApiResponse<object>>> EnableTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.SetTaskEnabledAsync(await ScopeNameAsync(name, cancellationToken), true, cancellationToken);
        await WriteAuditAsync("cron.enabled", $"Scheduled task '{name}' enabled.", cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.enabled"));
    }

    [HttpPost("{name}/disable")]
    public async Task<ActionResult<ApiResponse<object>>> DisableTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.SetTaskEnabledAsync(await ScopeNameAsync(name, cancellationToken), false, cancellationToken);
        await WriteAuditAsync("cron.disabled", $"Scheduled task '{name}' suspended.", cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.disabled"));
    }

    [HttpDelete("{name}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTaskAsync(string name, CancellationToken cancellationToken)
    {
        await schedulerService.DeleteTaskAsync(await ScopeNameAsync(name, cancellationToken), cancellationToken);
        await WriteAuditAsync("cron.deleted", $"Scheduled task '{name}' deleted.", cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.deleted"));
    }

    private async Task<string> ScopeNameAsync(string name, CancellationToken cancellationToken) =>
        IsAdmin ? name : await GetOwnerPrefixAsync(cancellationToken) + name;

    private Task WriteAuditAsync(string action, string details, CancellationToken cancellationToken) =>
        auditService.WriteAsync(
            action,
            "cron",
            details,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

    // Müşteri URL görevi yalnızca dış (genel) adresleri hedefleyebilir. SSRF koruması: sunucunun kendisi
    // (loopback), iç ağ (10/8, 172.16/12, 192.168/16), link-local ve IPv6 özel adresleri engellenir.
    // Literal IP kesin engellenir; alan adı ise en iyi çabayla DNS ile çözülür — çözülen IP'lerden biri
    // özel/loopback ise reddedilir (DNS çözülemezse literal-IP koruması yeterlidir, meşru site engellenmez).
    private static async Task EnsureCustomerUrlAllowedAsync(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AppException("cron.invalid_url", 400, 400);
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("cron.url_forbidden", 403, 403);
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsBlockedAddress(literal))
            {
                throw new AppException("cron.url_forbidden", 403, 403);
            }

            return;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Any(IsBlockedAddress))
            {
                throw new AppException("cron.url_forbidden", 403, 403);
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch
        {
            // DNS çözülemedi → engelleme (geçici DNS hatası meşru siteyi engellemesin; literal-IP SSRF zaten kapalı).
        }
    }

    private static bool IsBlockedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10                                  // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 (link-local)
                || b[0] == 0;                                  // 0.0.0.0/8
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal
                || ip.IsIPv6SiteLocal
                || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fc00::/7 (unique-local)
        }

        return false;
    }
}
