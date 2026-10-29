using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationStepIntegrityTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("io")]
    public async Task FailedSiteLookupIsNotReportedAsAnAvailableSiteName(string failure)
    {
        var iis = Stub<IIisSiteService>((_, _) => throw (failure == "cancel" ? new OperationCanceledException("fixture") : new IOException("fixture")));
        var service = new SiteMigrationService(null!, null!, null!, iis, NullLogger<SiteMigrationService>.Instance);
        var error = await Record.ExceptionAsync(() => service.ValidateMigrationAsync(new() { SiteName = "fixture.test", CreateSite = true }));
        Assert.NotNull(error);
        if (failure == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
    }

    [Fact]
    public async Task TypedMissingSiteStillPassesTheCreateSitePreflight()
    {
        var iis = Stub<IIisSiteService>((_, _) => throw new AppException("sites.site_not_found", 404, 404));
        var service = new SiteMigrationService(null!, null!, null!, iis, NullLogger<SiteMigrationService>.Instance);
        Assert.True((await service.ValidateMigrationAsync(new() { SiteName = "fixture.test", CreateSite = true })).AllPassed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryDoesNotConvertCallerCancellationToConnectionFailure(bool cpanel)
    {
        using var cts = new CancellationTokenSource();
        var service = new SiteMigrationService(null!, null!, null!, null!, NullLogger<SiteMigrationService>.Instance,
            outboundResolver: (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("8.8.8.8") }))
        { DiscoveryClientFactory = () => new HttpClient(new CancelHandler(cts)) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (cpanel) await service.DiscoverCpanelAsync(new() { Host = "fake.example" }, cts.Token);
            else await service.DiscoverPleskAsync(new() { Host = "fake.example", ApiKey = "fixture-key" }, cts.Token);
        });
    }

    private sealed class CancelHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cts.Cancel(); throw new OperationCanceledException(cts.Token); }
    }

    [Fact]
    public async Task FileTransportCancellationDoesNotBecomeAConnectFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-file-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var iis = Stub<IIisSiteService>((_, _) => Task.FromResult(new SiteDto { PhysicalPath = root }));
            var service = new FileMigrationService(iis, outboundResolver: (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("8.8.8.8") }))
            { PullTransport = _ => Task.FromException<RemoteFileMigrationResult>(new OperationCanceledException("fixture cancelled")) };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PullAsync(new() { SiteName = "fixture.test", Host = "fake.example", Protocol = "sftp" }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("CreateDatabaseAsync")]
    [InlineData("CreateUserAsync")]
    public async Task ProvisioningFailureStopsTransferAndDoesNotInventCredentials(string operation)
    {
        using var f = new Fixture(); f.Failures[operation] = new IOException("fixture permission denied");
        var result = await f.Run();
        Assert.False(result.OverallSuccess); Assert.False(Assert.Single(result.Steps).Success);
        Assert.DoesNotContain("TransferDatabaseAsync", f.Calls);
        Assert.DoesNotContain("password:", Assert.Single(result.Steps).Detail ?? "");
        if (operation == "CreateDatabaseAsync") Assert.DoesNotContain("CreateUserAsync", f.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingUserCannotBecomeAnUnverifiedSuccessfulLogin(bool generated)
    {
        using var f = new Fixture(); f.Failures["CreateUserAsync"] = new AppException("database.user_already_exists");
        var result = await f.Run(password: generated ? "" : "submitted-password");
        Assert.False(result.OverallSuccess); Assert.DoesNotContain("TransferDatabaseAsync", f.Calls);
        Assert.Equal("database.user_already_exists", Assert.Single(result.Steps).Message);
        Assert.DoesNotContain("SetUserPasswordAsync", f.Calls);
    }

    [Fact]
    public async Task OnlyTypedExistingDatabaseCanContinueWithVerifiedNewUser()
    {
        using var f = new Fixture(); f.Failures["CreateDatabaseAsync"] = new AppException("database.database_already_exists");
        var result = await f.Run();
        Assert.True(result.OverallSuccess); Assert.Contains("TransferDatabaseAsync", f.Calls);
        Assert.Contains(f.CreatedPassword!, Assert.Single(result.Steps).Detail);
    }

    [Fact]
    public async Task NewSiteFailureSkipsAllRequestedDependentMutations()
    {
        using var f = new Fixture(); f.Failures["CreateAndProvisionAsync"] = new IOException("existing site or provision failure");
        var result = await f.Run(create: true, files: true);
        Assert.False(result.OverallSuccess);
        Assert.Equal(new[] { "CreateAndProvisionAsync" }, f.Calls);
        Assert.Equal(new[] { "site", "files", "database" }, result.Steps.Select(s => s.Step));
        Assert.All(result.Steps, s => Assert.False(s.Success));
        Assert.Equal("done", f.Events.Last().Phase);
    }

    [Fact]
    public async Task ExplicitExistingSiteModeStillPullsFilesAndTransfersDatabase()
    {
        using var f = new Fixture();
        var result = await f.Run(files: true);
        Assert.True(result.OverallSuccess); Assert.DoesNotContain("CreateAndProvisionAsync", f.Calls);
        Assert.Contains("PullAsync", f.Calls); Assert.Contains("TransferDatabaseAsync", f.Calls);
    }

    [Theory]
    [InlineData("CreateAndProvisionAsync")]
    [InlineData("PullAsync")]
    [InlineData("CreateDatabaseAsync")]
    [InlineData("CreateUserAsync")]
    [InlineData("TransferDatabaseAsync")]
    public async Task CancellationIsRethrownWithoutLaterStepsOrDoneSuccess(string operation)
    {
        using var f = new Fixture(); f.Failures[operation] = new OperationCanceledException("fixture cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(create: true, files: true));
        Assert.Equal(operation, f.Calls.Last());
        Assert.DoesNotContain(f.Events, e => e.Phase == "done");
    }

    [Fact]
    public async Task CancelledTokenPreventsEvenTheFirstMutationWhenProgressIsAbsent()
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.MigrateSiteAsync(new() { SiteName = "fixture.test", CreateSite = true, PullFiles = true, TransferDatabase = true }, null, null, cancellationToken: cts.Token));
        Assert.Empty(f.Calls);
    }

    [Theory]
    [InlineData("CreateAndProvisionAsync")]
    [InlineData("PullAsync")]
    [InlineData("CreateDatabaseAsync")]
    [InlineData("CreateUserAsync")]
    public async Task CancellationAfterAnIgnoringProviderReturnsStopsTheNextMutation(string operation)
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource();
        f.AfterSuccess = name => { if (name == operation) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.MigrateSiteAsync(new()
        { SiteName = "fixture.test", CreateSite = true, PullFiles = true, TransferDatabase = true }, null, null, cancellationToken: cts.Token));
        Assert.Equal(operation, f.Calls.Last());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-migration-integrity-" + Guid.NewGuid().ToString("N"));
        internal readonly List<string> Calls = [];
        internal readonly Dictionary<string, Exception> Failures = [];
        internal readonly List<MigrationProgressEvent> Events = [];
        internal readonly SiteMigrationService Service;
        internal string? CreatedPassword;
        internal Action<string>? AfterSuccess;
        internal Fixture()
        {
            Directory.CreateDirectory(root);
            object? Handle(MethodInfo method, object?[] args)
            {
                Calls.Add(method.Name);
                if (Failures.TryGetValue(method.Name, out var failure)) throw failure;
                AfterSuccess?.Invoke(method.Name);
                return method.Name switch
                {
                    "CreateAndProvisionAsync" => Task.FromResult(new SiteDto { Name = "fixture.test", PhysicalPath = root }),
                    "PullAsync" => Task.FromResult(new RemoteFileMigrationResult { FilesDownloaded = 1, TargetPath = root }),
                    "CreateDatabaseAsync" => Task.FromResult(new DatabaseWorkspaceDto()),
                    "CreateUserAsync" => User((CreateDatabaseUserRequest)args[0]!),
                    "TransferDatabaseAsync" => Task.FromResult(new DatabaseTransferResultDto()),
                    _ => throw new NotSupportedException(method.Name)
                };
            }
            var iis = Stub<IIisSiteService>((method, _) => method.Name == "GetSiteAsync"
                ? Task.FromResult(new SiteDto { Name = "fixture.test", PhysicalPath = root }) : throw new NotSupportedException(method.Name));
            Service = new(Stub<ISiteProvisioningService>(Handle), Stub<IFileMigrationService>(Handle), Stub<IDatabaseService>(Handle), iis, NullLogger<SiteMigrationService>.Instance);
        }
        private Task<DatabaseWorkspaceDto> User(CreateDatabaseUserRequest request) { CreatedPassword = request.Password; return Task.FromResult(new DatabaseWorkspaceDto()); }
        internal Task<MigrateSiteResultDto> Run(bool create = false, bool files = false, string password = "") => Service.MigrateSiteAsync(new()
        { SiteName = "fixture.test", CreateSite = create, PullFiles = files, TransferDatabase = true, TargetDatabase = "fixture_db", SourceDatabase = "source_db", TargetDatabasePassword = password }, null, null,
            (e, _) => { Events.Add(e); return Task.CompletedTask; });
        public void Dispose() => Directory.Delete(root, true);
    }
    private static T Stub<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
