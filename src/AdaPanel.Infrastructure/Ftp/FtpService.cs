using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Ftp;

// logger İSTEĞE BAĞLI: DI gerçek kaydediciyi verir, dört argümanla kurulan mevcut testler
// (FtpHomeHealTests, FtpHomeProbeTests) derlenmeye devam eder.
public sealed partial class FtpService(
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    IPanelPreferencesService panelPreferencesService,
    IOptions<PanelRuntimeOptions> runtimeOptions,
    ILogger<FtpService>? logger = null,
    IConcurrencyLock? serverConcurrency = null) : IFtpService
{
    // Shared by ensure, TLS updates and account creation's infrastructure preparation.
    internal IConcurrencyLock InfrastructureLock { get; } = serverConcurrency ?? new ConcurrencyLock();
    private const string InfrastructureLockKey = "ftp-infrastructure";
    private const string StatePath = "config/ftp-users.json";
    private const string TlsStatePath = "config/ftp-tls.json";
    private const string InfraStatePath = "config/ftp-infra.json";
    private const string FtpSiteName = "AdaPanel FTP";
    private const int FtpPort = 21;
    private const int PassivePortStart = 50000;
    private const int PassivePortEnd = 51000;
    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    // Per-instance transport seams keep lifecycle tests off native accounts and IIS.
    // Production uses exactly the existing command/strict-PowerShell execution paths.
    internal Func<string, string, CancellationToken, Task<CommandResult>>? NativeCommand { get; set; }
    internal Func<string, bool, CancellationToken, Task<CommandResult>>? NativeScript { get; set; }
    // Local SAM status seam; native process output is never interpreted as proof of absence.
    internal Func<string, CancellationToken, Task<uint>>? NativeAccountStatus { get; set; }

    private Task<CommandResult> RunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken) =>
        NativeCommand is { } command ? command(fileName, arguments, cancellationToken)
            : ProcessRunner.RunAsync(fileName, arguments, cancellationToken: cancellationToken);

    private Task<CommandResult> RunScriptAsync(string script, CancellationToken cancellationToken) =>
        NativeScript is { } command ? command(script, false, cancellationToken)
            : ProcessRunner.RunPowerShellAsync(script, cancellationToken);

    private Task<CommandResult> RunStrictScriptAsync(string script, CancellationToken cancellationToken) =>
        NativeScript is { } command ? command(script, true, cancellationToken)
            : ProcessRunner.RunPowerShellStrictAsync(script, cancellationToken);

    public Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default) =>
        EnsureServerCoreAsync(null, cancellationToken);

    public Task<FtpServerStatusDto> EnsureServerAsync(Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforeMutation);
        return EnsureServerCoreAsync(beforeMutation, cancellationToken);
    }

    private Task<FtpServerStatusDto> EnsureServerCoreAsync(Func<CancellationToken, Task>? beforeMutation,
        CancellationToken cancellationToken) => InfrastructureLock.RunExclusiveAsync(InfrastructureLockKey, async () =>
    {
        await EnsureFtpInfrastructureCoreAsync(false, cancellationToken, beforeMutation);

        // KURULUMDAN SONRA DURUM ÖLÇÜLÜR, VARSAYILMAZ. Buraya eskiden `IsConfigured = true,
        // StatusMessage = "ftp.server_ready"` sabitleri yazılıyordu; gerekçe FtpServerStatusDto'da.
        var status = await GetServerStatusAsync(cancellationToken);
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        return status;
    }, cancellationToken);

    public async Task<FtpServerStatusDto> GetServerStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var durum = new FtpServerStatusDto
        {
            ServerAddress = preferences.ServerAddress,
            ControlPort = FtpPort,
            PassivePortStart = PassivePortStart,
            PassivePortEnd = PassivePortEnd
        };

        var olcum = await ProbeFtpRuntimeAsync(cancellationToken);
        if (olcum is null)
        {
            // A previous installation is not evidence that the server currently works.
            // The status key distinguishes an unknown measurement from a measured fault.
            durum.IsConfigured = false;
            durum.StatusMessage = "ftp.server_status_unknown";
            return durum;
        }

        durum.ServiceState = olcum.ServiceState;
        durum.SiteState = olcum.SiteState;
        durum.IsSiteRunning = olcum.SiteExists && olcum.SiteState.Equals("Started", StringComparison.OrdinalIgnoreCase);

        var (portDurumu, portSahibi) = ClassifyControlPort(olcum.PortOlculebildi, olcum.Listeners, olcum.ServicePids);
        durum.HasControlPortConflict = portDurumu == FtpControlPortDurumu.YabanciSurec;
        durum.ControlPortOwner = portSahibi;

        // SIRA ÖNEMLİ: en temel eksiklik önce söylenir. "Port çakışması" demek, site hiç yokken
        // yanıltıcı olurdu.
        durum.StatusMessage =
            !olcum.SiteExists ? "ftp.site_not_configured"
            : !olcum.ServiceState.Equals("Running", StringComparison.OrdinalIgnoreCase) ? "ftp.service_not_running"
            : !durum.IsSiteRunning ? "ftp.site_not_running"
            : portDurumu == FtpControlPortDurumu.YabanciSurec ? "ftp.control_port_conflict"
            : portDurumu == FtpControlPortDurumu.DinleyenYok ? "ftp.server_not_listening"
            : portDurumu == FtpControlPortDurumu.Olculemedi ? "ftp.server_status_unknown"
            : "ftp.server_ready";

        durum.IsConfigured = durum.StatusMessage == "ftp.server_ready";
        return durum;
    }

    /// <summary>Denetim portunun kimin elinde olduğuna dair HÜKÜM.</summary>
    internal enum FtpControlPortDurumu
    {
        /// <summary>Ölçüm yapılamadı (cmdlet yok / probe düştü). "Sorun yok" demek DEĞİLDİR.</summary>
        Olculemedi,

        /// <summary>Portu kimse dinlemiyor: hiçbir istemci bağlanamaz.</summary>
        DinleyenYok,

        /// <summary>Portu yalnız Microsoft FTP Service (FTPSVC) tutuyor — beklenen durum.</summary>
        PanelinServisi,

        /// <summary>Portu panelin FTP servisi DIŞINDA bir süreç de tutuyor.</summary>
        YabanciSurec
    }

    /// <summary>Denetim portunda dinleyen tek bir süreç (probe satırı).</summary>
    internal sealed record FtpPortDinleyici(int Pid, string Process, IReadOnlyCollection<string> Services);

    /// <summary>
    /// Denetim portu hükmü. SAF fonksiyon: girdileri dışarıdan alır, yan etkisi yoktur, bu yüzden
    /// gerçek bir port çakışması kurmadan tam doğruluk tablosuyla test edilebilir
    /// (<see cref="ClassifyUserNameClaim"/> ile aynı desen).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN "site çalışıyor mu" SORUSU YETMİYOR — ölçüldü (19.08.2026).</b> Bu makinede
    /// <c>Get-Website</c> siteyi <c>Started</c> raporluyor, FTPSVC <c>Running</c> ve 21 portunda
    /// GERÇEKTEN dinliyor (pid 5068). Buna rağmen porta bağlanan istemciyi FileZilla Server 0.9.59
    /// karşılıyor, çünkü FileZilla da aynı portu bağlamış (pid 5036). Yani iki dinleyici bir arada
    /// var olabiliyor ve tek arıza belirtisi portun PAYLAŞILMASI. Sitenin durumuna bakan bir kontrol
    /// bu hatayı GÖREMEZDİ.
    /// </para>
    /// <para>
    /// <b>Neden banner'a bakılmıyor:</b> 21 portundan okunan <c>220-FileZilla Server…</c> selamı
    /// suçluyu doğrudan söylüyor, ama IIS FTP'nin selamı yapılandırmayla değiştirilebilir
    /// (<c>ftpServer.messages.bannerMessage</c>) — metne bakan bir kontrol, selamı özelleştirmiş
    /// dürüst bir kurulumu suçlu ilan ederdi. Süreç kimliği karşılaştırması yapılandırmadan
    /// bağımsızdır.
    /// </para>
    /// <para>
    /// Süreç kimliği İKİ kaynaktan doğrulanır (PID listesi ve o süreçteki servis adları): FTPSVC
    /// paylaşımlı bir <c>svchost</c> içinde çalışır ve tek bir PID sorgusu yarış hâlinde bayatlayıp
    /// KENDİ servisimizi yabancı ilan edebilirdi — yanlış alarm, çakışmayı kaçırmak kadar kötüdür.
    /// </para>
    /// </remarks>
    internal static (FtpControlPortDurumu Durum, string Sahip) ClassifyControlPort(
        bool olculebildi,
        IReadOnlyCollection<FtpPortDinleyici> dinleyiciler,
        IReadOnlyCollection<int> ftpsvcPidleri)
    {
        if (!olculebildi)
        {
            return (FtpControlPortDurumu.Olculemedi, string.Empty);
        }

        if (dinleyiciler.Count == 0)
        {
            return (FtpControlPortDurumu.DinleyenYok, string.Empty);
        }

        var yabancilar = dinleyiciler
            .Where(item => !ftpsvcPidleri.Contains(item.Pid))
            .Where(item => !item.Services.Any(ad => string.Equals(ad, "FTPSVC", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (yabancilar.Count == 0)
        {
            return (FtpControlPortDurumu.PanelinServisi, string.Empty);
        }

        var aciklama = string.Join(", ", yabancilar.Select(item =>
        {
            var ad = string.IsNullOrWhiteSpace(item.Process) ? "bilinmeyen süreç" : item.Process + ".exe";
            var servisler = item.Services.Count == 0 ? string.Empty : $", Windows servisi: {string.Join("/", item.Services)}";
            return $"{ad} (PID {item.Pid}{servisler})";
        }));

        return (FtpControlPortDurumu.YabanciSurec, aciklama);
    }

    /// <summary>Probe'un ham ölçümü; <c>null</c> = hiç ölçülemedi.</summary>
    private sealed record FtpRuntimeOlcum(
        bool SiteExists,
        string SiteState,
        string ServiceState,
        IReadOnlyCollection<int> ServicePids,
        bool PortOlculebildi,
        IReadOnlyCollection<FtpPortDinleyici> Listeners);

    /// <summary>
    /// FTP çalışma zamanını TEK bir PowerShell çağrısında ölçer (servis, site, portu dinleyen tüm
    /// süreçler). Hiçbir şeyi değiştirmez.
    /// </summary>
    /// <remarks>
    /// <c>ComponentPreflight.BuildProbeScript</c> BİLEREK kullanılmadı: o probe port başına
    /// <c>Select-Object -First 1</c> ile TEK dinleyici alır, çünkü sorusu "kurulumdan önce port boş
    /// mu". Buradaki soru farklı ve tam da ölçülen hataya denk geliyor — port PAYLAŞILIYOR mu.
    /// Tek dinleyici alan bir sorgu bu makinede FTPSVC'yi döndürüp FileZilla'yı hiç göremezdi.
    /// </remarks>
    private async Task<FtpRuntimeOlcum?> ProbeFtpRuntimeAsync(CancellationToken cancellationToken)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $portOlculdu = $false
            $siteOlculdu = $false
            $servisOlculdu = $false
            $siteVar = $false
            $siteDurum = ''
            $servisDurum = ''
            $servisPidleri = @()
            try {
              $svc = Get-CimInstance Win32_Service -Filter "Name='FTPSVC'" -ErrorAction Stop
              if ($svc) {
                $servisDurum = [string]$svc.State
                if ([int]$svc.ProcessId -gt 0) { $servisPidleri = @([int]$svc.ProcessId) }
              } else { $servisDurum = 'NotInstalled' }
              $servisOlculdu = $true
            } catch { }
            try {
              Import-Module WebAdministration -ErrorAction Stop
              $siteler = @(Get-Website -ErrorAction Stop | Where-Object { $_.Name -eq '{{EscapePowerShellLiteral(FtpSiteName)}}' })
              if ($siteler.Count -gt 1) { throw 'Ambiguous FTP site measurement.' }
              if ($siteler.Count -eq 1) { $siteVar = $true; $siteDurum = [string]$siteler[0].State }
              $siteOlculdu = $true
            } catch { }
            $satirlar = @()
            if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
              try {
                # Filter after a successful inventory; no matching listener is a measured empty
                # result, whereas a failed native query must remain unknown.
                foreach ($k in @(Get-NetTCPConnection -ErrorAction Stop | Where-Object { $_.LocalPort -eq {{FtpPort}} -and $_.State -eq 'Listen' })) {
                  $sp = Get-Process -Id $k.OwningProcess -ErrorAction Stop
                  $servisAdlari = @(Get-CimInstance Win32_Service -Filter "ProcessId=$($k.OwningProcess)" -ErrorAction Stop | ForEach-Object { [string]$_.Name })
                  $satirlar += [pscustomobject]@{ pid = [int]$k.OwningProcess; process = [string]$sp.ProcessName; services = @($servisAdlari) }
                }
                $portOlculdu = $true
              } catch { $satirlar = @() }
            }
            [pscustomobject]@{
              siteMeasured = $siteOlculdu
              serviceMeasured = $servisOlculdu
              siteExists = $siteVar
              siteState = $siteDurum
              serviceState = $servisDurum
              servicePids = @($servisPidleri)
              portMeasured = $portOlculdu
              listeners = @($satirlar | Group-Object pid | ForEach-Object { $_.Group[0] })
            } | ConvertTo-Json -Depth 5 -Compress
            """;

        var sonuc = await RunScriptAsync(script, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!sonuc.Success || string.IsNullOrWhiteSpace(sonuc.StandardOutput))
        {
            return null;
        }

        try
        {
            using var belge = JsonDocument.Parse(sonuc.StandardOutput);
            var kok = belge.RootElement;
            if (kok.ValueKind != JsonValueKind.Object ||
                !TryBoolean(kok, "siteMeasured", out var siteMeasured) || !siteMeasured ||
                !TryBoolean(kok, "serviceMeasured", out var serviceMeasured) || !serviceMeasured ||
                !TryBoolean(kok, "siteExists", out var siteExists) ||
                !TryBoolean(kok, "portMeasured", out var portMeasured) ||
                !kok.TryGetProperty("siteState", out var siteState) || siteState.ValueKind != JsonValueKind.String ||
                !kok.TryGetProperty("serviceState", out var serviceState) || serviceState.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(serviceState.GetString()) ||
                (siteExists && string.IsNullOrWhiteSpace(siteState.GetString())) ||
                !kok.TryGetProperty("listeners", out var listeners) || listeners.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object) ||
                !kok.TryGetProperty("servicePids", out var pids) || pids.ValueKind is not (JsonValueKind.Array or JsonValueKind.Number))
                return null;

            var dinleyiciler = new List<FtpPortDinleyici>();
            foreach (var satir in DiziOku(kok, "listeners"))
            {
                if (satir.ValueKind != JsonValueKind.Object ||
                    !satir.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var pidDeger) || pidDeger <= 0 ||
                    !satir.TryGetProperty("process", out var process) || process.ValueKind != JsonValueKind.String ||
                    !satir.TryGetProperty("services", out var services) || services.ValueKind is not (JsonValueKind.Array or JsonValueKind.String))
                    return null;
                var serviceNames = DiziOku(satir, "services").ToArray();
                if (serviceNames.Any(item => item.ValueKind != JsonValueKind.String)) return null;
                dinleyiciler.Add(new FtpPortDinleyici(
                    pidDeger, process.GetString() ?? string.Empty,
                    serviceNames.Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()));
            }
            var servicePids = DiziOku(kok, "servicePids").ToArray();
            if (servicePids.Any(item => item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var pid) || pid <= 0)) return null;

            return new FtpRuntimeOlcum(
                siteExists, siteState.GetString() ?? string.Empty, serviceState.GetString() ?? string.Empty,
                servicePids.Select(item => item.GetInt32()).ToArray(), portMeasured,
                dinleyiciler);
        }
        catch (JsonException)
        {
            // Bozuk/eksik JSON "sorun yok" değildir: çağıran bunu "ölçülemedi" olarak raporlar.
            return null;
        }
    }

    /// <summary>
    /// ConvertTo-Json TEK ÖĞELİ diziyi diziye SARMAZ: <c>@($x) | ConvertTo-Json</c> nesne döndürür.
    /// Bu ayrım ölçülmeden yazılan her ayrıştırıcıyı tek dinleyici/tek PID durumunda sessizce
    /// boşa düşürür — tam da çakışmanın tek yabancı süreçten ibaret olduğu olağan durumda.
    /// </summary>
    private static IEnumerable<JsonElement> DiziOku(JsonElement kok, string ad)
    {
        if (!kok.TryGetProperty(ad, out var deger))
        {
            yield break;
        }

        if (deger.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in deger.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (deger.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            yield return deger;
        }
    }

    private static bool TryBoolean(JsonElement value, string name, out bool result)
    {
        result = false;
        if (!value.TryGetProperty(name, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = field.GetBoolean();
        return true;
    }

    // M5 (profesyonel paneller gibi opsiyonel "yalnız güvenli FTP"): FTPS zorunlu mu? Varsayılan false = düz + FTPS (kilitlenme yok).
    public async Task<bool> GetRequireSslAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return state.RequireSsl;
    }

    /// <summary>
    /// FTPS zorunluluğunu IIS'e uygular; ölçüm doğrularsa kalıcı tercihi yayımlar.
    /// </summary>
    /// <remarks>
    /// Eskiden ISTENEN bayrağı döndürüyordu ve uç de o bayraktan beslenip kullanıcıya "FTPS artık
    /// zorunlu; düz FTP bağlantıları reddedilecek" diyordu. Gerekçenin tamamı
    /// <see cref="FtpTlsMeasurement"/> içindedir: altyapı betiği gevşek koşucuyla çalışır, geri
    /// okuma betiğin İÇİNDE zaten vardı ama yalnız <c>Write-Warning</c> yazıyor ve C# tarafı
    /// çıktıya hiç bakmıyordu — ölçüm yapılıp atılıyordu.
    /// </remarks>
    public Task<FtpTlsMeasurement> SetRequireSslAsync(bool requireSsl, CancellationToken cancellationToken = default) =>
        SetRequireSslCoreAsync(requireSsl, null, cancellationToken);

    public Task<FtpTlsMeasurement> SetRequireSslAsync(bool requireSsl, Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforeMutation);
        return SetRequireSslCoreAsync(requireSsl, beforeMutation, cancellationToken);
    }

    private Task<FtpTlsMeasurement> SetRequireSslCoreAsync(bool requireSsl,
        Func<CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken) =>
        InfrastructureLock.RunExclusiveAsync(InfrastructureLockKey, async () =>
        {
            await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
            var saved = await GetRequireSslAsync(cancellationToken);
            // The requested value stays local until native application has been measured. Other
            // ensure/create requests cannot read or apply an unfinished preference change.
            var measurement = await EnsureFtpInfrastructureCoreAsync(true, cancellationToken,
                beforeMutation, requireSsl, publishReady: false);
            if (measurement.Verified == true)
            {
                await fileStore.UpdateAsync(TlsStatePath, new FtpTlsState(),
                    _ => new FtpTlsState { RequireSsl = requireSsl },
                    token => ApproveInfrastructureAsync(beforeMutation, token), cancellationToken);
                saved = requireSsl;
                await PublishInfrastructureReadyAsync(beforeMutation, cancellationToken);
            }
            await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
            return measurement with { SavedRequireSsl = saved };
        }, cancellationToken);

    private sealed class FtpTlsState
    {
        public bool RequireSsl { get; set; }
    }

    // FTP altyapısı (IIS FTP sitesi + SSL + firewall) bir kez tam kurulduğunda işaretlenir → sonraki kullanıcı
    // eklemeleri ağır kurulumu + FTPSVC yeniden başlatmayı ATLAR (hızlı yol). SSL politikası değişimi force=true ile tam kurar.
    private sealed class FtpInfraState
    {
        public bool Configured { get; set; }
    }

    public async Task<IReadOnlyCollection<FtpUserDto>> GetFtpUsersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken);
        var list = new List<FtpUserDto>(records.Length);

        foreach (var record in records.OrderBy(item => item.UserName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ev = ProbeHome(record.UserName);
            var exists = await UserExistsAsync(record.UserName, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            list.Add(new FtpUserDto
            {
                UserName = record.UserName,
                HomePath = record.HomePath,
                SiteName = record.SiteName,
                CreatedAtUtc = record.CreatedAtUtc,
                IsEnabled = record.IsEnabled,
                ExistsOnSystem = exists,
                HomeLinkPath = ev.LinkPath,
                HomeLinkExists = ev.LinkExists,
                HomeTargetExists = ev.TargetExists,
                ServerAddress = Environment.MachineName,
                Port = FtpPort
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        return list;
    }

    public Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request, CancellationToken cancellationToken = default) =>
        CreateFtpUserCoreAsync(request, null, cancellationToken);

    public Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        CreateFtpUserCoreAsync(request, beforeMutation, cancellationToken);

    private async Task<FtpUserDto> CreateFtpUserCoreAsync(CreateFtpUserRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        request = new CreateFtpUserRequest { UserName = request.UserName, Password = request.Password,
            SiteName = request.SiteName, HomePath = request.HomePath };
        var userName = NormalizeUserName(request.UserName);
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Trim().Length < 8)
        {
            throw new AppException("ftp.password_too_short", 400, 400);
        }

        EnsureCommandSafePassword(request.Password);

        // Interactive callers pin the site generation through native effects/publication.
        // Trusted provisioning already owns its exclusive site intent and uses the old overload;
        // independently capturing that unfinished site here would reject its own creation.
        using var target = beforeMutation is null ? null :
            await SiteMutationGuard.CaptureAsync(iisSiteService, new ConcurrencyLock(), request.SiteName, cancellationToken);
        var expectedAccount = await ReadAccountAsync(userName, cancellationToken);
        using var accountLease = await FtpAccountLifecycle.EnterAsync(userName, cancellationToken);
        async Task Approve(CancellationToken token)
        {
            await EnsureAccountUnchangedAsync(userName, expectedAccount, token);
            if (target is not null) await target.ValidateAsync(token);
            if (beforeMutation is not null) await beforeMutation(await iisSiteService.GetSiteAsync(request.SiteName, token), token);
            token.ThrowIfCancellationRequested();
        }
        await Approve(cancellationToken);

        // KULLANICI ADI DA BİR SAHİPLİK İDDİASIDIR — ve hiç sorulmuyordu.
        //
        // Bu uç SİTEYİ doğruluyordu (FtpController.EnsureSiteAsync), ama mutasyona uğrayan nesne
        // site değil GERÇEK BİR WINDOWS YEREL HESABI. Hesabın mevcut sahipliği hiç sorulmadığı için
        // zincir şöyle işliyordu:
        //   1. saldırgan kendi sitesi için UserName="Administrator" ile oluşturma isteği atar;
        //   2. hesap zaten var → `net user /add` atlanır ama akış DEVAM eder;
        //   3. Administrator'a saldırganın klasöründe (OI)(CI)M verilir, LocalUser junction'ı
        //      saldırganın klasörüne yönlendirilir;
        //   4. ftp-users.json kaydındaki SiteName saldırganın sitesiyle EZİLİR;
        //   5. FtpController.EnsureFtpUserScopeAsync sahipliği tam da o ezilmiş kayıttan okuduğu
        //      için kapsam denetimi GEÇER;
        //   6. POST /ftp/users/password → `net user "Administrator" "<yeni parola>" /y`.
        // Yani kapsam denetimi, saldırganın kendi yazdığı kayda soruyordu. Denetlenen değeri
        // kiracının değiştirebilmesi hata sınıfının ta kendisi.
        //
        // Karar SitesController'daki "SİTE ADI DA BİR SAHİPLİK İDDİASIDIR" desenini izler: iddia
        // edilen ad başkasınınsa reddedilir. İki kaynak var ve ikisi de sorulur — panelin kendi
        // kaydı (hangi siteye ait) ve Windows'un kendisi (panel dışı hesap).
        //
        // KONUM ÖNEMLİ: bu denetim klasör oluşturma / IIS FTP altyapısı kurulumu / ACL'den ÖNCE.
        // Reddedilen bir istek sunucuda HİÇBİR yan etki bırakmamalı; eskiden altyapı kurulumu ve
        // dizin oluşturma denetimden önce çalışıyordu.
        await EnsureUserNameIsClaimableAsync(userName, request.SiteName, cancellationToken);

        using var siteUse = target is null ? null :
            await target.ReserveAsync(async (_, token) => await Approve(token), cancellationToken);

        var homePath = await ResolveHomePathAsync(request, cancellationToken);
        await Approve(cancellationToken);
        Directory.CreateDirectory(homePath);
        await EnsureFtpInfrastructureAsync(false, cancellationToken, Approve);

        // BU ÇAĞRI hesabı gerçekten AÇTI mı? Geri alma yalnız BUNA bakar (gerekçe: RollbackCreateAsync).
        var hesabiBuCagriActi = false;
        if (!await UserExistsAsync(userName, cancellationToken))
        {
            await Approve(cancellationToken);
            var createResult = await RunCommandAsync("net.exe", $"user \"{userName}\" \"{request.Password}\" /add /y", cancellationToken: cancellationToken);
            if (!createResult.Success)
            {
                throw new AppException("ftp.user_create_failed", 500, 500, BuildCommandDetails(createResult));
            }

            hesabiBuCagriActi = true;
        }

        // Hesap AÇILDIĞI ANDA parola süresi kapatılır: aksi hâlde makinenin MaxPasswordAge'i
        // (varsayılan 42 gün) hesaba işler ve FTP kırk iki gün sonra sessizce ölür.
        //
        // SONUÇ ARTIK ATILMIYOR: aşağıdaki DTO'ya konur ve uç denetim kaydına yazar. Eskiden
        // yalnız günlüğe yazılıyordu, yani hesap eksik açıldığında bile panel sade "oluşturuldu"
        // diyordu ve kırk iki gün sonra kimse buraya bakmıyordu.
        bool? parolaSuresiz;

        // YARIM KALAN OLUŞTURMA ÖKSÜZ HESAP BIRAKIYORDU (19.08.2026, ölçüldü).
        //
        // Ölçülen zincir: yetki kuralı kaldırma sessizce başarısız olduğu için (bkz.
        // RemoveUserAuthorizationSnippet) ConfigureUserHomeAsync "yinelenen koleksiyon girişi" ile
        // 500 attı. Ama `net user /add` ÇOKTAN çalışmıştı: `net user <ad>` hesabın VAR olduğunu
        // gösteriyor, `GET /ftp/users` onu LİSTELEMİYORDU (kayıt aşağıda, hatadan SONRA yazılıyor).
        // Yani geriye parolası operatörün belirlediği, panelin hiç görmediği, panelden silinemeyen
        // bir Windows yerel hesabı kalıyordu — üstelik ana dizinde ACL'i duruyordu.
        //
        // Kök neden düzeltildi ama tek başına yetmez: aradaki HER adım (ACL, junction, IIS yazımı,
        // disk dolu, IIS kilidi) başarısız olabilir ve hepsi aynı öksüzü doğurur. Bu yüzden hata
        // yolu artık YAPILANLARI GERİ ALIYOR: oluşturma ya tamamlanır ya da sunucuda iz bırakmaz —
        // "SALDIRI_reddedilen_istek_sunucuda_HICBIR_iz_birakmaz" testinin koruduğu ilkenin aynısı,
        // bu kez reddedilen değil YARIM KALAN istek için.
        // A rejected retry can refer to a working account whose ACL/home was never touched.
        // Cleanup must not remove that existing access merely because an approval expired.
        var permissionsMayHaveChanged = false;
        try
        {
            await Approve(cancellationToken);
            parolaSuresiz = await EnsurePasswordNeverExpiresAsync(userName, cancellationToken);
            await Approve(cancellationToken);
            permissionsMayHaveChanged = true;
            var aclResult = await RunCommandAsync(
                "icacls.exe",
                $"\"{homePath}\" /grant \"{userName}:(OI)(CI)M\" /T /C",
                cancellationToken: cancellationToken);
            if (!aclResult.Success)
            {
                throw new AppException("ftp.acl_failed", 500, 500, BuildCommandDetails(aclResult));
            }

            await Approve(cancellationToken);
            await ConfigureUserHomeAsync(userName, homePath, cancellationToken);

            var record = new FtpUserState
            {
                UserName = userName,
                SiteName = request.SiteName.Trim(),
                HomePath = homePath,
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow
            };

            // KAYIT ATOMİK YAZILMALI. Kilitsiz oku-değiştir-yaz burada ÖKSÜZ WINDOWS HESABI bırakıyordu:
            // gerçek hesap yukarıda `net user /add` ile çoktan açılıyor, panel kaydı ancak burada
            // yazılıyor. Eşzamanlı bir silme işlemi bu yazımı ezerse hesap Windows'ta parolasıyla
            // var olmaya devam eder ama panel onu HİÇ görmez: listelenemez, silinemez, denetlenemez —
            // ve site klasöründeki ACL'i durur.
            await fileStore.UpdateAsync(
                StatePath,
                Array.Empty<FtpUserState>(),
                mevcut =>
                {
                    var liste = mevcut.ToList();
                    var index = liste.FindIndex(item => string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        liste[index] = record;
                    }
                    else
                    {
                        liste.Add(record);
                    }

                    return liste.ToArray();
                },
                Approve, cancellationToken);
            return new FtpUserDto
            {
                UserName = record.UserName,
                SiteName = record.SiteName,
                HomePath = record.HomePath,
                CreatedAtUtc = record.CreatedAtUtc,
                IsEnabled = true,
                ExistsOnSystem = true,
                PasswordNeverExpires = parolaSuresiz,
                ServerAddress = Environment.MachineName,
                Port = FtpPort
            };
        }
        catch (AppException error)
        {
            var recovery = hesabiBuCagriActi || permissionsMayHaveChanged
                ? await RollbackCreateAsync(userName, homePath, hesabiBuCagriActi) : string.Empty;
            throw new AppException(error.MessageKey, error.Code, error.HttpStatusCode, error.Details + recovery);
        }
        catch
        {
            if (hesabiBuCagriActi || permissionsMayHaveChanged)
                await RollbackCreateAsync(userName, homePath, hesabiBuCagriActi);
            throw;
        }
    }

    /// <summary>
    /// Yarım kalan bir oluşturmanın SUNUCUDAKİ izlerini geri alır; sonucu hata metnine eklenmek
    /// üzere metin olarak döner (boş dize = geri alacak bir şey yoktu).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>YALNIZ BU ÇAĞRININ YAPTIĞI GERİ ALINIR.</b> Windows hesabı ancak
    /// <paramref name="hesabiBuCagriActi"/> ise silinir. Aksi hâlde meşru "kendi hesabımı yeniden
    /// oluştur" akışı (<see cref="FtpUserNameClaim.KendiKaydi"/>) bir ara adımda takıldığında
    /// müşterinin ÇALIŞAN hesabını silerdik — hatayı düzeltirken ondan beter bir yıkım. Hesap zaten
    /// varken de öksüz doğmaz: hesap bizden önce oradaydı.
    /// </para>
    /// <para>
    /// <b>ANA DİZİNE DOKUNULMAZ.</b> <c>homePath</c> müşterinin SİTE KÖKÜDÜR; burada silinecek tek
    /// şey verilen ACL'dir (<c>icacls /remove:g</c> yalnız o hesabın izin girdilerini kaldırır),
    /// dosyalar değil. Silinen hesabın SID'i ACL'de "hesap bilinmiyor" olarak asılı kalmasın diye
    /// bu adım hesap silmeden ÖNCE yapılır.
    /// </para>
    /// <para>
    /// <b>İPTAL BELİRTECİ KULLANILMAZ (<see cref="CancellationToken.None"/>).</b> Geri almanın en
    /// çok gerektiği durumlardan biri isteğin iptal edilmesidir; iptal edilmiş bir belirteçle
    /// temizlik çalıştırmak, önlemeye çalıştığımız öksüz hesabı garanti ederdi.
    /// </para>
    /// <para>
    /// Adımlar EN İYİ ÇABA'dır ve biri diğerini engellemez: temizlik kendisi patlayıp asıl hata
    /// mesajını gölgelemesin. Ama sessiz de değil — her başarısızlık döndürülen nota yazılır.
    /// </para>
    /// </remarks>
    // internal: testler bu metodu DOGRUDAN kosturur. Tam CreateFtpUserAsync yolundan test etmek
    // MUMKUN DEGIL — o yol EnsureFtpInfrastructureAsync'i cagirir, yani yukseltilmis bir makinede
    // GERCEK "AdaPanel FTP" sitesini yeniden yapilandirip FTPSVC'yi yeniden baslatirdi. Bir testin
    // canli FTP sunucusunu yeniden baslatmasi kabul edilemez; geri alma davranisi burada olculur.
    internal async Task<string> RollbackCreateAsync(string userName, string homePath, bool hesabiBuCagriActi)
    {
        var notlar = new List<string>();

        async Task DeneAsync(string ad, Func<Task> is_)
        {
            try
            {
                await is_();
            }
            catch (Exception istisna)
            {
                notlar.Add($"{ad}: {istisna.Message}");
            }
        }

        await DeneAsync("acl-geri-alma", async () =>
        {
            var sonuc = await RunCommandAsync(
                "icacls.exe",
                $"\"{homePath}\" /remove:g \"{userName}\" /T /C",
                cancellationToken: CancellationToken.None);
            if (!sonuc.Success)
            {
                notlar.Add($"acl-geri-alma: ExitCode={sonuc.ExitCode}");
            }
        });

        // Yetki kuralı + junction: ikisi de bu akışta oluşturulmuş olabilir ve ikisi de idempotent
        // kaldırılır (yoksa sorun değil). Kural kalırsa ad bir daha kullanılamaz — düzeltilen
        // hatanın ta kendisi.
        await DeneAsync("yetki-junction-geri-alma", () => RemoveUserAuthorizationAsync(userName, CancellationToken.None));

        if (hesabiBuCagriActi)
        {
            await DeneAsync("hesap-silme", async () =>
            {
                var sonuc = await RunCommandAsync("net.exe", $"user \"{userName}\" /delete", cancellationToken: CancellationToken.None);

                // "Komut hata vermedi" yetmez: hesabın GERÇEKTEN gittiği ölçülür.
                if (await UserExistsAsync(userName, CancellationToken.None))
                {
                    notlar.Add($"hesap-silme: hesap HALA DURUYOR (ExitCode={sonuc.ExitCode}) — '{userName}' elle silinmeli.");
                }
            });
        }

        return notlar.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}GeriAlmaUyarilari:{Environment.NewLine}{string.Join(Environment.NewLine, notlar)}";
    }

    public Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default) =>
        SetFtpPasswordCoreAsync(request, null, cancellationToken);

    public Task SetFtpPasswordAsync(SetFtpPasswordRequest request,
        Func<FtpUserDto?, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        SetFtpPasswordCoreAsync(request, beforeMutation, cancellationToken);

    private async Task SetFtpPasswordCoreAsync(SetFtpPasswordRequest request,
        Func<FtpUserDto?, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        request = new SetFtpPasswordRequest { UserName = request.UserName, Password = request.Password };
        var userName = NormalizeUserName(request.UserName);
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Trim().Length < 8)
        {
            throw new AppException("ftp.password_too_short", 400, 400);
        }

        EnsureCommandSafePassword(request.Password);
        var expected = await ReadAccountAsync(userName, cancellationToken);
        using var accountLease = await FtpAccountLifecycle.EnterAsync(userName, cancellationToken);
        async Task Approve(CancellationToken token)
        {
            await EnsureAccountUnchangedAsync(userName, expected, token);
            if (beforeMutation is not null) await beforeMutation(ToAccountDto(expected), token);
            token.ThrowIfCancellationRequested();
        }
        await Approve(cancellationToken);

        // PANELİN YÖNETMEDİĞİ HESABIN PAROLASI DEĞİŞTİRİLEMEZ (K6 zincirinin son halkası).
        //
        // Oluşturma yolu artık ad iddiasını denetliyor; bu ikinci kapı savunma derinliği: kayıt
        // başka bir yoldan (elle düzenlenmiş ftp-users.json, gelecekteki bir çağıran) kirletilse
        // bile panel yalnız KENDİ açtığı hesapların parolasını değiştirir. `net user <ad> <parola>`
        // ayrım yapmaz — Administrator da bir yerel hesaptır.
        //
        // Kayıt kontrolü Windows sorgusundan ÖNCE yapılır ve aynı "bulunamadı" hatası döner:
        // aksi hâlde yanıt farkı, sunucuda hangi yerel hesapların VAR olduğunu sızdıran bir
        // numaralandırma aracına dönüşürdü.
        var kayitlar = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken);
        if (!kayitlar.Any(item => string.Equals(item.UserName.Trim(), userName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException("ftp.user_not_found", 404, 404);
        }

        if (!await UserExistsAsync(userName, cancellationToken))
        {
            throw new AppException("ftp.user_not_found", 404, 404);
        }

        await Approve(cancellationToken);

        // net user <ad> <parola> (/add YOK) parolayı değiştirir.
        var result = await RunCommandAsync(
            "net.exe",
            $"user \"{userName}\" \"{request.Password}\" /y",
            cancellationToken: cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.password_change_failed", 500, 500, BuildCommandDetails(result));
        }

        // Parola değişimi sayacı sıfırlar; bayrak burada da teyit edilir. Bu ayrıca bu düzeltmeden
        // ÖNCE açılmış (süresi dolan) hesapların kendiliğinden iyileşme yoludur.
        await Approve(cancellationToken);
        await EnsurePasswordNeverExpiresAsync(userName, cancellationToken);
    }

    public Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default) =>
        DeleteFtpUserCoreAsync(userName, null, cancellationToken);

    public Task DeleteFtpUserAsync(string userName,
        Func<FtpUserDto?, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        DeleteFtpUserCoreAsync(userName, beforeMutation, cancellationToken);

    private async Task DeleteFtpUserCoreAsync(string userName,
        Func<FtpUserDto?, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var normalizedUserName = NormalizeUserName(userName);
        var expected = await ReadAccountAsync(normalizedUserName, cancellationToken);
        using var accountLease = await FtpAccountLifecycle.EnterAsync(normalizedUserName, cancellationToken);
        async Task Approve(CancellationToken token)
        {
            await EnsureAccountUnchangedAsync(normalizedUserName, expected, token);
            if (beforeMutation is not null) await beforeMutation(ToAccountDto(expected), token);
            token.ThrowIfCancellationRequested();
        }
        await Approve(cancellationToken);
        var exists = await UserExistsAsync(normalizedUserName, cancellationToken);
        await Approve(cancellationToken);
        await RemoveUserAuthorizationAsync(normalizedUserName, cancellationToken);

        if (exists)
        {
            await Approve(cancellationToken);
            var result = await RunCommandAsync("net.exe", $"user \"{normalizedUserName}\" /delete", cancellationToken: cancellationToken);
            if (!result.Success)
            {
                // net.exe başarısızsa (lokalizasyon/kilit) PowerShell Remove-LocalUser ile dene.
                var psScript = $"$ErrorActionPreference='SilentlyContinue'; " +
                    $"Remove-LocalUser -Name '{normalizedUserName.Replace("'", "''")}'";
                await Approve(cancellationToken);
                await RunScriptAsync(psScript, cancellationToken);

                // Yine de duruyorsa gerçek hata; yoksa silinmiş kabul et (idempotent).
                if (await UserExistsAsync(normalizedUserName, cancellationToken))
                {
                    throw new AppException("ftp.user_delete_failed", 500, 500, BuildCommandDetails(result));
                }
            }
        }

        // Silme de atomik: aksi hâlde eşzamanlı bir oluşturma bu silmeyi ezip kaldırılmış hesabı
        // panelde geri getirirdi (gerekçe: CreateFtpUserAsync).
        await fileStore.UpdateAsync(
            StatePath,
            Array.Empty<FtpUserState>(),
            mevcut => mevcut
                .Where(item => !string.Equals(item.UserName, normalizedUserName, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            Approve, cancellationToken);
    }

    private async Task<FtpUserState?> ReadAccountAsync(string name, CancellationToken token) =>
        (await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), token))
            .SingleOrDefault(item => string.Equals(item.UserName.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private async Task EnsureAccountUnchangedAsync(string name, FtpUserState? expected, CancellationToken token)
    {
        var current = await ReadAccountAsync(name, token);
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(expected))
            throw new AppException("ftp.user_changed", 409, 409);
        token.ThrowIfCancellationRequested();
    }

    private static FtpUserDto? ToAccountDto(FtpUserState? value) => value is null ? null : new FtpUserDto
    { UserName = value.UserName, SiteName = value.SiteName, HomePath = value.HomePath, CreatedAtUtc = value.CreatedAtUtc, IsEnabled = value.IsEnabled };

    /// <summary>
    /// Kayıtlı FTP kullanıcılarının EKSİK ev bağlantısını tamamlar. Kaç tanesinin onarıldığını döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IIS'in hangi klasör düzenini aradığı makineye göre değişiyor, o yüzden panel ikisini birden
    /// kurar (gerekçe <see cref="HomeLinkPaths"/>'te). Ama bu, düzeltme YAZILDIKTAN SONRA açılan
    /// kullanıcılar için geçerliydi; makinede zaten duran hesaplar tek yollu kaldığı için
    /// güncellemeden sonra da giriş yapamıyorlardı.
    /// </para>
    /// <para>
    /// <b>Yalnızca EKLER, hiçbir şey silmez ya da değiştirmez.</b> Var olan bir bağlantıya
    /// dokunulmaz; eksik olan, VAR OLAN bağlantının hedefine kurulur. Hedef okunamıyorsa ya da
    /// yoksa o kullanıcı atlanır — kırık bir hedefe ikinci bir bağlantı kurmak, bozukluğu
    /// çoğaltmak olurdu. Panel zaten "ev klasörü yok / hedefi silinmiş" diye ölçüp gösteriyor.
    /// </para>
    /// <para>
    /// Junction oluşturma <c>mklink /J</c> ile yapılır: yönetici hakkı ister ama sembolik bağ gibi
    /// ayrı bir ilke ayarı GEREKTİRMEZ; panel zaten LocalSystem olarak koşar.
    /// </para>
    /// </remarks>
    internal async Task<int> HealHomeLinksAsync(CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeMutation = null)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken);
        var onarilan = 0;

        foreach (var record in records)
        {
            var yollar = HomeLinkPaths(record.UserName);

            string? hedef = null;
            foreach (var yol in yollar)
            {
                try
                {
                    if (!Directory.Exists(yol))
                    {
                        continue;
                    }

                    // Düz klasörse hedefi kendisidir; junction ise gösterdiği yer.
                    hedef = Directory.ResolveLinkTarget(yol, returnFinalTarget: true)?.FullName ?? yol;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Okunamayan yol "yok" DEĞİLDİR; onarmaya da çalışmayız.
                    hedef = null;
                }
            }

            if (hedef is null || !Directory.Exists(hedef))
            {
                continue;
            }

            foreach (var eksik in yollar.Where(yol => !Directory.Exists(yol)))
            {
                var ust = Path.GetDirectoryName(eksik);
                if (string.IsNullOrEmpty(ust))
                {
                    continue;
                }

                if (beforeMutation is not null) await beforeMutation(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(ust);

                var sonuc = await RunCommandAsync(
                    "cmd.exe",
                    $"/c mklink /J \"{eksik}\" \"{hedef}\"",
                    cancellationToken: cancellationToken);

                // Sonuç ÖLÇÜLÜR, çıkış koduna güvenilmez: bu deponun tekrarlanan dersi.
                if (sonuc.Success && Directory.Exists(eksik))
                {
                    onarilan++;
                }
            }
        }

        return onarilan;
    }

    private Task<FtpTlsMeasurement> EnsureFtpInfrastructureAsync(bool forceReconfigure, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeMutation = null) =>
        InfrastructureLock.RunExclusiveAsync(InfrastructureLockKey,
            () => EnsureFtpInfrastructureCoreAsync(forceReconfigure, cancellationToken, beforeMutation), cancellationToken);

    private static async Task ApproveInfrastructureAsync(Func<CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (beforeMutation is not null) await beforeMutation(token);
        token.ThrowIfCancellationRequested();
    }

    private Task<FtpInfraState> PublishInfrastructureReadyAsync(Func<CancellationToken, Task>? beforeMutation, CancellationToken token) =>
        fileStore.UpdateAsync(InfraStatePath, new FtpInfraState(), _ => new FtpInfraState { Configured = true },
            ct => ApproveInfrastructureAsync(beforeMutation, ct), token);

    private async Task<FtpTlsMeasurement> EnsureFtpInfrastructureCoreAsync(bool forceReconfigure, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeMutation = null, bool? requestedPolicy = null, bool publishReady = true)
    {
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        // IIS FTP rolü kurulu değilse New-WebFtpSite ve ftpServer config bölümleri bulunmaz → script
        // "ftp.site_configuration_failed" ile patlar (canlı VPS bulgusu). Classic ASP/PHP gibi OTOMATİK KUR (atlama yok).
        await EnsureFtpFeatureInstalledAsync(cancellationToken, beforeMutation);

        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);

        var ftpRoot = Path.Combine(dataRoot, "ftp-root");
        Directory.CreateDirectory(Path.Combine(ftpRoot, "LocalUser"));

        // ESKI KULLANICILAR DA IYILESTIRILIR -- yoksa duzeltme YARIM kalirdi.
        //
        // OLCULDU (kurulum sonrasi dogrulama betigi, 0.8.4): ev klasorunu iki yola birden kurma
        // duzeltmesi yalnizca YENI olusturulan kullanicilara uygulaniyordu. Guncellemeden sonra
        // makinede duran hesaplar hala tek yolluydu, yani FTP'leri hala kirikti. Bir hatayi
        // duzeltip mevcut kurulumlari iyilestirmemek, hatanin yarisini birakmaktir: musteri
        // "guncelledim ama duzelmedi" der ve hakli olur.
        //
        // Bu cagri BILEREK hizli yolun USTUNDE: altyapi zaten hazir oldugunda da calisir, cunku
        // iyilestirilecek sey altyapi degil KULLANICI kayitlaridir.
        await HealHomeLinksAsync(cancellationToken, beforeMutation);

        // HIZLI YOL: altyapı bir kez tam kurulduysa (bayrak) ve FTPSVC çalışıyorsa ağır yeniden-kurulumu + FTPSVC
        // yeniden başlatmayı ATLA → 2. ve sonraki FTP kullanıcısı eklemesi saniyelerce beklemeden, anında olur.
        // (SSL politikası değişimi gibi gerçekten gereken durumlar force=true ile tam yeniden kurar.)
        if (!forceReconfigure && await IsFtpInfrastructureReadyAsync(cancellationToken))
        {
            await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
            // Hızlı yolda TLS politikası YENİDEN YAZILMADI, dolayısıyla ÖLÇÜLMEDİ de. Burada
            // "uygulandı" demek, hiç bakmadan iddia etmek olurdu.
            return FtpTlsMeasurement.Olculemedi("FTP altyapisi hazir kabul edildi; TLS politikasi bu cagride yazilmadi.");
        }

        // Pasif (PASV) mod: sunucu, veri kanalı için istemciye bir IP bildirir. Bu IP yanlış/iç IP olursa
        // istemci bağlanır, giriş yapar ama DİZİN LİSTESİ/transfer TAKILIR. Panelde tanımlı public IPv4'ü
        // FTP sitesine externalIp4Address olarak yazıyoruz; tanımlı değilse IIS kendi IP'sini kullanır.
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        var externalIp = preferences.ServerAddress?.Trim() ?? string.Empty;
        var externalIpLine = System.Net.IPAddress.TryParse(externalIp, out var parsedIp)
                && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? $"Set-ItemProperty \"IIS:\\Sites\\$siteName\" -Name ftpServer.firewallSupport.externalIp4Address -Value '{EscapePowerShellLiteral(externalIp)}'"
            : "# sunucu IPv4 adresi panelde tanimli degil; pasif mod sunucunun kendi IP'sini kullanir";

        // M5 (profesyonel paneller gibi opsiyonel): FTPS zorunlu mu? 0=SslAllow (düz+FTPS, varsayılan), 1=SslRequire (yalnız FTPS).
        var requireSsl = requestedPolicy ?? await GetRequireSslAsync(cancellationToken);
        var policy = requireSsl ? 1 : 0;
        var policyName = requireSsl ? "SslRequire" : "SslAllow";

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module WebAdministration -ErrorAction Stop
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $root = '{{EscapePowerShellLiteral(ftpRoot)}}'
            if (-not (Test-Path "IIS:\Sites\$siteName")) {
              New-WebFtpSite -Name $siteName -Port {{FtpPort}} -PhysicalPath $root -Force | Out-Null
            }
            Set-ItemProperty "IIS:\Sites\$siteName" -Name serverAutoStart -Value $true
            # KRITIK KOK NEDEN (2026-06-21): FTP per-site ayarlari Set-WebConfigurationProperty -Location ile
            # YAZILAMAZ. ftpServer config <site> etiketinin ICINE gomulu; -Location ise <location> bloklarina
            # bakar -> sessizce "configuration object not found at location" UYARISI verip HICBIR SEYI
            # uygulamaz (script yine basarili doner). Sonuc: site IIS varsayilani SslRequire + sertifikasiz +
            # basicAuth kapali'da kalir = "534 Policy requires SSL" KILIDI. Dogru yol: IIS provider noktali
            # ozellik yolu (serverAutoStart'in calismasi gibi). Asagidaki TUM yazimlar provider yolu kullanir.
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.authentication.anonymousAuthentication.enabled -Value $false
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.authentication.basicAuthentication.enabled -Value $true
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.userIsolation.mode -Value 3
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.controlChannelPolicy -Value {{policy}}
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.dataChannelPolicy -Value {{policy}}
            # FTPS sertifikasi: yoksa self-signed olustur ve bagla. serverCertHash bos olursa IIS "AUTH TLS"i
            # reddeder; bagliyken FTPS calisir. controlChannelPolicy=0 ile duz baglanti da kabul -> kimse kilitlenmez.
            $ftpsCert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq 'AdaPanel FTPS' } | Select-Object -First 1
            if (-not $ftpsCert) {
              try { $ftpsCert = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME -FriendlyName 'AdaPanel FTPS' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) } catch { $ftpsCert = $null }
            }
            if ($ftpsCert) {
              try {
                Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.serverCertHash -Value $ftpsCert.Thumbprint
                Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.ssl128 -Value $true
              } catch { }
            }
            # Pasif port araligi SUNUCU seviyesinde (location'siz) -> Set-WebConfigurationProperty dogru calisir.
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.ftpServer/firewallSupport' -Name lowDataChannelPort -Value {{PassivePortStart}}
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.ftpServer/firewallSupport' -Name highDataChannelPort -Value {{PassivePortEnd}}
            {{externalIpLine}}
            Start-WebSite -Name $siteName -ErrorAction Stop
            # Restart failure must not be reported as a verified TLS application.
            Restart-Service FTPSVC -Force -ErrorAction Stop
            if (-not (Get-NetFirewallRule -DisplayName 'AdaPanel FTP Control' -ErrorAction SilentlyContinue)) {
              New-NetFirewallRule -DisplayName 'AdaPanel FTP Control' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {{FtpPort}} -Profile Any | Out-Null
            }
            if (-not (Get-NetFirewallRule -DisplayName 'AdaPanel FTP Passive' -ErrorAction SilentlyContinue)) {
              New-NetFirewallRule -DisplayName 'AdaPanel FTP Passive' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {{PassivePortStart}}-{{PassivePortEnd}} -Profile Any | Out-Null
            }
            # SESSIZ BASARISIZLIK BIR DAHA OLMASIN: ayarin gercekten uygulandigini geri-okuyup dogrula.
            # NOT: bu geri-okuma yalnız OLCER, ASLA throw etmez. Bazı IIS sürümlerinde Get-ItemProperty
            # değeri farklı tipte döndürdüğü için (.Value boş/enum-adı) eski throw, Set basarili olsa bile
            # FTP'yi tümden bloke ediyordu (yanlis-pozitif). Set yukarida calisti; dogrulama OLCUM amacli.
            #
            # SONUC ARTIK ATILMIYOR: asagidaki ADAPANEL-FTP-TLS damgasi C# tarafinda okunur
            # (FtpTlsMeasurement) ve kullaniciya soylenen cumleyi O belirler. Eskiden burada yalniz
            # Write-Warning vardi, C# tarafi ciktiya HIC bakmiyordu -- yani olcum yapilip atiliyordu
            # ve panel ISTENEN bayragi "FTPS artik zorunlu" diye gosteriyordu.
            #
            # Deger okunamazsa BOS birakilir: bos = "olculemedi", "uygulanmadi" DEGIL.
            function Oku-FtpAyari($ad) {
              try {
                $p = Get-ItemProperty "IIS:\Sites\$siteName" -Name $ad -ErrorAction Stop
                if ($null -eq $p) { return '' }
                if ($p -is [int] -or $p -is [string]) { return "$p" }
                if ($null -ne $p.Value -and "$($p.Value)" -ne '') { return "$($p.Value)" }
                return ''
              } catch { return '' }
            }
            $okunanControl = Oku-FtpAyari 'ftpServer.security.ssl.controlChannelPolicy'
            $okunanData = Oku-FtpAyari 'ftpServer.security.ssl.dataChannelPolicy'
            # Sertifika AYRI olculur: controlChannelPolicy=1 iken serverCertHash bossa IIS "AUTH TLS"i
            # reddeder -> duz baglanti da yasak, TLS de imkansiz = FTP TUMDEN kapanir. Yukaridaki
            # sertifika baglama `catch { }` icinde oldugu icin bu durum SESSIZCE olusabilir.
            #
            # A successful neighboring property read does not prove the certificate read succeeded.
            $okunanCert = ''
            try {
              $certProperty = Get-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.serverCertHash -ErrorAction Stop
              if ($certProperty -is [string]) {
                $okunanCert = if ($certProperty.Trim() -eq '') { 'yok' } else { $certProperty }
              } elseif ($null -ne $certProperty -and $null -ne $certProperty.PSObject.Properties['Value']) {
                $certValue = [string]$certProperty.Value
                $okunanCert = if ($certValue.Trim() -eq '') { 'yok' } else { $certValue }
              }
            } catch { $okunanCert = '' }
            if ($okunanControl -ne '{{policy}}' -and $okunanControl -ne '{{policyName}}') { Write-Warning "FTP SSL ayari dogrulanamadi (controlChannelPolicy=$okunanControl, beklenen={{policy}})." }
            [Console]::Out.WriteLine('ADAPANEL-FTP-TLS=' + $okunanControl + '|' + $okunanData + '|' + $okunanCert)
            """;

        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        // Once a new native configuration starts, the previous ready marker cannot authorize a
        // later fast path if application fails or its result is unknown. Keep the saved policy.
        var previous = await fileStore.ReadAsync(InfraStatePath, new FtpInfraState(), cancellationToken);
        if (previous.Configured)
            await fileStore.UpdateAsync(InfraStatePath, new FtpInfraState(), _ => new FtpInfraState(),
                token => ApproveInfrastructureAsync(beforeMutation, token), cancellationToken);
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        var result = await RunScriptAsync(script, cancellationToken);
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.site_configuration_failed", 500, 500, BuildCommandDetails(result));
        }

        // Completion alone does not prove the requested policy. Critical native commands stop
        // on error; the final independent read still decides whether policy and certificate agree.
        var olcum = FtpTlsMeasurement.Olc(requireSsl, result.StandardOutput);
        if (olcum.Verified == true && publishReady)
            await PublishInfrastructureReadyAsync(beforeMutation, cancellationToken);
        if (olcum.Failed)
        {
            logger?.LogWarning(
                "FTP TLS politikasi IIS'e uygulanmadi (istenen zorunluluk: {RequireSsl}). {Detay}",
                requireSsl,
                olcum.Detail);
        }

        return olcum;
    }

    // Altyapı hazır mı (hızlı kontrol): daha önce tam kuruldu (bayrak) VE FTPSVC çalışıyor mu. FTPSVC kontrolü
    // Get-Service ile yapılır (yavaş 'Import-Module WebAdministration' YOK → sub-saniye). Biri bile sağlanmazsa false →
    // çağıran tam kurulumu çalıştırır (kendini onarır). Böylece ağır betik + servis restart yalnız gerektiğinde çalışır.
    private async Task<bool> IsFtpInfrastructureReadyAsync(CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync(InfraStatePath, new FtpInfraState(), cancellationToken);
        if (!state.Configured)
        {
            return false;
        }

        var probe = await RunScriptAsync(
            "if ((Get-Service FTPSVC -ErrorAction SilentlyContinue).Status -eq 'Running') { 'up' } else { 'down' }",
            cancellationToken);
        return probe.Success && probe.StandardOutput.Contains("up", StringComparison.OrdinalIgnoreCase);
    }

    // IIS FTP sunucu rolü (FTPSVC servisi) yoksa OTOMATİK KURAR. Windows Server: Web-Ftp-Server (+ yönetim
    // araçları); Windows Client (10/11): IIS-FTPServer (-All ile alt özellik IIS-FTPSvc dahil). PHP/Classic ASP
    // on-demand kurulumuyla aynı desen — "atlama yok". Kurulamazsa net hata fırlatır (sessiz başarısızlık yok).
    private async Task EnsureFtpFeatureInstalledAsync(CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeMutation = null)
    {
        const string probe = "if (Get-Service FTPSVC -ErrorAction SilentlyContinue) { 'present' } else { 'absent' }";

        var check = await RunScriptAsync(probe, cancellationToken);
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        if (check.Success && check.StandardOutput.Contains("present", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        const string script =
            "if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) { " +
            "Install-WindowsFeature -Name Web-Ftp-Server -IncludeManagementTools -ErrorAction Stop | Out-Null } " +
            "else { Enable-WindowsOptionalFeature -Online -FeatureName IIS-FTPServer -All -NoRestart -ErrorAction Stop | Out-Null }";
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var install = await RunScriptAsync(script, cancellationToken);
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);

        var verify = await RunScriptAsync(probe, cancellationToken);
        await ApproveInfrastructureAsync(beforeMutation, cancellationToken);
        if (!(verify.Success && verify.StandardOutput.Contains("present", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException(
                "ftp.feature_install_failed",
                503,
                503,
                "IIS FTP rolu (Web-Ftp-Server/IIS-FTPServer) otomatik kurulamadi; FTPSVC servisi bulunamadi. "
                + BuildCommandDetails(install));
        }
    }

    /// <summary>
    /// <c>$userRoot</c> klasör bağlantısını GÜVENLE kaldıran PowerShell parçası.
    /// <para>
    /// NEDEN ÖZEL BİR YOL: <c>$userRoot</c> bir junction'dır ve HEDEFİ müşterinin site köküdür
    /// (bkz. <see cref="ResolveHomePathAsync"/>). Windows PowerShell 5.1'in bilinen reparse-point
    /// hatasında <c>Remove-Item -Recurse</c> bağlantıyı TAKİP EDER ve hedefteki dosyaları siler —
    /// yani bir FTP kullanıcısını silmek ya da ana dizinini değiştirmek, müşterinin bütün site
    /// dosyalarını götürebilir. Yedekten dönmeden geri alınamaz.
    /// </para>
    /// <para>
    /// <c>[IO.Directory]::Delete</c> bağlantıyı takip etmez; yalnızca bağlantının kendisini kaldırır.
    /// Bu yüzden önce "bu bir bağlantı mı" diye bakılır. Windows sürümünden bağımsız güvenlidir,
    /// dolayısıyla hatanın giderilmiş olduğu sürümlerde de doğru davranır.
    /// </para>
    /// </summary>
    // internal: testler KOPYASINI degil BU dizeyi calistirir; kopya test etmek hicbir sey kanitlamaz.
    internal const string RemoveUserRootSnippet = """
        if (Test-Path -LiteralPath $userRoot) {
          $adaOge = Get-Item -LiteralPath $userRoot -Force
          if ($adaOge.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            [IO.Directory]::Delete($userRoot)
          } else {
            Remove-Item -LiteralPath $userRoot -Force -Recurse
          }
        }
        """;

    /// <summary>
    /// Kullanıcının FTP yetki kuralını (<c>&lt;add accessType="Allow" users="…"/&gt;</c>) kaldıran
    /// PowerShell parçası. <c>$siteName</c> ve <c>$userName</c> tanımlı olmalıdır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KÖK NEDEN (19.08.2026, bu makinede ölçüldü): burada <c>Clear-WebConfiguration … -AtElement
    /// @{users=$userName} -ErrorAction SilentlyContinue</c> yazıyordu. <b>Bu cmdlet'in
    /// <c>-AtElement</c> parametresi YOKTUR.</b> Ölçüm — panelin kendi terminal ucundan,
    /// <c>-ErrorAction Stop</c> ile: <c>ParameterBindingException :: A parameter cannot be found
    /// that matches parameter name 'AtElement'</c>. Gerçek parametre listesi:
    /// <c>Clr, Force, Location, Filter, PSPath</c> (+ ortak parametreler). <c>-AtElement</c>'i
    /// destekleyen cmdlet <c>Remove-WebConfigurationProperty</c>'dir
    /// (<c>Name, Clr, AtElement, AtIndex, AtName, Force, Location, Filter, PSPath</c>).
    /// </para>
    /// <para>
    /// <c>-ErrorAction SilentlyContinue</c> bağlama hatasını YUTUYOR ve betik sıfırla çıkıyordu.
    /// İki ağır sonucu ölçüldü:
    /// <list type="number">
    /// <item><b>Silinen kullanıcı bir daha oluşturulamıyordu.</b> Silme <c>200 ftp.user_deleted</c>
    /// dönüyor, Windows hesabı ve junction gerçekten gidiyor, ama <c>applicationHost.config</c>'de
    /// <c>Allow</c> kuralı KALIYORDU. Aynı adla yeniden oluşturma, sonraki satırdaki
    /// <c>Add-WebConfiguration</c>'da "yinelenen koleksiyon girişi" ile <c>500
    /// ftp.site_configuration_failed</c> veriyordu.</item>
    /// <item><b>Silme erişimi gerçekten iptal etmiyordu.</b> Aynı adlı bir yerel hesap başka bir
    /// yolla var olursa FTP kökündeki Read+Write yetkisi panel hiçbir şey yapmadan geri gelir;
    /// kurallar ayrıca sonsuza dek birikirdi.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Neden <c>-ErrorAction Stop</c> güvenli:</b> ölçüldü — kural ZATEN YOKKEN
    /// <c>Remove-WebConfigurationProperty</c> hata değil <b>uyarı</b> üretir ("… özelliği …
    /// dizininde bulunamadı") ve sıfırla çıkar. Yani çağrı idempotenttir; <c>Stop</c> yalnız
    /// GERÇEK arızayı yüzeye çıkarır. Eşleşme büyük/küçük harfe duyarsızdır (ölçüldü:
    /// <c>ADA_Olcum_ZZ</c> ile eklenen kayıt <c>ada_olcum_zz</c> ile kaldırıldı) — Windows yerel
    /// hesap adları da öyle olduğu için doğru davranış budur.
    /// </para>
    /// <para>
    /// <b>GERİ OKUMA ŞART:</b> "cmdlet şikâyet etmedi" ile "kural gitti" aynı şey değildir — bu
    /// hatanın tamamı zaten bu farktan doğdu. Kaldırma sonrası koleksiyon yeniden okunur ve kural
    /// hâlâ duruyorsa istisna fırlatılır, böylece sessiz başarısızlık bir daha mümkün olmaz.
    /// </para>
    /// </remarks>
    // internal: testler KOPYASINI degil BU dizeyi calistirir; kopya test etmek hicbir sey kanitlamaz.
    internal const string RemoveUserAuthorizationSnippet = """
        Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization' -Name '.' -AtElement @{users=$userName} -ErrorAction Stop
        $adaKalanKurallar = @((Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization').Collection | Where-Object { $_.users -eq $userName })
        if ($adaKalanKurallar.Count -gt 0) { throw "FTP yetki kurali kaldirilamadi (kural hala duruyor): $userName" }
        """;

    /*
     * EV KLASÖRÜ İKİ YOLA BİRDEN KURULUR — ve bu, canlı bir sunucuda ölçülerek bulundu.
     *
     * BELİRTİ: müşterinin FTP'si hiç çalışmıyordu. Her giriş
     * `530 User cannot log in, home directory inaccessible.` alıyordu. Kimlik doğrulama GEÇİYORDU
     * (yanlış parolada IIS bambaşka bir cevap verir), junction duruyordu, hedefi duruyordu, NTFS
     * izinleri yerindeydi, yetki kuralı vardı, izolasyon kipi doğruydu, hesap Users grubundaydı.
     * Panelin kontrol ettiği HER ŞEY doğruydu.
     *
     * ÖLÇÜM: IIS FTP günlüğündeki `sc-win32-status` = 3 (ERROR_PATH_NOT_FOUND). "Yasak" değil,
     * "bulamadım". Microsoft'un `<userIsolation>` belgesi ev klasörü hiyerarşisini şöyle tanımlar:
     *
     *     Yerel Windows hesaplari  ->  %FtpRoot%\LocalUser\%UserName%
     *     Windows ETKI ALANI hesaplari ->  %FtpRoot%\%UserDomain%\%UserName%
     *
     * Panel her zaman birincisini kuruyordu. Ama bu sunucuda IIS hesabı `VM-185-7-242-22\adapanel`
     * diye MAKİNE ADIYLA NİTELENMİŞ görüyor ve İKİNCİ satırı uyguluyor — yani `%UserDomain%` yerine
     * makine adını koyup `<ftp-kök>\VM-185-7-242-22\adapanel` klasörünü arıyor. Orada bir şey
     * olmadığı için yol bulunamıyor. KANIT: makine adlı yola aynı junction elle kurulduğu anda
     * giriş `230 User logged in.` oldu ve dizin listesi geldi; hiçbir izin, kip ya da hesap
     * ayarı değiştirilmeden.
     *
     * NEDEN "DOĞRUSUNU SEÇ" DEĞİL DE İKİSİ BİRDEN: IIS'in yerel hesabı hangi sınıfa koyacağı
     * makineye göre değişiyor ve panel bunu güvenilir biçimde ÖNCEDEN bilemez (aynı makinede
     * `Get-ItemProperty` ile okunan izolasyon kipi bile bazı IIS sürümlerinde boş dönüyor — bu
     * dosyanın başka bir yerinde de aynı ölçüm notu var). Bir junction disk yeri kaplamaz ve iki
     * yol da AYNI hedefi gösterir, yani ikisini birden kurmanın bedeli yok; birini tahmin etmenin
     * bedeli ise FTP'nin sessizce ölü kalmasıdır. Kiracı izolasyonu da bozulmaz: her iki yol da
     * yalnızca o kullanıcının kendi sitesine bakar.
     */
    internal IReadOnlyList<string> HomeLinkPaths(string userName)
    {
        var ftpRoot = Path.Combine(dataRoot, "ftp-root");

        return new[]
        {
            Path.Combine(ftpRoot, "LocalUser", userName),
            Path.Combine(ftpRoot, Environment.MachineName, userName)
        };
    }

    private async Task ConfigureUserHomeAsync(string userName, string homePath, CancellationToken cancellationToken)
    {
        var userRoots = HomeLinkPaths(userName);
        var rootLiterals = string.Join(",", userRoots.Select(yol => $"'{EscapePowerShellLiteral(yol)}'"));

        // KATI çalıştırma (RunPowerShellStrictAsync): bu betiğin ortasında patlayan bir ifade
        // eskiden çıkış koduna yansımıyordu — PowerShell'in çıkış kodu SON ifadenin sonucudur.
        // Yetki kuralı kaldırma yolu tam da böyle sessizce ölmüştü.
        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $userName = '{{EscapePowerShellLiteral(userName)}}'
            $homePath = '{{EscapePowerShellLiteral(homePath)}}'
            foreach ($userRoot in @({{rootLiterals}})) {
              $adaUst = Split-Path -Parent $userRoot
              if (-not (Test-Path -LiteralPath $adaUst)) { New-Item -ItemType Directory -Path $adaUst -Force | Out-Null }
            {{RemoveUserRootSnippet}}
              New-Item -ItemType Junction -Path $userRoot -Target $homePath | Out-Null
            }
            {{RemoveUserAuthorizationSnippet}}
            Add-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization' -Value @{accessType='Allow';users=$userName;permissions=3}
            """;

        var result = await RunStrictScriptAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.site_configuration_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    private async Task RemoveUserAuthorizationAsync(string userName, CancellationToken cancellationToken)
    {
        // SİLME DE HER İKİ YOLU birden temizler: kurulum ikisini birden açıyor (gerekçesi
        // HomeLinkPaths'te). Yalnız birini silmek, geride HEDEFSİZ bir junction bırakırdı ve o
        // artık bir sonraki okuyucuya "bu kullanıcının evi var" diye yalan söylerdi.
        var rootLiterals = string.Join(
            ",",
            HomeLinkPaths(userName).Select(yol => $"'{EscapePowerShellLiteral(yol)}'"));

        // Site varlık denetimi try/catch İÇİNDE: katı kipte ($ErrorActionPreference='Stop') IIS
        // sağlayıcısı hiç yüklenmemişse "IIS:\..." yolu DriveNotFound ile PATLAR ve IIS FTP rolü
        // olmayan bir makinede hesap silme tümden imkânsız hâle gelirdi. Site yoksa kaldıracak
        // kural da yoktur; junction temizliği yine de yapılır.
        var script = $$"""
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $userName = '{{EscapePowerShellLiteral(userName)}}'
            $adaSiteVar = $false
            try { $adaSiteVar = Test-Path "IIS:\Sites\$siteName" } catch { $adaSiteVar = $false }
            if ($adaSiteVar) {
            {{RemoveUserAuthorizationSnippet}}
            }
            foreach ($userRoot in @({{rootLiterals}})) {
            {{RemoveUserRootSnippet}}
            }
            """;

        var result = await RunStrictScriptAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.user_delete_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    /// <summary>Bir FTP kullanıcısının ev klasörü ZİNCİRİNİN ölçüm sonucu.</summary>
    /// <param name="LinkPath">IIS'in ev olarak baktığı yol.</param>
    /// <param name="LinkExists"><c>null</c> = ölçülemedi.</param>
    /// <param name="TargetExists"><c>null</c> = ölçülemedi ya da bağlantı yok.</param>
    internal readonly record struct FtpHomeProbe(string LinkPath, bool? LinkExists, bool? TargetExists);

    /// <summary>
    /// IIS FTP'nin bu kullanıcı için BAKTIĞI yolu ölçer — kayda değil DİSKE bakarak.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gerekçesi <c>FtpUserDto.HomeLinkPath</c> üzerinde yazılı: panel bugüne kadar yalnız Windows
    /// hesabının varlığını ölçüyordu ve kırık bir ev klasörünü "sağlam" diye gösteriyordu.
    /// </para>
    /// <para>
    /// <b>Junction hedefi AYRI ölçülür.</b> Bir site silindiğinde junction geride kalır:
    /// <c>Directory.Exists</c> bağlantının kendisi için <c>true</c> döner, ama gösterdiği yer
    /// yoktur ve IIS girişte "home directory inaccessible" der. Bu yüzden bağlantının varlığı ile
    /// hedefinin varlığı İKİ AYRI ölçümdür; birini ötekinin yerine saymak kırık bir kurulumu
    /// sağlam göstermek olurdu.
    /// </para>
    /// <para>
    /// <b>İstisna yutulmuyor, ÜÇÜNCÜ DEĞERE çevriliyor.</b> Yol okunamazsa (erişim reddi, G/Ç)
    /// sonuç <c>null</c>'dır — "yok" değil. Ölçemediğini "yok" diye raporlamak, panelin bu
    /// depodaki en pahalı hata sınıfıdır.
    /// </para>
    /// </remarks>
    internal FtpHomeProbe ProbeHome(string userName)
    {
        // HER İKİ YOL da ölçülür ve EN İYİSİ raporlanır. Gerekçe: IIS bu ikisinden HANGİSİNE
        // bakacağını makineye göre seçiyor (bkz. HomeLinkPaths). Tek yola bakan bir ölçüm, IIS'in
        // ötekini kullandığı bir sunucuda çalışan bir kurulumu "bozuk" diye gösterirdi — panelin
        // yalan söylemesinin sadece ters yönü. Kullanılabilir bir yol VARSA kurulum sağlamdır.
        var sonuclar = HomeLinkPaths(userName).Select(TekYoluOlc).ToArray();

        var saglam = sonuclar.FirstOrDefault(s => s.LinkExists == true && s.TargetExists == true);
        if (saglam.LinkPath is not null)
        {
            return saglam;
        }

        // Sağlam yol yok. Sırayla: hedefi ölçülüp YOK bulunan (en açıklayıcı sebep), sonra
        // ölçülemeyen, sonra ilk yol (hiçbiri yok).
        var hedefiYok = sonuclar.FirstOrDefault(s => s.LinkExists == true && s.TargetExists == false);
        if (hedefiYok.LinkPath is not null)
        {
            return hedefiYok;
        }

        var olculemeyen = sonuclar.FirstOrDefault(s => s.LinkExists is null);
        return olculemeyen.LinkPath is not null ? olculemeyen : sonuclar[0];
    }

    private static FtpHomeProbe TekYoluOlc(string linkPath)
    {
        try
        {
            if (!Directory.Exists(linkPath))
            {
                // Bağlantı yok: hedefi sorulamaz, o yüzden "yok" DEĞİL, sorulmadı (null).
                return new FtpHomeProbe(linkPath, false, null);
            }

            // `ResolveLinkTarget` düz klasörde `null` döner; o durumda hedef bağlantının kendisidir.
            var target = Directory.ResolveLinkTarget(linkPath, returnFinalTarget: true);
            if (target is null)
            {
                return new FtpHomeProbe(linkPath, true, true);
            }

            return new FtpHomeProbe(linkPath, true, target.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FtpHomeProbe(linkPath, null, null);
        }
    }

    private async Task<string> ResolveHomePathAsync(CreateFtpUserRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.HomePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.HomePath.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(request.SiteName))
        {
            var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
            return string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                ? site.PhysicalPath
                : site.ApplicationRootPath;
        }

        return Path.Combine(dataRoot, "ftp", request.UserName.Trim());
    }

    private async Task<bool> UserExistsAsync(string userName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint status;
        try
        {
            status = NativeAccountStatus is { } query
                ? await query(userName, cancellationToken)
                : FtpLocalAccountStatus.Read(userName, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            // Keep native diagnostics useful without echoing a command, account data, or secrets.
            throw new AppException("ftp.user_status_unknown", 503, 503, $"Local account lookup failed ({error.GetType().Name}).");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return status switch
        {
            FtpLocalAccountStatus.Success => true,
            FtpLocalAccountStatus.UserNotFound => false,
            _ => throw new AppException("ftp.user_status_unknown", 503, 503, $"NetUserGetInfo status={status}.")
        };
    }

    /// <summary>
    /// Bir FTP kullanıcı adının, istenen site adına göre sahiplik durumu.
    /// </summary>
    internal enum FtpUserNameClaim
    {
        /// <summary>Ad boşta: panelde kaydı yok, Windows'ta hesabı yok.</summary>
        Serbest,

        /// <summary>Panel kaydı var ve AYNI siteye ait — yeniden oluşturma/parola sıfırlama meşru.</summary>
        KendiKaydi,

        /// <summary>Panel kaydı var ama BAŞKA bir siteye ait — çapraz kiracı devralma.</summary>
        BaskaSitenin,

        /// <summary>Windows'ta hesap var ama panelin kaydı YOK — panelin açmadığı bir hesap.</summary>
        PanelDisi
    }

    /// <summary>
    /// FTP kullanıcı adı sahiplik hükmü. SAF fonksiyon: girdileri dışarıdan alır, yan etkisi yoktur,
    /// bu yüzden gerçek Windows hesabı açmadan tam doğruluk tablosuyla test edilebilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sıralama bilinçli: <b>önce panel kaydı</b>. Panel kaydı olan bir ad, hesabın Windows'ta o an
    /// var olup olmamasından bağımsız olarak panele aittir (kayıt varken hesap silinmiş olabilir —
    /// o zaman meşru sahibi hesabı yeniden açabilmeli). Kayıt yoksa soru Windows'a sorulur.
    /// </para>
    /// <para>
    /// Karşılaştırma <see cref="StringComparison.OrdinalIgnoreCase"/>: Windows yerel hesap adları
    /// büyük/küçük harfe duyarsızdır, yani "administrator" ile "Administrator" AYNI hesaptır.
    /// Duyarlı karşılaştırma denetimi harf değiştirerek atlatılabilir bir süse çevirirdi.
    /// </para>
    /// </remarks>
    internal static FtpUserNameClaim ClassifyUserNameClaim(
        string userName,
        string siteName,
        IEnumerable<(string UserName, string SiteName)> kayitlar,
        bool sistemdeVar)
    {
        var ad = (userName ?? string.Empty).Trim();
        var istenenSite = (siteName ?? string.Empty).Trim();

        foreach (var kayit in kayitlar)
        {
            if (!string.Equals((kayit.UserName ?? string.Empty).Trim(), ad, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return string.Equals((kayit.SiteName ?? string.Empty).Trim(), istenenSite, StringComparison.OrdinalIgnoreCase)
                ? FtpUserNameClaim.KendiKaydi
                : FtpUserNameClaim.BaskaSitenin;
        }

        return sistemdeVar ? FtpUserNameClaim.PanelDisi : FtpUserNameClaim.Serbest;
    }

    /// <summary>
    /// İddia edilen FTP kullanıcı adı başkasınınsa reddeder (K6). Gerekçe çağrı yerinde.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>GERİYE DÖNÜK UYUM (ölçülerek karar verildi):</b> mevcut kurulumlarda zaten açılmış FTP
    /// hesaplarının hepsinin <c>ftp-users.json</c>'da kaydı vardır — panel hesabı ancak kaydı da
    /// yazarak açar. Dolayısıyla bugün çalışan iki meşru akış aynen çalışmaya devam eder:
    /// yeni ad (<see cref="FtpUserNameClaim.Serbest"/>) ve kendi hesabını yeniden oluşturma
    /// (<see cref="FtpUserNameClaim.KendiKaydi"/>).
    /// </para>
    /// <para>
    /// Kırılan tek durum ÖKSÜZ hesaptır: Windows'ta duran ama panel kaydı olmayan bir hesabın adı
    /// artık talep edilemez. Bu bilinçli — öksüz hesap ile <c>Administrator</c> panelin gözünde
    /// AYNI şeydir, ikisini ayırt edecek bir veri yok. Geçiş yolu panelde zaten mevcut ve
    /// kapatılmadı: yönetici <c>DELETE /ftp/users/{ad}</c> ile öksüz hesabı siler (silme yolu
    /// bilerek kayıt şartı aramıyor), sonra ad yeniden serbesttir. Kapsamlı roller o yolu
    /// kullanamaz, çünkü <c>EnsureFtpUserScopeAsync</c> kayıtsız adı zaten 403'lüyor.
    /// </para>
    /// <para>
    /// Yalnız yerel SAM sorgusunun NERR_UserNotFound sonucu hesabın yokluğunu kanıtlar.
    /// Erişim/işletim sistemi hatası ayrı 503 olur; net.exe çıkış kodundan veya yerelleştirilmiş
    /// hata metninden "hesap yok" sonucu çıkarılmaz.
    /// </para>
    /// </remarks>
    private async Task EnsureUserNameIsClaimableAsync(string userName, string siteName, CancellationToken cancellationToken)
    {
        var kayitlar = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken);
        var hukum = ClassifyUserNameClaim(
            userName,
            siteName,
            kayitlar.Select(item => (item.UserName, item.SiteName)),
            await UserExistsAsync(userName, cancellationToken));

        switch (hukum)
        {
            // Başka bir sitenin FTP hesabı: kapsam denetimi ezilmiş kayıttan okuduğu için
            // devralma buradan geçiyordu.
            case FtpUserNameClaim.BaskaSitenin:
                throw new AppException("ftp.user_name_taken", 409, 409);

            // Panelin açmadığı bir yerel hesap (Administrator, servis hesapları, elle açılmış
            // hesaplar). Ad deseni bunları kabul ediyor ve FTP adları site adından deterministik
            // üretildiği için tahmin de edilebilir.
            case FtpUserNameClaim.PanelDisi:
                throw new AppException("ftp.user_name_reserved", 409, 409);
        }
    }

    // Parola net.exe argümanına çift tırnak içinde geçiyor. Bir '"' ya da SONDAKİ '\' tırnağı kırıp
    // ek argüman enjekte edebilir (kontrol karakterleri de). ProcessRunner shell kullanmasa da
    // (tam komut enjeksiyonu yok) bunları reddederek argüman enjeksiyonunu önlüyoruz.
    private static void EnsureCommandSafePassword(string password)
    {
        if (password.Contains('"') || password.EndsWith('\\') || password.Any(ch => ch < 0x20))
        {
            throw new AppException("ftp.invalid_password", 400, 400);
        }
    }

    private static string NormalizeUserName(string value)
    {
        var normalized = value.Trim();
        if (!UserNamePattern().IsMatch(normalized))
        {
            throw new AppException("ftp.invalid_user_name", 400, 400);
        }

        // FTP kullanıcısı Windows yerel hesabıdır; ad EN FAZLA 20 karakter olabilir (net user limiti).
        if (normalized.Length > 20)
        {
            throw new AppException("ftp.user_name_too_long", 400, 400);
        }

        return normalized;
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    /// <summary>
    /// FTP hesabının parolasını SÜRESİZ yapar ve sonucu GERİ OKUR.
    /// </summary>
    /// <remarks>
    /// <b>ÖLÇÜLDÜ — BOZUK (bu makinede, canlı).</b> FTP kullanıcısı <c>net user … /add</c> ile
    /// açılan gerçek bir Windows yerel hesabıdır ve hiçbir yerde parola süresi kapatılmıyordu;
    /// hesap makinenin parola ilkesini (<c>MaxPasswordAge</c>, varsayılan <b>42 gün</b>) miras
    /// alıyordu. <c>Get-LocalUser</c> çıktısı:
    /// <code>
    /// alioral_xyz_ftp  PasswordExpires=13.09.2026  PasswordLastSet=02.08.2026  (42 gün)
    /// test_local_ftp   PasswordExpires=10.10.2026  PasswordLastSet=29.08.2026  (42 gün)
    /// aLi              PasswordExpires=&lt;boş&gt;                                   (hiç dolmuyor)
    /// </code>
    /// Yani süre sonu YALNIZCA panelin açtığı hesaplarda vardı.
    ///
    /// Sonuç: FTP kurulum günü çalışır, 42 gün sonra AYNI GÜN açılmış bütün hesaplar birden
    /// <c>530 Login incorrect</c> vermeye başlar. Panel hesabı hâlâ "etkin" gösterir, çünkü
    /// <c>IsEnabled</c> parola süresini ölçmez — müşteri "FTP çalışmıyor" der, panel "çalışıyor" der.
    ///
    /// <b>PAROLA DEĞİŞİMİNDE DE ÇAĞRILIR</b> ve bu bilinçli: hem bayrağı teyit eder, hem de bu
    /// düzeltmeden ÖNCE açılmış hesapları ilk parola değişiminde kendiliğinden iyileştirir.
    ///
    /// Betik bu makinede uçtan uca ölçüldü: <c>test_local_ftp</c> için süre sonu
    /// <c>10.10.2026</c> iken çağrıdan sonra <c>PasswordExpires</c> null döndü.
    ///
    /// <b>Neden hata FIRLATMIYOR:</b> hesap ve yetkilendirme bu noktada çalışır durumdadır;
    /// burada patlamak, çalışan bir FTP hesabını geri almak olurdu. Ama SESSİZ de değil —
    /// başarısızlık ölçülen değerle birlikte kaydedilir.
    /// </remarks>
    /// <returns>
    /// <c>true</c> ölçüldü ve süresiz, <c>false</c> ölçüldü ve HÂLÂ süre dolumu var,
    /// <c>null</c> ölçülemedi. Sonuç artık ATILMIYOR: çağıran onu <see cref="FtpUserDto"/>'ya
    /// koyar, uç de denetim kaydına yazar. Eskiden yalnız <c>LogWarning</c>'e gidiyordu.
    /// </returns>
    private async Task<bool?> EnsurePasswordNeverExpiresAsync(string userName, CancellationToken cancellationToken)
    {
        // Ad UserNamePattern ile ^[A-Za-z][A-Za-z0-9._-]{1,31}$ olarak doğrulanmıştır; tırnak giremez.
        var ad = EscapePowerShellLiteral(userName);

        // GERİ OKUMA ŞART: "hata vermedi" ile "oldu" aynı şey değil. Set-LocalUser sessizce
        // etkisiz kalırsa bayrağı ölçen tek şey burasıdır.
        var script = $@"$ErrorActionPreference = 'Stop'
Set-LocalUser -Name '{ad}' -PasswordNeverExpires $true
$hesap = Get-LocalUser -Name '{ad}'
if ($null -eq $hesap.PasswordExpires) {{ [Console]::Out.WriteLine('ADAPANEL-SURESIZ') }}
else {{ [Console]::Out.WriteLine('ADAPANEL-SON=' + $hesap.PasswordExpires.ToString('o')) }}";

        try
        {
            var sonuc = await RunScriptAsync(script, cancellationToken);
            var cikti = sonuc.StandardOutput ?? string.Empty;
            if (sonuc.Success && cikti.Contains("ADAPANEL-SURESIZ", StringComparison.Ordinal))
            {
                return true;
            }

            // ÖLÇÜLDÜ ve HÂLÂ SÜRE DOLUMU VAR ile HİÇ ÖLÇÜLEMEDİ aynı görüntüye düşmemeli.
            // ADAPANEL-SON damgası, Get-LocalUser'ın ÇALIŞTIĞININ kanıtıdır: bayrak okundu ve
            // bir son tarih döndü, yani "kapatılamadı" KESİN bilgidir. Damga hiç yoksa betik
            // daha o noktaya varamamıştır ve hiçbir şey iddia etmeyiz.
            var olculdu = cikti.Contains("ADAPANEL-SON=", StringComparison.Ordinal);

            // MASKELENEREK yazilir. Bu yol FTP hesabi uzerinde `net user` calistirir ve o komutta
            // parola KONUMSAL argumandir; basarisiz bir komutun ciktisi komut satirini geri
            // yankilayabilir. Komut ayrintisi normalde `AppException` kurucusundan gecip orada
            // maskeleniyor (0.8.9), ama burasi gunluge DOGRUDAN yaziyor -- yani tek kaynagin
            // disindaydi. Nobetci bunu ILK KOSUSUNDA yakaladi.
            logger?.LogWarning(
                "FTP hesabinin parola suresi kapatilamadi: {UserName}. Hesap {Gun} gun sonra baglanamaz hale gelebilir. {Detay}",
                userName,
                "MaxPasswordAge",
                SecretRedactor.SanitizeDetails(BuildCommandDetails(sonuc)));

            return olculdu ? false : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(
                exception,
                "FTP hesabinin parola suresi kapatilamadi: {UserName}.",
                userName);

            return null;
        }
    }

    private static string BuildCommandDetails(CommandResult result)
    {
        var details = $"""
            ExitCode={result.ExitCode}
            StandardOutput:
            {result.StandardOutput}
            StandardError:
            {result.StandardError}
            """;

        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private sealed class FtpUserState
    {
        public string UserName { get; set; } = string.Empty;

        public string SiteName { get; set; } = string.Empty;

        public string HomePath { get; set; } = string.Empty;

        public bool IsEnabled { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{1,31}$", RegexOptions.Compiled)]
    private static partial Regex UserNamePattern();
}
