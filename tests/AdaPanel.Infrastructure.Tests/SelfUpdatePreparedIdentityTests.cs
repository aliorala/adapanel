using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SelfUpdate;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SelfUpdatePreparedIdentityTests
{
    [Fact]
    public async Task StatusKeepsPreparedIdentitySeparateFromNewerRelease()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var status = await fixture.Service.CheckForUpdateAsync();
        Assert.Equal("99.0.2", status.LatestVersion);
        Assert.Equal(Fixture.Hash(Fixture.NewPackage), status.PackageSha256);
        Assert.Equal("99.0.1", status.PreparedVersion);
        Assert.Equal(Fixture.Hash(Fixture.OldPackage), status.PreparedPackageSha256);
        Assert.True(status.IsPackageReady);
    }

    [Fact]
    public async Task TamperedPreparedFileIsNotReportedReady()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        await File.WriteAllBytesAsync(fixture.PreparedPath, Fixture.NewPackage);
        var status = await fixture.Service.CheckForUpdateAsync();
        Assert.False(status.IsPackageReady);
        Assert.Empty(fixture.Launcher.Packages);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("missing")]
    public async Task InstallRejectsAConfirmationForAnotherPreparedIdentity(string difference)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var request = fixture.Confirmation();
        if (difference == "version") request.ExpectedVersion = "99.0.2";
        if (difference == "hash") request.ExpectedPackageSha256 = Fixture.Hash(Fixture.NewPackage);
        if (difference == "missing") request = new InstallUpdateRequest();
        var before = await File.ReadAllTextAsync(fixture.PendingPath);
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.InstallPreparedUpdateAsync(request));
        Assert.Equal("update.package_changed", error.MessageKey);
        Assert.Empty(fixture.Launcher.Packages);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.PendingPath));
    }

    [Fact]
    public async Task InstallConfirmedPackageReportsItsIdentityEvenWhenNewerReleaseExists()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var status = await fixture.Service.InstallPreparedUpdateAsync(fixture.Confirmation());
        Assert.Single(fixture.Launcher.Packages);
        Assert.Equal(Fixture.OldPackage, fixture.Launcher.Packages[0]);
        Assert.Equal("99.0.1", status.PreparedVersion);
        Assert.Equal(Fixture.Hash(Fixture.OldPackage), status.PreparedPackageSha256);
        Assert.True(status.IsInstallScheduled);
    }

    [Fact]
    public async Task ConfirmationIsCheckedAgainstTheCurrentPendingRecord()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var confirmation = fixture.Confirmation();
        await fixture.PrepareAsync("99.0.2", Fixture.NewPackage);
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.InstallPreparedUpdateAsync(confirmation));
        Assert.Equal("update.package_changed", error.MessageKey);
        Assert.Empty(fixture.Launcher.Packages);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("stream")]
    [InlineData("identity")]
    public async Task FailedNewDownloadPreservesThePreviouslyVerifiedPackage(string failure)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var before = await File.ReadAllTextAsync(fixture.PendingPath);
        fixture.Failure = failure;
        var error = await Record.ExceptionAsync(() => fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest()));
        Assert.NotNull(error);
        Assert.Equal(Fixture.OldPackage, await File.ReadAllBytesAsync(fixture.PreparedPath));
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.PendingPath));
        Assert.Empty(fixture.Launcher.Packages);
        var status = await fixture.Service.CheckForUpdateAsync();
        Assert.True(status.IsPackageReady);
    }

    [Fact]
    public async Task SuccessfulDownloadPublishesItsOwnPreparedIdentity()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var result = await fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest());
        Assert.Equal("99.0.2", result.PreparedVersion);
        Assert.Equal(Fixture.Hash(Fixture.NewPackage), result.PreparedPackageSha256);
        Assert.Equal(Fixture.NewPackage, await File.ReadAllBytesAsync(result.PackagePath));
        Assert.True(result.IsPackageReady);
    }

    [Theory]
    [InlineData("connect", "update.download_failed", 502)]
    [InlineData("stream", "update.download_failed", 502)]
    [InlineData("timeout", "update.download_timeout", 504)]
    public async Task DownloadTransportFailureReturnsActionableErrorAndPreservesPreparedPackage(
        string failure, string key, int httpStatus)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var before = await File.ReadAllTextAsync(fixture.PendingPath);
        fixture.Failure = failure;

        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest()));

        Assert.Equal(key, error.MessageKey);
        Assert.Equal(httpStatus, error.HttpStatusCode);
        Assert.Empty(error.Details);
        Assert.Null(error.InnerException);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.PendingPath));
        Assert.Equal(Fixture.OldPackage, await File.ReadAllBytesAsync(fixture.PreparedPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "updates"), "AdaPanelUpdate-*.exe"));
        Assert.Empty(fixture.Launcher.Packages);
    }

    [Fact]
    public async Task CancellationDuringPartialDownloadRemainsCancellationAndCleansCandidate()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var before = await File.ReadAllTextAsync(fixture.PendingPath);
        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var content = new PausedContent(requestCancellation.Token);
        fixture.FirstDownloadContent = content;
        var download = fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest(), requestCancellation.Token);
        try
        {
            await content.Entered.Task.WaitAsync(requestCancellation.Token);
            Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "updates"), "AdaPanelUpdate-*.exe"));
            requestCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.Equal(before, await File.ReadAllTextAsync(fixture.PendingPath));
            Assert.Equal(Fixture.OldPackage, await File.ReadAllBytesAsync(fixture.PreparedPath));
            Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "updates"), "AdaPanelUpdate-*.exe"));
            Assert.Empty(fixture.Launcher.Packages);
        }
        finally
        {
            content.Release.TrySetResult();
            await Record.ExceptionAsync(() => download);
        }
    }

    [Fact]
    public async Task SuccessfulPublicationRemovesOnlyItsPreviousManagedPackage()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var unrelated = Path.Combine(fixture.Root, "updates", "other.exe");
        await File.WriteAllBytesAsync(unrelated, Fixture.OldPackage);
        var first = await fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest());
        Assert.False(File.Exists(fixture.PreparedPath));
        Assert.True(File.Exists(unrelated));
        var second = await fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest());
        Assert.False(File.Exists(first.PackagePath));
        Assert.Equal(Fixture.NewPackage, await File.ReadAllBytesAsync(second.PackagePath));
        Assert.True(File.Exists(unrelated));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("unrelated")]
    [InlineData("file-link")]
    [InlineData("parent-link")]
    [InlineData("changed-bytes")]
    public async Task SupersededCleanupPreservesPathsItCannotOwn(string kind)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var protectedPath = fixture.PreparedPath;
        if (kind is "outside" or "unrelated")
        {
            protectedPath = kind == "outside" ? Path.Combine(fixture.Root, "AdaPanelSetup.exe")
                : Path.Combine(fixture.Root, "updates", "unrelated.exe");
            File.Move(fixture.PreparedPath, protectedPath);
            await fixture.ChangePendingPathAsync(protectedPath);
        }
        else if (kind == "file-link")
        {
            var target = Path.Combine(fixture.Root, "foreign.exe");
            File.Move(fixture.PreparedPath, target);
            File.CreateSymbolicLink(fixture.PreparedPath, target);
        }
        else if (kind == "parent-link")
        {
            var updates = Path.Combine(fixture.Root, "updates");
            var target = Path.Combine(fixture.Root, "linked-updates");
            Directory.Move(updates, target);
            Directory.CreateSymbolicLink(updates, target);
        }
        else await File.WriteAllBytesAsync(protectedPath, Fixture.NewPackage);
        var before = await File.ReadAllBytesAsync(protectedPath);
        var result = await fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest());
        Assert.Equal(before, await File.ReadAllBytesAsync(protectedPath));
        Assert.Equal(Fixture.NewPackage, await File.ReadAllBytesAsync(result.PackagePath));
        Assert.True(result.IsPackageReady);
    }

    [Fact]
    public async Task SupersededCleanupDoesNotRemoveAnotherRequestsActiveDownload()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var paused = new PausedContent(timeout.Token);
        fixture.FirstDownloadContent = paused;
        var firstDownload = fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest(), timeout.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(timeout.Token);
            var firstPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "updates"), "AdaPanelUpdate-*.exe"));
            var other = await fixture.CreateService().ApplyUpdateAsync(new ApplyUpdateRequest(), timeout.Token);
            Assert.False(File.Exists(fixture.PreparedPath));
            Assert.True(File.Exists(firstPath));
            Assert.Equal(Fixture.NewPackage, await File.ReadAllBytesAsync(other.PackagePath, timeout.Token));
            paused.Release.TrySetResult();
            var first = await firstDownload;
            Assert.Equal(firstPath, first.PackagePath);
            Assert.False(File.Exists(other.PackagePath));
            Assert.Equal(Fixture.NewPackage, await File.ReadAllBytesAsync(first.PackagePath, timeout.Token));
        }
        finally
        {
            paused.Release.TrySetResult();
            await firstDownload;
        }
    }

    [Fact]
    public async Task ADownloadCannotReplaceAnAlreadyScheduledInstaller()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        await fixture.Service.InstallPreparedUpdateAsync(fixture.Confirmation());
        var pendingBefore = await File.ReadAllTextAsync(fixture.PendingPath);
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest()));
        Assert.Equal("update.install_already_scheduled", error.MessageKey);
        Assert.Equal(pendingBefore, await File.ReadAllTextAsync(fixture.PendingPath));
        Assert.Equal(Fixture.OldPackage, await File.ReadAllBytesAsync(fixture.PreparedPath));
        Assert.Single(fixture.Launcher.Packages);
    }

    [Fact]
    public async Task ARepeatedInstallDoesNotRescheduleTheSameInstaller()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        await fixture.Service.InstallPreparedUpdateAsync(fixture.Confirmation());
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.InstallPreparedUpdateAsync(fixture.Confirmation()));
        Assert.Equal("update.install_already_scheduled", error.MessageKey);
        Assert.Single(fixture.Launcher.Packages);
    }

    [Fact]
    public async Task CanceledConfirmationDoesNotScheduleAnInstaller()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.InstallPreparedUpdateAsync(fixture.Confirmation(), cancellation.Token));
        Assert.Empty(fixture.Launcher.Packages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFinishedFailedAttemptAllowsAnExplicitRetry(bool downloadAgain)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        await fixture.Service.InstallPreparedUpdateAsync(fixture.Confirmation());
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "updates", "last-attempt.json"),
            JsonSerializer.Serialize(new { phase = "failed", updatedAtUtc = DateTime.UtcNow.AddSeconds(1), installerExitCode = 5 }));
        var before = await fixture.Service.CheckForUpdateAsync();
        Assert.False(before.IsInstallScheduled);
        if (downloadAgain)
        {
            var result = await fixture.Service.ApplyUpdateAsync(new ApplyUpdateRequest());
            Assert.Equal("99.0.2", result.PreparedVersion);
            Assert.Single(fixture.Launcher.Packages);
        }
        else
        {
            var result = await fixture.Service.InstallPreparedUpdateAsync(fixture.Confirmation());
            Assert.True(result.IsInstallScheduled);
            Assert.Equal(2, fixture.Launcher.Packages.Count);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("changed")]
    public async Task HttpInstallDoesNotScheduleOrAuditWithoutTheConfirmedIdentity(string difference)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var audit = new RecordingAuditService();
        var controller = new UpdateController(fixture.Service, audit)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var request = difference == "missing" ? null : difference == "empty" ? new InstallUpdateRequest() : fixture.Confirmation();
        if (difference == "changed") request!.ExpectedVersion = "99.0.2";
        var error = await Assert.ThrowsAsync<AppException>(() => controller.InstallAsync(request!, CancellationToken.None));
        Assert.Equal("update.package_changed", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Empty(fixture.Launcher.Packages);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task HttpInstallAuditsTheConfirmedVersionInsteadOfTheLatestRelease()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var audit = new RecordingAuditService();
        var controller = new UpdateController(fixture.Service, audit)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        await controller.InstallAsync(fixture.Confirmation(), CancellationToken.None);
        var written = Assert.Single(audit.Kayitlar);
        Assert.Equal("update.install.scheduled", written.Action);
        Assert.Contains("99.0.1", written.Details);
        Assert.DoesNotContain("99.0.2", written.Details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASecondServiceCannotReplaceThePackageDuringInstallationValidation(bool cancelWaiter)
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identities = 0;
        fixture.BeforeIdentity = async token =>
        {
            if (Interlocked.Increment(ref identities) != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var waiterToken = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var install = fixture.Service.InstallPreparedUpdateAsync(fixture.Confirmation(), timeout.Token);
        Task<UpdateStatusDto>? replacement = null;
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var secondService = fixture.CreateService();
            replacement = secondService.ApplyUpdateAsync(new ApplyUpdateRequest(), waiterToken.Token);
            while (SelfUpdateLifecycle.ReferencesForPath(fixture.PendingPath) < 2)
            {
                Assert.False(replacement.IsCompleted);
                await Task.Delay(1, timeout.Token);
            }
            Assert.Equal(Fixture.OldPackage, await File.ReadAllBytesAsync(fixture.PreparedPath, timeout.Token));
            if (cancelWaiter)
            {
                waiterToken.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement!);
            }
            release.TrySetResult();
            await install;
            if (!cancelWaiter)
            {
                var error = await Assert.ThrowsAsync<AppException>(() => replacement!);
                Assert.Equal("update.install_already_scheduled", error.MessageKey);
            }
            Assert.Equal(Fixture.OldPackage, Assert.Single(fixture.Launcher.Packages));
            Assert.Equal(Fixture.OldPackage, await File.ReadAllBytesAsync(fixture.PreparedPath, timeout.Token));
            Assert.Equal(0, SelfUpdateLifecycle.ReferencesForPath(fixture.PendingPath));
        }
        finally
        {
            release.TrySetResult();
            await install;
            if (replacement is not null) await Record.ExceptionAsync(() => replacement!);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly byte[] OldPackage = [(byte)'M', (byte)'Z', 1, 2, 3, 4];
        internal static readonly byte[] NewPackage = [(byte)'M', (byte)'Z', 5, 6, 7, 8];
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "adapanel-prepared-identity-" + Guid.NewGuid().ToString("N"));
        internal string PreparedPath => Path.Combine(Root, "updates", "AdaPanelSetup.exe");
        internal string PendingPath => Path.Combine(Root, "updates", "pending-update.json");
        internal readonly Launcher Launcher = new();
        internal ISelfUpdateService Service { get; }
        internal string Failure = string.Empty;
        internal Func<CancellationToken, Task>? BeforeIdentity;
        internal HttpContent? FirstDownloadContent;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Service = CreateService();
        }
        internal ISelfUpdateService CreateService()
        {
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }));
            return new SelfUpdateService(store, new Factory(this), Launcher,
                async (path, token) =>
                {
                    if (BeforeIdentity is not null) await BeforeIdentity(token);
                    return Failure == "identity" && !File.ReadAllBytes(path).SequenceEqual(OldPackage)
                    ? new UpdatePackageIdentity("foreign", "foreign", "NotSigned", string.Empty)
                    : new UpdatePackageIdentity("AdaPanel", "AdaPanel", "NotSigned", string.Empty);
                });
        }
        internal InstallUpdateRequest Confirmation() => new()
        {
            ExpectedVersion = "99.0.1", ExpectedPackageSha256 = Hash(OldPackage)
        };
        internal async Task PrepareAsync(string version = "99.0.1", byte[]? bytes = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreparedPath)!);
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            bytes ??= OldPackage;
            await File.WriteAllBytesAsync(PreparedPath, bytes);
            await File.WriteAllTextAsync(PendingPath, JsonSerializer.Serialize(new
            {
                version, packagePath = PreparedPath, packageSha256 = Hash(bytes),
                downloadUrl = "https://github.com/aliorala/adapanel/releases/download/v99.0.1/AdaPanelSetup.exe",
                stagedAtUtc = DateTime.UtcNow
            }));
            await File.WriteAllTextAsync(Path.Combine(Root, "config", "update-manifest.json"), JsonSerializer.Serialize(new
            {
                latestVersion = "99.0.2", packageSha256 = Hash(NewPackage), fetchedAtUtc = DateTime.UtcNow,
                downloadUrl = "https://github.com/aliorala/adapanel/releases/download/v99.0.2/AdaPanelSetup.exe"
            }));
        }
        internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        internal async Task ChangePendingPathAsync(string path)
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(PendingPath))!;
            fields["packagePath"] = JsonSerializer.SerializeToElement(path);
            await File.WriteAllTextAsync(PendingPath, JsonSerializer.Serialize(fields));
        }
        public void Dispose() => Directory.Delete(Root, true);
        private sealed class Factory(Fixture fixture) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(new Handler(fixture));
        }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.RequestUri!.Host == "api.github.com")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            tag_name = "v99.0.2", body = "release",
                            assets = new[] { new { name = "AdaPanelSetup.exe",
                                browser_download_url = "https://github.com/aliorala/adapanel/releases/download/v99.0.2/AdaPanelSetup.exe",
                                digest = "sha256:" + Hash(NewPackage) } }
                        }))
                    });
                if (fixture.Failure == "connect")
                    throw new HttpRequestException("fixture private upstream diagnostic",
                        new System.Net.Sockets.SocketException(10060));
                if (fixture.Failure == "timeout")
                    throw new TaskCanceledException("fixture private timeout diagnostic");
                HttpContent content = Interlocked.Exchange(ref fixture.FirstDownloadContent, null) ?? (fixture.Failure switch
                {
                    "checksum" => new ByteArrayContent([(byte)'M', (byte)'Z', 99]),
                    "stream" => new FailingContent(),
                    _ => new ByteArrayContent(NewPackage)
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
        }
        private sealed class FailingContent : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("MZpartial"));
                throw new IOException("fixture interrupted download");
            }
        }
    }
    private sealed class PausedContent(CancellationToken token) : HttpContent
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = Fixture.NewPackage.Length; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Fixture.NewPackage.AsMemory(0, 2), token);
            await stream.FlushAsync(token);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            await stream.WriteAsync(Fixture.NewPackage.AsMemory(2), token);
        }
    }
    private sealed class Launcher : IUpdateInstallerLauncher
    {
        internal List<byte[]> Packages { get; } = [];
        public Task ScheduleAsync(string packagePath, string packageSha256, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Packages.Add(File.ReadAllBytes(packagePath));
            return Task.CompletedTask;
        }
    }
}
