using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Dns;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsSharedPolicyAuthorityTests
{
    [Theory]
    [InlineData("shared.example.test")]
    [InlineData("SHARED.EXAMPLE.TEST")]
    [InlineData("shared.example.test.")]
    public async Task A_reseller_cannot_open_transfer_for_an_out_of_scope_native_zone_peer(string storedPeerZone)
    {
        using var f = await Fixture.Create(peerZone: storedPeerZone);
        var before = await f.ZoneBytes();
        var error = await Record.ExceptionAsync(() => f.Update());
        await f.AssertDeniedAndUnchanged(error, before);
    }

    [Fact]
    public async Task An_unassigned_peer_is_not_treated_as_authorized_by_the_selected_site()
    {
        using var f = await Fixture.Create(unassignedPeer: true);
        var before = await f.ZoneBytes();
        var error = await Record.ExceptionAsync(() => f.Update());
        await f.AssertDeniedAndUnchanged(error, before);
    }

    [Fact]
    public async Task Renaming_into_an_existing_zone_cannot_replace_a_foreign_peers_transfer_policy()
    {
        using var f = await Fixture.Create(rename: true);
        var before = await f.ZoneBytes();
        var error = await Record.ExceptionAsync(() => f.Update());
        await f.AssertDeniedAndUnchanged(error, before);
    }

    [Fact]
    public async Task A_missing_dns_role_does_not_authorize_future_shared_transfer_policy()
    {
        using var f = await Fixture.Create();
        f.DnsAvailable = false;
        var before = await f.ZoneBytes();
        var error = await Record.ExceptionAsync(() => f.Update());
        await f.AssertDeniedAndUnchanged(error, before);
    }

    [Theory]
    [InlineData("native-zone")]
    [InlineData("preferences")]
    public async Task Peer_ownership_is_rechecked_after_the_controller_preflight_and_service_wait(string boundary)
    {
        using var f = await Fixture.Create(ownPeer: true);
        var before = await f.ZoneBytes();
        var error = await f.RunWhilePaused(boundary, () => f.ReassignPeer());
        await f.AssertDeniedAndUnchanged(error, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reseller_can_change_policy_when_all_members_are_in_its_scope(bool sameSubscription)
    {
        using var f = await Fixture.Create(ownPeer: true, sameSubscription: sameSubscription);
        var result = await f.Update();
        Assert.Equal(new[] { Fixture.RequestedIp }, result.ZoneTransferIps);
        Assert.Equal(new[] { Fixture.RequestedIp }, f.NativeTransfers);
        Assert.Equal(1, f.ZoneAuditCount);
        Assert.True(f.NativeMutations > 0);
    }

    [Fact]
    public async Task An_administrator_keeps_the_explicit_shared_policy_management_path()
    {
        using var f = await Fixture.Create(administrator: true);
        var result = await f.Update();
        Assert.Equal(new[] { Fixture.RequestedIp }, result.ZoneTransferIps);
        Assert.Equal(new[] { Fixture.RequestedIp }, f.NativeTransfers);
        Assert.Equal(1, f.ZoneAuditCount);
    }

    [Fact]
    public async Task A_foreign_site_in_a_different_native_zone_does_not_block_a_legitimate_policy_change()
    {
        using var f = await Fixture.Create(peerZone: "unrelated.example.test");
        await f.Update();
        Assert.Equal(new[] { Fixture.RequestedIp }, f.NativeTransfers);
        Assert.Equal(1, f.ZoneAuditCount);
    }

    [Fact]
    public async Task Canceling_the_native_zone_wait_preserves_policy_and_emits_no_success_audit()
    {
        using var f = await Fixture.Create(ownPeer: true);
        var before = await f.ZoneBytes();
        using var canceled = new CancellationTokenSource();
        var error = await f.RunWhilePaused("native-zone", () => { canceled.Cancel(); return Task.CompletedTask; }, canceled.Token);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        await f.AssertUnchanged(before);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Zone = "shared.example.test", RequestedIp = "192.0.2.99";
        private const string MainSite = "authorized-app", PeerSite = "foreign-private-app";
        private const string StatePath = "config/dns-zones.json", Prefix = "adapanel-dns-authority-";
        private const string Hash = "dns-policy-test-hash";
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        private readonly Guid actor = Guid.NewGuid(), otherReseller = Guid.NewGuid(), mainOwner = Guid.NewGuid(), peerOwner = Guid.NewGuid();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly TaskCompletionSource serviceRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource preferenceEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releasePreference = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AdaPanelDbContext db;
        private readonly JsonFileStore store;
        private readonly CustomerScopeService scopes;
        private readonly IIisSiteService sites;
        private readonly IMailService mail;
        private readonly ServiceProvider services;
        private readonly DnsController controller;
        private bool preflightFinished, pausePreferences;
        private int preferenceReads;
        internal bool DnsAvailable = true;
        internal int NativeMutations { get; private set; }
        internal string[] NativeTransfers { get; private set; } = ["192.0.2.53"];
        internal int ZoneAuditCount => db.AuditLogs.Count(row => row.Action == "dns.zone_updated");
        private CancellationToken Token => lifetime.Token;

        private Fixture(bool administrator)
        {
            Directory.CreateDirectory(root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            foreach (var id in new[] { actor, otherReseller, mainOwner, peerOwner }) db.Users.Add(new PanelUser
            {
                Id = id, UserName = id.ToString("N"), Email = id.ToString("N") + "@example.test", IsActive = true,
                PasswordHash = Hash, Role = id == actor ? administrator ? UserRole.Administrator : UserRole.Reseller :
                    id == otherReseller ? UserRole.Reseller : UserRole.Customer
            });
            db.SaveChanges();
            sites = Stub<IIisSiteService>((method, args) => method.Name switch
            {
                nameof(IIisSiteService.GetSitesAsync) => Task.FromResult<IReadOnlyCollection<SiteDto>>([Site(MainSite), Site(PeerSite)]),
                nameof(IIisSiteService.GetSiteAsync) => ReadSite((string)args![0]!, (CancellationToken)args[^1]!),
                _ => throw new NotSupportedException(method.Name)
            });
            scopes = new CustomerScopeService(store, Stub<IServicePlanService>(),
                Stub<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            var observedScopes = Stub<ICustomerScopeService>((method, args) =>
                method.Name == nameof(ICustomerScopeService.GetResellerMailDomainsAsync)
                    ? CompleteDomainPreflight((Guid)args![0]!, (CancellationToken)args[^1]!)
                    : method.Invoke(scopes, args));
            mail = Stub<IMailService>((method, _) => method.Name == nameof(IMailService.GetWorkspaceAsync)
                ? Task.FromResult(new MailWorkspaceDto()) : throw new NotSupportedException(method.Name));
            var preferences = Stub<IPanelPreferencesService>((method, args) => method.Name == nameof(IPanelPreferencesService.GetAsync)
                ? Preferences((CancellationToken)args![^1]!) : throw new NotSupportedException(method.Name));
            var dns = new DnsService(sites, preferences, store, NullLogger<DnsService>.Instance)
            {
                NativeCommand = Native,
                AvailabilityOverride = token => { token.ThrowIfCancellationRequested(); return Task.FromResult(DnsAvailable); }
            };
            var audit = new AuditService(db, scopes, store);
            services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<ICustomerScopeService>(observedScopes).AddSingleton<IDnsService>(dns)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IAuditService>(audit).AddSingleton<IMailService>(mail)
                .BuildServiceProvider();
            controller = ActivatorUtilities.CreateInstance<DnsController>(services);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, actor.ToString()),
                    new Claim(ClaimTypes.Role, administrator ? "Administrator" : "Reseller"),
                    new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(Hash)),
                    new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())],
                    CookieAuthenticationDefaults.AuthenticationScheme))
            } };
        }

        internal static async Task<Fixture> Create(bool ownPeer = false, bool administrator = false, bool sameSubscription = false,
            bool unassignedPeer = false, bool rename = false, string peerZone = Zone)
        {
            var f = new Fixture(administrator);
            try
            {
                await f.scopes.SetScopeAsync(f.mainOwner, sameSubscription ? [MainSite, PeerSite] : [MainSite], 0, 0, f.Token);
                await f.scopes.SetOwnerAsync(f.mainOwner, f.actor, f.Token);
                if (!sameSubscription && !unassignedPeer)
                {
                    await f.scopes.SetScopeAsync(f.peerOwner, [PeerSite], 0, 0, f.Token);
                    await f.scopes.SetOwnerAsync(f.peerOwner, ownPeer ? f.actor : f.otherReseller, f.Token);
                }
                // Explicit administrator-assigned DNS membership need not match a site's HTTP
                // binding. DomainClaim examines IIS/mail ownership, not this shared-zone JSON.
                await f.store.WriteAsync(StatePath, new[]
                {
                    new SavedZone { SiteName = MainSite, ZoneName = rename ? "old.example.test" : Zone },
                    new SavedZone { SiteName = PeerSite, ZoneName = peerZone }
                }, f.Token);
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        private SiteDto Site(string name) => new()
        {
            Id = name == MainSite ? 1 : 2, Name = name, PhysicalPath = Path.Combine(root, name),
            Bindings = [new SiteBindingDto { HostName = name == MainSite ? Zone : "independent.example.test", IpAddress = "192.0.2.10" }]
        };

        private Task<SiteDto> ReadSite(string name, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Contains(name, new[] { MainSite, PeerSite });
            if (preflightFinished && name == MainSite) serviceRead.TrySetResult();
            return Task.FromResult(Site(name));
        }

        private async Task<IReadOnlyCollection<string>> CompleteDomainPreflight(Guid id, CancellationToken token)
        {
            var actual = await scopes.GetResellerMailDomainsAsync(id, token);
            preflightFinished = true;
            return actual;
        }

        private async Task<PanelPreferencesDto> Preferences(CancellationToken token)
        {
            if (pausePreferences && Interlocked.Increment(ref preferenceReads) == 1)
            {
                preferenceEntered.TrySetResult();
                await releasePreference.Task.WaitAsync(Token);
            }
            token.ThrowIfCancellationRequested();
            return new PanelPreferencesDto { ServerAddress = "192.0.2.10" };
        }

        internal async Task<SiteDnsZoneDto> Update(CancellationToken? token = null)
        {
            var response = await controller.UpdateZoneAsync(MainSite, new UpdateDnsZoneRequest
            {
                ZoneName = Zone, ZoneTransferIps = [RequestedIp], NameServers = ["ns1." + Zone, "ns2." + Zone]
            }, sites, mail, NullLogger<DnsController>.Instance, token ?? Token);
            return Assert.IsType<ApiResponse<SiteDnsZoneDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        }

        internal async Task<Exception?> RunWhilePaused(string boundary, Func<Task> change, CancellationToken? token = null)
        {
            IDisposable? nativeLease = null;
            if (boundary == "native-zone") nativeLease = await DnsZoneLifecycle.EnterNativeAsync(store.ResolvePath(StatePath), Zone, Token);
            else pausePreferences = true;
            var action = Record.ExceptionAsync(() => Update(token));
            Exception? orchestrationError = null;
            try
            {
                var entered = boundary == "native-zone" ? serviceRead.Task : preferenceEntered.Task;
                if (await Task.WhenAny(entered, action).WaitAsync(Token) == action)
                    throw new InvalidOperationException("DNS action ended before the intended authority boundary.", await action);
                Assert.False(action.IsCompleted);
                await change();
            }
            catch (Exception error) { orchestrationError = error; }
            finally { nativeLease?.Dispose(); releasePreference.TrySetResult(); }
            var result = await action.WaitAsync(Token);
            if (orchestrationError is not null) ExceptionDispatchInfo.Capture(orchestrationError).Throw();
            return result;
        }

        internal Task ReassignPeer() => scopes.SetOwnerAsync(peerOwner, otherReseller, Token);
        internal Task<byte[]> ZoneBytes() => File.ReadAllBytesAsync(store.ResolvePath(StatePath), Token);
        internal async Task AssertDeniedAndUnchanged(Exception? error, byte[] before)
        {
            var failure = Assert.IsType<AppException>(error);
            Assert.Equal(403, failure.HttpStatusCode);
            Assert.DoesNotContain(PeerSite, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(PeerSite, failure.Details ?? "", StringComparison.Ordinal);
            await AssertUnchanged(before);
        }
        internal async Task AssertUnchanged(byte[] before)
        {
            Assert.Equal(before, await ZoneBytes());
            Assert.Equal(new[] { "192.0.2.53" }, NativeTransfers);
            Assert.Equal(0, NativeMutations);
            Assert.Equal(0, ZoneAuditCount);
        }

        private Task<CommandResult> Native(string script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (script.Contains("$basarisiz = @()", StringComparison.Ordinal))
            {
                NativeMutations++;
                var transfer = Regex.Match(script, @"-SecondaryServers (.*?) -Notify");
                if (transfer.Success) NativeTransfers = Regex.Matches(transfer.Groups[1].Value, "'([^']+)'")
                    .Select(match => match.Groups[1].Value).ToArray();
                else if (script.Contains("-SecureSecondaries NoTransfer", StringComparison.Ordinal)) NativeTransfers = [];
                return Task.FromResult(new CommandResult());
            }
            if (script.Contains("Remove-DnsServerZone", StringComparison.Ordinal))
            { NativeMutations++; return Task.FromResult(new CommandResult()); }
            if (script.Contains("[string]$z.ZoneType", StringComparison.Ordinal)) return Task.FromResult(new CommandResult { StandardOutput = "Primary" });
            if (script.Contains("'SIGNED='", StringComparison.Ordinal)) return Task.FromResult(new CommandResult { StandardOutput = "SIGNED=False" });
            throw new InvalidOperationException("Unexpected DNS command: this fixture never executes native processes.");
        }

        public void Dispose()
        {
            releasePreference.TrySetResult(); lifetime.Cancel(); services.Dispose(); db.Dispose(); connection.Dispose(); lifetime.Dispose();
            if (!Directory.Exists(root)) return;
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var name = Path.GetFileName(root);
            if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Cleanup must stay in the unique owned DNS authority fixture.");
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var path))
            {
                if (path != root && !Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fixture cleanup escaped its owned root.");
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Fixture cleanup cannot follow reparse points.");
                if (attributes.HasFlag(FileAttributes.Directory)) foreach (var child in Directory.GetFileSystemEntries(path)) pending.Push(child);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    public sealed class SavedZone
    {
        public string SiteName { get; set; } = "";
        public string ZoneName { get; set; } = "";
        public string[] NameServers { get; set; } = ["ns1." + Fixture.Zone, "ns2." + Fixture.Zone];
        public string[] ZoneTransferIps { get; set; } = ["192.0.2.53"];
        public int DefaultsVersion { get; set; } = DnsService.CurrentDefaultsVersion;
        public object[] Records { get; set; } = [];
        public string[] PublishedGroups { get; set; } = [];
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var result = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)result).Handler = handler ?? ((method, _) => throw new NotSupportedException(method.Name));
        return result;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
