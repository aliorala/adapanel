using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslRenewalVerificationTests
{
    private static SiteDto Site => new() { Id = 12, Name = "example.test" };
    private static JsonElement Record(string host = "example.test", long siteId = 12, string validation = SslRenewalVerification.SelfHosting,
        string installation = SslRenewalVerification.IisInstallation, string id = "old-random-id", string? thumb = null, bool success = true) => JsonSerializer.SerializeToElement(new {
        Id = id,
        TargetPluginOptions = new { Plugin = SslRenewalVerification.IisSource, IncludeHosts = new[] {host}, IncludeSiteIds = new[] {siteId} },
        ValidationPluginOptions = new { Plugin = validation },
        InstallationPluginOptions = new[] { new { Plugin = installation, SiteId = siteId } },
        History = new[] { new { Success = success, OrderResults = new[] { new { Success = success, Thumbprint = thumb } } } }
    });
    [Fact] public void ExistingRandomIdIsPreserved() => Assert.Equal("old-random-id", SslRenewalVerification.String(
        SslRenewalVerification.Select([Record()], Site, ["example.test"])!.Value, "Id"));
    [Fact] public void DuplicateOrdersAreAmbiguous() => Assert.Throws<AppException>(() => SslRenewalVerification.Select([Record(), Record(id:"other")], Site, ["example.test"]));
    [Fact] public void ForeignSiteIsNotAdopted() => Assert.Null(SslRenewalVerification.Select([Record(siteId:13)], Site, ["example.test"]));
    [Fact] public void UnrelatedHostIsNotAdopted() => Assert.Null(SslRenewalVerification.Select([Record(host:"other.test")], Site, ["example.test"]));
    [Theory] [InlineData("self-hosting")] [InlineData(SslRenewalVerification.DnsScript)]
    public void WrongValidationCannotAppearSuccessful(string plugin) => Assert.Throws<AppException>(() => SslRenewalVerification.Select([Record(validation:plugin)], Site, ["example.test"]));
    [Fact] public void InstallationScriptIsNeverOverwritten() => Assert.Throws<AppException>(() => SslRenewalVerification.Select([Record(installation:"script")], Site, ["example.test"]));
    [Theory] [InlineData("bad id")] [InlineData("bad\" --renew")]
    public void UnsafeIdCannotReachCommandLine(string id) => Assert.Throws<AppException>(() => SslRenewalVerification.Select([Record(id:id)], Site, ["example.test"]));
    [Theory] [InlineData("example.test",true,true)] [InlineData("other.test",true,false)] [InlineData("example.test",false,false)]
    public void StoreCertificateAndLastOrderMustAgree(string host, bool successful, bool valid)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host); request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        var record = Record(thumb:certificate.Thumbprint,success:successful);
        if (valid) SslRenewalVerification.VerifyIssued(record, record, certificate, ["example.test"]);
        else Assert.Throws<AppException>(() => SslRenewalVerification.VerifyIssued(null, record, certificate, ["example.test"]));
        Assert.Throws<AppException>(() => SslRenewalVerification.VerifyIssued(null, Record(thumb:"OLD"), certificate, ["example.test"]));
    }
    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task CustomConfigurationRootRespectsEndpointAndLegacyClientSubdirectory(bool staging, bool legacyClientSubdirectory)
    {
        using var area = new BulutAlani();
        var executable = Path.Combine(area.Kok, "wacs.exe");
        var root = Path.Combine(area.Kok, "custom-config");
        var selectedRoot = legacyClientSubdirectory ? Path.Combine(root, "custom-client") : root;
        var folder = Path.Combine(selectedRoot, staging ? "acme-staging-v02.api.letsencrypt.org" : "acme-v02.api.letsencrypt.org");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(area.Kok, "settings.json"), JsonSerializer.Serialize(new {Client = new {ClientName="custom-client", ConfigurationPath=root}}));
        await File.WriteAllTextAsync(Path.Combine(folder, "old-random-id.renewal.json"), Record().GetRawText());
        var records = await SslRenewalVerification.ReadAsync(executable, staging, area.Iptal);
        Assert.Equal("old-random-id", SslRenewalVerification.String(Assert.Single(records), "Id"));
        Assert.Empty(await SslRenewalVerification.ReadAsync(executable, !staging, area.Iptal));
    }
    [Fact] public async Task MissingSettingsUsesTheBundledDefaultConfiguration()
    {
        using var area = new BulutAlani(); var root = Path.Combine(area.Kok, "default-config");
        var folder = Path.Combine(root,"acme-v02.api.letsencrypt.org"); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(area.Kok,"settings_default.json"), JsonSerializer.Serialize(new {Client=new {ClientName="win-acme",ConfigurationPath=root}}));
        await File.WriteAllTextAsync(Path.Combine(folder,"random.renewal.json"),Record().GetRawText());
        Assert.Single(await SslRenewalVerification.ReadAsync(Path.Combine(area.Kok,"wacs.exe"),false,area.Iptal));
    }
    [Fact] public void LastFailedOrderDoesNotFallBackToAnEarlierSuccess()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Record(thumb:"GOOD").GetRawText())!;
        var history = node["History"]!.AsArray();
        history.Add(System.Text.Json.Nodes.JsonNode.Parse(Record(thumb:"OLD",success:false).GetRawText())!["History"]![0]!.DeepClone());
        Assert.Throws<AppException>(() => SslRenewalVerification.LastThumbprint(JsonSerializer.SerializeToElement(node)));
    }
    [Fact] public void NonFatalPerOrderErrorsCannotAppearAsCleanSuccess()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Record(thumb:"GOOD").GetRawText())!;
        node["History"]![0]!["OrderResults"]![0]!["ErrorMessages"] = new System.Text.Json.Nodes.JsonArray("installation failed");
        Assert.Throws<AppException>(() => SslRenewalVerification.LastThumbprint(JsonSerializer.SerializeToElement(node)));
    }
    [Fact] public void ExternalManualFilesystemAndScriptPlanIsPreserved()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Record().GetRawText())!;
        node["TargetPluginOptions"] = JsonSerializer.SerializeToNode(new {Plugin=SslRenewalVerification.ManualSource,CommonName="example.test",AlternativeNames=new[]{"example.test"}});
        node["ValidationPluginOptions"] = JsonSerializer.SerializeToNode(new {Plugin="filesystem",Path="C:\\web",CopyWebConfig=true});
        node["InstallationPluginOptions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new {Plugin="script",Script="existing-hook.ps1"}));
        var original = node.ToJsonString();
        var error = Assert.Throws<AppException>(() => SslRenewalVerification.Select([JsonSerializer.SerializeToElement(node)],Site,["example.test"]));
        Assert.Equal("ssl.renewal_managed_externally",error.MessageKey); Assert.Equal(original,node.ToJsonString());
    }

}
