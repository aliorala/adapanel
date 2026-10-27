using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Real administrative controller/service and persisted settings; no native process or account is touched.</summary>
public sealed class FtpServerMutationLifecycleTests
{
    [Theory]
    [InlineData("ensure", "feature", "logout")]
    [InlineData("ensure", "feature", "password")]
    [InlineData("ensure", "feature", "disabled")]
    [InlineData("ensure", "feature", "role")]
    [InlineData("ensure", "preferences", "logout")]
    [InlineData("ensure", "preferences", "password")]
    [InlineData("ensure", "preferences", "disabled")]
    [InlineData("ensure", "preferences", "role")]
    [InlineData("tls", "feature", "logout")]
    [InlineData("tls", "feature", "password")]
    [InlineData("tls", "feature", "disabled")]
    [InlineData("tls", "feature", "role")]
    [InlineData("tls", "preferences", "logout")]
    [InlineData("tls", "preferences", "password")]
    [InlineData("tls", "preferences", "disabled")]
    [InlineData("tls", "preferences", "role")]
    public async Task ExpiredAdministrativeAuthorityStopsBeforeTheNextNativeEffectAndSettingsPublication(string route, string boundary, string change)
    {
        using var f = await Fixture.Create(route, boundary);
        var state = f.StateBytes();
        var action = f.Run();
        await f.WaitForBoundary(action);
        try { await f.ChangeActor(change); }
        finally { f.Resume(); }
        Assert.Equal(401, Assert.IsType<AppException>(await Record.ExceptionAsync(() => action)).HttpStatusCode);
        Assert.Empty(f.NativeWrites);
        Assert.Equal(state, f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("ensure", "feature")]
    [InlineData("ensure", "preferences")]
    [InlineData("tls", "feature")]
    [InlineData("tls", "preferences")]
    public async Task CancellationAfterACompletedReadCannotStartTheNextEffect(string route, string boundary)
    {
        using var f = await Fixture.Create(route, boundary);
        var state = f.StateBytes();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = f.Run(caller.Token);
        await f.WaitForBoundary(action);
        caller.Cancel(); f.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Empty(f.NativeWrites);
        Assert.Equal(state, f.StateBytes());
    }

    [Theory]
    [InlineData("ensure", false)]
    [InlineData("ensure", true)]
    [InlineData("tls", false)]
    [InlineData("tls", true)]
    public async Task AFinishedNativeCommandCannotPublishSettingsOrAuditAfterRevocationOrCancellation(string route, bool cancel)
    {
        using var f = await Fixture.Create(route, "native");
        var state = f.StateBytes();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = f.Run(caller.Token);
        await f.WaitForBoundary(action);
        if (cancel) caller.Cancel(); else await f.ChangeActor("logout");
        f.Resume();
        var error = await Record.ExceptionAsync(() => action);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Single(f.NativeWrites);
        Assert.Equal(state, f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("unmeasured")]
    [InlineData("not-applied")]
    [InlineData("certificate-unmeasured")]
    public async Task UnsuccessfulTlsApplicationPreservesThePreviousDurablePolicy(string outcome)
    {
        using var f = await Fixture.Create("tls", desired: true);
        f.Outcome = outcome;
        var state = f.StateBytes();
        if (outcome == "failed") Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Run()));
        else
        {
            var response = Data<FtpTlsStatusDto>(await f.Run());
            Assert.False(response.RequireSsl);
            if (outcome == "not-applied") Assert.False(response.Verified); else Assert.Null(response.Verified);
        }
        Assert.Equal(state, f.StateBytes());
        Assert.False(await f.Service.GetRequireSslAsync(f.Token));
    }

    [Fact]
    public async Task AnAdministrativeRequestIsImmutableWhileTheNativeOperationWaits()
    {
        using var f = await Fixture.Create("tls", "native", desired: true);
        var action = f.Run();
        await f.WaitForBoundary(action);
        f.Request.RequireSsl = false;
        f.Resume();
        var data = Data<FtpTlsStatusDto>(await action);
        Assert.True(data.RequireSsl);
        Assert.True(data.Verified);
        Assert.True(await f.Service.GetRequireSslAsync(f.Token));
        Assert.Contains("True", Assert.Single(await f.Db.AuditLogs.ToListAsync()).Details);
    }

