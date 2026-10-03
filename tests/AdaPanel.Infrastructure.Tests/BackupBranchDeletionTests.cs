using System.IO.Compression;
using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupBranchDeletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingOneBranchPreservesSiblingArchivesRecordsAndRemoteCopies(bool remote)
    {
        using var f = new Fixture(remote);
        var records = f.Branches();
        await f.Save(records);
        var siblingBytes = File.ReadAllBytes(records[2].Path);
        var result = await f.Service.DeleteBackupChainAsync("a1", f.Token);
        Assert.Equal(new[] { "a1", "a2" }, result.DeletedIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(new[] { "b1", "b2", "full" }, (await f.Service.GetBackupsAsync(f.Token)).Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(siblingBytes, File.ReadAllBytes(records[2].Path));
        Assert.True(File.Exists(records[4].Path));
        Assert.DoesNotContain(f.Cloud.Istekler, request => request.Url.Contains("/b1-") || request.Url.Contains("/b2-"));
        Assert.Equal(remote ? 2 : 0, f.Cloud.Istekler.Count);
        var remaining = await f.Service.GetChainAsync("b2", f.Token);
        Assert.False(remaining.Broken);
        Assert.Equal(new[] { "full", "b1", "b2" }, remaining.Links.Select(item => item.BackupId));
    }

    [Fact]
    public async Task ALeafCanBeDeletedAloneDespiteALaterSiblingAtTheSameDepth()
    {
        using var f = new Fixture(true);
        var records = f.Branches();
        await f.Save(records);
        await f.Service.DeleteBackupAsync("a2", f.Token);
        Assert.False(File.Exists(records[3].Path));
        Assert.True(File.Exists(records[4].Path));
        Assert.Equal(4, (await f.Service.GetBackupsAsync(f.Token)).Count);
        Assert.Contains("/a2-", Assert.Single(f.Cloud.Istekler).Url);
    }

    [Theory]
    [InlineData("a2", "a1")]
    [InlineData("b2", "b1")]
    public async Task ChainResolutionFollowsOnlyTheSelectedAncestors(string target, string parent)
    {
        using var f = new Fixture();
        await f.Save(f.Branches());
        var chain = await f.Service.GetChainAsync(target, f.Token);
        Assert.False(chain.Broken);
        Assert.Equal(new[] { "full", parent, target }, chain.Links.Select(item => item.BackupId));
        Assert.All(chain.Links, item => Assert.True(item.Available));
    }

    [Fact]
    public async Task LegacyLinearRecordsWithNoParentIdsRemainUsableAndDeletable()
    {
        using var f = new Fixture();
        var records = new[] { f.Record("full", 0), f.Record("one", 1), f.Record("two", 2) };
        await f.Save(records);
        var chain = await f.Service.GetChainAsync("two", f.Token);
        Assert.False(chain.Broken);
        Assert.Equal(new[] { "full", "one", "two" }, chain.Links.Select(item => item.BackupId));
        var result = await f.Service.DeleteBackupChainAsync("one", f.Token);
        Assert.Equal(new[] { "one", "two" }, result.DeletedIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.True(File.Exists(records[0].Path));
    }

    [Theory]
    [InlineData("legacy-ambiguous", false)]
    [InlineData("legacy-ambiguous", true)]
    [InlineData("duplicate-id", false)]
    [InlineData("duplicate-id", true)]
    [InlineData("cycle", false)]
    [InlineData("cycle", true)]
    [InlineData("missing-parent", false)]
    [InlineData("missing-parent", true)]
    [InlineData("cross-source-parent", false)]
    [InlineData("cross-source-parent", true)]
    [InlineData("negative-index", false)]
    [InlineData("negative-index", true)]
    public async Task InconsistentDependencyMetadataCannotDeleteAnyLocalOrRemoteCopy(string fault, bool chainDelete)
    {
        using var f = new Fixture(true);
        var first = f.Record("full", 0);
        var selected = f.Record("selected", 1, "full");
        var records = new List<BackupRecordDto> { first, selected };
        switch (fault)
        {
            case "legacy-ambiguous": records.Add(f.Record("sibling", 1, "full")); records.Add(f.Record("unknown-parent", 2)); break;
            case "duplicate-id": records.Add(f.Record("selected", 1, "full")); break;
            case "cycle": first.BaseBackupId = selected.Id; break;
            case "missing-parent": selected.BaseBackupId = "absent"; break;
            case "cross-source-parent": first.Source = "other.example"; break;
            case "negative-index": selected.ChainIndex = -1; selected.BaseBackupId = string.Empty; break;
        }
        await f.Save(records.ToArray());
        var before = await f.IndexBytes();
        var error = await Assert.ThrowsAsync<AppException>(() => chainDelete
            ? f.Service.DeleteBackupChainAsync(selected.Id, f.Token)
            : f.Service.DeleteBackupAsync(selected.Id, f.Token));
        Assert.Equal("backup.chain_ambiguous", error.MessageKey);
        Assert.Empty(f.Cloud.Istekler);
        Assert.All(records, item => Assert.True(File.Exists(item.Path), item.Name));
        Assert.Equal(before, await f.IndexBytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateIdsAcrossSourcesCannotSelectOneArchiveAndRemoveBothRecords(bool chainDelete)
    {
        using var f = new Fixture(true);
        var selected = f.Record("duplicate", 0);
        var foreign = f.Record("duplicate", 0, source: "other.example");
        await f.Save(selected, foreign);
        await Assert.ThrowsAsync<AppException>(() => chainDelete
            ? f.Service.DeleteBackupChainAsync(selected.Id, f.Token)
            : f.Service.DeleteBackupAsync(selected.Id, f.Token));
        Assert.Empty(f.Cloud.Istekler);
        Assert.True(File.Exists(selected.Path));
        Assert.True(File.Exists(foreign.Path));
        Assert.Equal(2, (await f.Service.GetBackupsAsync(f.Token)).Count);
        Assert.True((await f.Service.GetChainAsync(selected.Id, f.Token)).Broken);
    }

    [Theory]
    [InlineData("postgres:Foo", "postgresql:Foo", "postgresql:foo")]
    [InlineData("mysql:Orders", "mysql:orders", "mysql:other")]
    public async Task DeletionUsesCanonicalSourceIdentityWithoutCrossingAnotherSource(string baseSource, string ownSource, string foreignSource)
    {
        using var f = new Fixture(true);
        var first = f.Record("full", 0, source: baseSource, type: "database");
        var own = f.Record("own", 1, "full", ownSource, "database");
        var foreign = f.Record("foreign", 1, source: foreignSource, type: "database");
        await f.Save(first, own, foreign);
        var result = await f.Service.DeleteBackupChainAsync("full", f.Token);
        Assert.Equal(new[] { "full", "own" }, result.DeletedIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.True(File.Exists(foreign.Path));
        Assert.DoesNotContain(f.Cloud.Istekler, request => request.Url.Contains("/foreign-"));
    }

    [Theory]
    [InlineData("missing-confirmation")]
    [InlineData("empty-confirmation")]
    [InlineData("new-descendant")]
    [InlineData("removed-descendant")]
    [InlineData("different-case")]
    [InlineData("duplicate-confirmation")]
    [InlineData("unrelated-id")]
    public async Task AChangedOrMissingConfirmationCannotDeleteAnyArchive(string mismatch)
    {
        using var f = new Fixture(true);
        var records = new List<BackupRecordDto> { f.Record("full", 0), f.Record("one", 1, "full") };
        string[]? expected = ["full", "one"];
        switch (mismatch)
        {
            case "missing-confirmation": expected = null; break;
            case "empty-confirmation": expected = []; break;
            case "new-descendant": records.Add(f.Record("two", 2, "one")); break;
            case "removed-descendant": expected = ["full", "one", "removed"]; break;
            case "different-case": expected = ["FULL", "one"]; break;
            case "duplicate-confirmation": expected = ["full", "one", "one"]; break;
            case "unrelated-id": expected = ["full", "one", "unrelated"]; break;
        }
        await f.Save(records.ToArray());
        var before = await f.IndexBytes();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.DeleteBackupChainAsync("full", expected, f.Token));
        Assert.Equal("backup.chain_changed", error.MessageKey);
        Assert.Empty(f.Cloud.Istekler);
        Assert.All(records, item => Assert.True(File.Exists(item.Path)));
        Assert.Equal(before, await f.IndexBytes());
    }

    [Fact]
    public async Task AnExactConfirmationMayListTheIdsInAnyOrder()
    {
        using var f = new Fixture(true);
        await f.Save(f.Record("full", 0), f.Record("one", 1, "full"));
        var result = await f.Service.DeleteBackupChainAsync("full", ["one", "full"], f.Token);
        Assert.Equal(new[] { "full", "one" }, result.DeletedIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(2, f.Cloud.Istekler.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedChildKeepsItsAncestorsAndTheOtherBranch(bool remoteFailure)
    {
        using var f = new Fixture(remoteFailure);
        var records = f.Branches();
        await f.Save(records);
        using var locked = remoteFailure ? null : new FileStream(records[3].Path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (remoteFailure) f.Cloud.Yanit("DELETE", "/a2-", HttpStatusCode.Forbidden);
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.DeleteBackupChainAsync("a1", ["a1", "a2"], f.Token));
        Assert.Equal("backup.delete_failed", error.MessageKey);
        Assert.True(File.Exists(records[0].Path));
        Assert.True(File.Exists(records[1].Path));
        Assert.True(File.Exists(records[2].Path));
        Assert.True(File.Exists(records[4].Path));
        Assert.Equal(5, (await f.Service.GetBackupsAsync(f.Token)).Count);
        Assert.DoesNotContain(f.Cloud.Istekler, item => item.Url.Contains("/a1-") || item.Url.Contains("/full-"));
        Assert.False((await f.Service.GetChainAsync("b2", f.Token)).Broken);
        if (!remoteFailure) Assert.False((await f.Service.GetChainAsync("a1", f.Token)).Broken);
    }

    [Fact]
    public async Task APartlyDeletedBranchKeepsTheFailedLinkAndAllOfItsAncestors()
    {
        using var f = new Fixture();
        var full = f.Record("full", 0);
        var one = f.Record("one", 1, "full");
        var two = f.Record("two", 2, "one");
        await f.Save(full, one, two);
        using var locked = new FileStream(one.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await f.Service.DeleteBackupChainAsync("full", ["full", "one", "two"], f.Token);
        Assert.Equal(["two"], result.DeletedIds);
        Assert.True(File.Exists(full.Path));
        Assert.True(File.Exists(one.Path));
        Assert.False(File.Exists(two.Path));
        Assert.Equal(["full", "one"], (await f.Service.GetBackupsAsync(f.Token)).Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task PruningALockedChildPreservesItsBaseAndMetadata()
    {
        using var f = new Fixture();
        var full = f.Record("full", 0);
        var child = f.Record("child", 1, "full");
        full.CreatedAtUtc = child.CreatedAtUtc = DateTime.UtcNow.AddDays(-30);
        var recent = f.Record("recent", 0);
        recent.ChainId = "replacement";
        recent.CreatedAtUtc = DateTime.UtcNow;
        await f.Save(full, child, recent);
        using var locked = new FileStream(child.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await f.Service.PruneAsync(14, cancellationToken: f.Token);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.LocalDeleteFailed);
        Assert.All(new[] { full, child, recent }, item => Assert.True(File.Exists(item.Path)));
        Assert.Equal(3, (await f.Service.GetBackupsAsync(f.Token)).Count);
    }

    private sealed class Fixture(bool remote = false) : IDisposable
    {
        private readonly BulutAlani area = new();
        private int sequence;
        public readonly SahteBulut Cloud = new() { VarsayilanDurum = HttpStatusCode.NoContent };
        public CancellationToken Token => area.Iptal;
        public BackupService Service => area.ServisKur(Cloud);
        public BackupRecordDto[] Branches() => [Record("full", 0), Record("a1", 1, "full"), Record("b1", 1, "full"), Record("a2", 2, "a1"), Record("b2", 2, "b1")];
        public BackupRecordDto Record(string id, int index, string parent = "", string source = "shop.example", string type = "site")
        {
            var name = $"{id}-{sequence++}.zip";
            var path = Path.Combine(area.DosyaDeposu.EnsureDirectory("backups"), name);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("files/payload.txt").Open())) writer.Write("Archive " + name);
            return new BackupRecordDto
            {
                Id = id, Name = name, Path = path, Type = type, Source = source, Format = "zip", Status = "created",
                ChainId = "chain", ChainIndex = index, BackupMode = index == 0 ? "full" : "incremental", BaseBackupId = parent,
                CreatedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(sequence),
                Size = new FileInfo(path).Length, TargetType = remote ? "webdav" : "local",
                RemoteUri = remote ? "https://backup.example.test/archives/" + name : string.Empty
            };
        }
        public async Task Save(params BackupRecordDto[] records)
        {
            if (remote) await area.UzakHedefiYazAsync("webdav", "https://backup.example.test/archives", "fixture", SahteKoruyucu.Onek + "fixture-password");
            await area.KayitlariYazAsync(records);
        }
        public Task<byte[]> IndexBytes() => File.ReadAllBytesAsync(area.DosyaDeposu.ResolvePath("config/backups.json"), Token);
        public void Dispose() => area.Dispose();
    }
}
