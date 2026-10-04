using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SelfUpdate;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SelfUpdateCheckTruthTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AFailedForcedRefreshIsNotForgottenByTheNextCachedStatusRead(HttpStatusCode failure)
    {
        using var f = new Fixture();
        var verified = await f.Check();
        Assert.False(verified.IsCheckFailed);
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(failure));

        var failed = await f.Check(force: true);
        Assert.True(failed.IsCheckFailed);
        // A second scoped service must observe the failed check too: the API and
        // attention polling do not necessarily share a service instance.
        var polled = await f.CreateService().CheckForUpdateAsync(cancellationToken: f.Token);

        Assert.True(polled.IsCheckFailed);
        Assert.Equal("update.github_fetch_failed", polled.Notes);
        Assert.Equal(verified.LatestVersion, polled.LatestVersion);
        Assert.Equal(verified.DownloadUrl, polled.DownloadUrl);
        Assert.Equal(verified.PackageSha256, polled.PackageSha256);
        Assert.Equal("Verified release", (await f.ReadManifest())["notes"]!.GetValue<string>());
        Assert.Equal(0, f.Launcher.Calls);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    public async Task AClockRollbackDoesNotKeepAFutureDatedCacheFresh(int minutesAhead)
    {
        using var f = new Fixture();
        await f.Check();
        await f.SetFetchedAt(DateTime.UtcNow.AddMinutes(minutesAhead));
        f.Respond = (_, _) => Task.FromResult(Fixture.Release("v99.0.2", "New release"));

        var status = await f.Check();

        Assert.Equal(2, f.Requests.Count);
        Assert.False(status.IsCheckFailed);
        Assert.Equal("99.0.2", status.LatestVersion);
        Assert.Equal("New release", status.Notes);
    }

    [Fact]
    public async Task AFailedRefreshOfAFutureDatedCacheDoesNotClaimAVerifiedCurrentStatus()
    {
        using var f = new Fixture();
        var verified = await f.Check();
        await f.SetFetchedAt(DateTime.UtcNow.AddHours(1));
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var status = await f.Check();

        Assert.True(status.IsCheckFailed);
        Assert.Equal(2, f.Requests.Count);
        Assert.Equal(verified.LatestVersion, status.LatestVersion);
        Assert.Equal("update.github_fetch_failed", status.Notes);
    }

    // These are response-contract failures, not a claim that GitHub normally
    // publishes malformed releases. A usable asset alone is not version evidence.
    [Theory]
    [InlineData("<missing>")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("release-stable")]
    public async Task AResponseWithoutAUsableVersionCannotConfirmThatThePanelIsCurrent(string? tag)
    {
        using var f = new Fixture();
        f.Respond = (_, _) => Task.FromResult(Fixture.Release(tag));

        var status = await f.Check();

        Assert.True(status.IsCheckFailed);
        Assert.NotEqual("Verified release", status.Notes);
        Assert.False(status.IsUpdateAvailable);
        Assert.Equal(0, f.Launcher.Calls);
    }

    [Fact]
    public async Task AnInvalidRefreshCannotReplaceThePreviouslyVerifiedManifest()
    {
        using var f = new Fixture();
        var verified = await f.Check();
        f.Respond = (_, _) => Task.FromResult(Fixture.Release("", "Invalid replacement"));

        var failed = await f.Check(force: true);

        Assert.True(failed.IsCheckFailed);
        Assert.Equal(verified.LatestVersion, failed.LatestVersion);
        Assert.Equal(verified.DownloadUrl, failed.DownloadUrl);
        Assert.Equal(verified.PackageSha256, failed.PackageSha256);
        Assert.Equal("Verified release", (await f.ReadManifest())["notes"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFreshSuccessfulCacheAvoidsAnotherHttpRequest()
    {
        using var f = new Fixture();
        var verified = await f.Check();
        f.Respond = (_, _) => throw new InvalidOperationException("Fresh cache must not need HTTP.");

        var cached = await f.CreateService().CheckForUpdateAsync(cancellationToken: f.Token);

        Assert.False(cached.IsCheckFailed);
        Assert.Equal(verified.LatestVersion, cached.LatestVersion);
        Assert.Equal(verified.Notes, cached.Notes);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task AForcedCheckBypassesAFreshSuccessfulCache()
    {
        using var f = new Fixture();
        await f.Check();
        f.Respond = (_, _) => Task.FromResult(Fixture.Release("v99.0.2", "New release"));

        var refreshed = await f.Check(force: true);

        Assert.False(refreshed.IsCheckFailed);
        Assert.Equal("99.0.2", refreshed.LatestVersion);
        Assert.Equal(2, f.Requests.Count);
    }

    [Fact]
    public async Task AnExpiredSuccessfulCacheIsRefreshedNormally()
    {
        using var f = new Fixture();
        await f.Check();
        await f.SetFetchedAt(DateTime.UtcNow.AddMinutes(-16));
        f.Respond = (_, _) => Task.FromResult(Fixture.Release("v99.0.2"));

        var refreshed = await f.Check();

        Assert.False(refreshed.IsCheckFailed);
        Assert.Equal("99.0.2", refreshed.LatestVersion);
        Assert.Equal(2, f.Requests.Count);
    }

    [Fact]
    public async Task ASuccessfulForcedRefreshClearsAnEarlierFailureForLaterReaders()
    {
        using var f = new Fixture();
        await f.Check();
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.True((await f.Check(force: true)).IsCheckFailed);
        f.Respond = (_, _) => Task.FromResult(Fixture.Release("v99.0.2", "Recovered release"));

        var recovered = await f.Check(force: true);
        var cached = await f.CreateService().CheckForUpdateAsync(cancellationToken: f.Token);

        Assert.False(recovered.IsCheckFailed);
        Assert.False(cached.IsCheckFailed);
        Assert.Equal("99.0.2", cached.LatestVersion);
        Assert.Equal("Recovered release", cached.Notes);
        Assert.Equal(3, f.Requests.Count);
    }

    [Theory]
    [InlineData("v99.0.1", "99.0.1")]
    [InlineData("99.0.1+build.2", "99.0.1+build.2")]
    [InlineData("99.0.1-rc1", "99.0.1-rc1")]
    public async Task ValidVersionAndSuffixesRemainSupported(string tag, string expected)
    {
        using var f = new Fixture();
        f.Respond = (_, _) => Task.FromResult(Fixture.Release(tag));

        var status = await f.Check();

        Assert.False(status.IsCheckFailed);
        Assert.Equal(expected, status.LatestVersion);
        Assert.True(status.IsUpdateAvailable);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task AnAlreadyCanceledCheckDoesNotIssueAnHttpRequest()
    {
        using var f = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.CreateService().CheckForUpdateAsync(cancellationToken: canceled.Token));

        Assert.Empty(f.Requests);
        Assert.False(File.Exists(f.ManifestPath));
    }

    [Fact]
    public async Task CancellationDuringHttpDoesNotBecomeAnUpdateStatusOrReplaceTheCache()
    {
        using var f = new Fixture();
        await f.Check();
        var before = await File.ReadAllBytesAsync(f.ManifestPath, f.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Respond = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled HTTP must not return a release.");
        };
        var pending = f.CreateService().CheckForUpdateAsync(true, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(f.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            cancellation.Cancel();
            await Record.ExceptionAsync(() => pending);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(f.ManifestPath, f.Token));
        Assert.Equal(0, f.Launcher.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "update.github_no_release")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "update.github_fetch_failed")]
    public async Task AFirstHttpFailureIsExplicitlyUnknown(HttpStatusCode failure, string expectedNote)
    {
        using var f = new Fixture();
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(failure));

        var status = await f.Check();

        Assert.True(status.IsCheckFailed);
        Assert.False(status.IsUpdateAvailable);
        Assert.Equal(expectedNote, status.Notes);
        Assert.False(File.Exists(f.ManifestPath));
    }

    [Fact]
    public async Task AFailedVersionCheckPreservesAnIndependentlyVerifiedPreparedPackage()
    {
        using var f = new Fixture();
        await f.Check();
        var package = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
        var path = f.Store.ResolvePath("updates/AdaPanelSetup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, package, f.Token);
        var hash = Convert.ToHexString(SHA256.HashData(package));
        await f.Store.WriteAsync("updates/pending-update.json", new
        {
            version = "99.0.3", packagePath = path, packageSha256 = hash,
            stagedAtUtc = DateTime.UtcNow
        }, f.Token);
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var status = await f.Check(force: true);

        Assert.True(status.IsCheckFailed);
        Assert.True(status.IsPackageReady);
        Assert.Equal("99.0.3", status.PreparedVersion);
        Assert.Equal(hash, status.PreparedPackageSha256);
        Assert.Equal(package, await File.ReadAllBytesAsync(path, f.Token));
        Assert.Equal(0, f.Launcher.Calls);
    }

    [Fact]
    public async Task EquivalentRepositorySpellingStillUsesTheAllowedGitHubRepository()
    {
        using var f = new Fixture();
        await f.Store.WriteAsync("config/update-github.json", new { repo = "https://github.com/aliorala/adapanel.git" }, f.Token);

        var status = await f.Check();

        Assert.False(status.IsCheckFailed);
        Assert.Equal("https://api.github.com/repos/aliorala/adapanel/releases/latest", Assert.Single(f.Requests));
    }

    [Fact]
    public async Task ADelayedFailedCheckCannotEraseTheFollowingSuccessfulRefresh()
    {
        using var f = new Fixture();
        await f.Check();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        f.Respond = async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return Fixture.Release("v99.0.2", "Following successful release");
        };
        var failing = f.Check(force: true);
        Task<UpdateStatusDto>? following = null;
        try
        {
            await entered.Task.WaitAsync(f.Token);
            following = f.CreateService().CheckForUpdateAsync(true, f.Token);
            await f.WaitForQueuedCheck();
            Assert.Equal(1, Volatile.Read(ref calls));
            // Release fetching must not hold the package/installer gate.
            using (await SelfUpdateLifecycle.EnterAsync(f.Store.ResolvePath("updates/pending-update.json"), f.Token)) { }
            release.TrySetResult();
            Assert.True((await failing).IsCheckFailed);
            Assert.False((await following).IsCheckFailed);

            var cached = await f.CreateService().CheckForUpdateAsync(cancellationToken: f.Token);
            Assert.False(cached.IsCheckFailed);
            Assert.Equal("99.0.2", cached.LatestVersion);
            Assert.Equal("Following successful release", cached.Notes);
            Assert.Equal(2, calls);
            Assert.Equal(0, SelfUpdateCheckLifecycle.ReferencesForPath(f.ManifestPath));
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => failing);
            if (following is not null) await Record.ExceptionAsync(() => following);
        }
    }

    [Fact]
    public async Task CancelingAQueuedCheckDoesNotSendHttpOrPublishFailureAndReleasesItsWaiter()
    {
        using var f = new Fixture();
        await f.Check();
        var original = await File.ReadAllBytesAsync(f.ManifestPath, f.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Respond = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Fixture.Release("v99.0.2", "Uninterrupted release");
        };
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var active = f.Check(force: true);
        Task<UpdateStatusDto>? queued = null;
        try
        {
            await entered.Task.WaitAsync(f.Token);
            queued = f.CreateService().CheckForUpdateAsync(true, canceled.Token);
            await f.WaitForQueuedCheck();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(1, SelfUpdateCheckLifecycle.ReferencesForPath(f.ManifestPath));
            Assert.Equal(2, f.Requests.Count);
            Assert.Equal(original, await File.ReadAllBytesAsync(f.ManifestPath, f.Token));
            release.TrySetResult();

            Assert.False((await active).IsCheckFailed);
            var cached = await f.Check();
            Assert.False(cached.IsCheckFailed);
            Assert.Equal("99.0.2", cached.LatestVersion);
            Assert.Equal(0, SelfUpdateCheckLifecycle.ReferencesForPath(f.ManifestPath));
            Assert.Equal(2, f.Requests.Count);
        }
        finally
        {
            canceled.Cancel();
            release.TrySetResult();
            await Record.ExceptionAsync(() => active);
            if (queued is not null) await Record.ExceptionAsync(() => queued);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-check-truth-";
        private readonly string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        private readonly List<HttpClient> clients = [];
        internal readonly List<string> Requests = [];
        internal readonly Launcher Launcher = new();
        internal JsonFileStore Store { get; }
        internal CancellationToken Token => deadline.Token;
        internal string ManifestPath => Store.ResolvePath("config/update-manifest.json");
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond =
            (_, _) => Task.FromResult(Release("v99.0.1"));

        internal Fixture()
        {
            Directory.CreateDirectory(root);
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        }

        internal SelfUpdateService CreateService() => new(Store, new Factory(this), Launcher,
            (_, _) => throw new InvalidOperationException("Checking versions must not invoke native package identity tools."));
        internal Task<UpdateStatusDto> Check(bool force = false) => CreateService().CheckForUpdateAsync(force, Token);
        internal async Task<JsonObject> ReadManifest() =>
            JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath, Token))!.AsObject();
        internal async Task SetFetchedAt(DateTime value)
        {
            var manifest = await ReadManifest();
            manifest["fetchedAtUtc"] = value;
            await Store.WriteAsync("config/update-manifest.json", manifest, Token);
        }
        internal async Task WaitForQueuedCheck()
        {
            while (SelfUpdateCheckLifecycle.ReferencesForPath(ManifestPath) < 2)
                await Task.Delay(10, Token);
        }

        internal static HttpResponseMessage Release(string? tag, string notes = "Verified release")
        {
            var body = new Dictionary<string, object?>
            {
                ["body"] = notes,
                ["assets"] = new[] { new
                {
                    name = "AdaPanelSetup.exe",
                    browser_download_url = "https://github.com/aliorala/adapanel/releases/download/v99.0.1/AdaPanelSetup.exe",
                    digest = "sha256:" + new string('a', 64)
                } }
            };
            if (tag != "<missing>") body["tag_name"] = tag;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        }

        public void Dispose()
        {
            foreach (var client in clients) client.Dispose();
            deadline.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root);
            var name = Path.GetFileName(fullRoot);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Cleanup must remain inside the owned update-check fixture root.");
            var pending = new Stack<string>();
            pending.Push(fullRoot);
            while (pending.TryPop(out var entry))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Fixture cleanup does not follow links.");
                if (!attributes.HasFlag(FileAttributes.Directory)) continue;
                foreach (var child in Directory.EnumerateFileSystemEntries(entry))
                {
                    if (!Path.GetFullPath(child).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Fixture cleanup escaped its owned root.");
                    pending.Push(child);
                }
            }
            Directory.Delete(fullRoot, recursive: true);
        }

        private sealed class Factory(Fixture fixture) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                var client = new HttpClient(new Handler(fixture));
                fixture.clients.Add(client);
                return client;
            }
        }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var uri = request.RequestUri!;
                Assert.Equal("api.github.com", uri.Host);
                Assert.Equal("/repos/aliorala/adapanel/releases/latest", uri.AbsolutePath);
                fixture.Requests.Add(uri.AbsoluteUri);
                return fixture.Respond(request, token);
            }
        }
    }

    private sealed class Launcher : IUpdateInstallerLauncher
    {
        internal int Calls;
        public Task ScheduleAsync(string packagePath, string packageSha256, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("A status check must never start an installer.");
        }
    }
}
