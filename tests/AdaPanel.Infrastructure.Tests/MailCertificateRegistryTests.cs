using System.Text.Json.Nodes;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailCertificateRegistryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PascalCaseLegacyFieldsAreUpdatedWithoutDuplicateAliases(bool withInventory)
    {
        using var area = new BulutAlani();
        var first = JsonNode.Parse("""
            {"Domain":"first.test","HostName":"mail.first.test","Source":"windows-store",
             "Thumbprint":"FIRST","PemDirectory":"legacy-path","LegacyTag":{"keep":"exact-value"}}
            """)!.AsObject();
        var root = (JsonObject)first.DeepClone();
        root["PrimaryHostName"] = "mail.first.test";
        if (withInventory)
            root["Certificates"] = new JsonArray(first.DeepClone(), JsonNode.Parse("""
                {"Domain":"second.test","HostName":"mail.second.test","Source":"windows-store",
                 "Thumbprint":"SECOND","CustomMetadata":"preserve-second"}
                """));
        await area.DosyaDeposu.WriteAsync(MailCertificateStoreReference.StatePath, root, area.Iptal);
        await MailCertificateRegistry.UpsertAsync(area.DosyaDeposu, new
            { domain = "first.test", hostName = "mail.first.test", source = "windows-store",
                thumbprint = "RENEWED", pemDirectory = "renewed-path" }, area.Iptal);
        var saved = await MailCertificateRegistry.ReadAsync(area.DosyaDeposu, area.Iptal);
        Assert.Equal("mail.first.test", saved.PrimaryHostName);
        Assert.Equal(withInventory ? 2 : 1, saved.Records.Count);
        var updated = saved.Records.Single(record => MailCertificateRegistry.Field(record, "hostName") == "mail.first.test");
        Assert.Equal("RENEWED", MailCertificateRegistry.Field(updated, "thumbprint"));
        Assert.Equal("renewed-path", MailCertificateRegistry.Field(updated, "pemDirectory"));
        Assert.Equal("exact-value", updated["LegacyTag"]!["keep"]!.GetValue<string>());
        if (withInventory)
        {
            var other = saved.Records.Single(record => MailCertificateRegistry.Field(record, "hostName") == "mail.second.test");
            Assert.Equal("SECOND", MailCertificateRegistry.Field(other, "thumbprint"));
            Assert.Equal("preserve-second", other["CustomMetadata"]!.GetValue<string>());
        }
        foreach (var record in saved.Records.Append(saved.Document))
            Assert.All(record.GroupBy(field => field.Key, StringComparer.OrdinalIgnoreCase), group => Assert.Single(group));
        Assert.DoesNotContain(updated, field => field.Key is "HostName" or "Domain" or "Source" or "Thumbprint" or "PemDirectory");
        Assert.True(saved.Document.ContainsKey("primaryHostName"));
        Assert.True(saved.Document.ContainsKey("certificates"));
    }

    [Fact]
    public async Task MigrationKeepsOriginalMetadataAndEveryHostOnRepeatedUpdates()
    {
        using var area = new BulutAlani();
        var store = area.DosyaDeposu;
        await store.WriteAsync(MailCertificateStoreReference.StatePath, new
        { domain = "first.test", hostName = "mail.first.test", source = "windows-store", thumbprint = "FIRST", legacyTag = "retain-me" }, area.Iptal);
        await MailCertificateRegistry.UpsertAsync(store, new
        { domain = "second.test", hostName = "mail.second.test", source = "windows-store", thumbprint = "SECOND" }, area.Iptal);
        await MailCertificateRegistry.UpsertAsync(store, new
        { domain = "second.test", hostName = "mail.second.test", source = "windows-store", thumbprint = "RENEWED" }, area.Iptal);
        var snapshot = await MailCertificateRegistry.ReadAsync(store, area.Iptal);
        Assert.Equal("mail.first.test", snapshot.PrimaryHostName);
        Assert.Equal(2, snapshot.Records.Count);
        Assert.Equal("retain-me", MailCertificateRegistry.Field(snapshot.Records.Single(r =>
            MailCertificateRegistry.Field(r, "hostName") == "mail.first.test"), "legacyTag"));
        Assert.Equal(MailCertificateStoreReference.Usage.InUse, await MailCertificateStoreReference.ReadAsync(
            store.ResolvePath(MailCertificateStoreReference.StatePath), "FIRST", area.Iptal));
        Assert.Equal(MailCertificateStoreReference.Usage.InUse, await MailCertificateStoreReference.ReadAsync(
            store.ResolvePath(MailCertificateStoreReference.StatePath), "RENEWED", area.Iptal));
        Assert.Equal(MailCertificateStoreReference.Usage.None, await MailCertificateStoreReference.ReadAsync(
            store.ResolvePath(MailCertificateStoreReference.StatePath), "SECOND", area.Iptal));
    }

    [Theory]
    [InlineData("{\"certificates\":{}}")]
    [InlineData("{\"certificates\":[{}]}")]
    [InlineData("{\"certificates\":[{\"hostName\":\"mail.a.test\"},{\"hostName\":\"MAIL.A.TEST\"}]}")]
    public async Task InvalidInventoryIsNeverOverwrittenOrTreatedAsUnused(string text)
    {
        using var area = new BulutAlani();
        var path = area.DosyaDeposu.ResolvePath(MailCertificateStoreReference.StatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, area.Iptal);
        await Assert.ThrowsAsync<InvalidDataException>(() => MailCertificateRegistry.UpsertAsync(area.DosyaDeposu,
            new { domain = "new.test", hostName = "mail.new.test", source = "windows-store", thumbprint = "NEXT" }, area.Iptal));
        Assert.Equal(text, await File.ReadAllTextAsync(path, area.Iptal));
        Assert.Equal(MailCertificateStoreReference.Usage.Unknown, await MailCertificateStoreReference.ReadAsync(path, "NEXT", area.Iptal));
    }
}
