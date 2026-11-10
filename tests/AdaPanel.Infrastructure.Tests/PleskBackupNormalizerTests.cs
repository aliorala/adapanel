using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using Xunit;
using ZstdSharp;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Synthetic official-layout fixtures; these are not native vendor-produced Plesk backups.</summary>
public sealed class PleskBackupNormalizerTests
{
    private static readonly DateTimeOffset SourceDate = new(2022, 2, 3, 4, 5, 6, TimeSpan.Zero);
    [Theory]
    [InlineData("tar", "tgz")]
    [InlineData("tar", "tzst")]
    [InlineData("zip", "zip")]
    public async Task Full_export_receipt_and_explicit_xml_references_roundtrip_real_payloads(string outer, string component)
    {
        using var fixture = new Fixture(); var shape = Shape(outer == "zip", component);
        using var input = new MemoryStream(Archive(shape, outer)); var original = input.ToArray();
        var package = await fixture.Normalizer.NormalizeFullExportAsync(input, outer);
        var manifest = await fixture.Store.ReadManifestAsync(package.Id);
        Assert.Equal(original, input.ToArray()); Assert.Equal(PortableMigrationProvider.Plesk, manifest.Provider);
        Assert.Equal(2, manifest.Sites.Count); Assert.Equal(2, manifest.Databases.Count);
        Assert.Equal(2, manifest.MailDomains.Count); Assert.Equal(2, manifest.DnsZones.Count); Assert.Equal(2, manifest.Tasks.Count);
        Assert.All(manifest.Sites, site => { Assert.Equal(1, site.FileCount); Assert.EndsWith("httpdocs", site.SourcePhysicalPath); });
        Assert.All(manifest.Databases, database => Assert.Single(database.SiteIds));
        Assert.All(manifest.MailDomains, domain =>
        {
            var account = Assert.Single(domain.Accounts); Assert.Equal(1, account.EmailCount); Assert.Equal("maildir-zip", account.ArchiveFormat);
        });
        Assert.All(manifest.Tasks, task => { Assert.True(task.ScheduleUnknown); Assert.Empty(task.Command); Assert.Single(task.SiteIds); Assert.Single(task.ArtifactPaths); });
        Assert.False(manifest.Complete); // Actual source shell jobs require explicit destination mapping.
        Assert.DoesNotContain(manifest.Issues, issue => issue.Code.Contains("fixture", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest.Issues, issue => issue.Code == "plesk.full_backup_unverified");
        Assert.Contains(manifest.Issues, issue => issue.Code == "plesk.task.command_mapping_required");
        Assert.All(manifest.DnsZones, zone => Assert.Contains(zone.Records, record => record.Type == "TXT" && record.Value == "v=spf1 -all"));
        Assert.DoesNotContain("DO-NOT-NORMALIZE", JsonSerializer.Serialize(manifest));
        Assert.DoesNotContain(manifest.Payloads, payload => payload.Path.Contains("password") || payload.Path.Contains("private-key"));
        foreach (var payload in manifest.Payloads)
        {
            await using var data = await fixture.Store.OpenPayloadAsync(package.Id, payload.Path);
            Assert.Equal(payload.Size, data.Length); Assert.True(data.Length > 0);
        }
        fixture.AssertClean();
    }

    [Fact]
    public async Task Maildir_uses_original_tar_mtime_mode_and_filename_flags_not_message_date()
    {
        using var f = new Fixture(); using var input = new MemoryStream(Archive(Shape(), "tar"));
        var package = await f.Normalizer.NormalizeFullExportAsync(input, "tar");
        var account = package.Manifest.MailDomains[0].Accounts[0];
        await using var source = await f.Store.OpenPayloadAsync(package.Id, account.ArchivePath);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("cur/17123.M1:2,RS"));
        using var metadata = JsonDocument.Parse(zip.GetEntry(PortableMaildirWriter.MetadataEntryName)!.Open());
        var file = Assert.Single(metadata.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal(SourceDate, file.GetProperty("sourceDateUtc").GetDateTimeOffset());
        Assert.Equal(384, file.GetProperty("unixMode").GetInt32());
        Assert.Equal("RS", file.GetProperty("maildirFlags").GetString());
    }

    [Fact]
    public async Task Generic_archive_without_native_receipt_cannot_claim_full_backup()
    {
        using var f = new Fixture(); using var input = new MemoryStream(Archive(Shape(), "tar"));
        var manifest = (await f.Normalizer.NormalizeAsync(input, "tar")).Manifest;
        Assert.False(manifest.Complete); Assert.Contains(manifest.Issues, issue => issue.Code == "plesk.full_backup_unverified");
    }

    [Fact]
    public async Task Same_subscription_database_name_in_different_engines_keeps_distinct_selection_ids()
    {
        using var f = new Fixture(); var shape = Shape();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        var databases = xml.Descendants("domain").First().Element("databases")!;
        var postgres = new XElement(databases.Element("database")!); postgres.SetAttributeValue("type", "postgresql");
        databases.Add(postgres); shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString());
        using var input = new MemoryStream(Archive(shape, "tar"));
        var manifest = (await f.Normalizer.NormalizeFullExportAsync(input, "tar")).Manifest;
        Assert.Equal(3, manifest.Databases.Count);
        Assert.Contains(manifest.Databases, database => database.Id == "database:alpha.test:mysql:app_data");
        Assert.Contains(manifest.Databases, database => database.Id == "database:alpha.test:postgresql:app_data");
    }

