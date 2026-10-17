using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.Iis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Ssl;

public sealed class WinAcmeSslService(
    AdaPanelDbContext dbContext,
    IIisSiteService iisSiteService,
    IDnsService dnsService,
    JsonFileStore fileStore,
    IComponentBootstrapper componentBootstrapper,
    ILogger<WinAcmeSslService> logger,
    // ISTEGE BAGLI OLCUM DIKISI: IIS bag envanterini okuyan yol. Verilmezse GERCEK okuyucu
    // (ReadHttpsBindingsAsync -> PowerShell) kullanilir, yani uretim davranisi DEGISMEZ.
    //
    // Neden gerekli: yenileme turunun bag olcumunu CAGIRDIGI, aksi halde hicbir testle
    // olculemezdi -- ve tam bu sinif hata ("koruma yazildi ama kardes yol cagirmiyor") bu
    // depoda defalarca yakalandi. Ayni kalip SiteHostingSettingsService (outboundResolver) ve
    // MailEnableMailService (KomutKosucusu) icinde de var.
    Func<CancellationToken, Task<IReadOnlyCollection<IisHttpsBinding>?>>? bindingReader = null,
    IConcurrencyLock? concurrencyLock = null,
    IMailService? mailService = null) : ISslService
{
    private const string StatePath = "config/ssl.json";
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";
    private readonly IConcurrencyLock siteGates = concurrencyLock ?? new AdaPanel.Infrastructure.Services.ConcurrencyLock();

    internal Func<string, string, CancellationToken, Task<CommandResult>> RenewalCommand { get; init; } =
        (executable, arguments, token) => ProcessRunner.RunAsync(executable, arguments, cancellationToken: token);

    internal Func<string, CancellationToken, Task<CommandResult>> RedirectCommand { get; init; } =
        ProcessRunner.RunPowerShellAsync;

    internal Func<string, CancellationToken, Task<CommandResult>> RemoveBindingCommand { get; init; } =
        ProcessRunner.RunPowerShellAsync;

    internal Func<SslCertificateDto, CancellationToken, Task<SslStoreProbe>> CertificateProbeAsync { get; init; } =
        (record, _) => Task.FromResult(ProbeCertificate(record));

    internal Func<string, CancellationToken, Task<CommandResult>> RemoveCertificateCommand { get; init; } =
        ProcessRunner.RunPowerShellAsync;

    internal Func<byte[], string, X509KeyStorageFlags, X509Certificate2> ImportCertificateLoader { get; init; } =
        (raw, password, flags) => new X509Certificate2(raw, password, flags);

    internal Action<X509Certificate2> PersistImportedCertificate { get; init; } = PersistCertificate;
    internal Action<X509Certificate2> PersistImportedIntermediate { get; init; } = SslCertificateImport.PersistIntermediate;

    internal Func<string, CancellationToken, Task<CommandResult>> HttpsBindingCommand { get; init; } =
        ProcessRunner.RunPowerShellAsync;

    internal Func<string, string, CancellationToken, Task<CommandResult>> IssueCommand { get; init; } =
        (executable, arguments, token) => ProcessRunner.RunAsync(executable, arguments,
            cancellationToken: token, timeout: TimeSpan.FromMinutes(10));

    internal Func<string, X509Certificate2?> IssueCertificateLookup { get; init; } = FindBestCertificate;
    internal Func<string, string, X509Certificate2> IssueFallbackCertificate { get; init; } = CreateSelfSignedCertificate;
    internal Action<X509Certificate2> PersistIssuedCertificate { get; init; } = PersistCertificate;
    internal Func<string, CancellationToken, Task<CommandResult>> DnsPreflightCommand { get; init; } =
        ProcessRunner.RunPowerShellAsync;

    public Task<SslWorkspaceDto> GetSiteWorkspaceAsync(string siteName, CancellationToken cancellationToken = default) =>
        GetSiteWorkspaceCoreAsync(siteName, null, cancellationToken);

    public async Task<SslWorkspaceDto> GetSiteWorkspaceAsync(string siteName,
        Func<SiteDto, CancellationToken, Task> beforePublication, CancellationToken cancellationToken = default)
    {
        using var generation = await SiteMutationGuard.CaptureAsync(iisSiteService, siteGates, siteName, cancellationToken);
        Task ValidateAsync(CancellationToken token) => ValidateWorkspaceAuthorityAsync(generation, beforePublication, token);
        await generation.RunAsync(_ => ValidateAsync(cancellationToken), cancellationToken);
        var workspace = await GetSiteWorkspaceCoreAsync(siteName, ValidateAsync, cancellationToken,
            publish => generation.RunAsync(async _ =>
            {
                await ValidateAsync(cancellationToken);
                await publish();
            }, cancellationToken));
        // Even when reconciliation did not need to write, do not return a stale-authority read.
        await generation.RunAsync(_ => ValidateAsync(cancellationToken), cancellationToken);
        return workspace;
    }

    private static async Task ValidateWorkspaceAuthorityAsync(SiteMutationGuard.Target generation,
        Func<SiteDto, CancellationToken, Task> beforePublication, CancellationToken token)
    {
        var current = await generation.ValidateAsync(token);
        await beforePublication(current, token);
        token.ThrowIfCancellationRequested();
    }

    private async Task<SslWorkspaceDto> GetSiteWorkspaceCoreAsync(string siteName,
        Func<CancellationToken, Task>? beforePublication, CancellationToken cancellationToken,
        Func<Func<Task>, Task>? publish = null)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);

        // KAYIT DEĞİL, GERÇEK. ssl.json bir zamanlar yalnız sertifika ALINIRKEN yazılıyordu ve bir
        // daha hiç doğrulanmıyordu; ekran da, günlük uyarı e-postası da o donmuş kaydı gerçek
        // sanıyordu. Listeleme artık kaydı sertifika deposuyla uzlaştırır (bkz. SslStateReconciler).
        var states = await ReconcileStateAsync(
            record => string.Equals(record.SiteName, site.Name, StringComparison.OrdinalIgnoreCase),
            cancellationToken, beforePublication, publish);

        IReadOnlyCollection<IisHttpsBinding>? bindings = null;
        try { bindings = bindingReader is null
            ? await ReadHttpsBindingsAsync(cancellationToken)
            : await bindingReader(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "HTTPS binding inventory could not be read for {SiteName}.", site.Name);
        }
        SslBindingVerification.Refresh(states.Where(record =>
            string.Equals(record.SiteName, site.Name, StringComparison.OrdinalIgnoreCase)), bindings);

        var redirect = await ReadHttpsRedirectAsync(site.Name, cancellationToken);

        return new SslWorkspaceDto
        {
            SiteName = site.Name,
            HostNames = site.Bindings
                .Where(binding => !string.IsNullOrWhiteSpace(binding.HostName))
                .Select(binding => binding.HostName)
                .Where(host => !IsPreviewHost(host))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Certificates = states
                .Where(item => string.Equals(item.SiteName, site.Name, StringComparison.OrdinalIgnoreCase))
                .Where(item => !IsPreviewHost(item.HostName))
                .OrderByDescending(item => item.NotAfterUtc)
                .ToArray(),
            HttpsRedirectEnabled = redirect ?? false,
            HttpsRedirectMeasured = redirect.HasValue
        };
    }

    /// <summary>
    /// Bu bir GERCEK alan adi mi, yoksa domainsiz onizlemenin urettigi gecici host mu?
    /// <para>
    /// Onizleme host'u (<c>&lt;site&gt;.&lt;ip&gt;.nip.io</c>) kullanicinin sahip oldugu bir alan adi degildir;
    /// yalnizca alan adi yonlendirilmeden siteyi acabilmek icin uretilir ve kendinden imzali bir
    /// sertifika alir. Bu host SSL listesinde gercek bir alan adi gibi gorunuyordu -- kullanici
    /// "bu da nereden cikti" diye hakli olarak sordu. Baglamanin kendisi KALIR (onizleme linki
    /// calismaya devam etsin), yalnizca listelerde gosterilmez.
    /// </para>
    /// </summary>
    internal static bool IsPreviewHost(string? hostName) =>
        !string.IsNullOrWhiteSpace(hostName)
        && hostName.EndsWith(".nip.io", StringComparison.OrdinalIgnoreCase);

    public async Task<int> RemovePreviewCertificatesAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<SslCertificateDto>(), cancellationToken);
        var kalan = states
            .Where(item => !(string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase)
                             && IsPreviewHost(item.HostName)))
            .ToArray();

        var silinen = states.Length - kalan.Length;
        if (silinen > 0)
        {
            await fileStore.WriteAsync(StatePath, kalan, cancellationToken);
        }

        return silinen;
    }

    public Task<SslCertificateDto> IssueLetsEncryptAsync(IssueLetsEncryptRequest request, CancellationToken cancellationToken = default) =>
        IssueLetsEncryptCoreAsync(request, null, cancellationToken);

    public Task<SslCertificateDto> IssueLetsEncryptAsync(IssueLetsEncryptRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        IssueLetsEncryptCoreAsync(request, beforeMutation, cancellationToken);

    private async Task<SslCertificateDto> IssueLetsEncryptCoreAsync(IssueLetsEncryptRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var input = new IssueLetsEncryptRequest
        {
            SiteName = request.SiteName, HostName = request.HostName, ContactEmail = request.ContactEmail,
            UseStaging = request.UseStaging, Wildcard = request.Wildcard
        };
        using var target = await SiteMutationGuard.CaptureAsync(iisSiteService, siteGates, input.SiteName, cancellationToken);
        var site = target.Site;
        IDisposable? reservation = null;
        async Task ValidateAsync(SiteDto current, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (beforeMutation is not null) await beforeMutation(current, token);
            target.ValidateSnapshot(current);
            token.ThrowIfCancellationRequested();
        }
        async Task ApproveAsync(CancellationToken token)
        {
            // Resolve the executable before pinning. A replaced site during that read must
            // invalidate the old request; long ACME work only starts with a live reservation.
            if (reservation is null) reservation = await target.ReserveAsync(ValidateAsync, token);
            else await target.RunAsync(current => ValidateAsync(current, token), token);
        }
        async Task MutateAsync(Func<Task> operation)
        {
            await ApproveAsync(cancellationToken);
            await target.RunAsync(async current =>
            {
                await ValidateAsync(current, cancellationToken);
                await operation();
            }, cancellationToken);
        }
        async Task ValidatePublicationAsync(CancellationToken token) =>
            await ValidateAsync(await target.ValidateAsync(token), token);
        Task PublishAsync(SslCertificateDto dto) => MutateAsync(() => UpsertAsync(dto, cancellationToken, ValidatePublicationAsync));
        async Task<CommandResult> RunRedirectAsync(string script, CancellationToken token)
        {
            CommandResult result = null!;
            await target.RunAsync(async current =>
            {
                await ValidateAsync(current, token);
                result = await RedirectCommand(script, token);
            }, token);
            return result;
        }
        async Task ApplyRedirectAsync()
        {
            await ApplyHttpsRedirectCoreAsync(site.Name, true, cancellationToken, ApproveAsync, RunRedirectAsync);
        }
        try
        {
            if (input.Wildcard)
                return await IssueWildcardAsync(site, input, cancellationToken, ApproveAsync, MutateAsync, ApplyRedirectAsync, PublishAsync);

            var hostName = ResolveHostName(site, input.HostName);
            var outcome = await TryIssueWithWinAcmeAsync(site, hostName, input.ContactEmail,
                input.UseStaging, cancellationToken, ApproveAsync);
            X509Certificate2? certificate = outcome.Certificate;
            if (certificate is null)
            {
                using var existing = IssueCertificateLookup(hostName);
                var bindings = await ReadHttpsBindingsAsync(cancellationToken);
                await ApproveAsync(cancellationToken);
                if (existing is not null || bindings.Any(binding =>
                    string.Equals(binding.SiteName, site.Name, StringComparison.OrdinalIgnoreCase) && binding.Port == 443
                    && (string.IsNullOrEmpty(binding.HostName) || string.Equals(binding.HostName, hostName, StringComparison.OrdinalIgnoreCase))
                    && (!binding.CertificateReadable || !string.IsNullOrWhiteSpace(binding.Thumbprint))))
                    throw new AppException("ssl.issue_failed_existing_preserved", 409, 409);
                // The fallback constructor itself imports a persistent machine key.
                await MutateAsync(() =>
                {
                    certificate = IssueFallbackCertificate(hostName, site.Name);
                    return Task.CompletedTask;
                });
            }
            using var issuedCertificate = certificate!;
            if (!string.IsNullOrWhiteSpace(input.ContactEmail))
            {
                await MutateAsync(async () =>
                {
                    await fileStore.UpdateAsync(AcmeContactSettings.Path, new AcmeContactSettings(),
                        _ => new AcmeContactSettings { Email = input.ContactEmail.Trim() },
                        ValidatePublicationAsync, cancellationToken);
                });
            }
            await MutateAsync(() => { PersistIssuedCertificate(issuedCertificate); return Task.CompletedTask; });
            await MutateAsync(() => EnsureHttpsBindingAsync(site.Name, hostName, 443, issuedCertificate, cancellationToken));
            // URL Rewrite installation is deliberately outside the global IIS gate.
            await ApplyRedirectAsync();
            await ApproveAsync(cancellationToken);
            var dto = await OlcVeIsleAsync(
                ToDto(site.Name, hostName, issuedCertificate, ResolveCertificateSource(issuedCertificate), null),
                site.Name, hostName, 443, issuedCertificate.Thumbprint ?? string.Empty, cancellationToken);
            dto.FallbackReason = outcome.Certificate is null ? outcome.FailureReason : string.Empty;
            await PublishAsync(dto);
            if (mailService is not null && dto.Source != "self-signed")
                await mailService.SyncWebCertificateAsync(hostName, ApproveAsync, cancellationToken);
            return dto;
        }
        finally { reservation?.Dispose(); }
    }

    // Domainsiz önizleme (nip.io): HIZLI self-signed HTTPS bağlaması — ACME beklemez, redirect'i değiştirmez.
    public async Task EnsurePreviewCertificateAsync(string siteName, string hostName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var certificate = CreateSelfSignedCertificate(hostName, site.Name);
        PersistCertificate(certificate);
        await EnsureHttpsBindingAsync(site.Name, hostName, 443, certificate, cancellationToken);
    }

    public Task<SslCertificateDto> ImportCertificateAsync(ImportCertificateRequest request, CancellationToken cancellationToken = default) =>
        ImportCertificateCoreAsync(request, null, cancellationToken);

    public Task<SslCertificateDto> ImportCertificateAsync(ImportCertificateRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        ImportCertificateCoreAsync(request, beforeMutation, cancellationToken);

    private async Task<SslCertificateDto> ImportCertificateCoreAsync(ImportCertificateRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var siteName = request.SiteName;
        var requestedHost = request.HostName;
        var payload = request.Base64Content;
        var password = request.Password;
        var port = request.Port <= 0 ? 443 : request.Port;
        using var target = await SiteMutationGuard.CaptureAsync(iisSiteService, siteGates, siteName, cancellationToken);
        var site = target.Site;
        var hostName = ResolveHostName(site, requestedHost);
        if (port > 65535 || string.IsNullOrWhiteSpace(payload))
            throw new AppException("ssl.invalid_certificate_payload", 400, 400);

        byte[] raw;
        X509Certificate2 inspected;
        try
        {
            raw = Convert.FromBase64String(payload);
            // PersistKeySet creates a key during the constructor, before store.Add.
            // Inspect untrusted input entirely in memory before that first permanent effect.
            inspected = ImportCertificateLoader(raw, password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException)
        {
            throw new AppException("ssl.invalid_certificate_payload", 400, 400);
        }
        using var certificate = inspected;
        ValidateImportedCertificate(certificate, hostName);
        IReadOnlyList<byte[]> intermediates;
        try { intermediates = SslCertificateImport.ReadIntermediates(raw, password, certificate); }
        catch (CryptographicException) { throw new AppException("ssl.invalid_certificate_payload", 400, 400); }

        async Task ApproveAsync(SiteDto current, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (beforeMutation is not null) await beforeMutation(current, token);
            target.ValidateSnapshot(current);
            token.ThrowIfCancellationRequested();
        }
        Task ReapproveAsync(CancellationToken token) => target.RunAsync(current => ApproveAsync(current, token), token);
        using var reservation = await target.ReserveAsync(ApproveAsync, cancellationToken);
        foreach (var der in intermediates)
        {
            await target.RunAsync(async current =>
            {
                await ApproveAsync(current, cancellationToken);
                using var intermediate = new X509Certificate2(der);
                PersistImportedIntermediate(intermediate);
            }, cancellationToken);
        }
        await target.RunAsync(async current =>
        {
            await ApproveAsync(current, cancellationToken);
            // Persist only the selected leaf's key, never extra private keys from the bundle.
            var leafPfx = certificate.Export(X509ContentType.Pfx, password);
            try
            {
                using var persistent = ImportCertificateLoader(leafPfx, password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
                cancellationToken.ThrowIfCancellationRequested();
                PersistImportedCertificate(persistent);
            }
            finally { CryptographicOperations.ZeroMemory(leafPfx); }
        }, cancellationToken);
        await target.RunAsync(async current =>
        {
            await ApproveAsync(current, cancellationToken);
            await EnsureHttpsBindingAsync(site.Name, hostName, port, certificate, cancellationToken);
        }, cancellationToken);
        async Task<CommandResult> RunRedirectAsync(string script, CancellationToken token)
        {
            CommandResult result = null!;
            await target.RunAsync(async current =>
            {
                await ApproveAsync(current, token);
                result = await RedirectCommand(script, token);
            }, token);
            return result;
        }
        // Dependency installation must not retain the global IIS gate. The site pin stays live.
        await ApplyHttpsRedirectCoreAsync(site.Name, true, cancellationToken, ReapproveAsync, RunRedirectAsync);
        await ReapproveAsync(cancellationToken);
        var dto = ToDto(site.Name, hostName, certificate, ResolveCertificateSource(certificate), null);
        dto.Port = port;
        await OlcVeIsleAsync(dto, site.Name, hostName, port, certificate.Thumbprint ?? string.Empty, cancellationToken);
        await target.RunAsync(async current =>
        {
            await ApproveAsync(current, cancellationToken);
            await UpsertAsync(dto, cancellationToken, async token =>
            {
                var fresh = await target.ValidateAsync(token);
                await ApproveAsync(fresh, token);
            });
        }, cancellationToken);
        if (mailService is not null)
            await mailService.SyncWebCertificateAsync(hostName, ReapproveAsync, cancellationToken);
        return dto;
    }

    private static void ValidateImportedCertificate(X509Certificate2 certificate, string hostName)
    {
        if (!certificate.HasPrivateKey)
            throw new AppException("ssl.certificate_private_key_required", 400, 400);
        var now = DateTime.UtcNow;
        if (certificate.NotAfter.ToUniversalTime() <= now)
            throw new AppException("ssl.certificate_expired", 400, 400);
        if (certificate.NotBefore.ToUniversalTime() > now)
            throw new AppException("ssl.certificate_not_yet_valid", 400, 400);
        foreach (var usage in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            if (!usage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0"))
                throw new AppException("ssl.certificate_server_auth_required", 400, 400);
        bool matches;
        try
        {
            var host = hostName.TrimEnd('.');
            if (!System.Net.IPAddress.TryParse(host, out _)) host = new System.Globalization.IdnMapping().GetAscii(host);
            matches = certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: false);
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            throw new AppException("ssl.invalid_certificate_payload", 400, 400);
        }
        if (!matches) throw new AppException("ssl.certificate_host_mismatch", 400, 400);
    }

    public Task<RenewAllResultDto> RenewAllAsync(CancellationToken cancellationToken = default) =>
        RenewAllCoreAsync(null, cancellationToken);

    public Task<RenewAllResultDto> RenewAllAsync(Func<CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default) => RenewAllCoreAsync(beforeMutation, cancellationToken);

    private async Task<RenewAllResultDto> RenewAllCoreAsync(
        Func<CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var wacs = await ResolveWinAcmeExecutableAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Executable discovery may await storage after the caller passed the automation gate.
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        if (string.IsNullOrWhiteSpace(wacs))
        {
            // OLCUM YAPILAMADI. "0 sertifika yenilendi" DEMEK DEGIL -- yenileme hic denenemedi.
            //
            // Ama KAYDI DOGRULAMAK win-acme'ye BAGLI DEGIL: sertifika deposu her hâlükârda
            // okunabilir. win-acme kurulu olmasa da (ör. sertifikalar elle içe aktarılmışsa)
            // kaydın gerçekle uyuşup uyuşmadığı burada ölçülür.
            var dogrulamaYok = await ReconcileWithCountsAsync(cancellationToken);

            // BAG OLCUMU win-acme'ye BAGLI DEGIL: IIS bagi her halukarda okunabilir. win-acme
            // kurulu olmasa bile bagin yanlis sertifikada kalmasi GERCEK bir arizadir ve
            // soylenmelidir.
            var bagYok = await MeasureBindingsAndPersistAsync(cancellationToken);
            var mailResult = await SyncMailCertificateAsync(beforeMutation, cancellationToken);
            return new RenewAllResultDto
            {
                RenewedCount = 0,
                Measured = false,
                FailureReason = "win-acme kurulu degil; sertifikalar yenilenemiyor.",
                Output = "win-acme kurulu degil.",
                VerifiedCount = dogrulamaYok.Verified,
                MissingFromStoreCount = dogrulamaYok.MissingFromStore,
                UnverifiedCount = dogrulamaYok.Unmeasured,
                BindingDriftCount = bagYok.Drift,
                BindingsMeasured = bagYok.Measured,
                MailSynchronizationPerformed = mailService is not null,
                MailCertificate = mailResult
            };
        }

        var result = await RenewalCommand(wacs, "--renew --baseuri https://acme-v02.api.letsencrypt.org/", cancellationToken);
        var cikti = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        // Keep both diagnostic streams: progress on stdout must not hide errors/timeouts on stderr.
        // Counting retains its original stream selection so a diagnostic copy is not counted twice.
        var diagnostics = string.Join(Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));

        // SAYIM CIKTIDAN OKUNUR, cikis kodundan DEGIL.
        //
        // A zero exit code does not say how many renewals ran. win-acme 2.2.9 also emits
        // "succeeded with errors" when a completed renewal has error-level log entries.
        // Match the final outcome, not "success" inside a friendly name or "unsuccessful".
        const string outcomePattern = @"^[^\r\n]*?\bRenewal(?:[ \t]+for[ \t]+[^\r\n]+)?[ \t]+succeeded(?<errors>[ \t]+with[ \t]+errors)?[ \t]*\r?$";
        const RegexOptions outcomeOptions = RegexOptions.IgnoreCase | RegexOptions.Multiline
            | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        var yenilenen = Regex.Matches(cikti ?? string.Empty, outcomePattern, outcomeOptions).Count;
        // Errors on either stream matter even when stdout is the selected counting stream.
        var reportedErrors = Regex.Matches(diagnostics, outcomePattern, outcomeOptions)
            .Any(match => match.Groups["errors"].Success);
        var failureReason = !result.Success ? "win-acme yenileme komutu basarisiz oldu."
            : reportedErrors ? "win-acme yenilemeyi hata kayitlariyla tamamladi; komut ciktisini inceleyin." : string.Empty;
        // A bounded capture can omit later renewals or error summaries even with exit code zero.
        // Keep the observed count, but never publish it as a complete, clean result.
        if (new[] { result.StandardOutput, result.StandardError }
            .Any(output => output?.EndsWith(ProcessRunner.TruncationNotice, StringComparison.Ordinal) == true))
        {
            failureReason = string.Join(" ", new[] { failureReason,
                "win-acme cikti eksik; sayim kismi olabilir. Ayrintilar icin win-acme gunluklerini inceleyin." }
                .Where(reason => !string.IsNullOrWhiteSpace(reason)));
        }

        // YENILEMEDEN SONRA KAYIT MUTLAKA TAZELENIR.
        //
        // Bu satirin yoklugu, panelin en gurultulu yanlis alarminin kok nedeniydi: win-acme
        // sertifikayi sorunsuz yeniler, YENI parmak izi ve YENI bitis tarihiyle depoya koyar --
        // ama ssl.json'daki kayit sertifikanin ILK alindigi gunden kalma tarihi tasimaya devam
        // ederdi. Danisman ekrani "sertifikanin suresi DOLDU" kritik uyarisini, otomasyon ise her
        // gun ayni uyari e-postasini uretirdi. Kullanici bir sure sonra butun SSL uyarilarina
        // bakmaz olur; gercek bir bitis de o gurultunun icinde kaybolurdu.
        var dogrulama = await ReconcileWithCountsAsync(cancellationToken);

        // KAYIT TAZELENDI; SIMDI BAGIN GERCEGI. Bu satirin yoklugu, yukaridaki tazelemeyi
        // gurultulu bir YANLIS POZITIFTEN sessiz bir YANLIS NEGATIFE cevirmisti: kayit
        // saglikli gorunur, bag eski sertifikada kalir, site o sertifika dolana kadar onu
        // sunar ve panel hicbir sey soylemez.
        var bag = await MeasureBindingsAndPersistAsync(cancellationToken);

        var syncedMail = await SyncMailCertificateAsync(beforeMutation, cancellationToken);

        return new RenewAllResultDto
        {
            RenewedCount = yenilenen,
            Measured = result.Success,
            FailureReason = failureReason,
            Output = diagnostics,
            VerifiedCount = dogrulama.Verified,
            MissingFromStoreCount = dogrulama.MissingFromStore,
            UnverifiedCount = dogrulama.Unmeasured,
            BindingDriftCount = bag.Drift,
            BindingsMeasured = bag.Measured,
            MailSynchronizationPerformed = mailService is not null,
            MailCertificate = syncedMail
        };
    }

    private async Task<MailCertificateDto?> SyncMailCertificateAsync(Func<CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        if (mailService is null) return null;
        return beforeMutation is null ? await mailService.SyncCertificateAsync(token)
            : await mailService.SyncCertificateAsync(beforeMutation, token);
    }

    /// <summary>
    /// IIS bağlarını ölçer ve sonucu <c>ssl.json</c>'a YAZAR; sapan kayıt sayısını döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden yenileme turunun İÇİNDE:</b> kaydı depodan tazelemek tek başına yetmiyordu. Tazeleme
    /// kayda YENİ ve uzak bir bitiş tarihi yazar; bağ eski sertifikada kalmışsa Danışman artık
    /// sağlıklı bir tarih görür ve susar. Site ise eski sertifika dolana kadar onu sunar. Canlıda
    /// 26–27 Eylül 2026'da olan tam olarak budur. Ölçümün tazelemeyle AYNI turda yapılması,
    /// kaydın "sağlıklı" görünmesiyle bağın gerçeğinin aynı anda bilinmesini sağlar.
    /// </para>
    /// <para>
    /// <b>Bağ listesi OKUNAMAZSA kayda DOKUNULMAZ</b> ve <c>false</c> döner. Geçici bir IIS okuma
    /// hatası, daha önce ölçülmüş GERÇEK bir sapmayı silmemelidir — aynı disiplin
    /// <see cref="SslStateReconciler"/> sözleşmesinin 1. maddesinde yazılı.
    /// </para>
    /// <para>
    /// Ölçüm <see cref="SslBindingVerification"/> ile yapılır; ikinci bir okuyucu/karşılaştırıcı
    /// yazılmadı, yoksa iki taraf sessizce ayrışırdı.
    /// </para>
    /// </remarks>
    private async Task<(int Drift, bool Measured)> MeasureBindingsAndPersistAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<IisHttpsBinding>? baglamalar;
        try
        {
            baglamalar = bindingReader is null
                ? await ReadHttpsBindingsAsync(cancellationToken)
                : await bindingReader(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "HTTPS baglama envanteri okunamadi; bag olcumu atlandi.");
            return (0, false);
        }

        if (baglamalar is null)
        {
            return (0, false);
        }

        var olcumAni = DateTime.UtcNow;
        var yazilan = await fileStore.UpdateAsync(
            StatePath,
            Array.Empty<SslCertificateDto>(),
            existing =>
            {
                // Kilit ALTINDA taze icerik uzerinde olculur: olcum sirasinda baska bir akis
                // (sertifika alma) yeni kayit eklediyse onu EZMEYELIM.
                SslBindingVerification.Refresh(existing, baglamalar);
                foreach (var kayit in existing)
                {
                    kayit.BindingCheckedAtUtc = olcumAni;
                }

                return existing;
            },
            cancellationToken);

        var sapan = yazilan.Count(kayit =>
            SslBindingDrift.Evaluate(kayit.IsCertificateBound, kayit.BindingCheckedAtUtc, olcumAni)
                == SslBindingDrift.Verdict.Drifted);

        return (sapan, true);
    }
    /// <summary>Tüm kayıtları uzlaştırır ve sayımı döner (kayıtların kendisi burada gerekmiyor).</summary>
    private async Task<SslReconcileResult> ReconcileWithCountsAsync(CancellationToken cancellationToken)
    {
        var (_, sonuc) = await ReconcileStateInternalAsync(_ => true, cancellationToken);
        return sonuc;
    }

    /// <summary>
    /// <c>config/ssl.json</c> kayıtlarını sertifika deposundaki gerçekle uzlaştırır ve
    /// (gerekiyorsa) diske yazar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aynı disiplin bu depoda zaten kanıtlanmıştı: <c>MailService.SyncCertificateAsync</c> PEM'i
    /// diskten geri okuyup içerik özeti değiştiyse <c>NotAfterUtc</c>'yi tazeliyor. Site SSL'i bu
    /// disiplinin dışında kalmıştı.
    /// </para>
    /// <para>
    /// ÖLÇÜM dosya kilidinin DIŞINDA yapılır (sertifika deposu okuması yavaş olabilir ve
    /// ssl.json kilidini o süre boyunca tutmak eşzamanlı sertifika alımını bloklardı); sonuç,
    /// kilit altında yeniden okunan taze listeye <see cref="SslStateReconciler.Key"/> ile eşlenir.
    /// </para>
    /// </remarks>
    private async Task<SslCertificateDto[]> ReconcileStateAsync(
        Func<SslCertificateDto, bool> kapsam,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforePublication = null,
        Func<Func<Task>, Task>? publish = null)
    {
        var (kayitlar, _) = await ReconcileStateInternalAsync(kapsam, cancellationToken, beforePublication, publish);
        return kayitlar;
    }

    private async Task<(SslCertificateDto[] Kayitlar, SslReconcileResult Sonuc)> ReconcileStateInternalAsync(
        Func<SslCertificateDto, bool> kapsam,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforePublication = null,
        Func<Func<Task>, Task>? publish = null)
    {
        var mevcut = await fileStore.ReadAsync(StatePath, Array.Empty<SslCertificateDto>(), cancellationToken);
        var hedefler = mevcut.Where(kapsam).ToArray();
        if (hedefler.Length == 0)
        {
            return (mevcut, new SslReconcileResult(false, 0, 0, 0));
        }

        var olcumler = new Dictionary<CertificateProbeIdentity, SslStoreProbe>();
        foreach (var hedef in hedefler)
        {
            var identity = CertificateProbeIdentity.Capture(hedef);
            olcumler[identity] = await CertificateProbeAsync(hedef, cancellationToken);
        }

        SslStoreProbe Olc(SslCertificateDto kayit) =>
            olcumler.TryGetValue(CertificateProbeIdentity.Capture(kayit), out var olcum)
                ? olcum
                // A concurrently renewed/imported record is a different certificate even
                // when site/host/port match. Its old probe is not evidence about the new one.
                : SslStoreProbe.Unmeasured();

        var now = DateTime.UtcNow;
        var sonuc = SslStateReconciler.Reconcile(mevcut, Olc, now);

        if (!sonuc.ShouldPersist)
        {
            // Gerçek değişmemiş: gereksiz yazma yok. Elde tutulan liste zaten tazelenmiş durumda.
            return (mevcut, sonuc);
        }

        var yazilan = mevcut;
        async Task PublishAsync() => yazilan = await fileStore.UpdateAsync(
            StatePath,
            Array.Empty<SslCertificateDto>(),
            existing =>
            {
                // Kilit ALTINDA taze içerik üzerinde tekrar uygulanır: ölçüm sırasında başka bir
                // akış (sertifika alma) yeni kayıt eklediyse onu EZMEYELİM.
                sonuc = SslStateReconciler.Reconcile(existing, Olc, now);
                return existing;
            },
            beforePublication,
            cancellationToken);

        // Acquire any site publication gate before the JSON path lock, never from inside it.
        // Certificate probing above remains outside the IIS gate.
        if (publish is null) await PublishAsync();
        else await publish(PublishAsync);

        return (yazilan, sonuc);
    }

    private sealed record CertificateProbeIdentity(string Key, string Thumbprint, DateTime NotAfterUtc,
        string? Source, string? Subject, string? FriendlyName)
    {
        internal static CertificateProbeIdentity Capture(SslCertificateDto record) => new(
            SslStateReconciler.Key(record), (record.Thumbprint ?? string.Empty).ToUpperInvariant(),
            record.NotAfterUtc, record.Source, record.Subject, record.FriendlyName);
    }

    /// <summary>
    /// Tek bir kaydın karşılığını sertifika deposunda ARAR.
    /// </summary>
    /// <remarks>
    /// Depo hiç açılamadıysa <see cref="SslStoreProbe.Unmeasured"/> döner — "sertifika yok"
    /// DEMEZ. Aksi hâlde erişim hatası yaşayan bir sunucuda bütün kayıtlar tek turda
    /// "depoda yok" damgası yer ve geçici bir arıza kalıcı yanlış duruma dönüşürdü.
    /// </remarks>
    private static SslStoreProbe ProbeCertificate(SslCertificateDto kayit)
    {
        if (string.IsNullOrWhiteSpace(kayit.HostName))
        {
            // Host adı yoksa arama ölçütü de yok: ÖLÇEMEDİK.
            return SslStoreProbe.Unmeasured();
        }

        var sertifika = FindBestCertificate(kayit.HostName, out var depoOkunabildi);
        return BuildProbe(depoOkunabildi, sertifika);
    }

    /// <summary>
    /// Depo aramasının sonucunu ÖLÇÜM SONUCUNA çevirir.
    /// </summary>
    /// <remarks>
    /// Ayrı ve <c>internal</c> olmasının sebebi test edilebilirlik değil, KORUNABİLİRLİKTİR:
    /// "depo okunamadı" ile "sertifika bulunamadı" arasındaki ayrım bu düzeltmenin tam kalbidir ve
    /// bir gün birinin <c>storesReadable</c> kontrolünü sadeleştirip silmesi, hatayı sessizce geri
    /// getirirdi. Sözleşme burada, çalıştırılabilir bir testle sabitlenir.
    /// </remarks>
    internal static SslStoreProbe BuildProbe(bool storesReadable, X509Certificate2? certificate)
    {
        if (!storesReadable)
        {
            // Hiçbir depo okunamadı: "sertifika yok" DEĞİL, "BİLMİYORUZ".
            return SslStoreProbe.Unmeasured();
        }

        return certificate is null
            ? SslStoreProbe.NotFound()
            : SslStoreProbe.Found(
                certificate.Thumbprint ?? string.Empty,
                certificate.NotAfter.ToUniversalTime(),
                ResolveCertificateSource(certificate));
    }

    public Task<SslWorkspaceDto> SetHttpsRedirectAsync(SetHttpsRedirectRequest request, CancellationToken cancellationToken = default) =>
        SetHttpsRedirectCoreAsync(request, null, cancellationToken);

    public Task<SslWorkspaceDto> SetHttpsRedirectAsync(SetHttpsRedirectRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken = default) =>
        SetHttpsRedirectCoreAsync(request, beforeMutation, cancellationToken);

    private async Task<SslWorkspaceDto> SetHttpsRedirectCoreAsync(SetHttpsRedirectRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        using var target = await SiteMutationGuard.CaptureAsync(iisSiteService, siteGates, request.SiteName, cancellationToken);
        using var reservation = await target.ReserveAsync(beforeMutation, cancellationToken);
        var site = target.Site;
        async Task ValidateAsync(CancellationToken token)
        {
            var current = await target.ValidateAsync(token);
            if (beforeMutation is not null) await beforeMutation(current, token);
            token.ThrowIfCancellationRequested();
        }
        Task ApproveAsync(CancellationToken token) => target.RunAsync(_ => ValidateAsync(token), token);
        async Task<CommandResult> RunCommandAsync(string script, CancellationToken token)
        {
            CommandResult? result = null;
            await target.RunAsync(async _ =>
            {
                await ValidateAsync(token);
                result = await RedirectCommand(script, token);
            }, token);
            return result!;
        }
        Task PublishAsync(Func<Task> publication) => target.RunAsync(async _ =>
        {
            await ValidateAsync(cancellationToken);
            await publication();
        }, cancellationToken);
        try
        {
            if (!await ApplyHttpsRedirectCoreAsync(site.Name, request.Enabled, cancellationToken, ApproveAsync, RunCommandAsync))
                throw new AppException("ssl.https_redirect_failed", 502, 502);
            var workspace = await GetSiteWorkspaceCoreAsync(site.Name, ValidateAsync, cancellationToken, PublishAsync);
            await ApproveAsync(cancellationToken);
            if (!workspace.HttpsRedirectMeasured || workspace.HttpsRedirectEnabled != request.Enabled)
                throw new AppException("ssl.https_redirect_failed", 502, 502);
            return workspace;
        }
        catch (OperationCanceledException) { throw; }
        catch (AppException) { throw; }
        catch (Exception exception)
        {
            throw new AppException("ssl.https_redirect_failed", 502, 502, exception.Message);
        }
    }

    // HTTP→HTTPS kalıcı (301) yönlendirme kuralını (URL Rewrite) ekler/kaldırır. ACME HTTP-01
    // challenge yolu (/.well-known/acme-challenge/) HARİÇ tutulur ki Let's Encrypt yenilemesi
    // bozulmasın. Sertifika alma yolları best-effort sonucunu yok sayabilir; açık ayar değişikliği
    // ise bu sonucu ve geri okumayı birlikte doğrulamadan başarı bildirmez.
    private Task<bool> ApplyHttpsRedirectAsync(string siteName, bool enabled, CancellationToken cancellationToken) =>
        ApplyHttpsRedirectCoreAsync(siteName, enabled, cancellationToken, null);

    private async Task<bool> ApplyHttpsRedirectCoreAsync(string siteName, bool enabled, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeMutation,
        Func<string, CancellationToken, Task<CommandResult>>? commandRunner = null)
    {
        var site = siteName.Replace("'", "''");
        /*
         * $ErrorActionPreference = 'Stop' EKLENDI — ve bu, ayni surumde kapatilan hata sinifinin
         * bir ornegi daha.
         *
         * ESKI HAL: `Import-Module`'da `-ErrorAction Stop` vardi ama alttaki sekiz
         * `Set-/Add-WebConfigurationProperty` cagrisinin hicbirinde yoktu. PowerShell'de bu
         * cmdlet'lerin hatalari SONLANDIRICI DEGILDIR: ortadaki bir cagri patlasa bile betik
         * akmaya devam eder ve sondaki kosulsuz `'OK'` basilir. Kural YARIM kalir -- `name`li bir
         * kural elemani vardir ama `action` hic yazilmamistir -- ve panel "yonlendirme acik" der.
         * Gercekte hicbir istek yonlendirilmez.
         *
         * Iki katman birden konuldu, cunku tek basina hicbiri yetmez:
         *   (1) Stop  -> yarida patlayan cagri betigi DUSURUR, cikis kodu 0 olmaz.
         *   (2) geri okuma -> hata firlatmadan eksik kalan bir durum da yakalanir. Bu deponun
         *       tekrarlanan dersi: "hata vermedi" ile "oldu" ayni sey degildir.
         *
         * `Clear-WebConfiguration`in kendi `-ErrorAction SilentlyContinue`i BILEREK duruyor:
         * kural yoksa temizleme hata verir ve bu normaldir. Cmdlet duzeyindeki secim tercihi ezer.
         */
        var script = enabled
            ? $$"""
                $ErrorActionPreference = 'Stop'
                Import-Module WebAdministration -ErrorAction Stop
                if (-not (Get-WebGlobalModule | Where-Object { $_.Name -eq 'RewriteModule' })) { 'NOREWRITE'; return }
                $site = '{{site}}'; $f = 'system.webServer/rewrite/rules'; $name = 'AdaPanel-ForceHTTPS'
                Clear-WebConfiguration -pspath "IIS:\Sites\$site" -filter "$f/rule[@name='$name']" -ErrorAction SilentlyContinue
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter $f -name '.' -value @{name=$name; stopProcessing='True'}
                $r = "$f/rule[@name='$name']"
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/match" -name 'url' -value '(.*)'
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/conditions" -name '.' -value @{input='{HTTPS}'; pattern='off'}
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/conditions" -name '.' -value @{input='{REQUEST_URI}'; pattern='^/\.well-known/acme-challenge/'; negate='True'}
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'type' -value 'Redirect'
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'url' -value 'https://{HTTP_HOST}/{R:1}'
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'redirectType' -value 'Permanent'
                # YAZDIGINI GERI OKU. Bu satirlar olmadan betik, kural YARIM yazilmis olsa bile
                # 'OK' basiyordu (gerekcesi asagida, $ErrorActionPreference notunda).
                $adaKural = Get-WebConfiguration -pspath "IIS:\Sites\$site" -filter $r
                $adaEylem = Get-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'type' -ErrorAction SilentlyContinue
                $adaDesen = Get-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/match" -name 'url' -ErrorAction SilentlyContinue
                if (-not $adaKural) { throw "kural yazildi ama geri okunamadi: $name" }
                if ("$($adaEylem.Value)" -ne 'Redirect') { throw "kural YARIM: action/type='$($adaEylem.Value)' (beklenen Redirect)" }
                if ([string]::IsNullOrWhiteSpace("$($adaDesen.Value)")) { throw "kural YARIM: match/url bos" }
                'OK'
                """
            : $$"""
                $ErrorActionPreference = 'Stop'
                Import-Module WebAdministration -ErrorAction Stop
                if (-not (Get-WebGlobalModule | Where-Object { $_.Name -eq 'RewriteModule' })) { 'OK'; return }
                $site = '{{site}}'
                $rules = Get-WebConfiguration -pspath "IIS:\Sites\$site" -filter 'system.webServer/rewrite/rules' -ErrorAction Stop
                if (@($rules.Collection | Where-Object { $_.name -eq 'AdaPanel-ForceHTTPS' }).Count -gt 0) {
                    Clear-WebConfiguration -pspath "IIS:\Sites\$site" -filter "system.webServer/rewrite/rules/rule[@name='AdaPanel-ForceHTTPS']" -ErrorAction Stop
                }
                'OK'
                """;

        async Task ApproveAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeMutation is not null) await beforeMutation(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        async Task<CommandResult> RunAsync()
        {
            await ApproveAsync();
            var outcome = await (commandRunner ?? RedirectCommand)(script, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return outcome;
        }
        var result = await RunAsync();

        // URL Rewrite modülü yoksa panel bileşeni olarak OTOMATİK kur, sonra kuralı tekrar uygula.
        // (url-rewrite components.json'da default:true; ama kullanıcı ilk kurulumda önerilenleri
        // atlamışsa bile yönlendirme çalışsın.)
        if (enabled && result.Success && result.StandardOutput.Trim() == "NOREWRITE")
        {
            logger.LogInformation("URL Rewrite modülü kurulu değil; {Site} için otomatik kuruluyor (url-rewrite).", siteName);
            await ApproveAsync();
            var installationSucceeded = false;
            try
            {
                var installed = await componentBootstrapper.InstallAsync("url-rewrite", null, null, force: false, cancellationToken: cancellationToken);
                installationSucceeded = string.Equals(installed.Status, "Installed", StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning("url-rewrite otomatik kurulumu başarısız ({Site}): {Error}",
                    siteName, SecretRedactor.Redact(exception.Message));
            }
            // Installer failure is best-effort; authority rejection is not. Keep the
            // approval outside that catch and release IIS during component installation.
            await ApproveAsync();
            if (!installationSucceeded) return false;
            result = await RunAsync();
        }

        if (!result.Success)
        {
            logger.LogWarning("HTTP->HTTPS yönlendirme {State} ayarlanamadı (site {Site}): {Error}",
                enabled ? "açma" : "kapatma", siteName, SecretRedactor.Redact(result.StandardError));
        }
        else if (result.StandardOutput.Contains("NOREWRITE", StringComparison.Ordinal))
        {
            logger.LogWarning("URL Rewrite modülü kurulu değil ve otomatik kurulamadı; {Site} için HTTP->HTTPS yönlendirme uygulanamadı.", siteName);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result.Success && result.StandardOutput.Trim() == "OK";
    }

    private async Task<bool> IsHttpsRedirectEnabledAsync(string siteName, CancellationToken cancellationToken) =>
        await ReadHttpsRedirectAsync(siteName, cancellationToken)
            ?? throw new AppException("ssl.https_redirect_failed", 502, 502);

    private async Task<bool?> ReadHttpsRedirectAsync(string siteName, CancellationToken cancellationToken)
    {
        var site = siteName.Replace("'", "''");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module WebAdministration -ErrorAction Stop
            if (-not (Get-WebGlobalModule | Where-Object { $_.Name -eq 'RewriteModule' })) { 'NO'; return }
            $site = '{{site}}'
            $r = "system.webServer/rewrite/rules/rule[@name='AdaPanel-ForceHTTPS']"
            $rules = Get-WebConfiguration -pspath "IIS:\Sites\$site" -filter 'system.webServer/rewrite/rules' -ErrorAction Stop
            $rule = @($rules.Collection | Where-Object { $_.name -eq 'AdaPanel-ForceHTTPS' })
            if ($rule.Count -eq 0) { 'NO'; return }
            # KURALIN VARLIGI YETMEZ, TAM OLMASI GEREKIR.
            # Eski hal yalnizca "bu ADDA bir kural var mi" diye soruyordu. Yarim yazilmis bir kural
            # (ad eklenmis, action hic yazilmamis) da bu soruya EVET der -- yani panel hicbir istegi
            # yonlendirmeyen bir yapilandirmayi "yonlendirme acik" diye gosterirdi. Yazma yolundaki
            # kosulsuz 'OK' ile birlikte, iki taraf birbirinin yalanini dogruluyordu.
            $eylem = Get-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'type' -ErrorAction Stop
            $enabled = Get-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter $r -name 'enabled' -ErrorAction Stop
            if ("$($enabled.Value)" -eq 'True' -and "$($eylem.Value)" -eq 'Redirect') { 'YES' } else { 'NO' }
            """;
        try
        {
            var result = await RedirectCommand(script, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success) return null;
            return result.StandardOutput.Trim() switch { "YES" => true, "NO" => false, _ => null };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("HTTP->HTTPS yönlendirme durumu okunamadı (site {Site}): {Error}",
                siteName, SecretRedactor.Redact(exception.Message));
            return null;
        }
    }

    public async Task<bool> TrySecureHostAsync(string siteName, string hostName, string contactEmail, CancellationToken cancellationToken = default)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);

            // 1) host'u kapsayan geçerli bir sertifika zaten varsa (ör. yüklenen wildcard) onu bağla.
            using var existing = IssueCertificateLookup(hostName);
            if (existing is not null && existing.NotAfter > DateTime.Now)
            {
                await EnsureHttpsBindingAsync(site.Name, hostName, 443, existing, cancellationToken);
                if (mailService is not null && ResolveCertificateSource(existing) != "self-signed")
                    await mailService.SyncWebCertificateAsync(hostName, cancellationToken: cancellationToken);
                return true;
            }

            // 2) yoksa Let's Encrypt dene. ÖNCE host DNS'te çözülüyor mu bak — çözülmüyorsa win-acme
            // ~120 sn takılır; hızlıca atla ki paylaşımlı site cert isteğini yavaşlatmasın.
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostName, cancellationToken);
                if (addresses.Length == 0)
                {
                    return false;
                }
            }
            catch
            {
                return false; // host çözülmüyor → Let's Encrypt denemeye gerek yok
            }

            var issued = await TryIssueWithWinAcmeAsync(site, hostName, contactEmail, false, cancellationToken);
            if (issued.Certificate is null)
            {
                logger.LogInformation("Otomatik SSL alinamadi ({Host}): {Reason}", hostName, issued.FailureReason);
                return false;
            }

            using var certificate = issued.Certificate;
            PersistIssuedCertificate(certificate);
            await EnsureHttpsBindingAsync(site.Name, hostName, 443, certificate, cancellationToken);
            if (mailService is not null && ResolveCertificateSource(certificate) != "self-signed")
                await mailService.SyncWebCertificateAsync(hostName, cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Host '{Host}' güvenli (HTTPS) yapılamadı (best-effort).", hostName);
            return false;
        }
    }

    // --- Wildcard sertifika (DNS-01) — Plesk paritesi ---
    // win-acme "script" doğrulama eklentisiyle: panel, kendi Windows DNS'ine _acme-challenge TXT
    // kaydını ekleyen/silen bir PS scripti üretir; win-acme create/delete eylemleriyle çağırır.
    // CLI sözleşmesi (--source manual, --validation script, --dnsscript, create {Identifier}
    // {RecordName} {Token}) VPS'tekiyle AYNI binary'de (sha256 f4dc3b14...) yerelde doğrulandı.
    // ÖN KOŞUL: alan adının bölgesi panelin DNS'inde birincil bölge olarak yayında olmalı
    // (DNS ekranından); değilse script "bölge yok" der ve hata detayında görünür.

    private const string Dns01ScriptContent =
        """
        param(
            [Parameter(Position = 0)][string]$Action,
            [Parameter(Position = 1)][string]$Identifier,
            [Parameter(Position = 2)][string]$RecordName,
            [Parameter(Position = 3)][string]$Token
        )
        $ErrorActionPreference = 'Stop'
        $fqdn = $RecordName.TrimEnd('.')

        # En uzun eslesen birincil bolge (ornek: _acme-challenge.sub.example.com -> example.com).
        $zone = Get-DnsServerZone |
            Where-Object { -not $_.IsAutoCreated -and $_.ZoneType -eq 'Primary' -and ($fqdn -eq $_.ZoneName -or $fqdn.EndsWith('.' + $_.ZoneName)) } |
            Sort-Object { $_.ZoneName.Length } -Descending |
            Select-Object -First 1
        if (-not $zone) { throw "AdaPanel DNS-01: '$fqdn' icin panel DNS'inde birincil bolge bulunamadi. Once DNS ekranindan bolgeyi yayinlayin." }

        $name = if ($fqdn -eq $zone.ZoneName) { '@' } else { $fqdn.Substring(0, $fqdn.Length - $zone.ZoneName.Length - 1) }

        if ($Action -eq 'create') {
            Add-DnsServerResourceRecord -ZoneName $zone.ZoneName -Txt -Name $name -DescriptiveText $Token -TimeToLive ([TimeSpan]::FromSeconds(60)) | Out-Null
        }
        elseif ($Action -eq 'delete') {
            Get-DnsServerResourceRecord -ZoneName $zone.ZoneName -Name $name -RRType 'TXT' -ErrorAction SilentlyContinue |
                Where-Object { $_.RecordData.DescriptiveText -eq $Token } |
                Remove-DnsServerResourceRecord -ZoneName $zone.ZoneName -Force -ErrorAction SilentlyContinue
        }
        else { throw "AdaPanel DNS-01: bilinmeyen eylem '$Action'." }
        """;

    // DNS-01 ÖN KONTROLÜ (preflight). Neden gerekli: win-acme her deneme için ~5 dk boyunca
    // "No TXT records found / retry" döngüsüne girer, sonunda Let's Encrypt "query timed out"
    // der ve bu BAŞARISIZ doğrulama LE hesap limitine yazılır. Oysa kayıt panelin DNS'ine
    // yazılmıştır; sorun kaydın DIŞARIDAN görünmemesidir. Bunu önceden, 1 dakikanın altında,
    // gerçek nedeni söyleyerek tespit ediyoruz:
    //   1) bölge panelin DNS'inde birincil olarak var mı,
    //   2) alan adının NS delegasyonu (halka açık çözümleyiciden) var mı,
    //   3) o NS'ler gerçekten bu sunucunun yazdığı TXT'yi yanıtlıyor mu (53/UDP açık + servis ayakta).
    private const string Dns01PreflightScript =
        """
        $ErrorActionPreference = 'Continue'
        $result = [ordered]@{
            zone = ''
            nsNames = @()
            nsIps = @()
            localIps = @()
            publicVisible = $false
            authoritativeVisible = $false
            notes = @()
        }

        try {
            $zone = Get-DnsServerZone -ErrorAction Stop |
                Where-Object { -not $_.IsAutoCreated -and $_.ZoneType -eq 'Primary' -and ($domain -eq $_.ZoneName -or $domain.EndsWith('.' + $_.ZoneName)) } |
                Sort-Object { $_.ZoneName.Length } -Descending |
                Select-Object -First 1
            if ($zone) { $result.zone = $zone.ZoneName }
        }
        catch { $result.notes += "dns-role: $($_.Exception.Message)" }

        $result.localIps = @(
            Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
                Select-Object -ExpandProperty IPAddress
        )

        foreach ($resolver in @('1.1.1.1', '8.8.8.8')) {
            if ($result.nsNames.Count -gt 0) { break }
            try {
                $ns = Resolve-DnsName -Name $domain -Type NS -Server $resolver -DnsOnly -ErrorAction Stop |
                    Where-Object { $_.NameHost } |
                    Select-Object -ExpandProperty NameHost -Unique
                if ($ns) { $result.nsNames = @($ns) }
            }
            catch { $result.notes += "ns-query($resolver): $($_.Exception.Message)" }
        }

        foreach ($name in $result.nsNames) {
            try {
                $ips = Resolve-DnsName -Name $name -Type A -Server '1.1.1.1' -DnsOnly -ErrorAction Stop |
                    Where-Object { $_.IPAddress } |
                    Select-Object -ExpandProperty IPAddress -Unique
                foreach ($ip in $ips) { if ($result.nsIps -notcontains $ip) { $result.nsIps += $ip } }
            }
            catch { $result.notes += "ns-resolve($name): $($_.Exception.Message)" }
        }

        if ($result.zone) {
            $token = [guid]::NewGuid().ToString('N')
            $label = '_adapanel-acme-check'
            $probeFqdn = "$label.$domain"
            $relative = if ($domain -eq $result.zone) {
                $label
            } else {
                $label + '.' + $domain.Substring(0, $domain.Length - $result.zone.Length - 1)
            }

            $written = $false
            try {
                Add-DnsServerResourceRecord -ZoneName $result.zone -Txt -Name $relative -DescriptiveText $token -TimeToLive ([TimeSpan]::FromSeconds(30)) -ErrorAction Stop | Out-Null
                $written = $true
            }
            catch { $result.notes += "probe-write: $($_.Exception.Message)" }

            if ($written) {
                foreach ($ip in $result.nsIps) {
                    try {
                        $answer = Resolve-DnsName -Name $probeFqdn -Type TXT -Server $ip -DnsOnly -ErrorAction Stop
                        if (@($answer | ForEach-Object { $_.Strings }) -contains $token) {
                            $result.authoritativeVisible = $true
                            break
                        }
                    }
                    catch { $result.notes += "auth-query($ip): $($_.Exception.Message)" }
                }

                for ($i = 0; $i -lt 3; $i++) {
                    try {
                        $answer = Resolve-DnsName -Name $probeFqdn -Type TXT -Server '1.1.1.1' -DnsOnly -ErrorAction Stop
                        if (@($answer | ForEach-Object { $_.Strings }) -contains $token) {
                            $result.publicVisible = $true
                            break
                        }
                    }
                    catch { }
                    Start-Sleep -Seconds 2
                }

                try {
                    Get-DnsServerResourceRecord -ZoneName $result.zone -Name $relative -RRType 'TXT' -ErrorAction SilentlyContinue |
                        Where-Object { $_.RecordData.DescriptiveText -eq $token } |
                        Remove-DnsServerResourceRecord -ZoneName $result.zone -Force -ErrorAction SilentlyContinue
                }
                catch { $result.notes += "probe-cleanup: $($_.Exception.Message)" }
            }
        }

        $result | ConvertTo-Json -Depth 4 -Compress
        """;

    private sealed class Dns01Preflight
    {
        public string Zone { get; init; } = string.Empty;

        public string[] NsNames { get; init; } = [];

        public string[] NsIps { get; init; } = [];

        public string[] LocalIps { get; init; } = [];

        public bool PublicVisible { get; init; }

        public bool AuthoritativeVisible { get; init; }

        public string[] Notes { get; init; } = [];
    }

    private async Task EnsureDns01ResolvableAsync(string domain, CancellationToken cancellationToken)
    {
        // Alan adı doğrudan PowerShell'e gömüldüğü için katı doğrulama (komut enjeksiyonu yok).
        if (!System.Text.RegularExpressions.Regex.IsMatch(domain, "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$"))
        {
            throw new AppException("ssl.host_name_required", 400, 400);
        }

        var script = $"$domain = '{domain}'\r\n{Dns01PreflightScript}";
        var run = await DnsPreflightCommand(script, cancellationToken);

        Dns01Preflight? preflight = null;
        try
        {
            var json = run.StandardOutput.Trim();
            var start = json.IndexOf('{');
            if (start >= 0)
            {
                preflight = System.Text.Json.JsonSerializer.Deserialize<Dns01Preflight>(
                    json[start..],
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DNS-01 ön kontrol çıktısı ayrıştırılamadı; doğrulama yine denenecek.");
        }

        if (preflight is null)
        {
            // Ön kontrol çalıştırılamadıysa (ör. DnsServer/DnsClient modülü yok) yolu KAPATMA:
            // win-acme yine denenir, hata detayı kullanıcıya döner.
            logger.LogWarning(
                "DNS-01 ön kontrolü yapılamadı (çıkış {ExitCode}). Doğrulama doğrudan deneniyor. {Error}",
                run.ExitCode,
                run.StandardError);
            return;
        }

        var detail = new StringBuilder()
            .Append("domain=").Append(domain)
            .Append("; zone=").Append(string.IsNullOrWhiteSpace(preflight.Zone) ? "(none)" : preflight.Zone)
            .Append("; ns=").Append(preflight.NsNames.Length == 0 ? "(none)" : string.Join(",", preflight.NsNames))
            .Append("; nsIp=").Append(preflight.NsIps.Length == 0 ? "(unresolved)" : string.Join(",", preflight.NsIps))
            .Append("; serverIp=").Append(string.Join(",", preflight.LocalIps))
            .Append("; authoritativeAnswer=").Append(preflight.AuthoritativeVisible)
            .Append("; publicAnswer=").Append(preflight.PublicVisible);

        if (preflight.Notes.Length > 0)
        {
            detail.Append("; notes=").Append(string.Join(" | ", preflight.Notes.Take(6)));
        }

        var details = detail.ToString();

        if (string.IsNullOrWhiteSpace(preflight.Zone))
        {
            throw new AppException("ssl.dns01_zone_missing", 409, 409, details);
        }

        if (preflight.NsNames.Length == 0)
        {
            throw new AppException("ssl.dns01_no_delegation", 409, 409, details);
        }

        if (preflight.PublicVisible || preflight.AuthoritativeVisible)
        {
            if (!preflight.PublicVisible)
            {
                logger.LogInformation(
                    "DNS-01 ön kontrolü: yetkili sunucu TXT'yi yanıtlıyor ama genel çözümleyici henüz görmedi (yayılma). Devam ediliyor. {Details}",
                    details);
            }

            return;
        }

        // Kayıt panelin DNS'ine yazıldı, delegasyon var, ama delege edilen NS'ler kaydı YANITLAMIYOR.
        // BU NOKTADA EN SIK GERÇEK NEDEN (2026-07-23'te bu sunucuda yaşandı): DNS servisi çalışıyor
        // ama genel IP'ye bind olmamış. Onu ayırt edip kullanıcıyı tek tık onarıma yönlendiriyoruz;
        // aksi halde "NS kayıtlarınız yanlış" der ve kullanıcı boşuna registrar'da arar.
        try
        {
            var dnsHealth = await dnsService.GetServerHealthAsync(cancellationToken);
            if (dnsHealth.NeedsRepair)
            {
                throw new AppException("ssl.dns01_not_listening", 409, 409, $"{details}; dns={dnsHealth.Details}");
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DNS sunucu bind kontrolü yapılamadı; genel DNS-01 hatası dönülüyor.");
        }

        throw new AppException("ssl.dns01_ns_unreachable", 409, 409, details);
    }

    private async Task<SslCertificateDto> IssueWildcardAsync(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        IssueLetsEncryptRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> approve,
        Func<Func<Task>, Task> mutate,
        Func<Task> applyRedirect,
        Func<SslCertificateDto, Task> publish)
    {
        // Wildcard'da self-signed'a SESSİZ düşme YOK: kullanıcı bilinçli DNS-01 istedi, hata gerçek nedeniyle döner.
        // Apex türetme: "*." ve baştaki "www." atılır (kullanıcı listeden www'lu hostu seçmiş olabilir).
        var domain = ResolveHostName(site, request.HostName).TrimStart('*').TrimStart('.').ToLowerInvariant();
        if (domain.StartsWith("www.", StringComparison.Ordinal))
        {
            domain = domain[4..];
        }
        if (string.IsNullOrWhiteSpace(request.ContactEmail))
        {
            throw new AppException("ssl.contact_email_required", 400, 400);
        }

        // E-posta wacs.exe'nin KOMUT SATIRINA giriyor ve (host'un aksine) hiçbir yerde
        // doğrulanmıyordu — bkz. AcmeArgumentRules.
        if (!AcmeArgumentRules.IsCommandSafeEmail(request.ContactEmail))
        {
            throw new AppException("ssl.contact_email_invalid", 400, 400);
        }

        var wacs = await ResolveWinAcmeExecutableAsync(cancellationToken);
        await approve(cancellationToken);
        if (string.IsNullOrWhiteSpace(wacs))
        {
            throw new AppException("ssl.winacme_missing", 409, 409);
        }

        // Doğrulamayı denemeden ÖNCE zincirin dışarıdan çalıştığını kanıtla (bkz. EnsureDns01ResolvableAsync).
        await EnsureDns01ResolvableAsync(domain, cancellationToken);
        await approve(cancellationToken);

        var scriptPath = fileStore.ResolvePath("tools/acme-dns01.ps1");
        await mutate(async () =>
        {
            fileStore.EnsureDirectory("tools");
            await File.WriteAllTextAsync(scriptPath, Dns01ScriptContent, cancellationToken);
        });

        var baseUri = request.UseStaging
            ? "https://acme-staging-v02.api.letsencrypt.org/"
            : "https://acme-v02.api.letsencrypt.org/";

        var args =
            $"--source manual --host \"*.{domain},{domain}\" " +
            $"--validation script --dnsscript \"{scriptPath}\" --dnsscriptparallelism 0 " +
            $"--installation iis --installationsiteid {site.Id} " +
            $"--emailaddress \"{request.ContactEmail.Trim()}\" --accepttos --baseuri {baseUri}";

        // DNS-01 turları (kayıt yazma + yetkili sunucu doğrulaması) HTTP-01'den uzun sürebilir.
        await approve(cancellationToken);
        var result = await IssueCommand(wacs, args, cancellationToken);
        await approve(cancellationToken);
        if (!result.Success)
        {
            var detail = (result.StandardError + "\r\n" + result.StandardOutput).Trim();
            throw new AppException("ssl.wildcard_failed", 502, 502, detail.Length > 2000 ? detail[^2000..] : detail);
        }

        using var certificate = IssueCertificateLookup($"*.{domain}")
            ?? throw new AppException("ssl.wildcard_failed", 502, 502, "win-acme succeeded but the wildcard certificate was not found in the store.");

        await mutate(() => { PersistIssuedCertificate(certificate); return Task.CompletedTask; });
        await applyRedirect();
        await approve(cancellationToken);

        // Joker sertifikada bağlamayı win-acme kuruyor ve host başlığı "*.domain" OLMAK ZORUNDA
        // DEĞİL (alt alan adı başına ayrı bağlama açabilir). O yüzden burada belirli bir host
        // aranmaz; sitede HERHANGİ bir HTTPS bağlaması olması sorulur.
        var dto = await OlcVeIsleAsync(
            ToDto(site.Name, $"*.{domain}", certificate, ResolveCertificateSource(certificate), null),
            site.Name, string.Empty, 443, certificate.Thumbprint ?? string.Empty, cancellationToken);
        await publish(dto);
        if (mailService is not null && dto.Source != "self-signed")
            await mailService.SyncWebCertificateAsync(dto.HostName, approve, cancellationToken);
        return dto;
    }

    /// <summary>Let's Encrypt denemesinin sonucu: sertifika ya da NEDEN alınamadığı.</summary>
    private sealed record AcmeIssueResult(X509Certificate2? Certificate, string FailureReason);

    private async Task<AcmeIssueResult> TryIssueWithWinAcmeAsync(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        string hostName,
        string contactEmail,
        bool useStaging,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeMutation = null)
    {
        var wacs = await ResolveWinAcmeExecutableAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        if (string.IsNullOrWhiteSpace(wacs))
        {
            return new AcmeIssueResult(null, "ssl.fallback.winacme_missing");
        }

        if (string.IsNullOrWhiteSpace(contactEmail))
        {
            return new AcmeIssueResult(null, "ssl.fallback.email_missing");
        }

        // SINIK DOĞRULAMASI. İki değer de wacs.exe'nin KOMUT SATIRINA giriyor; içine boşluk
        // koyabilen taraf win-acme'ye kendi argümanını (`--validation script --dnsscript ...`)
        // ekler ve o betik SYSTEM olarak koşar. Bkz. AcmeArgumentRules.
        //
        // Burada FIRLATILMIYOR, "alınamadı" deniyor: bu yol best-effort çağıranlardan da
        // (TrySecureHostAsync, otomatik yenileme) geliyor ve orada bir istisna, sertifikayla
        // ilgisi olmayan bir işlemi düşürürdü. Kendinden imzalıya düşer, sebep günlüğe yazılır.
        if (!AcmeArgumentRules.IsCommandSafeHost(hostName) || !AcmeArgumentRules.IsCommandSafeEmail(contactEmail))
        {
            logger.LogWarning("win-acme argümanı reddedildi ({Host}): host/e-posta komut satırına konulamaz.", hostName);
            return new AcmeIssueResult(null, "ssl.fallback.invalid_argument");
        }

        var baseUri = useStaging
            ? "https://acme-staging-v02.api.letsencrypt.org/"
            : "https://acme-v02.api.letsencrypt.org/";

        var before = IssueCertificateLookup(hostName);
        // Değerler ayrıca TIRNAKLANIYOR. Doğrulamadan sonra tırnak gereksiz görünür; kasten
        // konuyor: doğrulama bir gün gevşerse tek satırlık bir değişiklik sessizce argüman
        // enjeksiyonunu geri getirmesin. İki katman birbirinden bağımsız.
        var args = $"--target iis --siteid {site.Id} --host \"{hostName.Trim()}\" --installation iis --emailaddress \"{contactEmail.Trim()}\" --accepttos --baseuri {baseUri}";

        // SÜRE SINIRI 10 DAKİKA. Eskiden 120 saniyeydi ve bu, MEŞRU bir yayınlamayı ortasında
        // kesiyordu: Let's Encrypt doğrulaması DNS yayılmasını, yeniden denemeleri ve hız
        // sınırı beklemelerini içerebilir. Süre dolunca süreç öldürülüyor, sessizce kendinden
        // imzalı üretiliyor ve işlem BAŞARILI dönüyordu. Aynı dosyadaki joker karakter yolu
        // (IssueWildcardAsync) zaten 10 dakika kullanıyor; tekil yol ondan geri kalmıştı.
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await IssueCommand(wacs, args, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning(
                "win-acme sertifika alamadi ({Host}): cikis={ExitCode}. {Error}",
                hostName,
                result.ExitCode,
                Kirp(result.StandardError));
            return new AcmeIssueResult(null, "ssl.fallback.acme_failed");
        }

        var after = IssueCertificateLookup(hostName);
        var certificate = after?.Thumbprint != before?.Thumbprint ? after : before;

        // win-acme "basarili" cikis kodu dondurup ORTADA SERTIFIKA BIRAKMAYABILIR. Cikis kodunu
        // sonuc yerine koymak, bu dosyada bir kez daha duzeltilen hatanin ta kendisiydi.
        return certificate is null
            ? new AcmeIssueResult(null, "ssl.fallback.certificate_not_found")
            : new AcmeIssueResult(certificate, string.Empty);
    }

    private static string Kirp(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, 500)];

    /// <summary>
    /// Sertifikanın kaynağını SERTİFİKADAN okur.
    /// <para>
    /// Kendinden imzalı olmanın tanımı: veren ile konu aynıdır. Önceki sürüm bunun yerine
    /// <c>FriendlyName</c> içinde "Let's Encrypt" arıyordu; o adı win-acme yazar ve biçimi
    /// "[win-acme] example.com @ ..." olduğu için koşul hiçbir zaman doğru olmuyordu.
    /// </para>
    /// </summary>
    internal static string ResolveCertificateSource(X509Certificate2 certificate)
    {
        if (string.Equals(certificate.Issuer, certificate.Subject, StringComparison.OrdinalIgnoreCase))
        {
            return "self-signed";
        }

        return certificate.Issuer.Contains("Let's Encrypt", StringComparison.OrdinalIgnoreCase)
            || certificate.Issuer.Contains("ISRG", StringComparison.OrdinalIgnoreCase)
            ? "letsencrypt"
            // Başka bir sertifika otoritesi imzalamış: "self-signed" demek yanlış olurdu, çünkü
            // tarayıcı bu sertifikaya güvenebilir. Elle içe aktarılanlarla aynı sınıfa girer.
            : "manual";
    }

    /// <summary>
    /// Sitede gerçekten HTTPS bağlaması var mı — IIS'ten TAZE okunarak ölçülür.
    /// <para>
    /// Bağlama bu sınıfın kendi çağrıları sırasında oluşuyor; elde tutulan eski bir
    /// <see cref="AdaPanel.Core.Dtos.Sites.SiteDto"/> anlık görüntüsü (bağlama listesi
    /// değişmez bir kopyadır) onu göremez ve az önce kurulan bağlamayı "yok" gösterir.
    /// Bu cevap ssl.json'a yazıldığı için yanlışlık kalıcı olur.
    /// </para>
    /// </summary>
    private async Task<bool?> MeasureHttpsBindingAsync(string siteName, string hostName, CancellationToken cancellationToken)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            return MeasureHttpsBinding(site, hostName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Okuyamadık: "bağlama yok" DEMEYİZ, bilmediğimizi söyleriz.
            logger.LogWarning(exception, "HTTPS baglamasi olculemedi ({SiteName}).", siteName);
            return null;
        }
    }

    /// <summary>
    /// Bağlamayı VE üzerindeki sertifikayı birlikte ölçer; sonucu doğrudan DTO'ya işler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gerekçenin tamamı <see cref="SslBindingVerification"/> içindedir. Özeti: bağlama önce
    /// kurulur, sertifika sonra iliştirilir; iliştirme düşerse geriye SERTİFİKASIZ bir HTTPS
    /// bağlaması kalır ve site 443'te ölü olur. Eski ölçüm yalnız protokole baktığı için bu
    /// duruma "HTTPS bağlaması var" diyordu.
    /// </para>
    /// <para>
    /// <b>ATMAZ.</b> <c>ReadHttpsBindingsAsync</c> okuyamadığında <c>ssl.bindings_unreadable</c>
    /// fırlatır; burada yakalanır ve ölçüm "bilinmiyor"a düşer. Sertifika ZATEN alınmış ve
    /// bağlama ZATEN kurulmuş oluyor — bu noktada patlamak, olmuş bir işi geri alınamaz biçimde
    /// hata gibi göstermek olurdu.
    /// </para>
    /// </remarks>
    private async Task<SslCertificateDto> OlcVeIsleAsync(
        SslCertificateDto dto,
        string siteName,
        string hostName,
        int port,
        string thumbprint,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<IisHttpsBinding>? baglamalar = null;
        try
        {
            baglamalar = await ReadHttpsBindingsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "HTTPS baglama/sertifika olcumu yapilamadi ({SiteName}).", siteName);
        }

        var (baglamaVar, sertifikaBagli, ayrinti) =
            SslBindingVerification.Olc(baglamalar, siteName, hostName, port, thumbprint);

        dto.IsHttpsBindingPresent = baglamaVar;
        dto.IsCertificateBound = sertifikaBagli;
        dto.BindingDetail = ayrinti;

        if (sertifikaBagli == false)
        {
            logger.LogWarning(
                "SSL kuruldu ama sertifika baglamaya islenmedi ({SiteName}). {Ayrinti}",
                siteName,
                ayrinti);
        }

        return dto;
    }

    /// <summary>
    /// Verilen site anlık görüntüsünde HTTPS bağlaması var mı. Site okunamadıysa null döner.
    /// </summary>
    private static bool? MeasureHttpsBinding(AdaPanel.Core.Dtos.Sites.SiteDto? site, string hostName)
    {
        if (site?.Bindings is null || site.Bindings.Count == 0)
        {
            // Bağlama listesi hiç okunamadıysa "yok" DEMEYİZ; bilmediğimizi söyleriz.
            return null;
        }

        // hostName boş verildiğinde (joker sertifika) belirli bir host aranmaz: sitede HERHANGİ
        // bir HTTPS bağlaması olması yeterlidir.
        var herhangiBiri = string.IsNullOrWhiteSpace(hostName);

        return site.Bindings.Any(binding =>
            string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase)
            && (herhangiBiri
                || string.IsNullOrWhiteSpace(binding.HostName)
                || string.Equals(binding.HostName, hostName, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<string> ResolveWinAcmeExecutableAsync(CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == "win-acme" && item.Status == ComponentInstallStatus.Installed, cancellationToken);

        if (component is null || string.IsNullOrWhiteSpace(component.InstallDir) || !Directory.Exists(component.InstallDir))
        {
            return string.Empty;
        }

        return Directory.GetFiles(component.InstallDir, "wacs.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveHostName(AdaPanel.Core.Dtos.Sites.SiteDto site, string? requestedHostName)
    {
        var hostName = string.IsNullOrWhiteSpace(requestedHostName)
            ? site.Bindings.FirstOrDefault(binding => !string.IsNullOrWhiteSpace(binding.HostName))?.HostName
            : requestedHostName.Trim();

        if (string.IsNullOrWhiteSpace(hostName))
        {
            throw new AppException("ssl.host_name_required", 400, 400);
        }

        // BİRİNCİ KATMAN (erken ve anlaşılır hata). İkincisi TryIssueWithWinAcmeAsync /
        // IssueWildcardAsync içinde, tam SINIKTA duruyor; bu ikisi BAĞIMSIZ olsun diye buradaki
        // kontrol "yeter" sayılmadı — bu yola girmeyen çağıranlar da var (TrySecureHostAsync,
        // EnsurePreviewCertificateAsync).
        if (!AcmeArgumentRules.IsCommandSafeHost(hostName))
        {
            throw new AppException("ssl.host_name_invalid", 400, 400);
        }

        return hostName;
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string hostName, string siteName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(hostName);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var export = certificate.Export(X509ContentType.Pfx);
        var imported = new X509Certificate2(export, string.Empty, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
        imported.FriendlyName = $"AdaPanel SelfSigned {siteName}";
        return imported;
    }

    // IIS/HTTP.sys YALNIZCA LocalMachine\My deposundaki sertifikayı bağlayabilir. Bu yüzden
    // CurrentUser'a düşmeyiz (binding "bağlanamadı" verirdi); LocalMachine başarısızsa hatayı
    // açıkça yükseltiriz. Anahtarlı kopyayı koruruz; depodaki anahtarsız kopya ise
    // aynı sertifikanın özel anahtarlı PFX ile onarılmasını engellememeli.
    private static void PersistCertificate(X509Certificate2 certificate)
    {
        using var machineStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        machineStore.Open(OpenFlags.ReadWrite);

        SslCertificateImport.PersistToStore(machineStore, certificate);
    }

    private static X509Certificate2? FindBestCertificate(string hostName)
        => FindBestCertificate(hostName, out _);

    /// <param name="storesReadable">
    /// EN AZ BİR depo gerçekten açılıp okunabildi mi. false ise sonuç "sertifika yok" değil,
    /// "BİLMİYORUZ" demektir; çağıran bunu ayırt etmek zorundadır (bkz. <see cref="ProbeCertificate"/>).
    /// Eskiden depo açma hatası sessizce <c>continue</c> ile yutuluyordu ve dışarıdan
    /// "bulunamadı"dan ayırt edilemiyordu.
    /// </param>
    private static X509Certificate2? FindBestCertificate(string hostName, out bool storesReadable)
    {
        // win-acme, IIS kurulumunda LE sertifikasını LocalMachine\WebHosting deposuna koyar
        // (My DEĞİL). Önce WebHosting taranır; bulunamazsa My. Özel anahtarlı (bağlanabilir)
        // sertifika tercih edilir, yoksa son çare anahtarsız eşleşme.
        var stores = new (string Name, StoreLocation Location)[]
        {
            ("WebHosting", StoreLocation.LocalMachine),
            ("My", StoreLocation.LocalMachine),
            ("My", StoreLocation.CurrentUser)
        };

        storesReadable = false;
        var candidates = new List<X509Certificate2>();
        X509Certificate2? selected = null;
        try
        {
            foreach (var (name, location) in stores)
            {
                using var store = new X509Store(name, location);
                try
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    candidates.AddRange(store.Certificates.Cast<X509Certificate2>());
                    storesReadable = true;
                }
                catch (CryptographicException) { }
            }
            selected = SslCertificateSelection.Select(candidates, hostName);
            return selected;
        }
        finally
        {
            foreach (var certificate in candidates)
                if (!ReferenceEquals(certificate, selected)) certificate.Dispose();
        }
    }

    // IIS HTTPS binding'i artık PowerShell WebAdministration ile yapılır (New-WebBinding -SslFlags 1
    // + AddSslCertificate). Microsoft.Web.Administration reflection bazı IIS sürümlerinde SNI
    // sertifikası bağlamayı "bağlanamadı" ile reddediyordu; PowerShell yöntemi güvenilir çalışır.
    private async Task EnsureHttpsBindingAsync(string siteName, string hostName, int port, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var thumbprint = certificate.Thumbprint ?? string.Empty;
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new AppException("ssl.binding_failed", 500, 500, "Sertifika parmak izi (thumbprint) yok.");
        }

        var site = siteName.Replace("'", "''");
        var dn = (hostName ?? string.Empty).Replace("'", "''");
        var thumb = thumbprint.Replace("'", "''");

        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $ErrorActionPreference = 'Stop'
            $site = '{{site}}'; $dn = '{{dn}}'; $port = {{port}}; $thumb = '{{thumb}}'
            Get-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn -ErrorAction SilentlyContinue |
              Remove-WebBinding -ErrorAction SilentlyContinue
            if ($dn) {
              New-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn -SslFlags 1
              $b = Get-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn
            } else {
              New-WebBinding -Name $site -Protocol https -Port $port
              $b = Get-WebBinding -Name $site -Protocol https -Port $port
            }
            # Sertifika hangi depodaysa ORADAN bağla. win-acme LE sertifikasını WebHosting'e koyar;
            # self-signed/import ise My'da olur. Yanlış depo "güvenli değil" / boş binding'e yol açar.
            $store = 'My'
            if (-not (Test-Path "Cert:\LocalMachine\My\$thumb") -and (Test-Path "Cert:\LocalMachine\WebHosting\$thumb")) {
              $store = 'WebHosting'
            }
            $b.AddSslCertificate($thumb, $store)
            'OK'
            """;

        var result = await HttpsBindingCommand(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException(
                "ssl.binding_failed",
                500,
                500,
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        }
    }

    // =====================================================================================
    // HTTPS BAĞLAMASINI KALDIRMA
    //
    // Denetimde ölçülen durum: RemoveSslCertificateRequest DTO'su yazılmış ama servis metodu, uç ve
    // ekran hiç yapılmamıştı — yani panelde HTTPS bağlamasını kaldırmanın HİÇBİR yolu yoktu.
    // Kullanıcı yanlış sertifika bağladığında ya da alan adını bıraktığında elle IIS'e girmek
    // zorundaydı.
    //
    // Buradaki iki kural pazarlık konusu değildir:
    //  (1) Siteyi ERİŞİLEMEZ BIRAKMA. "Force HTTPS" açıkken son HTTPS bağlamasını kaldırmak,
    //      siteyi olmayan bir dinleyiciye yönlendirmek demektir; yönlendirme önce kapatılır,
    //      kapatılamıyorsa işlem yapılmaz.
    //  (2) Sertifikayı TAHMİNLE silme. "Başka site kullanıyor mu" sorusu IIS'ten OKUNARAK
    //      yanıtlanır; okunamıyorsa silinmez.
    // =====================================================================================

    public Task<RemoveSslCertificateResultDto> RemoveCertificateAsync(
        RemoveSslCertificateRequest request,
        CancellationToken cancellationToken = default) =>
        RemoveCertificateCoreAsync(request, null, cancellationToken);

    public Task<RemoveSslCertificateResultDto> RemoveCertificateAsync(
        RemoveSslCertificateRequest request, Func<SiteDto, CancellationToken, Task> beforeMutation,
        CancellationToken cancellationToken = default) =>
        RemoveCertificateCoreAsync(request, beforeMutation, cancellationToken);

    private async Task<RemoveSslCertificateResultDto> RemoveCertificateCoreAsync(
        RemoveSslCertificateRequest request, Func<SiteDto, CancellationToken, Task>? beforeMutation,
        CancellationToken cancellationToken)
    {
        var siteName = request.SiteName;
        var hostName = request.HostName;
        var port = request.Port <= 0 ? 443 : request.Port;
        var removeFromStore = request.RemoveCertificateFromStore;
        using var generation = await SiteMutationGuard.CaptureAsync(iisSiteService, siteGates, siteName, cancellationToken);
        var site = generation.Site;
        var originalState = await fileStore.ReadAsync(StatePath, Array.Empty<SslCertificateDto>(), cancellationToken);

        var allBindings = await ReadHttpsBindingsAsync(cancellationToken);
        var siteBindings = allBindings
            .Where(binding => string.Equals(binding.SiteName, site.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var (target, errorKey) = SslRemovalPlan.SelectTarget(siteBindings, hostName, port);
        if (target is null)
        {
            throw new AppException(errorKey, 404, errorKey == SslRemovalPlan.HostNameRequiredKey ? 400 : 404);
        }
        var expectedState = originalState.Where(item => IsRemovalRecord(item, target)).ToArray();

        // Keep this generation alive through native changes, metadata and final measurement.
        // Long reads do not hold IIS; every subsequent write revalidates under that gate.
        using var reservation = await generation.ReserveAsync(beforeMutation, cancellationToken);
        Task MutateAsync(Func<Task> operation, bool verifyBinding = false) => generation.RunAsync(async current =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (verifyBinding)
            {
                var measured = await ReadHttpsBindingsAsync(cancellationToken);
                var candidates = measured.Where(binding => string.Equals(binding.SiteName, site.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var (freshTarget, _) = SslRemovalPlan.SelectTarget(candidates, target.HostName, target.Port);
                if (freshTarget is null || !SameRemovalBinding(target, freshTarget)) throw RemovalTargetChanged();
                if (!SslRemovalPlan.IsLastHttpsBindingOfSite(siteBindings, target) &&
                    SslRemovalPlan.IsLastHttpsBindingOfSite(candidates, freshTarget))
                    throw RemovalTargetChanged();
                var state = await fileStore.ReadAsync(StatePath, Array.Empty<SslCertificateDto>(), cancellationToken);
                EnsureRemovalStateUnchanged(state, target, expectedState);
                // The extra measurement is another wait: authority follows it, before mutation.
                current = await generation.ValidateAsync(cancellationToken);
            }
            if (beforeMutation is not null) await beforeMutation(current, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }, cancellationToken);

        var result = new RemoveSslCertificateResultDto
        {
            SiteName = site.Name,
            HostName = target.HostName,
            Port = target.Port,
            CertificateThumbprint = target.Thumbprint
        };

        // (1) ÖNCE yönlendirme. Sıralama bilinçlidir: önce bağlama kaldırılsaydı, yönlendirme
        // kapatılana kadar site erişilemez kalırdı. Bu sırada ise en kötü ihtimalle site HTTPS'i
        // korur ama zorlaması kalkar — erişilebilirlik bozulmaz.
        var sonBaglama = SslRemovalPlan.IsLastHttpsBindingOfSite(siteBindings, target);
        if (sonBaglama && await IsHttpsRedirectEnabledAsync(site.Name, cancellationToken))
        {
            await MutateAsync(async () => { await ApplyHttpsRedirectAsync(site.Name, false, cancellationToken); }, verifyBinding: true);

            // Sertifika alma yolları yönlendirmeyi best-effort uygular; son HTTPS bağlamasını
            // silerken ise kapandığını VARSAYMAYIZ. Okuma belirsizse de bağlamaya dokunmayız.
            if (await IsHttpsRedirectEnabledAsync(site.Name, cancellationToken))
            {
                throw new AppException(
                    "ssl.remove_would_break_site",
                    409,
                    409,
                    $"Site '{site.Name}' HTTP->HTTPS yonlendirmesi kapatilamadi; son HTTPS baglamasi kaldirilirsa site erisilemez kalir.");
            }

            result.HttpsRedirectDisabled = true;
        }

        // (2) Bağlamayı kaldır ve KALKTIĞINI DOĞRULA.
        await MutateAsync(() => RemoveHttpsBindingAsync(site.Name, target, cancellationToken), verifyBinding: true);

        var afterBindings = await ReadHttpsBindingsAsync(cancellationToken);
        var halaVar = afterBindings.Any(binding =>
            string.Equals(binding.SiteName, target.SiteName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(binding.BindingInformation, target.BindingInformation, StringComparison.OrdinalIgnoreCase));
        if (halaVar)
        {
            throw new AppException("ssl.remove_binding_failed", 500, 500, target.BindingInformation);
        }

        result.BindingRemoved = true;

        // (3) Panel kaydı (ssl.json) temizlenir; yoksa liste öksüz bir sertifika göstermeye devam ederdi.
        await MutateAsync(() => RemoveCertificateStateAsync(target, expectedState, async token =>
        {
            var current = await generation.ValidateAsync(token);
            if (beforeMutation is not null) await beforeMutation(current, token);
        }, cancellationToken));

        // (4) Sertifika deposu kararı — ÖLÇÜMLE.
        var digerKullananlar = SslRemovalPlan.FindOtherUsersOfCertificate(afterBindings, target);
        result.CertificateUsedBy = digerKullananlar;

        if (!removeFromStore)
        {
            result.CertificateAction = "kept";
        }
        else
        {
            // Acquire before entering IIS: mail activation holds this lifecycle while its
            // authority callback briefly acquires IIS. Reversing that order would deadlock.
            using var mailOperation = await Mail.MailCertificateLifecycle.EnterAsync(
                fileStore.ResolvePath(Mail.MailCertificateStoreReference.StatePath), cancellationToken);
            await MutateAsync(async () =>
            {
                // JSON cleanup and approval may both wait. A new user, including a
                // recreation of the removed binding itself, must protect this certificate.
                var freshBindings = await ReadHttpsBindingsAsync(cancellationToken);
                var current = await generation.ValidateAsync(cancellationToken);
                if (beforeMutation is not null) await beforeMutation(current, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var users = SslRemovalPlan.FindCertificateUsers(freshBindings, target.Thumbprint);
                result.CertificateUsedBy = users;
                if (users.Length > 0)
                {
                    result.CertificateAction = "kept_in_use";
                    result.Detail = string.Join(", ", users);
                    return;
                }

                var mailUsage = await Mail.MailCertificateStoreReference.ReadAsync(
                    fileStore.ResolvePath(Mail.MailCertificateStoreReference.StatePath), target.Thumbprint, cancellationToken);
                if (mailUsage != Mail.MailCertificateStoreReference.Usage.None)
                {
                    result.CertificateAction = "kept_in_use";
                    result.CertificateUsedBy = [mailUsage == Mail.MailCertificateStoreReference.Usage.InUse
                        ? "SMTP TLS" : "SMTP TLS (status unknown)"];
                    result.Detail = result.CertificateUsedBy[0];
                    return;
                }
                current = await generation.ValidateAsync(cancellationToken);
                if (beforeMutation is not null) await beforeMutation(current, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var deletion = await TryDeleteCertificateAsync(target.Thumbprint, cancellationToken);
                result.CertificateAction = deletion.Action;
                result.Detail = deletion.Detail;
                result.CertificateUsedBy = deletion.UsedBy;
            });
        }

        // Workspace reconciliation can write ssl.json, so it shares the same authority boundary.
        await MutateAsync(async () =>
        {
            result.Workspace = await GetSiteWorkspaceCoreAsync(site.Name, async token =>
            {
                // The probe and JSON path lock may both await after MutateAsync checked us.
                // Its IIS gate is already held, so validate without acquiring it again.
                var current = await generation.ValidateAsync(token);
                if (beforeMutation is not null) await beforeMutation(current, token);
            }, cancellationToken);
        });
        return result;
    }

    /// <summary>
    /// TÜM sitelerin HTTPS bağlamalarını (parmak izi + depo dahil) IIS'ten okur.
    /// <para>
    /// Okunamazsa <c>ssl.bindings_unreadable</c> ile durur — boş liste DÖNMEZ. Boş liste dönmek
    /// "hiçbir site bu sertifikayı kullanmıyor" anlamına gelirdi ve sertifika silme kararını
    /// ölçüm yerine cehalete dayandırırdı.
    /// </para>
    /// </summary>
    internal const string HttpsBindingsScript = """
            Import-Module WebAdministration -ErrorAction Stop
            $ErrorActionPreference = 'Stop'
            $liste = @()
            foreach ($s in Get-Website) {
              foreach ($b in $s.Bindings.Collection) {
                if ($b.protocol -ne 'https') { continue }
                # SERTIFIKA YOK ile SERTIFIKAYI OKUYAMADIM ayni goruntuye DUSMEMELI. Ikisi de
                # bos parmak izine dusuyordu; "baglama var ama sertifikasi yok" (site 443'te olu)
                # ile "bakamadim" arasindaki fark tam da kullaniciya soylenecek cumleyi belirler.
                $thumb = ''
                $certOkundu = $true
                try {
                  $hash = $b.certificateHash
                  # WebAdministration exposes hexadecimal text; the managed IIS API uses byte[].
                  if ($hash -is [string]) {
                    $thumb = $hash.Trim()
                    if ($thumb -and $thumb -notmatch '^(?:[0-9a-fA-F]{2})+$') { throw 'Invalid certificate hash' }
                    $thumb = $thumb.ToLowerInvariant()
                  } elseif ($hash -is [byte[]]) {
                    $thumb = [BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
                  } elseif ($null -ne $hash) { throw 'Unsupported certificate hash type' }
                } catch { $thumb = ''; $certOkundu = $false }
                $store = ''
                try { $store = [string]$b.certificateStoreName } catch { }
                $liste += [pscustomobject]@{
                  siteName = [string]$s.Name
                  bindingInformation = [string]$b.bindingInformation
                  thumbprint = $thumb
                  certificateReadable = $certOkundu
                  storeName = $store
                }
              }
            }
            ConvertTo-Json -InputObject @($liste) -Depth 3 -Compress
            """;

    private async Task<IReadOnlyCollection<IisHttpsBinding>> ReadHttpsBindingsAsync(CancellationToken cancellationToken)
    {
        if (bindingReader is not null)
            return await bindingReader(cancellationToken)
                ?? throw new AppException("ssl.bindings_unreadable", 503, 503);
        var result = await ProcessRunner.RunPowerShellStrictAsync(HttpsBindingsScript, cancellationToken);
        if (!result.Success)
        {
            throw new AppException(
                "ssl.bindings_unreadable",
                503,
                503,
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        }

        var json = (result.StandardOutput ?? string.Empty).Trim();
        if (json.Length == 0)
        {
            // Gerçekten hiç HTTPS bağlaması yoksa ConvertTo-Json "[]" yazar. Boş çıktı, ölçümün
            // yapılamadığı anlamına gelir.
            throw new AppException("ssl.bindings_unreadable", 503, 503, "IIS baglama listesi bos dondu.");
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<IisHttpsBinding>>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new AppException("ssl.bindings_unreadable", 503, 503, Kirp(exception.Message));
        }
    }

    private async Task RemoveHttpsBindingAsync(string siteName, IisHttpsBinding target, CancellationToken cancellationToken)
    {
        var site = siteName.Replace("'", "''");
        var dn = target.HostName.Replace("'", "''");
        var bindingInformation = target.BindingInformation.Replace("'", "''");
        var thumbprint = target.Thumbprint.Replace("'", "''");
        var storeName = target.StoreName.Replace("'", "''");

        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $ErrorActionPreference = 'Stop'
            $site = '{{site}}'; $dn = '{{dn}}'; $port = {{target.Port}}
            $bindings = @(Get-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn -ErrorAction Stop)
            if ($bindings.Count -ne 1) { 'BINDING_CHANGED'; return }
            $b = $bindings[0]
            $hash = $b.certificateHash
            $thumb = ''
            if ($hash -is [string]) { $thumb = $hash.Trim() }
            elseif ($hash -is [byte[]]) { $thumb = [BitConverter]::ToString($hash).Replace('-', '') }
            elseif ($null -ne $hash) { 'BINDING_CHANGED'; return }
            if ([string]$b.bindingInformation -ine '{{bindingInformation}}' -or
                $thumb -ine '{{thumbprint}}' -or [string]$b.certificateStoreName -ine '{{storeName}}') {
              'BINDING_CHANGED'; return
            }
            $b | Remove-WebBinding -ErrorAction Stop
            'OK'
            """;

        var result = await RemoveBindingCommand(script, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Success && result.StandardOutput.Trim() == "BINDING_CHANGED") throw RemovalTargetChanged();
        if (!result.Success || result.StandardOutput.Trim() != "OK")
        {
            throw new AppException(
                "ssl.remove_binding_failed",
                500,
                500,
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        }
    }

    /// <summary>
    /// Sertifikayı LocalMachine depolarından siler. Çağrılmadan ÖNCE başka kullanan olmadığı
    /// ölçülmüş olmalıdır (bkz. <see cref="SslRemovalPlan.FindOtherUsersOfCertificate"/>).
    /// </summary>
    private async Task<(string Action, string Detail, string[] UsedBy)> TryDeleteCertificateAsync(string thumbprint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return ("remove_failed", "Sertifika parmak izi okunamadi.", []);
        }

        var thumb = thumbprint.Replace("'", "''");
        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $ErrorActionPreference = 'Stop'
            $thumb = '{{thumb}}'
            $users = @()
            $bindingsMeasured = $true
            # The managed inventory may predate process startup. Recheck ALL sites here;
            # a recreated target binding is a current user too.
            foreach ($site in @(Get-Website -ErrorAction Stop)) {
              foreach ($binding in @($site.Bindings.Collection)) {
                if ($binding.protocol -ne 'https') { continue }
                $used = $false
                try {
                  $hash = $binding.certificateHash
                  $current = ''
                  if ($hash -is [string]) {
                    $current = $hash.Trim()
                    if ($current -and $current -notmatch '^(?:[0-9a-fA-F]{2})+$') { throw 'Invalid certificate hash' }
                  } elseif ($hash -is [byte[]]) {
                    $current = [BitConverter]::ToString($hash).Replace('-', '')
                  } elseif ($null -ne $hash) { throw 'Unsupported certificate hash type' }
                  $used = $current -ieq $thumb
                } catch { $used = $true; $bindingsMeasured = $false }
                if ($used) { $users += ([string]$site.Name + ' (' + [string]$binding.bindingInformation + ')') }
              }
            }
            if ($users.Count -gt 0) {
              ConvertTo-Json -InputObject ([pscustomobject]@{
                status = 'in_use'; bindingsMeasured = $bindingsMeasured; storesMeasured = $false
                stores = @(); usedBy = @($users)
              }) -Compress -Depth 3
              return
            }
            $silinen = @()
            foreach ($store in 'My','WebHosting') {
              $yol = "Cert:\LocalMachine\$store\$thumb"
              if (Test-Path -LiteralPath $yol -ErrorAction Stop) {
                Remove-Item -LiteralPath $yol -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $yol -ErrorAction Stop) { throw 'Certificate deletion could not be verified.' }
                $silinen += $store
              }
            }
            $status = if ($silinen.Count -eq 0) { 'not_found' } else { 'removed' }
            ConvertTo-Json -InputObject ([pscustomobject]@{
              status = $status; bindingsMeasured = $true; storesMeasured = $true
              stores = @($silinen); usedBy = @()
            }) -Compress -Depth 3
            """;

        var result = await RemoveCertificateCommand(script, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            var detail = Kirp(SecretRedactor.Redact(string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput : result.StandardError));
            logger.LogWarning("Sertifika {Thumbprint} depodan silinemedi: {Error}", thumbprint, detail);
            return ("remove_failed", detail, []);
        }

        CertificateDeletionMeasurement? measurement = null;
        try
        {
            measurement = System.Text.Json.JsonSerializer.Deserialize<CertificateDeletionMeasurement>(
                result.StandardOutput, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException) { }

        if (measurement is { Status: "in_use", Stores.Length: 0, UsedBy.Length: > 0 } &&
            measurement.UsedBy.All(user => !string.IsNullOrWhiteSpace(user)))
            return ("kept_in_use", string.Join(", ", measurement.UsedBy), measurement.UsedBy);
        if (measurement is { Status: "removed", BindingsMeasured: true, StoresMeasured: true,
                Stores.Length: > 0, UsedBy.Length: 0 } &&
            measurement.Stores.All(store => store is "My" or "WebHosting") &&
            measurement.Stores.Distinct(StringComparer.Ordinal).Count() == measurement.Stores.Length)
            return ("removed", string.Join(",", measurement.Stores), []);
        if (measurement is { Status: "not_found", BindingsMeasured: true, StoresMeasured: true,
                Stores.Length: 0, UsedBy.Length: 0 })
            return ("remove_failed", "Sertifika depoda bulunamadi (zaten yok).", []);
        return ("remove_failed", "Sertifika silme sonucu dogrulanamadi.", []);
    }

    private sealed class CertificateDeletionMeasurement
    {
        public CertificateDeletionMeasurement() { }
        public string Status { get; set; } = string.Empty;
        public bool BindingsMeasured { get; set; }
        public bool StoresMeasured { get; set; }
        public string[]? Stores { get; set; }
        public string[]? UsedBy { get; set; }
    }

    private static AppException RemovalTargetChanged() => new("ssl.binding_changed", 409, 409);

    private static bool SameRemovalBinding(IisHttpsBinding expected, IisHttpsBinding current) =>
        expected.CertificateReadable && current.CertificateReadable &&
        string.Equals(expected.SiteName, current.SiteName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.BindingInformation, current.BindingInformation, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.Thumbprint, current.Thumbprint, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.StoreName, current.StoreName, StringComparison.OrdinalIgnoreCase);

    private static bool IsRemovalRecord(SslCertificateDto item, IisHttpsBinding target) =>
        string.Equals(item.SiteName, target.SiteName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(item.HostName, target.HostName, StringComparison.OrdinalIgnoreCase) &&
        (item.Port == target.Port || item.Port == 0);

    private static void EnsureRemovalStateUnchanged(SslCertificateDto[] records, IisHttpsBinding target,
        SslCertificateDto[] expected)
    {
        var current = records.Where(item => IsRemovalRecord(item, target)).ToList();
        if (current.Count != expected.Length) throw RemovalTargetChanged();
        foreach (var original in expected)
        {
            // Verification timestamps are refreshed by normal reads. Certificate identity
            // and material must still match; a renewed/imported replacement is foreign.
            var index = current.FindIndex(item =>
                string.Equals(item.Thumbprint, original.Thumbprint, StringComparison.OrdinalIgnoreCase) &&
                item.NotAfterUtc == original.NotAfterUtc && item.Source == original.Source &&
                item.Subject == original.Subject && item.FriendlyName == original.FriendlyName);
            if (index < 0) throw RemovalTargetChanged();
            current.RemoveAt(index);
        }
    }

    /// <summary>Only remove the captured certificate record, under the JSON update lock.</summary>
    private async Task RemoveCertificateStateAsync(IisHttpsBinding target, SslCertificateDto[] expected,
        Func<CancellationToken, Task> beforePublication, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(StatePath, Array.Empty<SslCertificateDto>(), existing =>
        {
            EnsureRemovalStateUnchanged(existing, target, expected);
            return existing.Where(item => !IsRemovalRecord(item, target)).ToArray();
        }, beforePublication, cancellationToken);
    }

    public async Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(StatePath, Array.Empty<SslCertificateDto>(), existing => existing
            .Where(item => !string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase))
            .ToArray(), cancellationToken);
    }

    private async Task UpsertAsync(SslCertificateDto dto, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforePublication = null)
    {
        // Atomik oku-değiştir-yaz (eşzamanlı SSL işlemlerinde kayıp güncelleme olmasın).
        await fileStore.UpdateAsync(StatePath, Array.Empty<SslCertificateDto>(), existing =>
        {
            var states = existing.ToList();
            var index = states.FindIndex(item =>
                string.Equals(item.SiteName, dto.SiteName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.HostName, dto.HostName, StringComparison.OrdinalIgnoreCase) &&
                item.Port == dto.Port);

            if (index >= 0)
            {
                states[index] = dto;
            }
            else
            {
                states.Add(dto);
            }

            return states.ToArray();
        }, beforePublication, cancellationToken);
    }

    private static SslCertificateDto ToDto(
        string siteName,
        string hostName,
        X509Certificate2 certificate,
        string source,
        bool? httpsBindingPresent)
    {
        return new SslCertificateDto
        {
            SiteName = siteName,
            HostName = hostName,
            Thumbprint = certificate.Thumbprint ?? string.Empty,
            FriendlyName = certificate.FriendlyName ?? string.Empty,
            Subject = certificate.Subject,
            NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
            Source = source,
            // ÖLÇÜLMÜŞ değer. Burada sabit "true" yazmak, site sağlığı ekranının 443 el sıkışması
            // başarısızken bile "https binding=True" demesine ve kullanıcıyı yanlış yöne
            // göndermesine yol açıyordu.
            IsHttpsBindingPresent = httpsBindingPresent,
            Port = 443,
            // Sertifika bu akışta AZ ÖNCE depoya yazıldı (PersistCertificate) — yani varlığı
            // tahmin değil, bu isteğin kendi ölçümü. Damgayı burada basmak, ilk uzlaştırmaya
            // kadar kaydın "hiç doğrulanmadı" görünmesini engeller.
            IsPresentInStore = true,
            LastVerifiedUtc = DateTime.UtcNow
        };
    }

    private static IDisposable CreateServerManager()
    {
        var assemblyPath = Environment.ExpandEnvironmentVariables(WebAdministrationAssemblyPath);
        if (!File.Exists(assemblyPath))
        {
            throw new AppException("sites.iis_not_available", 503, 503);
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var serverManagerType = assembly.GetType(ServerManagerTypeName)
            ?? throw new AppException("sites.iis_not_available", 503, 503);

        return Activator.CreateInstance(serverManagerType) as IDisposable
            ?? throw new AppException("sites.iis_not_available", 503, 503);
    }

    private static object GetPropertyObject(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            return property.GetValue(target)
                ?? throw new AppException("sites.iis_not_available", 503, 503);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static object? FindByName(object collection, string name)
    {
        return Enumerate(collection).FirstOrDefault(item =>
            string.Equals(GetStringProperty(item, "Name"), name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<object> Enumerate(object collection)
    {
        return collection is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object>()
            : [];
    }

    private static string GetStringProperty(object target, string propertyName)
    {
        return GetPropertyObject(target, propertyName).ToString() ?? string.Empty;
    }

    private static void SetPropertyValue(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            property.SetValue(target, value);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static object InvokeMethod(object target, string methodName, params object[] arguments)
    {
        try
        {
            return target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null,
                target,
                arguments)
                ?? throw new InvalidOperationException($"IIS method '{methodName}' returned no result.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static AppException MapIisException(Exception exception)
    {
        // Dile bağımsız (HRESULT + İngilizce) sınıflandırma — Türkçe Windows'ta da doğru çalışır.
        var message = Support.Win32ErrorHelper.Flatten(exception);
        if (Support.Win32ErrorHelper.IsAccessDenied(message))
        {
            return CreateDiagnosticException("sites.iis_permission_denied", 403, exception);
        }

        if (Support.Win32ErrorHelper.IsInUse(message))
        {
            return CreateDiagnosticException("sites.iis_configuration_locked", 409, exception);
        }

        if (message.Contains("section", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("applicationHost.config", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_install_required", 503, exception);
        }

        return CreateDiagnosticException("ssl.binding_failed", 500, exception);
    }

    private static AppException CreateDiagnosticException(string messageKey, int statusCode, Exception exception)
    {
        return new AppException(messageKey, statusCode, statusCode, BuildDiagnosticDetails(exception));
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        var details = exception.ToString();
        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private static string FlattenExceptionMessage(Exception exception)
    {
        var builder = new StringBuilder(exception.Message);
        var current = exception.InnerException;
        while (current is not null)
        {
            builder.Append(' ').Append(current.Message);
            current = current.InnerException;
        }

        return builder.ToString();
    }
}
