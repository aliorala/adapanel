using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Data.Common;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> DirectRemovalRevocations() =>
        from boundary in new[] { "inventory", "redirect-read", "redirect-verification", "binding-verification" }
        from change in new[] { "disabled", "deleted", "role", "password", "source" }
        select new object[] { boundary, change };

    [Theory]
    [MemberData(nameof(DirectRemovalRevocations))]
    public async Task DirectSslRemovalRechecksAuthorityBeforeTheNextEffect(string boundary, string change)
    {
        using var f = new Fixture();
        await f.PrepareDirectRemoval(boundary);
        var controller = f.RemovalController(f.Customer);
        var before = await f.RemovalState();
        var action = Capture(async () => await controller.RemoveCertificateAsync(f.RemovalRequest, f.Token));
        await f.WaitDirectRemoval(action);
        await f.Change(change, f.Customer);
        f.RemovalRelease.TrySetResult();
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(boundary == "binding-verification" ? 1 : 0, f.RemovalCommands);
        Assert.Equal(boundary is "redirect-verification" or "binding-verification" ? 1 : 0, f.RemovalRedirectWrites);
        Assert.Equal(before, await f.RemovalState());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("section")]
    [InlineData("reparented")]
    [InlineData("detached")]
    [InlineData("parent_disabled")]
    [InlineData("parent_deleted")]
    public async Task DirectSslRemovalRechecksSubUserAndParentAuthority(string change)
    {
        using var f = new Fixture();
        await f.PrepareDirectRemoval("inventory");
        var controller = f.RemovalController(f.Child);
        var before = await f.RemovalState();
        var action = Capture(async () => await controller.RemoveCertificateAsync(f.RemovalRequest, f.Token));
        await f.WaitDirectRemoval(action);
        await f.Change(change, f.Child);
        f.RemovalRelease.TrySetResult();
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(0, f.RemovalCommands);
        Assert.Equal(0, f.RemovalRedirectWrites);
        Assert.Equal(before, await f.RemovalState());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("redirect-read")]
    [InlineData("redirect-verification")]
    [InlineData("binding-verification")]
    public async Task DirectSslRemovalCancellationDoesNotStartTheNextEffect(string boundary)
    {
        using var f = new Fixture();
        await f.PrepareDirectRemoval(boundary);
        var before = await f.RemovalState();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = Capture(async () => await f.RemovalController(f.Customer)
            .RemoveCertificateAsync(f.RemovalRequest, canceled.Token));
        await f.WaitDirectRemoval(action);
        canceled.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        Assert.Equal(boundary == "binding-verification" ? 1 : 0, f.RemovalCommands);
        Assert.Equal(before, await f.RemovalState());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("customer")]
    [InlineData("child")]
    public async Task DirectSslRemovalStillWorksForAnUnchangedAuthorizedActor(string role)
    {
        using var f = new Fixture();
        await f.PrepareDirectRemoval("");
        var actor = role == "admin" ? f.Admin : role == "child" ? f.Child : f.Customer;
        var response = await f.RemovalController(actor).RemoveCertificateAsync(f.RemovalRequest, f.Token);
        var result = Assert.IsType<ApiResponse<RemoveSslCertificateResultDto>>(
            Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        Assert.True(result.BindingRemoved);
        Assert.True(result.HttpsRedirectDisabled);
        Assert.True(result.Workspace.HttpsRedirectMeasured);
        Assert.False(result.Workspace.HttpsRedirectEnabled);
        Assert.Empty(result.Workspace.Certificates);
        Assert.Equal("kept", result.CertificateAction);
        Assert.Equal(1, f.RemovalCommands);
        Assert.Equal(1, f.RemovalRedirectWrites);
        Assert.Single(f.Area.Denetim.Kayitlar, item => item.Action == "ssl.binding_removed");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("same-snapshot")]
    public async Task DirectSslRemovalCannotAdoptASiteReplacedDuringItsActorLookup(string change)
    {
        using var f = new Fixture();
        await f.PrepareDirectRemoval("actor");
        var state = await f.RemovalState();
        var action = Capture(async () => await f.RemovalController(f.Customer)
            .RemoveCertificateAsync(f.RemovalRequest, f.Token));
        await f.WaitDirectRemoval(action);
        if (change == "same-snapshot")
            await SiteMutationGuard.RunMutationAsync(f.RemovalGates, Fixture.Site,
                SiteMutationGuard.MutationKind.IdentityChange, () => Task.FromResult(true), f.Token);
        else f.RemovalSite = new SiteDto
        {
            Id = change == "id" ? 99 : 18, Name = Fixture.Site,
            PhysicalPath = change == "path" ? f.Target : f.Area.SiteKoku,
            ApplicationRootPath = change == "path" ? f.Target : f.Area.SiteKoku,
            ApplicationPoolName = "actor-removal-pool"
        };
        f.RemovalRelease.TrySetResult();
        Assert.Equal("sites.target_changed", Assert.IsType<AdaPanel.Core.Common.AppException>(
            await action.WaitAsync(f.Token)).MessageKey);
        Assert.Equal(0, f.RemovalCommands);
        Assert.Equal(0, f.RemovalRedirectWrites);
        Assert.Equal(state, await f.RemovalState());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    private sealed partial class Fixture
    {
        internal readonly TaskCompletionSource RemovalReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RemovalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrencyLock RemovalGates = new();
        internal IIisSiteService RemovalSites = null!;
        internal SiteDto RemovalSite = null!;
        internal WinAcmeSslService RemovalService = null!;
        internal Func<SslCertificateDto, CancellationToken, Task<SslStoreProbe>>? RemovalProbe;
        internal ICustomerScopeService? RemovalScopeOverride;
        internal int RemovalCommands;
        internal int RemovalRedirectWrites;
        private string removalBoundary = "";
        private int removalReads;
        private int removalRedirectReads;
        private bool removalRedirectEnabled = true;
        internal RemoveSslCertificateRequest RemovalRequest => new()
        { SiteName = Site, HostName = Site, Port = 443, RemoveCertificateFromStore = false };

        internal async Task PrepareDirectRemoval(string boundary)
        {
            removalBoundary = boundary;
            RemovalSite = new SiteDto { Id = 18, Name = Site, PhysicalPath = Area.SiteKoku,
                ApplicationRootPath = Area.SiteKoku, ApplicationPoolName = "actor-removal-pool" };
            RemovalSites = Fake<IIisSiteService>((method, args) =>
            {
                args.OfType<CancellationToken>().Single().ThrowIfCancellationRequested();
                return method.Name == "GetSiteAsync" ? Task.FromResult(RemovalSite) : throw new NotSupportedException(method.Name);
            });
            RemovalService = new WinAcmeSslService(Context(), RemovalSites, null!, Area.DosyaDeposu, null!,
                NullLogger<WinAcmeSslService>.Instance, ReadRemovalBindings)
            {
                CertificateProbeAsync = (record, token) => RemovalProbe?.Invoke(record, token)
                    ?? Task.FromResult(SslStoreProbe.Unmeasured()),
                RedirectCommand = async (script, token) =>
                {
                    if (script.Contains("Clear-WebConfiguration", StringComparison.Ordinal))
                    { RemovalRedirectWrites++; removalRedirectEnabled = false; return new() { ExitCode = 0, StandardOutput = "OK" }; }
                    await PauseRemoval(removalRedirectReads++ == 0 ? "redirect-read" : "redirect-verification", token);
                    return new() { ExitCode = 0, StandardOutput = removalRedirectEnabled ? "YES" : "NO" };
                },
                RemoveBindingCommand = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    RemovalCommands++;
                    return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "OK" });
                }
            };
            await Area.DosyaDeposu.WriteAsync("config/ssl.json", new[] { new SslCertificateDto
            { SiteName = Site, HostName = Site, Port = 443, Thumbprint = "fixture-only", Source = "imported" } }, Token);
        }

        private async Task<IReadOnlyCollection<IisHttpsBinding>?> ReadRemovalBindings(CancellationToken token)
        {
            await PauseRemoval(removalReads++ == 0 ? "inventory" : RemovalCommands > 0 ? "binding-verification" : "before-binding-removal", token);
            return RemovalCommands > 0 ? [] : [new IisHttpsBinding
            { SiteName = Site, BindingInformation = "*:443:" + Site, Thumbprint = "fixture-only", CertificateReadable = true }];
        }

        private async Task PauseRemoval(string boundary, CancellationToken token)
        {
            if (removalBoundary == boundary)
            { RemovalReached.TrySetResult(); await RemovalRelease.Task.WaitAsync(token); }
            token.ThrowIfCancellationRequested();
        }

        internal async Task WaitDirectRemoval(Task action)
        {
            await Task.WhenAny(action, RemovalReached.Task).WaitAsync(Token);
            Assert.True(RemovalReached.Task.IsCompleted, "The real controller/service failed before the controlled SSL read.");
            Assert.False(action.IsCompleted);
        }

        internal SslController RemovalController(Guid actor)
        {
            var context = Controller(actor).ControllerContext;
            if (removalBoundary == "actor")
            {
                ControllerDb = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>()
                    .UseSqlite(keeper.ConnectionString).AddInterceptors(new RemovalActorReadBarrier(this)).Options);
                contexts.Add(ControllerDb);
            }
            var removalScopes = RemovalScopeOverride ?? Scopes;
            var services = new ServiceCollection().AddSingleton(ControllerDb)
                .AddSingleton<ICustomerScopeService>(removalScopes).AddSingleton<IIisSiteService>(RemovalSites)
                .AddSingleton<IConcurrencyLock>(RemovalGates).BuildServiceProvider();
            providers.Add(services);
            context.HttpContext.RequestServices = services;
            return new(RemovalService, removalScopes, null!, Area.Denetim) { ControllerContext = context };
        }

        internal Task<byte[]> RemovalState() => File.ReadAllBytesAsync(Area.DosyaDeposu.ResolvePath("config/ssl.json"), Token);

        private sealed class RemovalActorReadBarrier(Fixture fixture) : DbCommandInterceptor
        {
            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                await fixture.PauseRemoval("actor", cancellationToken);
                return result;
            }
        }
    }
}