    [Fact]
    public async Task Explicit_xml_offset_can_select_a_nested_archive_without_filename_guessing()
    {
        using var f = new Fixture(); var shape = Shape();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        var cid = xml.Descendants("domain").First().Element("phosting")!.Element("content")!.Element("cid")!;
        cid.SetAttributeValue("offset", "inner.tgz"); cid.Element("content-file")!.Value = "wrapper.zip";
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString());
        shape.Remove("domains/alpha.test/opaque-web.tgz");
        shape["domains/alpha.test/wrapper.zip"] = Archive(new()
        { ["inner.tgz"] = Archive(new() { ["index.html"] = Encoding.UTF8.GetBytes("nested-site") }, "tgz") }, "zip");
        using var input = new MemoryStream(Archive(shape, "tar"));
        var package = await f.Normalizer.NormalizeFullExportAsync(input, "tar");
        var site = Assert.Single(package.Manifest.Sites, site => site.Name == "alpha.test");
        await using var content = await f.Store.OpenPayloadAsync(package.Id, site.ContentPath + "/index.html");
        using var reader = new StreamReader(content); Assert.Equal("nested-site", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Missing_dump_and_mail_content_are_visible_and_never_count_as_payload()
    {
        using var f = new Fixture(); var shape = Shape();
        shape.Remove("domains/alpha.test/databases/opaque-db/blob.tgz");
        shape.Remove("domains/alpha.test/opaque-mail.tgz");
        using var input = new MemoryStream(Archive(shape, "tar"));
        var manifest = (await f.Normalizer.NormalizeFullExportAsync(input, "tar")).Manifest;
        Assert.False(manifest.Complete); Assert.Single(manifest.Databases);
        var account = manifest.MailDomains.Single(domain => domain.Name == "alpha.test").Accounts[0];
        Assert.Null(account.EmailCount); Assert.Empty(account.ArchivePath);
        Assert.Contains(manifest.Issues, issue => issue.Code == "plesk.content_payload_missing" && issue.ResourceId.StartsWith("database:"));
        Assert.Contains(manifest.Issues, issue => issue.Code == "plesk.content_payload_missing" && issue.ResourceId == "mail:alpha.test");
    }

    [Fact]
    public async Task MailEnable_files_are_not_reported_as_importable_maildir_messages()
    {
        using var f = new Fixture(); var shape = Shape(true, "zip");
        shape["domains/alpha.test/opaque-mail.zip"] = Archive(new() { ["alice/Maildir/Inbox/message.MAI"] = Encoding.UTF8.GetBytes("MailEnable-message") }, "zip");
        using var input = new MemoryStream(Archive(shape, "zip"));
        var manifest = (await f.Normalizer.NormalizeFullExportAsync(input, "zip")).Manifest;
        var account = manifest.MailDomains.Single(domain => domain.Name == "alpha.test").Accounts[0];
        Assert.Null(account.EmailCount); Assert.Empty(account.ArchivePath);
        Assert.Contains(manifest.Issues, issue => issue.Code == "plesk.mail.mailenable_conversion_unsupported");
    }

    [Theory]
    [InlineData("incremental=\"true\"")]
    [InlineData("base-backup=\"prior.xml\"")]
    [InlineData("split=\"true\"")]
    public async Task Incremental_or_multivolume_metadata_is_rejected_even_with_receipt(string flag)
    {
        using var f = new Fixture(); var shape = Shape();
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]).Replace("dump-format=\"panel\"", "dump-format=\"panel\" " + flag));
        using var input = new MemoryStream(Archive(shape, "tar"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeFullExportAsync(input, "tar")); f.AssertClean();
    }

    [Fact]
    public async Task Filename_similarity_does_not_replace_the_explicit_missing_reference()
    {
        using var f = new Fixture(); var shape = Shape();
        shape["domains/alpha.test/backup_user-data_2601011200.tgz"] = shape["domains/alpha.test/opaque-web.tgz"];
        shape.Remove("domains/alpha.test/opaque-web.tgz");
        using var input = new MemoryStream(Archive(shape, "tar"));
        var manifest = (await f.Normalizer.NormalizeFullExportAsync(input, "tar")).Manifest;
        Assert.DoesNotContain(manifest.Sites, site => site.Name == "alpha.test");
        Assert.Contains(manifest.Issues, issue => issue.Code == "plesk.content_payload_missing" && issue.ResourceId == "site:alpha.test");
    }

    [Fact]
    public async Task Empty_but_proven_maildir_is_zero_not_unknown()
    {
        using var f = new Fixture(); var shape = Shape();
        shape["domains/alpha.test/opaque-mail.tgz"] = Archive([], "tgz", ["alice/Maildir/cur", "alice/Maildir/new", "alice/Maildir/tmp"]);
        using var input = new MemoryStream(Archive(shape, "tar"));
        var manifest = (await f.Normalizer.NormalizeFullExportAsync(input, "tar")).Manifest;
        var account = manifest.MailDomains.Single(domain => domain.Name == "alpha.test").Accounts[0];
        Assert.Equal(0, account.EmailCount); Assert.NotEmpty(account.ArchivePath);
    }

    [Theory]
    [InlineData("tar")]
    [InlineData("zip")]
    public async Task Traversal_and_case_collisions_are_rejected_and_owned_stage_is_cleaned(string format)
    {
        using var f = new Fixture(); var shape = Shape(); shape["../escape.txt"] = [1];
        using var input = new MemoryStream(Archive(shape, format));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeFullExportAsync(input, format)); f.AssertClean();
        shape.Remove("../escape.txt"); shape["Collision.txt"] = [1]; shape["collision.txt"] = [2];
        using var duplicate = new MemoryStream(Archive(shape, format));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeFullExportAsync(duplicate, format)); f.AssertClean();
    }

    [Fact]
    public async Task Dtd_and_external_entities_are_rejected_without_reading_the_target_file()
    {
        using var f = new Fixture(); var shape = Shape();
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes("<!DOCTYPE migration-dump [<!ENTITY xxe SYSTEM 'file:///C:/must-not-read'>]>" + Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        using var input = new MemoryStream(Archive(shape, "tar"));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Normalizer.NormalizeFullExportAsync(input, "tar")); f.AssertClean();
    }

    [Fact]
    public async Task Aggregate_nested_expansion_is_bounded_across_distinct_payloads()
    {
        using var f = new Fixture(new() { MaxTotalUncompressedBytes = 20_000 });
        var shape = Shape();
        foreach (var host in new[] { "alpha.test", "beta.test" })
            shape["domains/" + host + "/opaque-web.tgz"] = Archive(new() { ["httpdocs/index.html"] = Encoding.UTF8.GetBytes(new string('x', 8_000)) }, "tgz");
        using var input = new MemoryStream(Archive(shape, "tar"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeFullExportAsync(input, "tar")); f.AssertClean();
    }

    [Theory]
    [InlineData("tgz")]
    [InlineData("tzst")]
    public async Task Truncated_or_invalid_compressed_component_cannot_create_success(string component)
    {
        using var f = new Fixture(); var shape = Shape(component: component);
        var key = "domains/alpha.test/opaque-web." + component;
        shape[key] = shape[key][..^4];
        using var input = new MemoryStream(Archive(shape, "tar"));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Normalizer.NormalizeFullExportAsync(input, "tar")); f.AssertClean();
    }

    [Fact]
    public async Task Wrong_provider_and_configuration_only_are_rejected()
    {
        using var f = new Fixture(); var shape = Shape();
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes("<cpanel />");
        using var wrong = new MemoryStream(Archive(shape, "tar"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeFullExportAsync(wrong, "tar"));
        shape = Shape(); shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]).Replace("content-included=\"true\"", "content-included=\"false\""));
        using var config = new MemoryStream(Archive(shape, "tar"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeFullExportAsync(config, "tar")); f.AssertClean();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData(" ", false)]
    [InlineData(" ", true)]
    [InlineData("   ", false)]
    [InlineData("   ", true)]
    [InlineData("  MiXeD+/=;  ", false)]
    [InlineData("  MiXeD+/=;  ", true)]
    [InlineData("  MiXeD \"quoted\" \\path & <tag>  ", false)]
    [InlineData("  MiXeD \"quoted\" \\path & <tag>  ", true)]
    public async Task TXT_attribute_or_child_preserves_empty_spaces_case_and_decoded_XML_characters(string value, bool child)
    {
        using var fixture = new Fixture(); var shape = Shape();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]), LoadOptions.PreserveWhitespace);
        var record = xml.Descendants("domain").First().Element("properties")!.Element("dns-zone")!.Elements("dnsrec").Single(r => (string?)r.Attribute("type") == "TXT");
        record.Attribute("dst")!.Remove();
        if (child) record.Add(new XElement("dst", value)); else record.SetAttributeValue("dst", value);
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        using var source = new MemoryStream(Archive(shape, "tar")); var original = source.ToArray();
        var package = await fixture.Normalizer.NormalizeFullExportAsync(source, "tar");
        var manifest = await fixture.Store.ReadManifestAsync(package.Id);
        var zone = Assert.Single(manifest.DnsZones, z => z.Name == "alpha.test");
        Assert.Equal(value, Assert.Single(zone.Records, r => r.Type == "TXT").Value);
        Assert.DoesNotContain(manifest.Issues, i => i.Code == "plesk.dns.record_invalid" && i.ResourceId == "dns:alpha.test");
        Assert.Equal(original, source.ToArray()); fixture.AssertClean();
    }

    [Fact]
    public async Task Explicit_empty_TXT_attribute_has_presence_and_does_not_fall_back_to_child_value()
    {
        using var fixture = new Fixture(); var shape = Shape();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        var record = xml.Descendants("domain").First().Element("properties")!.Element("dns-zone")!.Elements("dnsrec").Single(r => (string?)r.Attribute("type") == "TXT");
        record.SetAttributeValue("dst", ""); record.Add(new XElement("dst", "must-not-replace-explicit-empty"));
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        using var input = new MemoryStream(Archive(shape, "tar"));
        var zone = (await fixture.Normalizer.NormalizeFullExportAsync(input, "tar")).Manifest.DnsZones.Single(z => z.Name == "alpha.test");
        Assert.Equal("", Assert.Single(zone.Records, r => r.Type == "TXT").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonTXT_destination_still_normalizes_outer_whitespace(bool child)
    {
        using var fixture = new Fixture(); var shape = Shape();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        var record = xml.Descendants("domain").First().Element("properties")!.Element("dns-zone")!.Elements("dnsrec").Single(r => (string?)r.Attribute("type") == "A");
        record.Attribute("dst")!.Remove();
        if (child) record.Add(new XElement("dst", " 192.0.2.1 ")); else record.SetAttributeValue("dst", " 192.0.2.1 ");
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        using var input = new MemoryStream(Archive(shape, "tar"));
        var zone = (await fixture.Normalizer.NormalizeFullExportAsync(input, "tar")).Manifest.DnsZones.Single(z => z.Name == "alpha.test");
        Assert.Equal("192.0.2.1", Assert.Single(zone.Records, r => r.Type == "A").Value);
    }

    [Fact]
    public async Task Missing_TXT_destination_is_an_issue_while_explicit_empty_is_valid()
    {
        using var fixture = new Fixture(); var shape = Shape();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        xml.Descendants("domain").First().Element("properties")!.Element("dns-zone")!.Elements("dnsrec").Single(r => (string?)r.Attribute("type") == "TXT").Attribute("dst")!.Remove();
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        using var input = new MemoryStream(Archive(shape, "tar"));
        var manifest = (await fixture.Normalizer.NormalizeFullExportAsync(input, "tar")).Manifest;
        Assert.DoesNotContain(manifest.DnsZones.Single(z => z.Name == "alpha.test").Records, r => r.Type == "TXT");
        Assert.Contains(manifest.Issues, i => i.Code == "plesk.dns.record_invalid" && i.ResourceId == "dns:alpha.test");
    }

    [Fact]
    public async Task Unverified_mailbox_settings_are_explicit_email_scoped_mapping_issues_and_never_source_passwords()
    {
        using var fixture = new Fixture(); using var input = new MemoryStream(Archive(Shape(), "tar"));
        var package = await fixture.Normalizer.NormalizeFullExportAsync(input, "tar");
        Assert.False(package.Manifest.Complete);
        foreach (var domain in package.Manifest.MailDomains)
        foreach (var mailbox in domain.Accounts)
        {
            Assert.Null(mailbox.Active);
            foreach (var code in new[] { "plesk.mail.quota_unknown", "plesk.mail.active_state_unknown", "plesk.mail.new_password_required" })
                Assert.Contains(package.Manifest.Issues, i => i.Code == code && i.ResourceId == mailbox.Email);
        }
        Assert.DoesNotContain("DO-NOT-NORMALIZE", JsonSerializer.Serialize(package.Manifest));
        var artifacts = package.Manifest.Payloads.Where(p => p.Path.StartsWith("source/mail-settings/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, artifacts.Length);
        foreach (var artifact in artifacts)
        {
            await using var stream = await fixture.Store.OpenPayloadAsync(package.Id, artifact.Path);
            using var document = await JsonDocument.ParseAsync(stream);
            Assert.Contains(document.RootElement.GetProperty("email").GetString(), new[] { "alice@alpha.test", "alice@beta.test" });
            Assert.Empty(document.RootElement.GetProperty("fields").EnumerateArray());
            Assert.DoesNotContain("DO-NOT-NORMALIZE", document.RootElement.GetRawText());
        }
        fixture.AssertClean();
    }

    [Fact]
    public async Task Raw_quota_and_enabled_fields_are_preserved_privately_without_inventing_backup_units_or_effective_login_state()
    {
        using var fixture = new Fixture(); var shape = Shape();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        var user = xml.Descendants("domain").First().Element("mailsystem")!.Element("mailusers")!.Element("mailuser")!;
        user.SetAttributeValue("mailbox-quota", "  inherited  ");
        var box = user.Element("preferences")!.Element("mailbox")!;
        box.SetAttributeValue("quota", " 10485760 "); box.SetAttributeValue("enabled", " false ");
        box.Add(new XElement("quota", new XAttribute("unit", "unverified-source-unit"), " -1 "), new XElement("enabled", " true "));
        box.Add(new XElement("password", "PASSWORD-HASH-MUST-NOT-BE-COPIED"), new XElement("private-key", "PRIVATE-KEY-MUST-NOT-BE-COPIED"));
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        using var input = new MemoryStream(Archive(shape, "tar"));
        var package = await fixture.Normalizer.NormalizeFullExportAsync(input, "tar");
        var mailbox = package.Manifest.MailDomains.Single(d => d.Name == "alpha.test").Accounts.Single();
        Assert.Equal(0, mailbox.QuotaMb); Assert.Null(mailbox.Active);
        Assert.Contains(package.Manifest.Issues, i => i.Code == "plesk.mail.quota_unknown" && i.ResourceId == mailbox.Email);
        Assert.Contains(package.Manifest.Issues, i => i.Code == "plesk.mail.active_state_unknown" && i.ResourceId == mailbox.Email);
        JsonDocument? metadata = null;
        try
        {
            foreach (var artifact in package.Manifest.Payloads.Where(p => p.Path.StartsWith("source/mail-settings/", StringComparison.Ordinal)))
            {
                await using var source = await fixture.Store.OpenPayloadAsync(package.Id, artifact.Path);
                var candidate = await JsonDocument.ParseAsync(source);
                if (candidate.RootElement.GetProperty("email").GetString() == mailbox.Email) { metadata = candidate; break; }
                candidate.Dispose();
            }
            Assert.NotNull(metadata);
            var fields = metadata.RootElement.GetProperty("fields").EnumerateArray().ToArray();
            Assert.Contains(fields, f => f.GetProperty("name").GetString() == "quota" && f.GetProperty("kind").GetString() == "attribute" && f.GetProperty("raw").GetString() == " 10485760 ");
            Assert.Contains(fields, f => f.GetProperty("name").GetString() == "enabled" && f.GetProperty("kind").GetString() == "attribute" && f.GetProperty("raw").GetString() == " false ");
            Assert.Contains(fields, f => f.GetProperty("name").GetString() == "quota" && f.GetProperty("kind").GetString() == "child" && f.GetProperty("raw").GetString() == " -1 ");
            Assert.Contains(fields, f => f.GetProperty("name").GetString() == "enabled" && f.GetProperty("kind").GetString() == "child" && f.GetProperty("raw").GetString() == " true ");
            var rawQuotaChild = Assert.Single(fields, f => f.GetProperty("name").GetString() == "quota" && f.GetProperty("kind").GetString() == "child");
            Assert.Contains(rawQuotaChild.GetProperty("qualifiers").EnumerateArray(), q => q.GetProperty("name").GetString() == "unit" && q.GetProperty("raw").GetString() == "unverified-source-unit");
            Assert.Contains(fields, f => f.GetProperty("name").GetString() == "mailbox-quota" && f.GetProperty("raw").GetString() == "  inherited  ");
            Assert.DoesNotContain("PASSWORD-HASH", metadata.RootElement.GetRawText()); Assert.DoesNotContain("PRIVATE-KEY", metadata.RootElement.GetRawText());
            Assert.DoesNotContain("DO-NOT-NORMALIZE", metadata.RootElement.GetRawText());
        }
        finally { metadata?.Dispose(); }
        Assert.DoesNotContain("PASSWORD-HASH", JsonSerializer.Serialize(package.Manifest));
        fixture.AssertClean();
    }

    [Fact]
    public async Task Missing_mail_content_does_not_hide_settings_or_new_credential_decisions()
    {
        using var fixture = new Fixture(); var shape = Shape(); shape.Remove("domains/alpha.test/opaque-mail.tgz");
        using var input = new MemoryStream(Archive(shape, "tar"));
        var manifest = (await fixture.Normalizer.NormalizeFullExportAsync(input, "tar")).Manifest;
        var account = manifest.MailDomains.Single(d => d.Name == "alpha.test").Accounts.Single();
        Assert.Null(account.EmailCount); Assert.Empty(account.ArchivePath);
        foreach (var code in new[] { "plesk.mail.quota_unknown", "plesk.mail.active_state_unknown", "plesk.mail.new_password_required" })
            Assert.Contains(manifest.Issues, i => i.ResourceId == account.Email && i.Code == code);
        Assert.Contains(manifest.Payloads, p => p.Path.StartsWith("source/mail-settings/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("beta.test", false)]
    [InlineData("alpha.test", true)]
    public async Task Full_receipt_subscription_must_match_exactly_one_top_level_domain(string expected, bool keepBoth)
    {
        using var f = new Fixture(); var shape = Shape();
        var xml = System.Xml.Linq.XElement.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        if (!keepBoth) xml.Descendants("domain").Single(d => (string?)d.Attribute("name") == "beta.test").Remove();
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString());
        using var input = new MemoryStream(Archive(shape, "tar"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Normalizer.NormalizeFullExportAsync(input, "tar", expected));
        f.AssertClean();
    }

    [Fact]
    public async Task Matching_subscription_preserves_real_child_site_and_alias()
    {
        using var f = new Fixture(); var shape = Shape();
        var xml = System.Xml.Linq.XElement.Parse(Encoding.UTF8.GetString(shape["backup_info_2601011200.xml"]));
        xml.Descendants("domain").Single(d => (string?)d.Attribute("name") == "beta.test").Remove();
        var domain = xml.Descendants("domain").Single();
        var child = new System.Xml.Linq.XElement("site", new System.Xml.Linq.XAttribute("name", "child.alpha.test"), new System.Xml.Linq.XElement(domain.Element("phosting")!));
        domain.Add(new System.Xml.Linq.XElement("sites", child));
        domain.Add(System.Xml.Linq.XElement.Parse("<domain-aliases><domain-alias name='alias.alpha.test'/></domain-aliases>"));
        shape["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml.ToString());
        using var input = new MemoryStream(Archive(shape, "tar"));
        var manifest = (await f.Normalizer.NormalizeFullExportAsync(input, "tar", "alpha.test")).Manifest;
        Assert.Equal("alpha.test", manifest.SourceName);
        Assert.Contains(manifest.Sites, site => site.Name == "child.alpha.test");
        Assert.Contains(manifest.Sites.SelectMany(site => site.HostNames), host => host == "alias.alpha.test");
    }

    private static Dictionary<string, byte[]> Shape(bool windows = false, string component = "tgz")
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string Domain(string host)
        {
            var path = "domains/" + host;
            files[path + "/opaque-web." + component] = Archive(new() { ["httpdocs/index.html"] = Encoding.UTF8.GetBytes("site " + host) }, component);
            files[path + "/databases/opaque-db/blob." + component] = Archive(new() { ["arbitrary-file-name"] = Encoding.UTF8.GetBytes("CREATE TABLE t(id int);") }, component);
            files[path + "/opaque-mail." + component] = Archive(new() { ["alice/Maildir/cur/17123.M1:2,RS"] = Encoding.UTF8.GetBytes("Date: Tue, 1 Jan 1980 00:00:00 +0000\r\nSubject: source-time-fixture\r\n\r\nmail") }, component,
                ["alice/Maildir/cur", "alice/Maildir/new", "alice/Maildir/tmp"]);
            var root = windows ? @"C:\Inetpub\vhosts\" + host + @"\httpdocs" : "/var/www/vhosts/" + host + "/httpdocs";
            return $"""
                <domain name="{host}"><phosting><www-root>{root}</www-root><php-version>8.3</php-version><preferences><sysuser><password>DO-NOT-NORMALIZE</password><private-key>DO-NOT-NORMALIZE</private-key></sysuser></preferences><content><cid type="docroot" path="{path}" offset="httpdocs"><content-file>opaque-web.{component}</content-file></cid></content></phosting>
                <databases><database name="app_data" type="mysql"><content><cid type="sqldump" path="{path}/databases/opaque-db"><content-file>blob.{component}</content-file></cid></content></database></databases>
                <mailsystem><mailusers><mailuser name="alice"><preferences><password>DO-NOT-NORMALIZE</password><mailbox><content><cid type="mailbox" path="{path}" offset="alice/Maildir"><content-file>opaque-mail.{component}</content-file></cid></content></mailbox></preferences></mailuser></mailusers></mailsystem>
                <properties><dns-zone ttl="3600"><dnsrec type="A" src="{host}." dst="192.0.2.1"/><dnsrec type="TXT" src="{host}." dst="v=spf1 -all"/></dns-zone></properties>
                <scheduled-tasks><task><schedule>0 0 * * *</schedule><command>/usr/bin/php /var/www/vhosts/{host}/httpdocs/job.php</command></task></scheduled-tasks></domain>
                """;
        }
        var alpha = Domain("alpha.test"); var beta = Domain("beta.test");
        var xml = $"""<migration-dump agent-name="Plesk.PHP" dump-version="18.0.75" content-included="true" dump-format="panel" encryption-type="none"><dump-info><os-description type="{(windows ? "windows" : "unix")}"/></dump-info><admin><domains>{alpha}</domains><resellers><reseller name="seller"><clients><client name="customer"><domains>{beta}</domains></client></clients></reseller></resellers></admin></migration-dump>""";
        files["backup_info_2601011200.xml"] = Encoding.UTF8.GetBytes(xml);
        return files;
    }
    private static byte[] Archive(Dictionary<string, byte[]> files, string format, string[]? directories = null)
    {
        using var output = new MemoryStream();
        if (format == "zip")
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var directory in directories ?? []) zip.CreateEntry(directory + "/");
                foreach (var file in files)
                {
                    var entry = zip.CreateEntry(file.Key); entry.LastWriteTime = SourceDate; entry.ExternalAttributes = (0x8000 | 384) << 16;
                    using var target = entry.Open(); target.Write(file.Value);
                }
            }
            return output.ToArray();
        }
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, leaveOpen: true))
        {
            foreach (var directory in directories ?? []) writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, directory + "/"));
            foreach (var file in files)
            {
                using var input = new MemoryStream(file.Value);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Key) { DataStream = input, ModificationTime = SourceDate, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            }
        }
        if (format == "tar") return tar.ToArray();
        if (format == "tgz") { using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true); gzip.Write(tar.ToArray()); gzip.Dispose(); }
        else if (format == "tzst") { using var zstd = new CompressionStream(output, leaveOpen: true); zstd.Write(tar.ToArray()); zstd.Dispose(); }
        else throw new InvalidOperationException();
        return output.ToArray();
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "AdaPanel.PleskNormalizer." + Guid.NewGuid().ToString("N"));
        internal readonly PortableMigrationPackageStore Store;
        internal readonly PleskBackupNormalizer Normalizer;
        internal Fixture(PortableMigrationPackageOptions? limits = null)
        {
            var runtime = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root });
            var options = Microsoft.Extensions.Options.Options.Create(limits ?? new());
            Store = new(runtime, options); Normalizer = new(runtime, options, Store);
        }
        internal void AssertClean() => Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Root, "migration-normalization")));
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
}
