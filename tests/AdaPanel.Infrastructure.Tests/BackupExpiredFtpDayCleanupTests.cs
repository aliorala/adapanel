using System.Net;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupExpiredFtpDayCleanupTests
{
    private static readonly Uri Root = new("ftp://backup.example/backups/");
    private static readonly NetworkCredential Credential = new("test", "test");

    [Fact]
    public async Task Service_prunes_unindexed_expired_day_with_contents_on_calendar_boundary()
    {
        using var area = new BulutAlani();
        var ftp = new FakeFtp();
        var day = DateTime.Today.AddDays(-5).ToString(BackupLayout.DayFormat, System.Globalization.CultureInfo.InvariantCulture);
        ftp.AddFile(day + "/databases/shop/unindexed.sql.zst.enc");
        var service = area.ServisKur(new SahteBulut(), ftp: ftp);
        var result = await service.PruneAsync(5, new BackupPruneRemoteOptions
            { RemoteUrl = Root.AbsoluteUri, UserName = "test", Password = "test" }, area.Iptal);
        Assert.False(result.HasFailures);
        Assert.Empty(ftp.Files);
        Assert.DoesNotContain("/backups/" + day + "/", ftp.Directories);
        Assert.Contains("ftp_expired_day_folders_removed=1", result.RetentionNotes);
    }

    [Fact]
    public async Task Service_reports_unindexed_expired_day_delete_failure()
    {
        using var area = new BulutAlani();
        var ftp = new FakeFtp { FailDelete = true };
        var day = DateTime.Today.AddDays(-5).ToString(BackupLayout.DayFormat, System.Globalization.CultureInfo.InvariantCulture);
        ftp.AddFile(day + "/panel/unindexed.zip");
        var result = await area.ServisKur(new SahteBulut(), ftp: ftp).PruneAsync(5, new BackupPruneRemoteOptions
            { RemoteUrl = Root.AbsoluteUri, UserName = "test", Password = "test" }, area.Iptal);
        Assert.True(result.HasFailures);
        Assert.Equal(1, result.RemoteDeleteFailed);
        Assert.Contains(result.Failures, failure => failure.Contains("FTP DELE"));
        Assert.Single(ftp.Files);
    }

    [Fact]
    public async Task Cutoff_day_is_deleted_with_all_known_layout_contents_but_newer_and_sibling_data_survive()
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/sites/shop/site.zip");
        ftp.AddFile("2026-10-27/sites/shop/databases/db/db.zip");
        ftp.AddFile("2026-10-27/panel/config.zip");
        ftp.AddFile("2026-10-28/panel/keep.zip");
        ftp.AddFile("other/panel/keep.zip");
        ftp.AddFile("loose.zip");
        var gates = 0;

        var result = await Sweep(ftp, beforeMutation: _ => { gates++; return Task.CompletedTask; });

        Assert.Equal(1, result.RemovedDays);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain("/backups/2026-10-27/", ftp.Directories);
        Assert.Contains("/backups/2026-10-28/panel/keep.zip", ftp.Files);
        Assert.Contains("/backups/other/panel/keep.zip", ftp.Files);
        Assert.Contains("/backups/loose.zip", ftp.Files);
        Assert.Contains("/backups/", ftp.Directories);
        Assert.Equal(ftp.Mutations.Count, gates);
    }

    [Fact]
    public async Task One_protected_archive_skips_the_entire_expired_day()
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/panel/keep.zip");
        ftp.AddFile("2026-10-27/panel/other.zip");
        var result = await Sweep(ftp, [new Uri(Root, "2026-10-27/panel/keep.zip")]);
        Assert.Equal(0, result.RemovedDays);
        Assert.Empty(result.Failures);
        Assert.Empty(ftp.Mutations);
    }

    [Theory]
    [InlineData("ftp://BACKUP.example/BACKUPS/2026-10-27/PANEL/keep.zip")]
    [InlineData("ftp://backup.example/backups/2026-10-27")]
    public async Task Protected_paths_use_conservative_case_and_day_boundary_comparison(string protectedUri)
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/panel/file.zip");
        var result = await Sweep(ftp, [new Uri(protectedUri)]);
        Assert.Equal(0, result.RemovedDays);
        Assert.Empty(ftp.Mutations);
    }

    [Theory]
    [InlineData("file.sql")]
    [InlineData("file.bak")]
    [InlineData("file.sql.zst")]
    [InlineData("file.sql.gz")]
    [InlineData("file.sql.zst.enc")]
    [InlineData("file.zip.enc")]
    public async Task Database_dumps_compressed_and_encrypted_archives_are_removed(string name)
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/databases/shop/" + name);
        var result = await Sweep(ftp);
        Assert.Equal(1, result.RemovedDays);
        Assert.Empty(result.Failures);
        Assert.Empty(ftp.Files);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("%2e%2e%2foutside")]
    [InlineData("a\\outside")]
    [InlineData("%252foutside")]
    public async Task Unsafe_entries_abort_preflight_before_any_mutation(string entry)
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/panel/normal.zip");
        ftp.ExtraEntries["/backups/2026-10-27/"] = [entry];
        var result = await Sweep(ftp);
        Assert.Equal(0, result.RemovedDays);
        Assert.NotEmpty(result.Failures);
        Assert.Empty(ftp.Mutations);
    }

    [Fact]
    public async Task Unknown_tree_is_never_followed_even_if_it_might_be_a_symlink()
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/panel/normal.zip");
        ftp.AddFile("2026-10-27/sentinel/precious.zip");
        var result = await Sweep(ftp);
        Assert.Equal(0, result.RemovedDays);
        Assert.NotEmpty(result.Failures);
        Assert.DoesNotContain("/backups/2026-10-27/sentinel/", ftp.Listed);
        Assert.Empty(ftp.Mutations);
    }

    [Fact]
    public async Task Nonempty_listing_550_requires_rmd_proof_and_failed_rmd_is_reported()
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/panel/file.zip");
        ftp.UnavailableDirectory = "/backups/2026-10-27/panel/";
        var result = await Sweep(ftp);
        Assert.Equal(0, result.RemovedDays);
        Assert.Contains(result.Failures, x => x.Contains("550"));
        Assert.Equal("RMD /backups/2026-10-27/panel/", Assert.Single(ftp.Mutations));
        Assert.Contains("/backups/2026-10-27/panel/file.zip", ftp.Files);
        Assert.Contains("/backups/2026-10-27/", ftp.Directories);
    }

    [Fact]
    public async Task Empty_listing_550_is_proved_by_rmd_and_expired_day_is_removed_with_its_files()
    {
        var ftp = new FakeFtp { EmptyListing550 = true };
        ftp.AddFile("2026-10-27/panel/first.zip");
        ftp.AddFile("2026-10-27/panel/second.zip.enc");
        // Exercise both a 550 during preflight and 550 after deleting known archives.
        ftp.Directories.Add("/backups/2026-10-27/mail/");
        var gates = 0;
        var result = await Sweep(ftp, beforeMutation: _ => { gates++; return Task.CompletedTask; });
        Assert.Equal(1, result.RemovedDays);
        Assert.Empty(result.Failures);
        Assert.Empty(ftp.Files);
        Assert.Equal("/backups/", Assert.Single(ftp.Directories));
        Assert.Equal(ftp.Mutations.Count, gates);
        Assert.DoesNotContain("RMD /backups/", ftp.Mutations);
    }

    [Fact]
    public async Task Root_listing_550_never_grants_permission_to_remove_configured_root()
    {
        var ftp = new FakeFtp { EmptyListing550 = true };
        var result = await Sweep(ftp);
        Assert.Equal(0, result.RemovedDays);
        Assert.Contains(result.Failures, failure => failure.Contains("FTP NLST /backups/: 550"));
        Assert.Empty(ftp.Mutations);
        Assert.Contains("/backups/", ftp.Directories);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_delete_or_rmd_is_reported_and_day_is_not_counted(bool failRemove)
    {
        var ftp = new FakeFtp { FailDelete = !failRemove, FailRemove = failRemove };
        ftp.AddFile("2026-10-27/panel/file.zip");
        var result = await Sweep(ftp);
        Assert.Equal(0, result.RemovedDays);
        Assert.NotEmpty(result.Failures);
        Assert.Contains("/backups/2026-10-27/", ftp.Directories);
    }

    [Fact]
    public async Task Zip_named_directory_is_not_recursively_followed_after_dele_fails()
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/panel/sentinel.zip/precious.zip");
        var result = await Sweep(ftp);
        Assert.Equal(0, result.RemovedDays);
        Assert.NotEmpty(result.Failures);
        Assert.DoesNotContain("/backups/2026-10-27/panel/sentinel.zip/", ftp.Listed);
        Assert.Contains("/backups/2026-10-27/panel/sentinel.zip/precious.zip", ftp.Files);
    }

    [Fact]
    public async Task Cancellation_in_mutation_gate_prevents_mutation()
    {
        var ftp = new FakeFtp();
        ftp.AddFile("2026-10-27/panel/file.zip");
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sweep(ftp,
            beforeMutation: _ => { cts.Cancel(); return Task.CompletedTask; }, ct: cts.Token));
        Assert.Empty(ftp.Mutations);
    }

    [Fact]
    public async Task Full_path_listing_entries_stay_beneath_configured_root()
    {
        var ftp = new FakeFtp { ReturnFullPaths = true };
        ftp.AddFile("2026-10-27/panel/file.zip");
        var result = await Sweep(ftp);
        Assert.Equal(1, result.RemovedDays);
        Assert.Empty(result.Failures);
        Assert.All(ftp.Mutations, command => Assert.Contains("/backups/2026-10-27/", command));
    }

    [Fact]
    public async Task Excessive_listing_is_bounded_before_any_mutation()
    {
        var ftp = new FakeFtp();
        ftp.ExtraEntries["/backups/"] = Enumerable.Range(0, 10001).Select(i => $"entry-{i}").ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sweep(ftp));
        Assert.Empty(ftp.Mutations);
    }

    private static Task<BackupExpiredFtpDayCleanup.Report> Sweep(FakeFtp ftp,
        IReadOnlyCollection<Uri>? protectedUris = null, Func<CancellationToken, Task>? beforeMutation = null,
        CancellationToken ct = default) => BackupExpiredFtpDayCleanup.SweepAsync(ftp, Root, Credential,
            new DateTime(2026, 10, 27, 23, 59, 59), protectedUris ?? [], beforeMutation, ct);

    private sealed class FakeFtp : IBackupFtpCommands
    {
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal) { "/backups/" };
        public Dictionary<string, string[]> ExtraEntries { get; } = new();
        public List<string> Mutations { get; } = [];
        public List<string> Listed { get; } = [];
        public string? UnavailableDirectory { get; set; }
        public bool FailDelete { get; init; }
        public bool FailRemove { get; init; }
        public bool ReturnFullPaths { get; init; }
        public bool EmptyListing550 { get; init; }

        public void AddFile(string relative)
        {
            var path = "/backups/" + relative;
            Files.Add(path);
            var directory = path[..(path.LastIndexOf('/') + 1)];
            while (directory.Length >= "/backups/".Length)
            {
                Directories.Add(directory);
                directory = directory.TrimEnd('/');
                directory = directory[..(directory.LastIndexOf('/') + 1)];
            }
        }

        public Task<FtpListingResult> ListDirectoryAsync(Uri uri, NetworkCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            Listed.Add(path);
            if (UnavailableDirectory == path || !Directories.Contains(path))
                return Task.FromResult(new FtpListingResult(false, [], 550, "Unavailable"));
            var names = Files.Concat(Directories).Where(p => p.StartsWith(path, StringComparison.Ordinal) && p != path)
                .Select(p => p[path.Length..].Split('/')[0]).Distinct().ToList();
            if (ExtraEntries.TryGetValue(path, out var extra)) names.AddRange(extra);
            if (EmptyListing550 && names.Count == 0)
                return Task.FromResult(new FtpListingResult(false, [], 550, "No files found"));
            if (ReturnFullPaths) names = names.Select(name => path + name).ToList();
            return Task.FromResult(new FtpListingResult(true, names, 226, "OK"));
        }

        public Task<FtpCommandResult> DeleteFileAsync(Uri uri, NetworkCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Mutations.Add("DELE " + uri.AbsolutePath);
            return Task.FromResult(!FailDelete && Files.Remove(Uri.UnescapeDataString(uri.AbsolutePath))
                ? FtpCommandResult.Ok(250, "Deleted") : FtpCommandResult.Fail(550, "Unavailable"));
        }

        public Task<FtpCommandResult> RemoveDirectoryAsync(Uri uri, NetworkCredential credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            Mutations.Add("RMD " + path);
            var nonempty = Files.Concat(Directories).Any(p => p != path && p.StartsWith(path, StringComparison.Ordinal));
            return Task.FromResult(!FailRemove && !nonempty && Directories.Remove(path)
                ? FtpCommandResult.Ok(250, "Removed") : FtpCommandResult.Fail(550, "Unavailable"));
        }
    }
}
