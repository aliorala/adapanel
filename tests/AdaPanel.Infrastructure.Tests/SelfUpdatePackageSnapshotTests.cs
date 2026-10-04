using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Dtos.Update;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SelfUpdate;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SelfUpdatePackageSnapshotTests
{
    [Theory]
    [InlineData("overwrite")]
    [InlineData("truncate")]
    [InlineData("replace")]
    public async Task StagingCannotPublishAFileChangedWhileItsPublisherIdentityIsBeingRead(string change)
    {
        using var f = await Fixture.Create();
        var previous = await File.ReadAllBytesAsync(f.PendingPath, f.Token);
        f.ChangeDuringIdentity = change;
        UpdateStatusDto? result = null;

        var failure = await Record.ExceptionAsync(async () => result = await f.Service.ApplyUpdateAsync(new(), f.Token));

        Assert.Equal(1, f.MutationAttempts);
        if (f.MutationSucceeded)
        {
            Assert.NotNull(failure);
            Assert.Null(result);
            Assert.Equal(previous, await File.ReadAllBytesAsync(f.PendingPath, f.Token));
            Assert.Equal(Fixture.OldPackage, await File.ReadAllBytesAsync(f.PreparedPath, f.Token));
        }
        else
        {
            Assert.Null(failure);
            Assert.NotNull(result);
            Assert.True(result.IsPackageReady);
            Assert.Equal(Fixture.NewPackage, await File.ReadAllBytesAsync(result.PackagePath, f.Token));
            Assert.Equal(Fixture.Hash(Fixture.NewPackage), result.PreparedPackageSha256);
            f.AssertReleased(result.PackagePath);
        }
        Assert.Empty(f.Scheduled);
        f.AssertNoUnpublishedCandidates(result?.PackagePath);
    }

    [Theory]
    [InlineData("overwrite")]
    [InlineData("truncate")]
    [InlineData("replace")]
    public async Task InstallationCannotScheduleDifferentBytesFromTheConfirmedHashAfterIdentityWait(string change)
    {
        using var f = await Fixture.Create();
        var pendingBefore = await File.ReadAllBytesAsync(f.PendingPath, f.Token);
        var attemptBefore = await File.ReadAllBytesAsync(f.AttemptPath, f.Token);
        f.ChangeDuringIdentity = change;
        UpdateStatusDto? result = null;

        var failure = await Record.ExceptionAsync(async () => result = await f.Service.InstallPreparedUpdateAsync(f.Confirmation(), f.Token));

        Assert.Equal(1, f.MutationAttempts);
        if (f.MutationSucceeded)
        {
            Assert.NotNull(failure);
            Assert.Empty(f.Scheduled);
            Assert.Equal(pendingBefore, await File.ReadAllBytesAsync(f.PendingPath, f.Token));
            Assert.Equal(attemptBefore, await File.ReadAllBytesAsync(f.AttemptPath, f.Token));
        }
        else
        {
            Assert.Null(failure);
            Assert.NotNull(result);
            Assert.True(result.IsInstallScheduled);
            var scheduled = Assert.Single(f.Scheduled);
            Assert.Equal(Fixture.OldPackage, scheduled.Bytes);
            Assert.Equal(Fixture.Hash(scheduled.Bytes), scheduled.Hash);
        }
        f.AssertReleased(f.PreparedPath);
    }

    [Theory]
    [InlineData("overwrite")]
    [InlineData("replace")]
    public async Task TheConfirmedPackageStaysReadableAndStableUntilSchedulingReturns(string change)
    {
        using var f = await Fixture.Create();
        f.ChangeDuringScheduling = change;

        var result = await f.Service.InstallPreparedUpdateAsync(f.Confirmation(), f.Token);

        Assert.Equal(1, f.MutationAttempts);
        Assert.True(result.IsInstallScheduled);
        var scheduled = Assert.Single(f.Scheduled);
        Assert.Equal(Fixture.Hash(scheduled.Bytes), scheduled.Hash);
        Assert.Equal(Fixture.OldPackage, scheduled.Bytes);
        Assert.Equal(Fixture.Hash(Fixture.OldPackage), result.PreparedPackageSha256);
        f.AssertReleased(f.PreparedPath);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("install")]
    public async Task NormalIdentityReadersAndTheLauncherCanReadTheProtectedPackage(string operation)
    {
        using var f = await Fixture.Create();

        var result = operation == "download"
            ? await f.Service.ApplyUpdateAsync(new(), f.Token)
            : await f.Service.InstallPreparedUpdateAsync(f.Confirmation(), f.Token);

        Assert.Equal(1, f.IdentityReads);
        Assert.True(result.IsPackageReady);
        var expected = operation == "download" ? Fixture.NewPackage : Fixture.OldPackage;
        Assert.Equal(expected, await File.ReadAllBytesAsync(result.PackagePath, f.Token));
        Assert.Equal(Fixture.Hash(expected), result.PreparedPackageSha256);
        if (operation == "download") Assert.Empty(f.Scheduled);
        else Assert.Equal(expected, Assert.Single(f.Scheduled).Bytes);
        f.AssertReleased(result.PackagePath);
        f.AssertNoUnpublishedCandidates(operation == "download" ? result.PackagePath : null);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("install")]
    public async Task CancellationAfterIdentityReadPreservesThePreviousAttemptAndPreparedRecord(string operation)
    {
        using var f = await Fixture.Create();
        var pendingBefore = await File.ReadAllBytesAsync(f.PendingPath, f.Token);
        var attemptBefore = await File.ReadAllBytesAsync(f.AttemptPath, f.Token);
        f.CancelAfterIdentity = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation == "download"
            ? f.Service.ApplyUpdateAsync(new(), f.Token)
            : f.Service.InstallPreparedUpdateAsync(f.Confirmation(), f.Token));

        Assert.Empty(f.Scheduled);
        Assert.Equal(pendingBefore, await File.ReadAllBytesAsync(f.PendingPath));
        Assert.Equal(attemptBefore, await File.ReadAllBytesAsync(f.AttemptPath));
        Assert.Equal(Fixture.OldPackage, await File.ReadAllBytesAsync(f.PreparedPath));
        f.AssertReleased(f.PreparedPath);
        f.AssertNoUnpublishedCandidates(null);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-package-snapshot-";
        private readonly string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        private readonly List<HttpClient> clients = [];
        internal static readonly byte[] OldPackage = [(byte)'M', (byte)'Z', 1, 2, 3, 4];
        internal static readonly byte[] NewPackage = [(byte)'M', (byte)'Z', 5, 6, 7, 8];
        private static readonly byte[] ChangedPackage = [(byte)'M', (byte)'Z', 91, 92, 93, 94];
        internal readonly List<(byte[] Bytes, string Hash)> Scheduled = [];
        internal string? ChangeDuringIdentity;
        internal string? ChangeDuringScheduling;
        internal bool CancelAfterIdentity;
        internal int MutationAttempts;
        internal bool MutationSucceeded;
        internal int IdentityReads;
        internal CancellationToken Token => deadline.Token;
        internal JsonFileStore Store { get; }
        internal SelfUpdateService Service { get; }
        internal string PreparedPath => Store.ResolvePath("updates/AdaPanelSetup.exe");
        internal string PendingPath => Store.ResolvePath("updates/pending-update.json");
        internal string AttemptPath => Store.ResolvePath("updates/last-attempt.json");

        private Fixture()
        {
            Directory.CreateDirectory(root);
            Store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            Service = new SelfUpdateService(Store, new Factory(this), new Launcher(this), ReadIdentity);
        }

        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f.PreparedPath)!);
                await File.WriteAllBytesAsync(f.PreparedPath, OldPackage, f.Token);
                await f.Store.WriteAsync("updates/pending-update.json", new
                {
                    version = "99.0.1", packagePath = f.PreparedPath, packageSha256 = Hash(OldPackage),
                    stagedAtUtc = DateTime.UtcNow
                }, f.Token);
                await f.Store.WriteAsync("updates/last-attempt.json", new
                {
                    phase = "failed", error = "Previous attempt remains useful until a new schedule starts.",
                    installerExitCode = 5, updatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
                }, f.Token);
                await f.Store.WriteAsync("config/update-manifest.json", new
                {
                    latestVersion = "99.0.2", downloadUrl = DownloadUrl,
                    packageSha256 = Hash(NewPackage), fetchedAtUtc = DateTime.UtcNow
                }, f.Token);
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        private const string DownloadUrl = "https://github.com/aliorala/adapanel/releases/download/v99.0.2/AdaPanelSetup.exe";
        internal InstallUpdateRequest Confirmation() => new()
        {
            ExpectedVersion = "99.0.1", ExpectedPackageSha256 = Hash(OldPackage)
        };
        internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private async Task<UpdatePackageIdentity> ReadIdentity(string path, CancellationToken token)
        {
            ValidateOwnedPath(path);
            token.ThrowIfCancellationRequested();
            // The real reader opens the path again for VersionInfo/Authenticode.
            // This fake measures the same read-sharing contract without native tools.
            using (var input = File.OpenRead(path)) Assert.Equal((int)'M', input.ReadByte());
            IdentityReads++;
            await Task.Yield();
            if (ChangeDuringIdentity is { } change) TryChange(path, change);
            if (CancelAfterIdentity) deadline.Cancel();
            return new UpdatePackageIdentity("AdaPanel", "AdaPanel", "NotSigned", string.Empty);
        }

        private void TryChange(string path, string change)
        {
            ValidateOwnedPath(path);
            MutationAttempts++;
            try
            {
                if (change == "replace")
                {
                    File.Move(path, path + ".previous");
                    File.WriteAllBytes(path, ChangedPackage);
                }
                else File.WriteAllBytes(path, change == "truncate" ? [(byte)'M', (byte)'Z'] : ChangedPackage);
                MutationSucceeded = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An immutable open-file snapshot may prevent the competing writer.
                // Result assertions below still verify the bytes and approved checksum.
            }
        }

        internal void AssertReleased(string path)
        {
            ValidateOwnedPath(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(input.CanWrite);
        }
        internal void AssertNoUnpublishedCandidates(string? published)
        {
            var candidates = Directory.GetFiles(Store.ResolvePath("updates"), "AdaPanelUpdate-*.exe");
            if (published is null) Assert.Empty(candidates);
            else Assert.Equal(published, Assert.Single(candidates));
        }
        private void ValidateOwnedPath(string path)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture file operation escaped its owned root.");
        }

        public void Dispose()
        {
            foreach (var client in clients) client.Dispose();
            deadline.Dispose();
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(full);
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Cleanup must remain inside the unique update fixture root.");
            var pending = new Stack<string>(); pending.Push(full);
            while (pending.TryPop(out var entry))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Cleanup must not follow links.");
                if (!attributes.HasFlag(FileAttributes.Directory)) continue;
                foreach (var child in Directory.GetFileSystemEntries(entry))
                {
                    ValidateOwnedPath(child);
                    pending.Push(child);
                }
            }
            Directory.Delete(full, recursive: true);
        }

        private sealed class Launcher(Fixture fixture) : IUpdateInstallerLauncher
        {
            public async Task ScheduleAsync(string packagePath, string packageSha256, CancellationToken cancellationToken = default)
            {
                fixture.ValidateOwnedPath(packagePath);
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                if (fixture.ChangeDuringScheduling is { } change) fixture.TryChange(packagePath, change);
                fixture.Scheduled.Add((await File.ReadAllBytesAsync(packagePath, cancellationToken), packageSha256));
            }
        }
        private sealed class Factory(Fixture fixture) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                var client = new HttpClient(new Handler());
                fixture.clients.Add(client);
                return client;
            }
        }
        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/aliorala/adapanel/releases/latest")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            tag_name = "v99.0.2", body = "Fixture verified release",
                            assets = new[] { new { name = "AdaPanelSetup.exe", browser_download_url = DownloadUrl, digest = "sha256:" + Hash(NewPackage) } }
                        }))
                    });
                Assert.Equal(DownloadUrl, request.RequestUri.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(NewPackage) });
            }
        }
    }
}
