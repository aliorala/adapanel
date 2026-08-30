using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
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

        // Zamanlayıcıya YAZILAN ad (abonelik önekiyle) korunur: aşağıda kullanıcıya dönerken önek
        // kırpılıyor, oysa kaydın konusu tam da o önekten ölçülüyor. Kırpılmış adı kullansaydık
        // müşteri görevlerinin hepsi sessizce kiracısız kaydedilirdi.
        var kayitliAd = task.Name;
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
        await WriteAuditAsync("cron.created", kayitliAd, $"Scheduled task '{task.Name}' ({request.TaskType}) -> {target}{scriptContext}");
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
        var kapsamliAd = await ScopeNameAsync(name, cancellationToken);
        await schedulerService.RunTaskAsync(kapsamliAd, cancellationToken);
        await WriteAuditAsync("cron.run", kapsamliAd, $"Scheduled task '{name}' triggered manually.");
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
    /// Görevlerin çalışacağı ortam: sunucuda hangi Python kurulu, SYSTEM onu görebiliyor mu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>YALNIZCA YÖNETİCİ — ölçülmüş bir sızıntı kapatıldı.</b> Yanıt sunucudaki yorumlayıcıların
    /// TAM YOLLARINI taşır ve bu yollar makinenin iç yapısını ele verir; canlı panelde ölçülen değer
    /// <c>C:\Users\&lt;yönetici&gt;\AppData\Local\Python\…</c> idi, yani sunucu yöneticisinin Windows
    /// kullanıcı adı bir hosting müşterisine düz metin olarak dönüyordu.
    /// </para>
    /// <para>
    /// <b>Kısıt bir yetenek kaybı DEĞİL:</b> müşteri/bayi zaten yalnız <c>url</c> tipi görev
    /// kurabiliyor (<see cref="CreateTaskAsync"/> diğer alanları zorla temizler), dolayısıyla bu
    /// ölçümle yapabileceği hiçbir şey yok. Panel de bu kutuyu yalnız komut/betik formunda
    /// gösteriyor — yani kapsamlı rol veriyi hiç görmüyordu bile, sadece taşıyordu.
    /// </para>
    /// <para>
    /// Kardeş uç (<see cref="AddPythonToPathAsync"/>) aynı gerekçeyle zaten yöneticiye özeldi;
    /// okuma ucunun açık kalması, aynı bilginin ikinci kapısını açık bırakmaktı.
    /// </para>
    /// </remarks>
    [HttpGet("environment")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<TaskEnvironmentDto>>> GetEnvironmentAsync(CancellationToken cancellationToken)
    {
        var ortam = await schedulerService.GetEnvironmentAsync(cancellationToken);
        return Ok(ApiResponse<TaskEnvironmentDto>.Ok(ortam, "cron.environment"));
    }

    /// <summary>
    /// Bulunan Python'u makine PATH'ine ekler.
    /// <para>
    /// YALNIZCA YÖNETİCİ: sunucu genelini etkileyen bir değişikliktir; tek bir sitenin sahibi
    /// bunu yapamamalı.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Kısıt gövdedeki bir <c>if</c> ile değil ÖZNİTELİKLE uygulanıyor — kardeş okuma ucuyla
    /// (<see cref="GetEnvironmentAsync"/>) aynı mekanizma. Gerekçe: iki uç aynı kuralı iki farklı
    /// yolla uyguladığında kuralın makineyle denetlenmesi mümkün olmaz ve biri değişince diğeri
    /// sessizce geride kalır. Öznitelik ayrıca eylem gövdesinden ÖNCE çalışır.
    /// </remarks>
    [HttpPost("environment/python-path")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<object>>> AddPythonToPathAsync(CancellationToken cancellationToken)
    {
        var eklenen = await schedulerService.AddPythonToMachinePathAsync(cancellationToken);
        // SUNUCU GENELİ: makine PATH'i bütün kiracıların görevlerini etkiler, tek bir aboneliğe ait değil.
        await WriteServerAuditAsync("cron.python_path", $"Python added to machine PATH ({eklenen} folder(s)).");
        return Ok(ApiResponse<object>.Ok(new { added = eklenen }, "cron.python_path_added"));
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
    /// Süren bir çalışmayı durdurur. Görev SİLİNMEZ, devre dışı da bırakılmaz; yalnızca sürmekte
    /// olan tur sonlandırılır ve görev bir sonraki zamanında normal çalışmaya döner.
    /// <para>
    /// Sonuç ölçülür ve olduğu gibi bildirilir: kaç süreç ağacı sonlandırıldı, görev hâlâ çalışıyor
    /// mu. Arayüz "durduruldu" derken bu ölçüme dayanır.
    /// </para>
    /// </summary>
    [HttpPost("{name}/stop")]
    public async Task<ActionResult<ApiResponse<StopScheduledTaskResultDto>>> StopTaskAsync(string name, CancellationToken cancellationToken)
    {
        var kapsamliAd = await ScopeNameAsync(name, cancellationToken);
        var sonuc = await schedulerService.StopTaskAsync(kapsamliAd, cancellationToken);
        await WriteAuditAsync(
            "cron.stopped",
            kapsamliAd,
            $"Scheduled task '{name}' run stopped (killed={sonuc.KilledProcesses}, stillRunning={sonuc.StillRunning}).");
        return Ok(ApiResponse<StopScheduledTaskResultDto>.Ok(sonuc, sonuc.StillRunning ? "cron.stop_incomplete" : "cron.stopped"));
    }

    [HttpPost("{name}/enable")]
    public async Task<ActionResult<ApiResponse<object>>> EnableTaskAsync(string name, CancellationToken cancellationToken)
    {
        var kapsamliAd = await ScopeNameAsync(name, cancellationToken);
        await schedulerService.SetTaskEnabledAsync(kapsamliAd, true, cancellationToken);
        await WriteAuditAsync("cron.enabled", kapsamliAd, $"Scheduled task '{name}' enabled.");
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.enabled"));
    }

    [HttpPost("{name}/disable")]
    public async Task<ActionResult<ApiResponse<object>>> DisableTaskAsync(string name, CancellationToken cancellationToken)
    {
        var kapsamliAd = await ScopeNameAsync(name, cancellationToken);
        await schedulerService.SetTaskEnabledAsync(kapsamliAd, false, cancellationToken);
        await WriteAuditAsync("cron.disabled", kapsamliAd, $"Scheduled task '{name}' suspended.");
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.disabled"));
    }

    [HttpDelete("{name}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTaskAsync(string name, CancellationToken cancellationToken)
    {
        // Ad silmeden ÖNCE çözülür: kaydın konusu bu addan ölçülüyor ve görev silindikten sonra
        // sunucuda ona dair hiçbir kanıt kalmaz.
        var kapsamliAd = await ScopeNameAsync(name, cancellationToken);
        await schedulerService.DeleteTaskAsync(kapsamliAd, cancellationToken);
        await WriteAuditAsync("cron.deleted", kapsamliAd, $"Scheduled task '{name}' deleted.");
        return Ok(ApiResponse<object>.Ok(new { name }, "cron.deleted"));
    }

    private async Task<string> ScopeNameAsync(string name, CancellationToken cancellationToken) =>
        IsAdmin ? name : await GetOwnerPrefixAsync(cancellationToken) + name;

    /// <summary>Abonelik ad alanı öneki: <c>u&lt;32 onaltılık&gt;--</c> (bkz. <see cref="GetOwnerPrefixAsync"/>).</summary>
    private static readonly Regex SahipOneki = new(@"^u(?<id>[0-9a-fA-F]{32})--", RegexOptions.Compiled);

    /// <summary>
    /// Görevin sahibi abonelik — GÖREV ADINDAN ÖLÇÜLÜR, çağıranın kimliğinden türetilmez.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden addan:</b> müşteri/bayi görevleri Windows Görev Zamanlayıcı'da abonelik ad alanında
    /// yaşar (<c>u&lt;abonelikId&gt;--&lt;ad&gt;</c>) — yani sahiplik zaten kaynağın KENDİ adında
    /// yazılıdır. Çağıranın kimliğini konu yazmak, YÖNETİCİNİN bir kiracının görevini silmesi
    /// durumunda kaydı yöneticinin aboneliğine (yani hiçbir aboneliğe) bağlardı ve kiracı, kendi
    /// zamanlanmış görevinin silindiğini kendi denetim ekranında HİÇ göremezdi — oysa bu kaydın var
    /// olma sebebi tam olarak o.
    /// </para>
    /// <para>
    /// <b>Önek yoksa konu BOŞ kalır:</b> öneksiz ad, yöneticinin sunucu düzeyindeki kendi görevidir
    /// (komut/betik görevleri yalnız yöneticiye açıktır). Onu çağıran yöneticiye bağlamak,
    /// sunucu geneli bir işi bir "abonelik kaydı" gibi göstermek olurdu.
    /// </para>
    /// </remarks>
    private static Guid? TaskOwner(string? scopedName)
    {
        var eslesme = SahipOneki.Match(scopedName ?? string.Empty);

        return eslesme.Success && Guid.TryParseExact(eslesme.Groups["id"].Value, "N", out var sahip)
            ? sahip
            : null;
    }

    /// <summary>Bir GÖREVE ait kayıt: konusu, görevin ad alanındaki abonelik.</summary>
    private Task WriteAuditAsync(string action, string scopedName, string details) =>
        auditService.AuditAsync(HttpContext, action, "cron", details, TaskOwner(scopedName));

    /// <summary>
    /// SUNUCU GENELİ kayıt — hiçbir aboneliğe ait olmayan zamanlayıcı işlemleri.
    /// </summary>
    /// <remarks>
    /// Ayrı bir metot olması bilinçli: konusu olan ve olmayan kaydı tek sarmalayıcıda toplamak,
    /// "bu kaynak kiracıya ait mi" kararını çağrı yerinden gizler ve unutulduğu an kayıt sessizce
    /// kiracısız kalırdı (aynı ayrım DatabasesController'da da var).
    /// </remarks>
    private Task WriteServerAuditAsync(string action, string details) =>
        auditService.AuditAsync(HttpContext, action, "cron:server", details);

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
