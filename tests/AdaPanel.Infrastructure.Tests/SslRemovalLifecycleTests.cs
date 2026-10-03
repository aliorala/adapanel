using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class SslRemovalLifecycleTests
{
    public static IEnumerable<object[]> ReplacementWaits() =>
        from wait in new[] { "site", "bindings", "redirect" }
        from change in new[] { "id", "path", "application", "pool", "missing" }
        select new object[] { wait, change };

    [Theory]
    [MemberData(nameof(ReplacementWaits))]
    public async Task AReplacementSiteCannotReceiveTheOldRemovalRequest(string wait, string change)
    {
        using var f = await Fixture.Create(wait);
        var state = await f.StateBytes();
        var action = Capture(() => f.Remove());
        try
        {
            await f.Wait(action);
            f.Change(change);
        }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(f.Token)).MessageKey);
        Assert.Equal(0, f.RedirectWrites);
        Assert.Equal(0, f.BindingDeletes);
        Assert.Equal(0, f.CertificateDeletes);
        Assert.Single(f.Bindings);
        Assert.Equal(state, await f.StateBytes());
    }

    [Fact]
    public async Task ASameSnapshotRecreatedThroughTheNativeGuardIsStillANewGeneration()
    {
        using var f = await Fixture.Create("bindings");
        var state = await f.StateBytes();
        var action = Capture(() => f.Remove());
        try
        {
            await f.Wait(action);
            await f.NativeMutation();
        }
        finally { f.Release.TrySetResult(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(f.Token)).MessageKey);
        Assert.Equal(0, f.BindingDeletes);
        Assert.Equal(state, await f.StateBytes());
    }

    [Theory]
    [InlineData("site")]
    [InlineData("bindings")]
    [InlineData("redirect")]
    public async Task CancelingAReadWaitCannotMutateAndReleasesItsSiteReservation(string wait)
    {
        using var f = await Fixture.Create(wait);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var state = await f.StateBytes();
        var action = Capture(() => f.Remove(cancellation.Token));
        try
        {
            await f.Wait(action);
            cancellation.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        }
        finally { f.Release.TrySetResult(); }
        Assert.Equal(0, f.BindingDeletes);
        Assert.Equal(0, f.RedirectWrites);
        Assert.Equal(state, await f.StateBytes());
        await f.NativeMutation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSameSiteCanStillDisableRedirectAndRemoveItsBinding(bool redirect)
    {
        using var f = await Fixture.Create("");
        f.Redirect = redirect;
        var result = await f.Remove();
        Assert.True(result.BindingRemoved);
        Assert.Equal(redirect, result.HttpsRedirectDisabled);
        Assert.Equal("kept", result.CertificateAction);
        Assert.Equal(redirect ? 1 : 0, f.RedirectWrites);
        Assert.Equal(1, f.BindingDeletes);
        Assert.Equal(0, f.CertificateDeletes);
        Assert.Empty(await f.Records());
        Assert.Empty(f.Bindings);
        Assert.True(result.Workspace.HttpsRedirectMeasured);
        await f.NativeMutation();
    }

    [Fact]
    public async Task NativeBindingMutationUsesTheSharedIisGate()
    {
        using var f = await Fixture.Create("");
        await f.Remove();
        Assert.True(f.BindingDeleteHeldIis);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledNativeRemovalReleasesThePinForALaterRetry(bool canceled)
    {
        using var f = await Fixture.Create("");
        var original = canceled ? (Exception)new OperationCanceledException("fixture removal canceled")
            : new IOException("fixture removal failed");
        f.BindingFailure = original;
        Assert.Same(original, await Capture(() => f.Remove()));
        Assert.Single(await f.Records());
        await f.NativeMutation();
        f.BindingFailure = null;
        Assert.True((await f.Remove()).BindingRemoved);
    }

    [Fact]
    public async Task ADeletionAlreadyInProgressCannotStartAnSslRemoval()
    {
        using var f = await Fixture.Create("");
        using (await SiteMutationGuard.BeginDeletionAsync(f.Gates, f.Name, f.Token))
        {
            var error = await Capture(() => f.Remove());
            Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
            Assert.Equal(0, f.BindingDeletes);
            Assert.Single(await f.Records());
        }
        Assert.True((await f.Remove()).BindingRemoved);
    }

    [Theory]
    [InlineData("certificate")]
    [InlineData("address")]
    [InlineData("unreadable")]
    [InlineData("duplicate-address")]
    [InlineData("store")]
    public async Task AChangedBindingCannotReceiveTheRemovalApprovedForItsOldSnapshot(string change)
    {
        using var f = await Fixture.Create("redirect");
        var state = await f.StateBytes();
        var action = Capture(() => f.Remove());
        IReadOnlyCollection<IisHttpsBinding> changed = [];
        try
        {
            await f.Wait(action);
            var original = Assert.Single(f.Bindings);
            var replacement = new IisHttpsBinding
            {
                SiteName = original.SiteName,
                BindingInformation = change is "address" or "duplicate-address"
                    ? "127.0.0.2:443:" + f.Name : original.BindingInformation,
                Thumbprint = change == "certificate" ? "replacement-certificate" : original.Thumbprint,
                CertificateReadable = change != "unreadable",
                StoreName = change == "store" ? "WebHosting" : original.StoreName
            };
            changed = change == "duplicate-address" ? [original, replacement] : [replacement];
            f.Bindings = changed;
        }
        finally { f.Release.TrySetResult(); }

        Assert.IsType<AppException>(await action.WaitAsync(f.Token));
        Assert.Equal(0, f.BindingDeletes);
        Assert.Equal(0, f.RedirectWrites);
        Assert.Equal(0, f.CertificateDeletes);
        Assert.Equal(changed, f.Bindings);
        Assert.Equal(state, await f.StateBytes());
    }

    [Fact]
    public async Task BindingReadbackCannotDeleteAReplacementCertificateMetadataRecord()
    {
        using var f = await Fixture.Create("binding-verification");
        var action = Capture(() => f.Remove(removeFromStore: true));
        byte[] replacement = [];
        try
        {
            await f.Wait(action);
            Assert.Equal(1, f.BindingDeletes);
            await f.WriteCertificate("replacement-certificate");
            replacement = await f.StateBytes();
        }
        finally { f.Release.TrySetResult(); }

        Assert.IsType<AppException>(await action.WaitAsync(f.Token));
        Assert.Equal(replacement, await f.StateBytes());
        Assert.Equal("replacement-certificate", Assert.Single(await f.Records()).Thumbprint);
        Assert.Equal(0, f.CertificateDeletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnchangedTargetRemainsRemovableAfterAReadWait(bool anotherHostAppears)
    {
        using var f = await Fixture.Create("redirect");
        var action = f.Remove();
        try
        {
            await f.Wait(action);
            if (anotherHostAppears)
                f.Bindings = [.. f.Bindings, new IisHttpsBinding
                {
                    SiteName = f.Name, BindingInformation = "*:443:other." + f.Name,
                    Thumbprint = "neighbor-certificate", CertificateReadable = true
                }];
        }
        finally { f.Release.TrySetResult(); }

        Assert.True((await action.WaitAsync(f.Token)).BindingRemoved);
        Assert.Equal(1, f.BindingDeletes);
        Assert.Empty(await f.Records());
        if (anotherHostAppears)
            Assert.Equal("other." + f.Name, Assert.Single(f.Bindings).HostName);
        else
            Assert.Empty(f.Bindings);
    }

    [Fact]
    public async Task AHostAndPortSharedByTwoIpBindingsIsNotAnUnambiguousRemovalRequest()
    {
        using var f = await Fixture.Create("");
        var original = Assert.Single(f.Bindings);
        f.Bindings = [original, new IisHttpsBinding
        {
            SiteName = f.Name, BindingInformation = "127.0.0.2:443:" + f.Name,
            Thumbprint = "other-certificate", CertificateReadable = true
        }];
        var state = await f.StateBytes();

        var error = Assert.IsType<AppException>(await Capture(() => f.Remove()));
        Assert.Equal("ssl.host_name_required", error.MessageKey);
        Assert.Equal(0, f.BindingDeletes);
        Assert.Equal(0, f.RedirectWrites);
        Assert.Equal(2, f.Bindings.Count);
        Assert.Equal(state, await f.StateBytes());
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOTFOUND")]
    [InlineData("BINDING_CHANGED")]
    [InlineData("OK\nUnexpected native output")]
    public async Task AnUnconfirmedNativeRemovalCannotPublishSuccessOrDeleteMetadata(string output)
    {
        using var f = await Fixture.Create("");
        f.BindingOutput = output;
        var state = await f.StateBytes();
        var error = Assert.IsType<AppException>(await Capture(() => f.Remove()));
        Assert.Equal(output == "BINDING_CHANGED" ? "ssl.binding_changed" : "ssl.remove_binding_failed", error.MessageKey);
        Assert.Equal(1, f.BindingDeletes);
        Assert.Equal(0, f.CertificateDeletes);
        Assert.Equal(state, await f.StateBytes());
    }

    [Fact]
    public async Task ACertificateVerificationTimestampRefreshDoesNotInventAReplacement()
    {
        using var f = await Fixture.Create("binding-verification");
        var action = f.Remove();
        try
        {
            await f.Wait(action);
            await f.Area.DosyaDeposu.UpdateAsync("config/ssl.json", Array.Empty<SslCertificateDto>(), records =>
            {
                Assert.Single(records).LastVerifiedUtc = DateTime.UtcNow;
                records[0].IsPresentInStore = true;
                return records;
            }, f.Token);
        }
        finally { f.Release.TrySetResult(); }

        Assert.True((await action.WaitAsync(f.Token)).BindingRemoved);
        Assert.Empty(await f.Records());
        Assert.Equal(1, f.BindingDeletes);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly BulutAlani Area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        internal readonly TestGate Gates = new();
        internal readonly string Name = "ssl-remove-" + Guid.NewGuid().ToString("N") + ".example";
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string wait;
        private int siteReads;
        private int bindingReads;
        private int redirectReads;
        private bool bindingVerificationPaused;
        internal SiteDto? Site;
        internal IReadOnlyCollection<IisHttpsBinding> Bindings;
        internal bool Redirect;
        internal int RedirectWrites;
        internal int BindingDeletes;
        internal int CertificateDeletes;
        internal int CertificateCommands;
        internal bool CertificateCommandDeletes = true;
        internal Action? BeforeCertificateCommand;
        internal Exception? CertificateFailure;
        internal string CertificateOutput = """{"status":"removed","bindingsMeasured":true,"storesMeasured":true,"stores":["My"],"usedBy":[]}""";
        internal bool BindingDeleteHeldIis;
        internal Exception? BindingFailure;
        internal string BindingOutput = "OK";
        internal readonly WinAcmeSslService Service;
        internal CancellationToken Token => Area.Iptal;

        private Fixture(string wait)
        {
            this.wait = wait;
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            Change("initial");
            Bindings = [new() { SiteName = Name, BindingInformation = "*:443:" + Name,
                Thumbprint = "fixture-certificate", CertificateReadable = true }];
            var sites = DispatchProxy.Create<IIisSiteService, SiteProxy>();
            ((SiteProxy)(object)sites).Read = ReadSite;
            Service = new WinAcmeSslService(db, sites, null!, Area.DosyaDeposu, null!,
                NullLogger<WinAcmeSslService>.Instance, ReadBindings, Gates)
            {
                RedirectCommand = ReadOrWriteRedirect,
                RemoveBindingCommand = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    BindingDeletes++;
                    BindingDeleteHeldIis = Gates.Held;
                    if (BindingFailure is not null) throw BindingFailure;
                    Bindings = Bindings.Where(binding => binding.HostName != Name || binding.Port != 443).ToArray();
                    return Task.FromResult(Ok(BindingOutput));
                },
                RemoveCertificateCommand = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    CertificateCommands++;
                    BeforeCertificateCommand?.Invoke();
                    if (CertificateFailure is not null) throw CertificateFailure;
                    if (CertificateCommandDeletes) CertificateDeletes++;
                    return Task.FromResult(Ok(CertificateOutput));
                }
            };
        }

        internal static async Task<Fixture> Create(string wait)
        {
            var fixture = new Fixture(wait);
            await fixture.WriteCertificate("fixture-certificate");
            return fixture;
        }

        internal Task WriteCertificate(string thumbprint) =>
            Area.DosyaDeposu.WriteAsync("config/ssl.json", new[] { new SslCertificateDto
            {
                SiteName = Name, HostName = Name, Port = 443,
                Thumbprint = thumbprint, Source = "manual", NotAfterUtc = DateTime.UtcNow.AddDays(30)
            } }, Token);

        internal void Change(string change)
        {
            if (change == "missing") { Site = null; return; }
            Site = new SiteDto { Id = change == "id" ? 2 : 1, Name = Name,
                PhysicalPath = change == "path" ? Path.Combine(Area.Kok, "replacement") : Area.SiteKoku,
                ApplicationRootPath = change == "application" ? "/replacement" : "/",
                ApplicationPoolName = change == "pool" ? "replacement-pool" : "original-pool" };
        }

        private async Task<SiteDto> ReadSite(string name, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = Site ?? throw new AppException("sites.not_found", 404, 404);
            if (++siteReads == 1) await Pause("site", token);
            return snapshot;
        }

        private async Task<IReadOnlyCollection<IisHttpsBinding>?> ReadBindings(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++bindingReads == 1) await Pause("bindings", token);
            else if (BindingDeletes == 0) await Pause("before-binding-removal", token);
            if (BindingDeletes > 0 && !bindingVerificationPaused)
            {
                bindingVerificationPaused = true;
                await Pause("binding-verification", token);
            }
            return Bindings;
        }

        private async Task<CommandResult> ReadOrWriteRedirect(string script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (script.Contains("Clear-WebConfiguration", StringComparison.Ordinal))
            {
                RedirectWrites++;
                Redirect = false;
                return Ok("OK");
            }
            if (++redirectReads == 1) await Pause("redirect", token);
            return Ok(Redirect ? "YES" : "NO");
        }

        private async Task Pause(string boundary, CancellationToken token)
        {
            if (wait != boundary) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }

        internal async Task Wait(Task action)
        {
            await Task.WhenAny(action, Entered.Task).WaitAsync(Token);
            Assert.True(Entered.Task.IsCompleted, "Removal did not reach its controlled read wait.");
            Assert.False(action.IsCompleted);
        }

        internal Task<RemoveSslCertificateResultDto> Remove(CancellationToken? token = null, bool removeFromStore = false) =>
            Service.RemoveCertificateAsync(new() { SiteName = Name, HostName = Name, Port = 443,
                RemoveCertificateFromStore = removeFromStore }, token ?? Token);
        internal Task<byte[]> StateBytes() => File.ReadAllBytesAsync(Area.DosyaDeposu.ResolvePath("config/ssl.json"), Token);
        internal Task<SslCertificateDto[]> Records() => Area.DosyaDeposu.ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>(), Token);
        internal Task<bool> NativeMutation() => SiteMutationGuard.RunMutationAsync(Gates, Name,
            SiteMutationGuard.MutationKind.IdentityChange, () => Task.FromResult(true), Token);
        private static CommandResult Ok(string output) => new() { ExitCode = 0, StandardOutput = output };
        public void Dispose() { db.Dispose(); connection.Dispose(); Area.Dispose(); }
    }

    public class SiteProxy : DispatchProxy
    {
        internal Func<string, CancellationToken, Task<SiteDto>> Read = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name == "GetSiteAsync"
            ? Read((string)args![0]!, args.OfType<CancellationToken>().Single())
            : throw new NotSupportedException(targetMethod.Name);
    }

    private sealed class TestGate : IConcurrencyLock
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        internal bool Held { get; private set; }
        public Task RunExclusiveAsync(Func<Task> action, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, action, token);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, action, token);
        public Task RunExclusiveAsync(string key, Func<Task> action, CancellationToken token = default) => RunExclusiveAsync(key, async () => { await action(); return true; }, token);
        public async Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> action, CancellationToken token = default)
        {
            Assert.Equal(ConcurrencyKeys.Iis, key);
            await gate.WaitAsync(token);
            try { token.ThrowIfCancellationRequested(); Held = true; return await action(); }
            finally { Held = false; gate.Release(); }
        }
    }
}
