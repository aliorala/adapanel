using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Generated platform-shape fixtures based on the cPanel tarball documentation; not vendor-produced backups.</summary>
public sealed class CpanelBackupNormalizerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Documented_account_shape_roundtrips_sites_databases_maildir_dns_and_tasks(bool wrapped)
    {
        using var fixture = new Fixture();
        using var source = MakeArchive(Shape(), wrapped: wrapped);
        var original = source.ToArray();
        var package = await fixture.Normalizer.NormalizeAsync(source);
        Assert.Equal(original, source.ToArray());
        var manifest = await fixture.Store.ReadManifestAsync(package.Id);
        Assert.Equal(PortableMigrationProvider.Cpanel, manifest.Provider);
        Assert.Equal(2, manifest.Sites.Count);
        Assert.Equal(2, manifest.Databases.Count);
        Assert.Equal(2, manifest.MailDomains.Count);
        Assert.Equal(2, manifest.DnsZones.Count);
        Assert.Equal(2, manifest.Tasks.Count);
        Assert.False(manifest.Complete); // Explicit new credentials and unresolved DB ownership.
        Assert.DoesNotContain(manifest.Issues, i => i.Code.Contains("layout_version", StringComparison.Ordinal));
        var addon = Assert.Single(manifest.Sites, s => s.Name == "addon.test");
        Assert.Equal("/home/alice/apps/addon", addon.SourcePhysicalPath);
        Assert.Equal(1, addon.FileCount);
        var main = Assert.Single(manifest.Sites, s => s.Name == "main.test");
        Assert.Equal("/home/alice/www main", main.SourcePhysicalPath);
        Assert.Equal(2, main.FileCount);
        foreach (var db in manifest.Databases)
        {
            Assert.Empty(db.SiteIds);
            Assert.Contains(manifest.Issues, i => i.ResourceId == db.Id && i.Code == "cpanel.database.site_mapping_required");
            await using var dump = await fixture.Store.OpenPayloadAsync(package.Id, db.DumpPath);
            Assert.True(dump.Length > 0);
        }
        var mail = Assert.Single(manifest.MailDomains, m => m.Name == "main.test");
        Assert.Equal(new[] { main.Id }, mail.SiteIds);
        var alice = Assert.Single(mail.Accounts, m => m.Email == "alice@main.test");
        Assert.Equal(2, alice.EmailCount);
        Assert.Equal("maildir-zip", alice.ArchiveFormat);
        await using (var payload = await fixture.Store.OpenPayloadAsync(package.Id, alice.ArchivePath))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "cur/17123.M1:2,S");
            Assert.Contains(zip.Entries, e => e.FullName == ".Sent/cur/17124.M2:2,S");
            Assert.Contains(zip.Entries, e => e.FullName == "dovecot-uidlist");
        }
        var bob = Assert.Single(mail.Accounts, m => m.Email == "bob@main.test");
        Assert.Equal(0, bob.EmailCount);
        Assert.NotEmpty(bob.ArchivePath); // Empty but proven Maildir is still real archive coverage.
        Assert.Single(mail.Aliases);
        var dns = Assert.Single(manifest.DnsZones, z => z.Name == "main.test");
        var txt = Assert.Single(dns.Records, r => r.Type == "TXT");
        Assert.Equal("v=DKIM1; p=ABC DEF", txt.Value);
        Assert.Equal(3600, txt.Ttl);
        Assert.Contains(dns.Records, r => r.Type == "SOA" && r.Value.Contains("ns.main.test.", StringComparison.Ordinal));
        Assert.False(Assert.Single(manifest.Tasks, t => t.Kind == "php").ScheduleUnknown);
        Assert.Contains(manifest.Tasks, t => t.Kind == "php" && t.ScheduleType == "daily" && t.SiteIds.Contains(main.Id));
        Assert.Contains(manifest.Tasks, t => t.Kind == "url" && t.ScheduleType == "cron" && t.ScheduleUnknown && t.ScheduleValue == "*/5 * * * *" && t.SiteIds.Contains(addon.Id));
        var serialized = System.Text.Json.JsonSerializer.Serialize(manifest);
        Assert.DoesNotContain("do-not-copy", serialized);
        Assert.DoesNotContain("$6$", serialized);
        Assert.DoesNotContain(manifest.Payloads, p => p.Path.Contains("passwd", StringComparison.Ordinal) || p.Path.Contains("shadow", StringComparison.Ordinal));
        fixture.AssertSourceStagingClean();
    }

    [Fact]
    public async Task An_explicit_empty_document_root_is_not_a_missing_or_guessed_public_html()
    {
        using var f = new Fixture();
        var shape = Shape(); shape.Files.Remove("homedir/apps/addon/index.html");
        shape.Directories.Add("homedir/apps/addon/cache");
        using var source = MakeArchive(shape);
        var manifest = (await f.Normalizer.NormalizeAsync(source)).Manifest;
        var addon = Assert.Single(manifest.Sites, s => s.Name == "addon.test");
        Assert.Equal(0, addon.FileCount);
        Assert.Contains("cache", addon.EmptyDirectories);
        Assert.DoesNotContain(manifest.Issues, i => i.Code == "cpanel.site.content_missing" && i.ResourceId == addon.Id);
    }

    [Fact]
    public async Task Missing_dump_and_maildir_are_visible_and_metadata_never_counts_as_mail_payload()
    {
        using var f = new Fixture();
        var shape = Shape(); shape.Files.Remove("mysql/alice_addon.sql");
        shape.Directories.Remove("homedir/mail/main.test/alice/cur");
        foreach (var key in shape.Files.Keys.Where(k => k.StartsWith("homedir/mail/main.test/alice/cur/", StringComparison.Ordinal)).ToArray()) shape.Files.Remove(key);
        using var source = MakeArchive(shape);
        var manifest = (await f.Normalizer.NormalizeAsync(source)).Manifest;
        Assert.False(manifest.Complete);
        Assert.Contains(manifest.Issues, i => i.Code == "cpanel.database.dump_missing" && i.ResourceId.Contains("alice_addon", StringComparison.Ordinal));
        Assert.Contains(manifest.Issues, i => i.Code == "cpanel.mail.archive_missing" && i.ResourceId == "alice@main.test");
        var account = manifest.MailDomains.Single(m => m.Name == "main.test").Accounts.Single(m => m.Email == "alice@main.test");
        Assert.Null(account.EmailCount); Assert.Empty(account.ArchivePath);
    }

    [Fact]
    public async Task Missing_userdata_never_uses_a_public_html_fallback()
    {
        using var f = new Fixture(); var shape = Shape();
        shape.Files.Remove("userdata/addon.test");
        shape.Files["homedir/public_html/index.html"] = "unproven fallback";
        using var source = MakeArchive(shape);
        var manifest = (await f.Normalizer.NormalizeAsync(source)).Manifest;
        Assert.DoesNotContain(manifest.Sites, s => s.Name == "addon.test");
        Assert.Contains(manifest.Issues, i => i.ResourceId == "site:addon.test" && i.Code == "cpanel.site.metadata_missing");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/drive")]
    [InlineData("//server/share")]
    [InlineData("homedir\\escape")]
    public async Task Unsafe_tar_paths_are_rejected_and_only_owned_staging_is_cleaned(string path)
    {
        using var f = new Fixture(); var shape = Shape(); shape.Files[path] = "bad";
        using var source = MakeArchive(shape);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeAsync(source));
        f.AssertSourceStagingClean(); Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.Fifo)]
    [InlineData(TarEntryType.CharacterDevice)]
    public async Task Late_special_entry_rejects_whole_source_and_preserves_previous_package(TarEntryType type)
    {
        using var f = new Fixture();
        using var first = MakeArchive(Shape()); var prior = await f.Normalizer.NormalizeAsync(first);
        using var bad = MakeArchive(Shape(), finalType: type);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeAsync(bad));
        Assert.Single(Directory.GetDirectories(f.StoreRoot));
        Assert.Equal(prior.Manifest.PackageId, (await f.Store.ReadManifestAsync(prior.Id)).PackageId);
        f.AssertSourceStagingClean();
    }

    [Fact]
    public async Task Case_collisions_are_rejected_even_though_source_unix_paths_are_distinct()
    {
        using var f = new Fixture(); var shape = Shape();
        shape.Files["homedir/apps/addon/INDEX.html"] = "collision";
        using var source = MakeArchive(shape);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeAsync(source));
        f.AssertSourceStagingClean();
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("tar")]
    [InlineData("checksum")]
    public async Task Truncated_or_corrupt_archives_never_publish_partial_success(string damage)
    {
        using var f = new Fixture(); using var original = MakeArchive(Shape());
        var bytes = original.ToArray();
        if (damage == "gzip") bytes = bytes[..^5];
        else
        {
            using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var plain = new MemoryStream(); input.CopyTo(plain);
            var tar = plain.ToArray();
            if (damage == "tar") tar = tar[..^1024]; else tar[0] ^= 1;
            bytes = Gzip(tar);
        }
        using var bad = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeAsync(bad));
        f.AssertSourceStagingClean(); Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("total")]
    [InlineData("entry")]
    [InlineData("items")]
    [InlineData("ratio")]
    public async Task Source_budgets_are_enforced_before_normalized_publication(string kind)
    {
        var options = new PortableMigrationPackageOptions();
        switch (kind)
        {
            case "upload": options.MaxUploadBytes = 100; break;
            case "total": options.MaxTotalUncompressedBytes = 1024; break;
            case "entry": options.MaxPayloadBytes = 2; break;
            case "items": options.MaxItems = 10; break;
            case "ratio": options.MaxCompressionRatio = 1; break;
        }
        using var f = new Fixture(options); using var source = MakeArchive(Shape());
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeAsync(source));
        f.AssertSourceStagingClean(); Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Fact]
    public async Task Nonseekable_source_cancellation_removes_owned_tempfiles_and_leaves_input_unchanged()
    {
        using var f = new Fixture(); using var zip = MakeArchive(Shape());
        var original = zip.ToArray(); using var cts = new CancellationTokenSource();
        using var source = new CancellingStream(original, cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Normalizer.NormalizeAsync(source, cts.Token));
        Assert.Equal(original, source.ToArray());
        f.AssertSourceStagingClean(); Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Fact]
    public async Task Unsupported_records_and_cron_commands_remain_explicit_manual_work()
    {
        using var f = new Fixture(); var shape = Shape();
        shape.Files["dnszones/main.test.db"] += "unknown IN TYPE65280 \\# 2 AABB\n";
        shape.Files["cron/alice"] += "@reboot echo PASSWORD=do-not-copy\n";
        using var source = MakeArchive(shape);
        var package = await f.Normalizer.NormalizeAsync(source);
        Assert.False(package.Manifest.Complete);
        Assert.Contains(package.Manifest.Issues, i => i.Code.StartsWith("cpanel.dns.", StringComparison.Ordinal));
        var task = package.Manifest.Tasks.Last();
        Assert.True(task.ScheduleUnknown); Assert.Empty(task.Command);
        Assert.Contains(package.Manifest.Issues, i => i.Code == "cpanel.task.schedule_unknown" && i.ResourceId == task.Id);
        await using var raw = await f.Store.OpenPayloadAsync(package.Id, task.ArtifactPaths[0]);
        using var reader = new StreamReader(raw);
        Assert.Contains("PASSWORD=do-not-copy", await reader.ReadToEndAsync());
        Assert.DoesNotContain("do-not-copy", System.Text.Json.JsonSerializer.Serialize(package.Manifest));
    }

    [Fact]
    public async Task Unsafe_yaml_aliases_are_visible_issues_without_silent_site_success()
    {
        using var f = new Fixture(); var shape = Shape();
        shape.Files["userdata/addon.test"] = "servername: addon.test\nhomedir: &home /home/alice\ndocumentroot: *home\n";
        using var source = MakeArchive(shape);
        var manifest = (await f.Normalizer.NormalizeAsync(source)).Manifest;
        Assert.DoesNotContain(manifest.Sites, s => s.Name == "addon.test");
        Assert.Contains(manifest.Issues, i => i.Code == "cpanel.site.userdata_malformed" && i.ResourceId == "site:addon.test");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("cp/alice")]
    public async Task Unsupported_or_non_account_layout_never_returns_a_package(string missing)
    {
        using var f = new Fixture(); var shape = Shape(); shape.Files.Remove(missing);
        using var source = MakeArchive(shape);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeAsync(source));
        f.AssertSourceStagingClean(); Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Fact]
    public async Task Missing_site_zone_is_visible_even_when_dns_directory_itself_exists()
    {
        using var f = new Fixture(); var shape = Shape(); shape.Files.Remove("dnszones/addon.test.db");
        using var source = MakeArchive(shape);
        var manifest = (await f.Normalizer.NormalizeAsync(source)).Manifest;
        Assert.Contains(manifest.Issues, i => i.Code == "cpanel.dns.zone_missing" && i.ResourceId == "dns:addon.test");
        Assert.False(manifest.Complete);
    }

    [Fact]
    public async Task Proven_complete_coverage_has_no_permanent_fixture_or_version_blocker()
    {
        using var f = new Fixture(); var shape = Shape();
        foreach (var key in shape.Files.Keys.Where(k => k.StartsWith("mysql", StringComparison.Ordinal) || k.StartsWith("psql/", StringComparison.Ordinal)
            || k.StartsWith("homedir/mail/", StringComparison.Ordinal) || k.StartsWith("homedir/etc/", StringComparison.Ordinal) || k.StartsWith("va/", StringComparison.Ordinal)).ToArray())
            shape.Files.Remove(key);
        shape.Directories.RemoveWhere(d => d.StartsWith("homedir/mail/", StringComparison.Ordinal) || d.StartsWith("homedir/etc/", StringComparison.Ordinal));
        shape.Files["cron/alice"] = "15 2 * * * /usr/local/bin/php -q '/home/alice/www main/cron.php'\n";
        using var source = MakeArchive(shape);
        var manifest = (await f.Normalizer.NormalizeAsync(source)).Manifest;
        Assert.True(manifest.Complete);
        Assert.Empty(manifest.Issues); Assert.Empty(manifest.Databases); Assert.Empty(manifest.MailDomains);
        Assert.Equal(2, manifest.Sites.Count); Assert.Equal(2, manifest.DnsZones.Count); Assert.Single(manifest.Tasks);
    }

    [Theory]
    [InlineData("1979-12-31T23:59:59.1234567Z")]
    [InlineData("2020-06-15T12:34:57.7654321Z")]
    public async Task Shared_maildir_sidecar_preserves_source_internaldate_ticks_modes_and_flags(string date)
    {
        using var f = new Fixture(); var shape = Shape();
        shape.Files["homedir/mail/main.test/alice/cur/17123.M1:2,S"] = "Date: Tue, 01 Jan 2030 00:00:00 +0000\r\nSubject: Header is not INTERNALDATE\r\n\r\nBody";
        var timestamp = DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture);
        using var input = MakeArchive(shape, wrapped: true, messageDate: timestamp);
        var package = await f.Normalizer.NormalizeAsync(input);
        var account = package.Manifest.MailDomains.Single(m => m.Name == "main.test").Accounts.Single(a => a.Email == "alice@main.test");
        await using var payload = await f.Store.OpenPayloadAsync(package.Id, account.ArchivePath);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var sidecar = archive.GetEntry(PortableMaildirWriter.MetadataEntryName);
        Assert.NotNull(sidecar);
        await using var metadata = sidecar.Open();
        using var json = await System.Text.Json.JsonDocument.ParseAsync(metadata);
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        var rows = json.RootElement.GetProperty("files").EnumerateArray().ToArray();
        var message = Assert.Single(rows, row => row.GetProperty("path").GetString() == "cur/17123.M1:2,S");
        Assert.Equal(timestamp, message.GetProperty("sourceDateUtc").GetDateTimeOffset());
        Assert.Equal((int)(UnixFileMode.UserRead | UnixFileMode.UserWrite), message.GetProperty("unixMode").GetInt32());
        Assert.Equal("S", message.GetProperty("maildirFlags").GetString());
        Assert.True(message.GetProperty("isMessage").GetBoolean());
        var dovecot = Assert.Single(rows, row => row.GetProperty("path").GetString() == "dovecot-uidlist");
        Assert.False(dovecot.GetProperty("isMessage").GetBoolean());
        using var text = new StreamReader(archive.GetEntry("cur/17123.M1:2,S")!.Open());
        Assert.Equal(shape.Files["homedir/mail/main.test/alice/cur/17123.M1:2,S"], await text.ReadToEndAsync());
        Assert.Equal(2, account.EmailCount); // Sidecar itself never counts as a message.
        f.AssertSourceStagingClean();
    }

    [Theory]
    [InlineData("__adapanel_source_metadata__.json")]
    [InlineData("__ADAPANEL_SOURCE_METADATA__.JSON")]
    [InlineData("__adapanel_source_metadata__.json/child")]
    public async Task Reserved_maildir_sidecar_collision_is_rejected_without_overwriting_source_data(string name)
    {
        using var f = new Fixture(); var shape = Shape();
        shape.Files["homedir/mail/main.test/alice/" + name] = "original source data";
        using var input = MakeArchive(shape);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeAsync(input));
        Assert.Equal("migration.maildir.metadata_name_collision", error.Message);
        f.AssertSourceStagingClean(); Assert.Empty(Directory.GetDirectories(f.StoreRoot));
    }

    [Fact]
    public async Task Shared_maildir_producer_requires_source_dates_instead_of_inventing_them()
    {
        using var f = new Fixture();
        var message = Path.Combine(f.Root, "message"); File.WriteAllText(message, "body");
        var inventory = new SourceArchiveInventory(new Dictionary<string,string> { ["mail/cur/message:2,S"] = message }, new HashSet<string> { "mail/cur", "mail/new", "mail/tmp" });
        var output = Path.Combine(f.Root, "mail-without-date.zip");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => PortableMaildirWriter.CreateAsync(inventory, "mail", output, new()));
        Assert.Equal("migration.maildir.source_date_missing", error.Message); Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Shared_maildir_zip_output_including_sidecar_and_headers_is_bounded()
    {
        using var f = new Fixture();
        var message = Path.Combine(f.Root, "message"); File.WriteAllText(message, "body");
        var inventory = new SourceArchiveInventory(new Dictionary<string,string> { ["mail/cur/message:2,S"] = message }, new HashSet<string> { "mail/cur", "mail/new", "mail/tmp" })
        { FileMetadata = new Dictionary<string,SourceArchiveFileMetadata> { ["mail/cur/message:2,S"] = new(DateTimeOffset.UnixEpoch, 384) } };
        var output = Path.Combine(f.Root, "bounded-mail.zip");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => PortableMaildirWriter.CreateAsync(inventory, "mail", output, new() { MaxPayloadBytes = 64 }));
        Assert.Equal("migration.maildir.archive_size_limit", error.Message);
        Assert.True(new FileInfo(output).Length <= 64);
    }

    [Theory]
    [InlineData("*/5 * * * * curl -fsS https://addon.test/tick?token=do-not-copy")]
    [InlineData("*/5 * * * * curl -fsS https://addon.test/tick#do-not-copy")]
    [InlineData("15 2 * * * /usr/local/bin/php '/home/alice/www main/cron.php' --password=do-not-copy")]
    public async Task Source_cron_arguments_or_query_credentials_remain_only_in_private_artifact(string command)
    {
        using var f = new Fixture(); var shape = Shape(); shape.Files["cron/alice"] = command;
        using var input = MakeArchive(shape);
        var package = await f.Normalizer.NormalizeAsync(input);
        var task = Assert.Single(package.Manifest.Tasks);
        Assert.True(task.ScheduleUnknown); Assert.Empty(task.Command); Assert.Empty(task.Arguments); Assert.Empty(task.Url);
        Assert.DoesNotContain("do-not-copy", System.Text.Json.JsonSerializer.Serialize(package.Manifest));
        await using var source = await f.Store.OpenPayloadAsync(package.Id, task.ArtifactPaths[0]);
        using var text = new StreamReader(source);
        Assert.Equal(command, await text.ReadToEndAsync());
        Assert.Contains(package.Manifest.Issues, i => i.Code == "cpanel.task.command_unsupported" && i.ResourceId == task.Id);
    }

    private sealed record ArchiveShape(Dictionary<string,string> Files, HashSet<string> Directories);
    private static ArchiveShape Shape()
    {
        var files = new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["version"] = "10\n4\n", ["cp/alice"] = "USER=alice\nDNS=main.test\nDNS1=addon.test\n",
            ["userdata/main"] = "main_domain: main.test\naddon_domains:\n  addon.test: addon.main.test\nsub_domains: []\n",
            ["userdata/main.test"] = "---\nservername: main.test\nhomedir: /home/alice\ndocumentroot: '/home/alice/www main'\nserveralias: [www.main.test]\nphpversion: ea-php82\n",
            ["userdata/addon.test"] = "---\nservername: addon.test\nhomedir: /home/alice\ndocumentroot: /home/alice/apps/addon\nserveralias: www.addon.test\nphpversion: ea-php82\n",
            ["homedir/www main/index.php"] = "<?php echo 'main';", ["homedir/www main/cron.php"] = "<?php echo 'job';",
            ["homedir/apps/addon/index.html"] = "addon",
            ["mysql/alice_main.sql"] = "-- MySQL dump\nCREATE TABLE example(id int);\nINSERT INTO example VALUES(1);\n-- Dump completed\n",
            ["mysql/alice_addon.sql"] = "-- MySQL dump\nCREATE TABLE example(id int);\nINSERT INTO example VALUES(2);\n-- Dump completed\n",
            ["mysql-timestamps/alice_main.txt"] = "1700000000", ["mysql-timestamps/alice_addon.txt"] = "1700000001",
            ["homedir/etc/main.test/passwd"] = "alice:$6$do-not-copy:1000:1000::/home/alice/mail/main.test/alice:/usr/local/cpanel/bin/noshell\nbob:x:1001:1001::/home/alice/mail/main.test/bob:/bin/false\n",
            ["homedir/etc/main.test/quota"] = "alice:1048576\nbob:0\n",
            ["homedir/etc/addon.test/passwd"] = "carol:x:1002:1002::/home/alice/mail/addon.test/carol:/bin/false\n",
            ["homedir/etc/addon.test/quota"] = "carol:2097152\n",
            ["homedir/mail/main.test/alice/cur/17123.M1:2,S"] = "Subject: First\r\n\r\nFirst message",
            ["homedir/mail/main.test/alice/.Sent/cur/17124.M2:2,S"] = "Subject: Sent\r\n\r\nSent message",
            ["homedir/mail/main.test/alice/dovecot-uidlist"] = "3 V1 N3\n1 :17123.M1\n",
            ["homedir/mail/addon.test/carol/new/17125.M3"] = "Subject: New\r\n\r\nNew message",
            ["va/main.test"] = "hello: alice@main.test\n*: :fail:\n",
            ["dnszones/main.test.db"] = "$ORIGIN main.test.\n$TTL 1h\n@ IN SOA ns hostmaster ( 2026111001 1h 10m 1w 1h )\n@ IN NS ns\nns IN A 192.0.2.1\nselector._domainkey IN TXT (\"v=DKIM1; \" \"p=ABC\\032DEF\") ; comment\n",
            ["dnszones/addon.test.db"] = "$ORIGIN addon.test.\n$TTL 3600\n@ IN A 192.0.2.2\n@ IN MX 10 mail\nmail IN A 192.0.2.2\n",
            ["cron/alice"] = "15 2 * * * /usr/local/bin/php -q '/home/alice/www main/cron.php'\n*/5 * * * * curl -fsS https://addon.test/tick\n"
        };
        var directories = new HashSet<string>(StringComparer.Ordinal) { "cp", "userdata", "homedir", "homedir/apps/addon", "homedir/www main", "mysql", "psql", "dnszones", "cron", "va", "homedir/etc", "homedir/mail" };
        foreach (var mailbox in new[] { "main.test/alice", "main.test/bob", "addon.test/carol", "main.test/alice/.Sent" })
            foreach (var state in new[] { "cur", "new", "tmp" }) directories.Add("homedir/mail/" + mailbox + "/" + state);
        return new(files, directories);
    }
    private static MemoryStream MakeArchive(ArchiveShape shape, bool wrapped = false, TarEntryType? finalType = null, DateTimeOffset? messageDate = null)
    {
        using var tar = new MemoryStream();
        var prefix = wrapped ? "cpmove-alice/" : "";
        using (var writer = new TarWriter(tar, leaveOpen: true))
        {
            foreach (var directory in shape.Directories.Order(StringComparer.Ordinal)) writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, prefix + directory + "/"));
            foreach (var file in shape.Files)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(file.Value));
                if (messageDate is not null && file.Key.Contains("/cur/", StringComparison.Ordinal))
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, prefix + file.Key) { DataStream = data, ModificationTime = messageDate.Value, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
                else writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, prefix + file.Key) { DataStream = data });
            }
            if (finalType is not null)
            {
                var entry = new UstarTarEntry(finalType.Value, prefix + "homedir/late-special");
                if (finalType is TarEntryType.HardLink or TarEntryType.SymbolicLink) entry.LinkName = "../../outside";
                writer.WriteEntry(entry);
            }
        }
        return new MemoryStream(Gzip(tar.ToArray()));
    }
    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = base.ReadAsync(buffer[..Math.Min(buffer.Length, 32)], cancellationToken);
            cancellation.Cancel(); return result;
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-cpanel-normalizer-" + Guid.NewGuid().ToString("N"));
        internal string StoreRoot => Path.Combine(Root, "migration-packages");
        internal readonly PortableMigrationPackageStore Store;
        internal readonly CpanelBackupNormalizer Normalizer;
        internal Fixture(PortableMigrationPackageOptions? limits = null)
        {
            var runtime = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root });
            var options = Microsoft.Extensions.Options.Options.Create(limits ?? new());
            Store = new(runtime, options); Normalizer = new(runtime, options, Store);
        }
        internal void AssertSourceStagingClean() => Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Root, "migration-sources")));
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
}