    [Fact]
    public async Task OppositeTlsRequestsCannotPublishAPolicyDifferentFromTheFinalNativeState()
    {
        using var f = await Fixture.Create("tls", "native", desired: true);
        var first = f.Service.SetRequireSslAsync(true, f.Token);
        await f.WaitForBoundary(first);
        var second = f.Service.SetRequireSslAsync(false, f.Token);
        // The baseline reaches the second native effect. The serialized implementation reports
        // the second request queued on the real gate. Neither branch uses a timing assumption.
        await Task.WhenAny(f.SecondNativeCompleted.Task, f.Gate.SecondRequest.Task).WaitAsync(f.Token);
        f.Resume();
        Assert.True((await first).Verified);
        Assert.True((await second).Verified);
        Assert.Equal(f.NativePolicy, await f.Service.GetRequireSslAsync(f.Token));
        Assert.Equal(1, f.MaxConcurrentNative);
    }

    [Theory]
    [InlineData("ensure", false)]
    [InlineData("ensure", true)]
    [InlineData("tls", false)]
    [InlineData("tls", true)]
    public async Task AQueuedServerRequestMustStillBeAuthorizedAndUncanceledWhenItAcquiresTheGate(string route, bool cancel)
    {
        using var f = await Fixture.Create(route, "native", desired: true);
        var holding = f.Service.SetRequireSslAsync(true, f.Token);
        await f.WaitForBoundary(holding);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var queued = f.Run(caller.Token);
        await Task.WhenAny(f.SecondNativeCompleted.Task, f.Gate.SecondRequest.Task).WaitAsync(f.Token);
        if (cancel) caller.Cancel(); else await f.ChangeActor("logout");
        f.Resume();
        await holding;
        var error = await Record.ExceptionAsync(() => queued);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Single(f.NativeWrites);
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("ensure", false)]
    [InlineData("ensure", true)]
    [InlineData("tls", false)]
    [InlineData("tls", true)]
    public async Task CurrentAdministratorsAndApiTokensKeepTheMeasuredNormalPath(string route, bool token)
    {
        using var f = await Fixture.Create(route, token: token);
        var result = await f.Run();
        if (route == "ensure") Assert.True(Data<FtpServerStatusDto>(result).IsConfigured);
        else { var data = Data<FtpTlsStatusDto>(result); Assert.False(data.RequireSsl); Assert.True(data.Verified); }
        Assert.Single(f.NativeWrites);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task OptionalTlsDoesNotRequireACertificateToAllowTheMeasuredPlainFtpPolicy()
    {
        using var f = await Fixture.Create("tls");
        f.Outcome = "certificate-missing";
        var data = Data<FtpTlsStatusDto>(await f.Run());
        Assert.False(data.RequireSsl); Assert.True(data.Verified);
        Assert.False(await f.Service.GetRequireSslAsync(f.Token));
    }

    [Fact]
    public async Task TrustedEnsureCallersRemainSupportedWithoutAController()
    {
        using var f = await Fixture.Create("ensure");
        Assert.True((await f.Service.EnsureServerAsync(f.Token)).IsConfigured);
        Assert.Single(f.NativeWrites);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("unmeasured")]
    public async Task AnUnsuccessfulReconfigurationCannotLeaveTheOldReadyMarkerForANewFastPath(string outcome)
    {
        using var f = await Fixture.Create("tls", desired: true);
        await f.SetConfigured(true);
        f.Outcome = outcome;
        if (outcome == "failed") await Assert.ThrowsAsync<AppException>(() => f.Service.SetRequireSslAsync(true, f.Token));
        else Assert.Null((await f.Service.SetRequireSslAsync(true, f.Token)).Verified);
        Assert.False(await f.IsConfigured());
        Assert.False(await f.Service.GetRequireSslAsync(f.Token));

        f.Outcome = "applied";
        await f.Service.EnsureServerAsync(f.Token);
        // The service probe says running, so only invalidating the old marker prevents a skip.
        Assert.Equal(new[] { true, false }, f.NativeWrites);
        Assert.True(await f.IsConfigured());
    }

    [Theory]
    [InlineData("restart-failed")]
    [InlineData("ftp-start-failed")]
    [InlineData("ftp-not-running")]
    [InlineData("certificate-unmeasured")]
    [InlineData("applied")]
    [InlineData("already-started")]
    [InlineData("already-starting")]
    [InlineData("assembly-load-failed")]
    public async Task TheGeneratedScriptMeasuresTlsOnlyAfterCriticalNativeSteps(string mode)
    {
        using var f = await Fixture.Create("tls", desired: true);
        await f.Run();
        Assert.NotNull(f.ConfigurationScript);
        // Execute the actual generated script, with every native cmdlet replaced by memory-only
        // functions. No IIS module, certificate store, firewall, Windows service or filesystem is used.
        var fakeNative = $$"""
            $script:ftpValues = @{}
            $script:restarted = $false
            function Import-Module { [CmdletBinding()]param($Name) }
            function Test-Path { [CmdletBinding()]param($Path) return $true }
            function New-WebFtpSite { [CmdletBinding()]param($Name,$Port,$PhysicalPath,[switch]$Force) throw 'Unexpected site creation' }
            function Set-ItemProperty { [CmdletBinding()]param($Path,$Name,$Value) $script:ftpValues[$Name] = $Value }
            function Get-ChildItem { [CmdletBinding()]param($Path) [pscustomobject]@{ FriendlyName = 'AdaPanel FTPS'; Thumbprint = 'AABBCC' } }
            function New-SelfSignedCertificate { [CmdletBinding()]param($DnsName,$FriendlyName,$CertStoreLocation,$KeyExportPolicy,$NotAfter) throw 'Unexpected certificate creation' }
            function Set-WebConfigurationProperty { [CmdletBinding()]param($PSPath,$Filter,$Name,$Value) }
            function Start-WebSite { [CmdletBinding()]param($Name) throw 'HTTP startup must not be used for an FTP-only site' }
            function Get-Service { [CmdletBinding()]param($Name)
              $service = [pscustomobject]@{}
              $service | Add-Member ScriptMethod WaitForStatus { param($state,$timeout) if (-not $script:restarted) { throw 'FTP startup before service restart' } }
              return $service
            }
            function Restart-Service { [CmdletBinding()]param($Name,[switch]$Force)
              if ('{{mode}}' -eq 'restart-failed') { throw 'Measured restart failure' }
              $script:restarted = $true
            }
            function Get-NetFirewallRule { [CmdletBinding()]param($DisplayName) return $true }
            function New-NetFirewallRule { [CmdletBinding()]param($DisplayName,$Direction,$Action,$Protocol,$LocalPort,$Profile) throw 'Unexpected firewall creation' }
            function Get-ItemProperty { [CmdletBinding()]param($Path,$Name)
              if (-not $script:restarted) { throw 'Measurement before restart' }
              if (-not $script:ftpStarted) { throw 'TLS measurement before FTP start' }
              if ($Name -eq 'ftpServer.security.ssl.serverCertHash' -and '{{mode}}' -eq 'certificate-unmeasured') { throw 'Certificate property unreadable' }
              [pscustomobject]@{ Value = $script:ftpValues[$Name] }
            }
            """;
        var initialState = mode == "already-started" ? 1 : mode == "already-starting" ? 0 : 3;
        var result = await ProcessRunner.RunPowerShellAsync(fakeNative + Environment.NewLine + FakeFtpRuntimeScript(mode, initialState) + Environment.NewLine + f.ConfigurationScript, f.Token);
        if (mode is "restart-failed" or "ftp-start-failed" or "ftp-not-running" or "assembly-load-failed")
        {
            Assert.False(result.Success);
            Assert.DoesNotContain(FtpTlsMeasurement.Stamp, result.StandardOutput, StringComparison.Ordinal);
            if (mode == "assembly-load-failed") Assert.Contains("Measured assembly load failure", result.StandardError, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(result.Success, result.StandardError);
            var measurement = FtpTlsMeasurement.Olc(true, result.StandardOutput);
            if (mode is "applied" or "already-started" or "already-starting") Assert.True(measurement.Verified);
            else { Assert.Null(measurement.Verified); Assert.Equal(FtpTlsPolicyState.Unmeasured, measurement.Certificate); }
        }
    }

    [Fact]
    public async Task AReadyMarkerAndRunningServiceCannotSkipRepairOfAStoppedFtpSite()
    {
        using var f = await Fixture.Create("ensure");
        await f.SetConfigured(true);
        f.RuntimeSiteState = "Stopped";
        await f.Service.EnsureServerAsync(f.Token);
        Assert.Single(f.NativeWrites);
    }

    [Theory]
    [InlineData(1, "Started")]
    [InlineData(3, "Stopped")]
    public async Task RuntimeProbeReadsFtpStateRatherThanTheHttpSiteState(int ftpState, string expected)
    {
        using var f = await Fixture.Create("ensure");
        await f.Service.GetServerStatusAsync(f.Token);
        Assert.NotNull(f.ProbeScript);
        var fakeNative = $$"""
            function Import-Module { [CmdletBinding()]param($Name) }
            function Get-CimInstance { [CmdletBinding()]param($ClassName,$Filter) [pscustomobject]@{ State = 'Running'; ProcessId = 100 } }
            function Get-Website { [CmdletBinding()]param() throw 'HTTP runtime must not be measured' }
            function Get-Command { [CmdletBinding()]param($Name) return $null }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(fakeNative + Environment.NewLine + FakeFtpRuntimeScript("probe", ftpState) + Environment.NewLine + f.ProbeScript, f.Token);
        Assert.True(result.Success, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.True(json.RootElement.GetProperty("siteMeasured").GetBoolean());
        Assert.True(json.RootElement.GetProperty("siteExists").GetBoolean());
        Assert.Equal(expected, json.RootElement.GetProperty("siteState").GetString());
    }

    // A memory-only stand-in for the documented MWA configuration API. HTTP start/state
    // are deliberately unavailable, so the generated script must use ftpServer's API.
    private static string FakeFtpRuntimeScript(string mode, int initialState = 3) => $$"""
        $script:mwaLoaded = $false
        function Add-Type { [CmdletBinding()]param($Path)
          if ($Path -ne "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll") { throw 'Unexpected MWA assembly path' }
          if ('{{mode}}' -eq 'assembly-load-failed') { throw 'Measured assembly load failure' }
          $script:mwaLoaded = $true
        }
        $script:ftpStarted = {{(initialState is 0 or 1 ? "$true" : "$false")}}
        $script:ftpState = {{initialState}}
        $script:runtimeReads = 0
        $script:runtime = [pscustomobject]@{ Methods = @{} }
        $script:runtime.Methods['Start'] = $script:runtime
        $script:runtime | Add-Member ScriptMethod GetApplicationHostConfiguration { return $this }
        $script:runtime | Add-Member ScriptMethod GetSection { param($name) if ($name -ne 'system.applicationHost/sites') { throw 'Wrong section' }; return $this }
        $script:runtime | Add-Member ScriptMethod GetCollection { return ,$this }
        $script:runtime | Add-Member ScriptMethod GetChildElement { param($name) if ($name -ne 'ftpServer') { throw 'Wrong runtime' }; return $this }
        $script:runtime | Add-Member ScriptMethod GetAttributeValue { param($name)
          switch ($name) { 'name' { return 'AdaPanel FTP' }; 'state' { return $script:ftpState }; 'lastStartupStatus' { return 0 }; default { throw 'Wrong attribute' } }
        }
        $script:runtime | Add-Member ScriptMethod CreateInstance { return $this }
        $script:runtime | Add-Member ScriptMethod Execute {
          if ('{{mode}}' -eq 'already-started' -or '{{mode}}' -eq 'already-starting') { throw 'Redundant FTP start' }
          if (-not $script:restarted) { throw 'FTP runtime start before service restart' }
          if (-not $script:ftpValues['ftpServer.serverAutoStart']) { throw 'FTP autostart not configured' }
          if ('{{mode}}' -eq 'ftp-start-failed') { throw 'Measured FTP start failure' }
          $script:ftpStarted = $true
          if ('{{mode}}' -ne 'ftp-not-running') { $script:ftpState = 1 }
        }
        $script:runtime | Add-Member ScriptMethod Dispose { }
        function New-Object { [CmdletBinding()]param($TypeName)
          if ($TypeName -ne 'Microsoft.Web.Administration.ServerManager') { throw 'Unexpected object creation' }
          if (-not $script:mwaLoaded) { throw 'Cannot find type [Microsoft.Web.Administration.ServerManager]: assembly was not explicitly loaded' }
          $script:runtimeReads++
          if ('{{mode}}' -eq 'already-starting' -and $script:runtimeReads -gt 1) { $script:ftpState = 1 }
          return $script:runtime
        }
        """;

    private static T Data<T>(object result) => Assert.IsType<ApiResponse<T>>(result).Data!;

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-ftp-server-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondNativeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Guid actor = Guid.NewGuid();
        private readonly string sessionId = SessionRevocationStore.NewSessionId();
        private readonly string route, boundary;
        private readonly ServiceProvider services;
        private readonly JsonFileStore store;
        private readonly SessionRevocationStore sessions;
        private readonly FtpController controller;
        private int enteredBoundary, nativeNumber, activeNative;
        internal readonly CountingGate Gate = new();
        internal FtpService Service { get; }
        internal AdaPanelDbContext Db { get; }
        internal FtpTlsRequest Request { get; }
        internal CancellationToken Token => lifetime.Token;
        internal readonly List<bool> NativeWrites = [];
        internal bool NativePolicy;
        internal int MaxConcurrentNative;
        internal string Outcome = "applied";
        internal string? ConfigurationScript;
        internal string? ProbeScript;
        internal string RuntimeSiteState = "Started";

        private Fixture(string route, string boundary, bool desired, bool token)
        {
            this.route = route; this.boundary = boundary;
            Request = new FtpTlsRequest { RequireSsl = desired };
            Directory.CreateDirectory(root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            Db.Users.Add(new PanelUser { Id = actor, UserName = "ftp-admin", Email = "ftp-admin@example.test", PasswordHash = "accepted-server-hash", IsActive = true, Role = UserRole.Administrator });
            Db.SaveChanges();
            var sites = Fake<IIisSiteService>((method, _) => method.Name == "GetSitesAsync" ? Task.FromResult<IReadOnlyCollection<SiteDto>>([]) : throw new NotSupportedException(method.Name));
            var scopes = new CustomerScopeService(store, new ServicePlanService(store),
                Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync" ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            Service = new FtpService(sites, store, Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync" ? Preferences() : throw new NotSupportedException(method.Name)),
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }), serverConcurrency: Gate)
            { NativeScript = NativeScript, NativeCommand = (_, _, _) => throw new InvalidOperationException("Unexpected native account command.") };
            sessions = new SessionRevocationStore(store);
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(sessions).AddSingleton<IConcurrencyLock, ConcurrencyLock>()
                .AddSingleton<IFtpService>(Service).AddSingleton<ICustomerScopeService>(scopes).AddSingleton<IIisSiteService>(sites)
                .AddSingleton<IAuditService>(new AuditService(Db, scopes, store)).BuildServiceProvider();
            controller = ActivatorUtilities.CreateInstance<FtpController>(services);
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor.ToString()), new(ClaimTypes.Role, "Administrator") };
            if (!token)
            { claims.Add(new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("accepted-server-hash"))); claims.Add(new(SessionRevocationStore.SessionIdClaim, sessionId)); }
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, token ? PanelTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)) } };
        }
        internal static async Task<Fixture> Create(string route, string boundary = "", bool desired = false, bool token = false)
        {
            var f = new Fixture(route, boundary, desired, token);
            await f.store.WriteAsync("config/ftp-users.json", Array.Empty<object>(), f.Token);
            await f.store.WriteAsync("config/ftp-infra.json", new { configured = false }, f.Token);
            await f.store.WriteAsync("config/ftp-tls.json", new { requireSsl = !desired }, f.Token);
            f.NativePolicy = !desired;
            return f;
        }
        internal string StateBytes() => string.Join("\n", new[] { "ftp-infra.json", "ftp-tls.json" }.Select(name => File.ReadAllText(store.ResolvePath("config/" + name))));
        internal Task SetConfigured(bool value) => store.WriteAsync("config/ftp-infra.json", new { configured = value }, Token);
        internal async Task<bool> IsConfigured() => (await store.ReadAsync("config/ftp-infra.json", new ReadyState(), Token)).Configured;
        private sealed class ReadyState { public bool Configured { get; set; } }
        internal async Task<object> Run(CancellationToken? token = null)
        {
            var ct = token ?? Token;
            IActionResult? result = route == "ensure" ? (await controller.EnsureServerAsync(ct)).Result : (await controller.SetTlsAsync(Request, ct)).Result;
            return Assert.IsType<OkObjectResult>(result).Value!;
        }
        internal Task ChangeActor(string change) => change switch
        {
            "logout" => sessions.RevokeAsync(sessionId, Token),
            "password" => Db.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, "changed-server-hash"), Token),
            "disabled" => Db.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.IsActive, false), Token),
            "role" => Db.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.Role, UserRole.Reseller), Token),
            _ => throw new InvalidOperationException(change)
        };
        internal async Task WaitForBoundary(Task action)
        {
            if (await Task.WhenAny(entered.Task, action).WaitAsync(Token) == action)
            { await action; throw new InvalidOperationException("FTP operation ended before its intended boundary."); }
            Assert.False(action.IsCompleted);
        }
        internal void Resume() => release.TrySetResult();
        private async Task Pause(string current)
        {
            if (boundary == current && Interlocked.Exchange(ref enteredBoundary, 1) == 0)
            { entered.TrySetResult(); await release.Task.WaitAsync(Token); }
        }
        private async Task<PanelPreferencesDto> Preferences()
        { await Pause("preferences"); return new PanelPreferencesDto { ServerAddress = "192.0.2.30" }; }
        private async Task<CommandResult> NativeScript(string script, bool strict, CancellationToken token)
        {
            if (script.Contains("Get-NetTCPConnection", StringComparison.Ordinal)) { ProbeScript = script; return new CommandResult { ExitCode = 0, StandardOutput =
                "{\"siteMeasured\":true,\"serviceMeasured\":true,\"siteExists\":true,\"siteState\":\"" + RuntimeSiteState + "\",\"serviceState\":\"Running\",\"servicePids\":[100],\"portMeasured\":true,\"listeners\":[{\"pid\":100,\"process\":\"fixture\",\"services\":[\"FTPSVC\"]}]}" }; }
            if (script.StartsWith("if (Get-Service FTPSVC", StringComparison.Ordinal))
            { await Pause("feature"); return new CommandResult { ExitCode = 0, StandardOutput = "present" }; }
            if (script.StartsWith("if ((Get-Service FTPSVC", StringComparison.Ordinal)) return new CommandResult { ExitCode = 0, StandardOutput = RuntimeSiteState == "Started" ? "up" : "down" };
            Assert.Contains("ftpServer.security.ssl.controlChannelPolicy -Value", script);
            ConfigurationScript = script;
            var requested = script.Contains("ftpServer.security.ssl.controlChannelPolicy -Value 1", StringComparison.Ordinal);
            var number = Interlocked.Increment(ref nativeNumber);
            var active = Interlocked.Increment(ref activeNative);
            MaxConcurrentNative = Math.Max(MaxConcurrentNative, active);
            lock (NativeWrites) NativeWrites.Add(requested);
            try
            {
                await Pause("native");
                NativePolicy = Outcome == "not-applied" ? !requested : requested;
                if (number == 2) SecondNativeCompleted.TrySetResult();
                if (Outcome == "failed") return new CommandResult { ExitCode = 1, StandardError = "fixture configuration failure" };
                var policy = Outcome == "not-applied" ? (requested ? "0" : "1") : requested ? "1" : "0";
                var cert = Outcome == "certificate-unmeasured" ? "" : Outcome == "certificate-missing" ? "yok" : "AABBCC";
                return new CommandResult { ExitCode = 0, StandardOutput = Outcome == "unmeasured" ? "no measurement" : $"ADAPANEL-FTP-TLS={policy}|{policy}|{cert}" };
            }
            finally { Interlocked.Decrement(ref activeNative); }
        }
        public void Dispose()
        {
            Resume(); lifetime.Cancel(); services.Dispose(); Db.Dispose(); connection.Dispose(); lifetime.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    internal sealed class CountingGate : IConcurrencyLock
    {
        private readonly ConcurrencyLock actual = new();
        private int requests;
        internal readonly TaskCompletionSource SecondRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Enter() { if (Interlocked.Increment(ref requests) == 2) SecondRequest.TrySetResult(); }
        public Task RunExclusiveAsync(Func<Task> operation, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, operation, token);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, operation, token);
        public Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken token = default)
        { Enter(); return actual.RunExclusiveAsync(key, operation, token); }
        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken token = default)
        { Enter(); return actual.RunExclusiveAsync(key, operation, token); }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> action) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Action = action; return value; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Action = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!, args ?? []); }
}
