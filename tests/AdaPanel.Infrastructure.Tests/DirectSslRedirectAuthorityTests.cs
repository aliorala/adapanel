using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> DirectRedirectRevocations() =>
        from boundary in new[] { "scope", "site", "norewrite", "install", "reconcile" }
        from change in new[] { "disabled", "deleted", "role", "password", "source" }
        select new object[] { boundary, change };

    [Theory]
    [MemberData(nameof(DirectRedirectRevocations))]
    public async Task DirectSslRedirectRechecksAuthorityBeforeTheNextMutation(string boundary, string change)
    {
        using var f = new Fixture();
        await f.PrepareDirectRedirect(boundary);
        var state = await f.RedirectStateBytes();
        var action = Capture(async () => await f.RedirectController(f.Customer)
            .SetHttpsRedirectAsync(f.RedirectRequest, f.Token));
        try { await f.WaitRedirect(action); await f.Change(change, f.Customer); }
        finally { f.RedirectRelease.TrySetResult(); }
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(boundary == "reconcile" ? 1 : 0, f.RedirectMutations);
        Assert.Equal(boundary == "install" ? 1 : 0, f.RedirectInstallCalls);
        Assert.Equal(state, await f.RedirectStateBytes());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("role")]
    [InlineData("password")]
    public async Task DirectSslRedirectRechecksTheAdministratorAfterAutomaticInstallation(string change)
    {
        using var f = new Fixture();
        await f.PrepareDirectRedirect("install");
        var action = Capture(async () => await f.RedirectController(f.Admin)
            .SetHttpsRedirectAsync(f.RedirectRequest, f.Token));
        try { await f.WaitRedirect(action); await f.Change(change, f.Admin); }
        finally { f.RedirectRelease.TrySetResult(); }
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(0, f.RedirectMutations);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("section")]
    [InlineData("reparented")]
    [InlineData("detached")]
    [InlineData("parent_disabled")]
    [InlineData("parent_deleted")]
    public async Task DirectSslRedirectRechecksSubUserAndParentAfterAutomaticInstallation(string change)
    {
        using var f = new Fixture();
        await f.PrepareDirectRedirect("install");
        var action = Capture(async () => await f.RedirectController(f.Child)
            .SetHttpsRedirectAsync(f.RedirectRequest, f.Token));
        try { await f.WaitRedirect(action); await f.Change(change, f.Child); }
        finally { f.RedirectRelease.TrySetResult(); }
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(0, f.RedirectMutations);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("scope", "id")]
    [InlineData("scope", "path")]
    [InlineData("scope", "version")]
    [InlineData("site", "id")]
    [InlineData("site", "path")]
    [InlineData("site", "version")]
    [InlineData("install", "id")]
    [InlineData("install", "path")]
    public async Task DirectSslRedirectCannotAdoptAReplacementSiteAfterWaiting(string boundary, string change)
    {
        using var f = new Fixture();
        await f.PrepareDirectRedirect(boundary);
        var state = await f.RedirectStateBytes();
        var action = Capture(async () => await f.RedirectController(f.Customer)
            .SetHttpsRedirectAsync(f.RedirectRequest, f.Token));
        try
        {
            await f.WaitRedirect(action);
            if (change == "version")
                await SiteMutationGuard.RunMutationAsync(f.RedirectGates, Fixture.Site,
                    SiteMutationGuard.MutationKind.IdentityChange, () => Task.FromResult(true), f.Token);
            else
                f.RedirectSite = new SiteDto
                {
                    Id = change == "id" ? 99 : 21, Name = Fixture.Site,
                    PhysicalPath = change == "path" ? f.Target : f.Area.SiteKoku,
                    ApplicationRootPath = change == "path" ? f.Target : f.Area.SiteKoku,
                    ApplicationPoolName = "redirect-actor-pool"
                };
        }
        finally { f.RedirectRelease.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(f.Token)).MessageKey);
        Assert.Equal(0, f.RedirectMutations);
        Assert.Equal(state, await f.RedirectStateBytes());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("site")]
    [InlineData("norewrite")]
    [InlineData("install")]
    [InlineData("reconcile")]
    public async Task DirectSslRedirectCancellationStopsTheNextEffect(string boundary)
    {
        using var f = new Fixture();
        await f.PrepareDirectRedirect(boundary);
        var state = await f.RedirectStateBytes();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = Capture(async () => await f.RedirectController(f.Customer)
            .SetHttpsRedirectAsync(f.RedirectRequest, canceled.Token));
        try { await f.WaitRedirect(action); canceled.Cancel(); }
        finally { f.RedirectRelease.TrySetResult(); }
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        Assert.Equal(boundary == "reconcile" ? 1 : 0, f.RedirectMutations);
        Assert.Equal(state, await f.RedirectStateBytes());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("admin", false)]
    [InlineData("customer", true)]
    [InlineData("customer", false)]
    [InlineData("child", true)]
    [InlineData("child", false)]
    public async Task DirectSslRedirectPreservesTheMeasuredAuthorizedFlow(string role, bool enabled)
    {
        using var f = new Fixture();
        await f.PrepareDirectRedirect("", enabled);
        var actor = role == "admin" ? f.Admin : role == "child" ? f.Child : f.Customer;
        var response = await f.RedirectController(actor).SetHttpsRedirectAsync(f.RedirectRequest, f.Token);
        var result = Assert.IsType<ApiResponse<SslWorkspaceDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        Assert.True(result.HttpsRedirectMeasured);
        Assert.Equal(enabled, result.HttpsRedirectEnabled);
        Assert.Equal(1, f.RedirectMutations);
        Assert.Single(f.Area.Denetim.Kayitlar, record => record.Action == "ssl.https_redirect_changed");
    }

    [Fact]
    public async Task DirectSslRedirectPreservesTrustedAutomaticInstallation()
    {
        using var f = new Fixture();
        await f.PrepareDirectRedirect("install");
        f.RedirectRelease.TrySetResult();
        var result = await f.RedirectService.SetHttpsRedirectAsync(f.RedirectRequest, f.Token);
        Assert.True(result.HttpsRedirectMeasured);
        Assert.True(result.HttpsRedirectEnabled);
        Assert.Equal(1, f.RedirectInstallCalls);
        Assert.Equal(1, f.RedirectMutations);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    private sealed partial class Fixture
    {
        internal readonly TaskCompletionSource RedirectReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RedirectRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrencyLock RedirectGates = new();
        internal SiteDto RedirectSite = null!;
        internal IIisSiteService RedirectSites = null!;
        internal ICustomerScopeService RedirectScopes = null!;
        internal WinAcmeSslService RedirectService = null!;
        internal int RedirectMutations;
        internal int RedirectInstallCalls;
        private string redirectWait = "";
        private bool requestedRedirect;
        private bool measuredRedirect;
        private bool rewriteAvailable;
        private int redirectSiteReads;
        private int redirectScopeReads;
        internal SetHttpsRedirectRequest RedirectRequest => new() { SiteName = Site, Enabled = requestedRedirect };

        internal async Task PrepareDirectRedirect(string wait, bool enabled = true)
        {
            redirectWait = wait;
            requestedRedirect = enabled;
            measuredRedirect = !enabled;
            rewriteAvailable = wait is not ("norewrite" or "install");
            RedirectSite = new SiteDto { Id = 21, Name = Site, PhysicalPath = Area.SiteKoku,
                ApplicationRootPath = Area.SiteKoku, ApplicationPoolName = "redirect-actor-pool" };
            RedirectSites = Fake<IIisSiteService>((method, args) => method.Name == "GetSiteAsync"
                ? ReadRedirectSite(args.OfType<CancellationToken>().Single()) : throw new NotSupportedException(method.Name));
            RedirectScopes = Fake<ICustomerScopeService>((method, args) => method.Name == "GetAssignedSitesAsync"
                ? ReadRedirectScope((Guid)args[0]!, args.OfType<CancellationToken>().Single())
                : method.Invoke(Scopes, args));
            var installer = Fake<IComponentBootstrapper>((method, args) => method.Name == "InstallAsync"
                ? InstallRedirectRewrite(args.OfType<CancellationToken>().Single()) : throw new NotSupportedException(method.Name));
            RedirectService = new WinAcmeSslService(Context(), RedirectSites, null!, Area.DosyaDeposu, installer,
                NullLogger<WinAcmeSslService>.Instance, _ => Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>([]), RedirectGates)
            {
                RedirectCommand = RunRedirectCommand,
                CertificateProbeAsync = async (_, token) =>
                {
                    await PauseRedirect("reconcile", token);
                    return SslStoreProbe.NotFound();
                }
            };
            await Area.DosyaDeposu.WriteAsync("config/ssl.json", new[] { new SslCertificateDto
            {
                SiteName = Site, HostName = Site, Port = 443, Thumbprint = "redirect-fixture-certificate",
                IsPresentInStore = true, Source = "manual", NotAfterUtc = DateTime.UtcNow.AddDays(10)
            } }, Token);
        }

        private async Task<SiteDto> ReadRedirectSite(CancellationToken token)
        {
            var snapshot = RedirectSite;
            if (++redirectSiteReads == 1) await PauseRedirect("site", token);
            token.ThrowIfCancellationRequested();
            return snapshot;
        }

        private async Task<IReadOnlyCollection<string>> ReadRedirectScope(Guid actor, CancellationToken token)
        {
            var snapshot = await Scopes.GetAssignedSitesAsync(actor, token);
            if (++redirectScopeReads == 1) await PauseRedirect("scope", token);
            token.ThrowIfCancellationRequested();
            return snapshot;
        }

        private async Task<ComponentInstallResultDto> InstallRedirectRewrite(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RedirectInstallCalls++;
            await PauseRedirect("install", token);
            rewriteAvailable = true;
            return new() { ComponentId = "url-rewrite", Status = "Installed" };
        }

        private async Task<CommandResult> RunRedirectCommand(string script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (script.Contains("Clear-WebConfiguration", StringComparison.Ordinal))
            {
                if (requestedRedirect && !rewriteAvailable)
                {
                    await PauseRedirect("norewrite", token);
                    return new() { ExitCode = 0, StandardOutput = "NOREWRITE" };
                }
                RedirectMutations++;
                measuredRedirect = requestedRedirect;
                return new() { ExitCode = 0, StandardOutput = "OK" };
            }
            return new() { ExitCode = 0, StandardOutput = measuredRedirect ? "YES" : "NO" };
        }

        private async Task PauseRedirect(string boundary, CancellationToken token)
        {
            if (redirectWait != boundary) return;
            RedirectReached.TrySetResult();
            await RedirectRelease.Task.WaitAsync(token);
        }

        internal async Task WaitRedirect(Task action)
        {
            await Task.WhenAny(action, RedirectReached.Task).WaitAsync(Token);
            Assert.True(RedirectReached.Task.IsCompleted, "Redirect did not reach its controlled external wait.");
            Assert.False(action.IsCompleted);
        }

        internal SslController RedirectController(Guid actor)
        {
            var context = Controller(actor).ControllerContext;
            var services = new ServiceCollection().AddSingleton(ControllerDb)
                .AddSingleton<ICustomerScopeService>(RedirectScopes).AddSingleton<IIisSiteService>(RedirectSites)
                .AddSingleton<IConcurrencyLock>(RedirectGates).BuildServiceProvider();
            providers.Add(services);
            context.HttpContext.RequestServices = services;
            return new(RedirectService, RedirectScopes, null!, Area.Denetim) { ControllerContext = context };
        }

        internal Task<byte[]> RedirectStateBytes() => File.ReadAllBytesAsync(Area.DosyaDeposu.ResolvePath("config/ssl.json"), Token);
    }
}
