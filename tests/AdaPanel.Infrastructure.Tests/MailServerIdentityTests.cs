using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailServerIdentityTests
{
    [Theory]
    [InlineData("mail.other.test")]
    [InlineData("-mail.example.test")]
    [InlineData("mail..example.test")]
    [InlineData("mail.example.test --script x")]
    [InlineData("example.test")]
    [InlineData("*.example.test")]
    public async Task InvalidHostnameNeverWrites(string host)
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<AppException>(() => f.Service.SetServerIdentityAsync(Request(host)));
        Assert.Empty(f.Commands);
        Assert.False(File.Exists(f.Store.ResolvePath(MailService.ServerIdentityPath)));
    }

    [Fact]
    public async Task SuccessfulIdentityPersistsAcrossServiceInstancesAndRepairsWithoutPtrOrDomainFallback()
    {
        using var f = new Fixture();
        var result = await f.Service.SetServerIdentityAsync(Request());
        Assert.True(result.Applied);
        Assert.True(result.Pinned);
        Assert.Equal(Fixture.Target, result.CurrentHostName);
        Assert.Empty(result.PtrRequest);
        Assert.DoesNotContain(result.Steps, s => s.Key == "ptr");
        var saved = await File.ReadAllBytesAsync(f.Store.ResolvePath(MailService.ServerIdentityPath));
        f.CurrentHost = "mail.other-domain.test";
        f.CurrentCertificate = "another-certificate";
        f.Commands.Clear();
        var restarted = f.CreateService();
        Assert.True(await restarted.EnsureServerHostnameAsync(false));
        Assert.Equal("mail.other-domain.test", f.CurrentHost);
        Assert.True(await restarted.EnsureServerHostnameAsync());
        Assert.Equal(Fixture.Target, f.CurrentHost);
        Assert.Equal("another-certificate", f.CurrentCertificate); // hostname repair does not replace SNI or certificate selection
        Assert.Contains("query domain --json", f.Commands); // Inventory validates the native reference, never derives the pin's name.
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("get domain", StringComparison.OrdinalIgnoreCase));
        Assert.False(await restarted.EnsureServerHostnameAsync());
        Assert.Equal(saved, await File.ReadAllBytesAsync(f.Store.ResolvePath(MailService.ServerIdentityPath)));
        var status = await restarted.GetServerIdentityAsync();
        Assert.True(status.Applied);
        Assert.Equal(Fixture.Target, status.ConfiguredHostName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinnedRepairDetectsAndRepairsDeletedDefaultDomainWithoutChangingIdentityOrCertificate(bool hostnameDrift)
    {
        using var f = new Fixture();
        Assert.True((await f.Service.SetServerIdentityAsync(Request())).Applied);
        var selectedCertificate = f.CurrentCertificate;
        f.CurrentDomainId = "deleted-domain";
        if (hostnameDrift) f.CurrentHost = "mail.unrelated.test";
        f.Commands.Clear();
        Assert.True(await f.CreateService().EnsureServerHostnameAsync(false));
        Assert.Equal("deleted-domain", f.CurrentDomainId);
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("update "));
        Assert.True(await f.CreateService().EnsureServerHostnameAsync());
        Assert.Equal("valid-domain", f.CurrentDomainId);
        Assert.Equal(Fixture.Target, f.CurrentHost);
        Assert.Equal(selectedCertificate, f.CurrentCertificate);
        Assert.False(await f.CreateService().EnsureServerHostnameAsync(false));
    }

    [Fact]
    public async Task ExistingWindowsCertificateStillPersistsSuccessfulIdentityContactAndReadsItBack()
    {
        using var f = new Fixture();
        await f.Store.WriteAsync(AcmeContactSettings.Path, new AcmeContactSettings { Email = "old@example.test" });
        f.InstallStoreCertificate();
        Assert.True((await f.Service.SetServerIdentityAsync(Request(contact: "new@example.test"))).Applied);
        // Second selection reaches ApplyStoreCertificateAsync's already-active early return.
        var result = await f.CreateService().SetServerIdentityAsync(Request(contact: "updated@example.test"));
        Assert.True(result.Applied);
        Assert.Equal("updated@example.test", result.ContactEmail);
        Assert.Equal("updated@example.test", (await f.Store.ReadAsync<AcmeContactSettings?>(AcmeContactSettings.Path, null))!.Email);
        Assert.Equal("updated@example.test", (await f.CreateService().GetServerIdentityAsync()).ContactEmail);
        Assert.Equal(0, f.IssuerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinnedDomainInventoryFailureCannotLookHealthyOrAuthorizeAMutation(bool apply)
    {
        using var f = new Fixture();
        Assert.True((await f.Service.SetServerIdentityAsync(Request())).Applied);
        f.Failure = "domain-query";
        f.Commands.Clear();
        await Assert.ThrowsAsync<IOException>(() => f.CreateService().EnsureServerHostnameAsync(apply));
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("update "));
        Assert.Equal(Fixture.Target, f.CurrentHost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialIdentityActivationAndRollbackRepairStaleDefaultDomain(bool greetingFails)
    {
        using var f = new Fixture { CurrentDomainId = "deleted-domain", Failure = greetingFails ? "greeting" : null };
        var result = await f.Service.SetServerIdentityAsync(Request());
        Assert.Equal(!greetingFails, result.Applied);
        Assert.Equal("valid-domain", f.CurrentDomainId);
        Assert.Equal(greetingFails ? Fixture.Previous : Fixture.Target, f.CurrentHost);
        Assert.Equal(greetingFails ? "previous-cert" : Fixture.Target, f.CurrentCertificate);
        if (greetingFails) Assert.Contains(result.Steps, step => step.Key == "rollback" && step.Status == "ok");
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("certificate-reload")]
    [InlineData("dns")]
    public async Task PreparationFailureDoesNotSwitchPreviousPin(string failure)
    {
        using var f = new Fixture();
        await f.Store.WriteAsync(AcmeContactSettings.Path, new AcmeContactSettings { Email = "previous@example.test" });
        await f.Store.WriteAsync(MailService.ServerIdentityPath, new MailService.ServerIdentityPin
            { Domain = "previous.test", HostName = Fixture.Previous });
        var saved = await File.ReadAllBytesAsync(f.Store.ResolvePath(MailService.ServerIdentityPath));
        f.Failure = failure;
        var result = await f.Service.SetServerIdentityAsync(Request(contact: "new@example.test"));
        Assert.False(result.Applied);
        Assert.Equal(Fixture.Previous, result.ConfiguredHostName);
        Assert.Equal(Fixture.Previous, f.CurrentHost);
        Assert.Equal("previous-cert", f.CurrentCertificate);
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("update SystemSettings"));
        Assert.Equal(saved, await File.ReadAllBytesAsync(f.Store.ResolvePath(MailService.ServerIdentityPath)));
        Assert.Equal("previous@example.test", (await f.Store.ReadAsync<AcmeContactSettings?>(AcmeContactSettings.Path, null))!.Email);
    }

    [Fact]
    public async Task AnotherDomainsCertificateIssuanceCannotReplacePinnedHostname()
    {
        using var f = new Fixture();
        Assert.True((await f.Service.SetServerIdentityAsync(Request())).Applied);
        var selected = f.CurrentCertificate;
        await f.Service.IssueCertificateAsync(new IssueMailCertificateRequest
            { Domain = "another.test", HostName = "mail.another.test" });
        Assert.Equal(Fixture.Target, f.CurrentHost);
        Assert.Equal(selected, f.CurrentCertificate);
        Assert.False(await f.CreateService().EnsureServerHostnameAsync());
        Assert.True((await f.Service.GetServerIdentityAsync()).Pinned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedDnsWritesOnlyTargetAAndWaitsForPublicPublication(bool publicationFails)
    {
        using var f = new Fixture { ManagedDns = true, Failure = publicationFails ? "publication" : null };
        var result = await f.Service.SetServerIdentityAsync(Request());
        Assert.Equal(1, f.DnsWrites);
        Assert.Equal(!publicationFails, result.Applied);
        Assert.Contains(result.Steps, s => s.Key == "dns-preparation" && s.Status == "ok");
        if (publicationFails)
        {
            Assert.Equal(Fixture.Previous, f.CurrentHost);
            Assert.DoesNotContain(f.Commands, c => c.StartsWith("create Certificate"));
        }
    }

    [Fact]
    public async Task GreetingFailureRestoresPreviousActiveIdentityAndPin()
    {
        using var f = new Fixture();
        await f.Store.WriteAsync(AcmeContactSettings.Path, new AcmeContactSettings { Email = "previous@example.test" });
        await f.Store.WriteAsync(MailService.ServerIdentityPath, new MailService.ServerIdentityPin
            { Domain = "previous.test", HostName = Fixture.Previous });
        var saved = await File.ReadAllBytesAsync(f.Store.ResolvePath(MailService.ServerIdentityPath));
        f.Failure = "greeting";
        var result = await f.Service.SetServerIdentityAsync(Request(contact: "new@example.test"));
        Assert.False(result.Applied);
        Assert.Contains(result.Steps, s => s.Key == "rollback" && s.Status == "ok");
        Assert.Equal(Fixture.Previous, f.CurrentHost);
        Assert.Equal("previous-cert", f.CurrentCertificate);
        Assert.Equal(saved, await File.ReadAllBytesAsync(f.Store.ResolvePath(MailService.ServerIdentityPath)));
        Assert.Equal("previous@example.test", (await f.Store.ReadAsync<AcmeContactSettings?>(AcmeContactSettings.Path, null))!.Email);
    }

    [Fact]
    public async Task GetIsReadOnlyAndUnpinnedLegacyHostIsNotClaimedAsAnExplicitPreference()
    {
        using var f = new Fixture();
        var status = await f.Service.GetServerIdentityAsync();
        Assert.False(status.Pinned);
        Assert.False(status.Applied);
        Assert.Equal(Fixture.Previous, status.CurrentHostName);
        Assert.Equal(["get SystemSettings --json"], f.Commands);
        Assert.False(File.Exists(f.Store.ResolvePath(MailService.ServerIdentityPath)));
    }

    [Fact]
    public async Task MalformedSettingsCannotAuthorizeAnyIdentityMutation()
    {
        using var f = new Fixture { Failure = "malformed-settings" };
        await Assert.ThrowsAsync<AppException>(() => f.Service.SetServerIdentityAsync(Request()));
        Assert.Equal(["get SystemSettings --json"], f.Commands);
        Assert.False(File.Exists(f.Store.ResolvePath(MailService.ServerIdentityPath)));
    }

    [Fact]
    public async Task LegacyProviderDefaultIsUnsupportedWithoutProviderInvocation()
    {
        IMailService service = new SahtePostaServisi();
        Assert.False((await service.GetServerIdentityAsync()).Supported);
        var result = await service.SetServerIdentityAsync(Request());
        Assert.False(result.Supported);
        Assert.False(result.Applied);
        Assert.Contains(result.Steps, step => step.Status == "unsupported");
    }

    [Fact]
    public void BothEndpointsRequireAdministrator()
    {
        var controller = typeof(MailServerIdentityController);
        Assert.Equal("Administrator", controller.GetCustomAttribute<AuthorizeAttribute>()?.Roles);
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    private static SetMailServerIdentityRequest Request(string host = Fixture.Target, string contact = "") => new()
        { Domain = "example.test", HostName = host, ContactEmail = contact };

    private sealed class Fixture : IDisposable
    {
        internal const string Target = "mail.example.test", Previous = "mail.previous.test";
        private readonly BulutAlani area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly Dictionary<string, object> certificates = [];
        private readonly string cliDirectory;
        internal readonly List<string> Commands = [];
        internal string CurrentHost = Previous, CurrentCertificate = "previous-cert";
        internal string CurrentDomainId = "valid-domain";
        internal MailCertificateMaterial? StoreMaterial;
        internal int IssuerCalls;
        internal string? Failure;
        internal bool ManagedDns, DnsPrepared;
        internal int DnsWrites;
        internal MailService Service { get; }
        internal JsonFileStore Store => area.DosyaDeposu;

        internal Fixture()
        {
            connection.Open();
            cliDirectory = Store.EnsureDirectory("fake-cli");
            File.WriteAllText(Path.Combine(cliDirectory, "stalwart-cli.exe"), "not executable");
            using var seed = NewContext();
            seed.Database.EnsureCreated();
            seed.InstalledComponents.Add(new InstalledComponent
            { ComponentId = "stalwart-cli", InstallDir = cliDirectory, Status = ComponentInstallStatus.Installed, Type = ComponentType.Zip });
            seed.SaveChanges();
            Store.WriteAsync("config/mail.json", new { adminPasswordProtected = SahteKoruyucu.Onek + "fixture-password" }).GetAwaiter().GetResult();
            Service = CreateService();
        }
        private AdaPanelDbContext NewContext() => new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        internal MailService CreateService()
        {
            var db = NewContext(); contexts.Add(db);
            var protector = new SahteKoruyucu();
            var sites = IdentityProxy.Make<IIisSiteService>((name, _) => name == "GetSitesAsync"
                ? Task.FromResult<IReadOnlyCollection<SiteDto>>([new() { Name = "example.test" }]) : throw new NotSupportedException(name));
            var dns = IdentityProxy.Make<IDnsService>((name, args) =>
            {
                if (name == "GetZoneAsync") return Task.FromResult(new SiteDnsZoneDto
                {
                    ZoneName = "example.test", IsWindowsDnsAvailable = ManagedDns, IsWindowsZonePresent = ManagedDns,
                    WindowsZoneType = ManagedDns ? "Primary" : "",
                    Records = [new() { Id = "target-a", Host = "mail", Type = "A", Value = "185.7.242.226" },
                        new() { Id = "mx", Host = "@", Type = "MX", Value = Previous }]
                });
                if (name == "UpsertRecordAsync")
                {
                    var record = Assert.IsType<UpsertDnsRecordRequest>(args![1]);
                    Assert.Equal("target-a", record.Id); Assert.Equal("A", record.Type);
                    Assert.Equal("mail", record.Host); Assert.Equal("185.7.242.225", record.Value);
                    DnsPrepared = true; DnsWrites++;
                    return Task.FromResult(new SiteDnsZoneDto());
                }
                if (name == "GetPublishedRecordKeysAsync") return Task.FromResult<IReadOnlyCollection<string>?>(["A|mail", "MX|@"]);
                throw new NotSupportedException("Unexpected DNS mutation: " + name);
            });
            return new MailService(Store, protector, new SabitTercihler { Adres = "185.7.242.225" }, sites, dns, db,
                new StalwartRuntime(Store, protector, db, new SahteIstemciFabrikasi(new SahteBulut())), NullLogger<MailService>.Instance)
            {
                CertificateCommandRunner = Execute,
                IdentityDnsQuery = (host, type, _) => Task.FromResult(type == "NS"
                    ? (ManagedDns ? new[] { "ns1.example.test" } : [])
                    : Failure == "dns" || (ManagedDns && host == Target && (!DnsPrepared || Failure == "publication"))
                        ? [] : new[] { "185.7.242.225" }),
                IdentityGreetingProbe = (host, _) => Task.FromResult(host == CurrentHost && !(Failure == "greeting" && host == Target)),
                StoreCertificateLookup = host => host == Target ? StoreMaterial : null,
                ValidateCertificateMaterial = (_, pem, _) => pem,
                CertificateIssuer = (_, host, _, _, directory, _) =>
                {
                    IssuerCalls++;
                    if (Failure == "issuer") return Task.FromResult(false);
                    using var key = RSA.Create(2048);
                    var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host); request.CertificateExtensions.Add(san.Build());
                    using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
                    File.WriteAllText(Path.Combine(directory, "host-crt.pem"), cert.ExportCertificatePem());
                    File.WriteAllText(Path.Combine(directory, "host-key.pem"), key.ExportPkcs8PrivateKeyPem());
                    return Task.FromResult(true);
                },
                TlsProbe = (_, port, _, _) => Task.FromResult(new MailTlsEndpointDto { Port = port, Status = "ok" })
            };
        }
        internal void InstallStoreCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + Target, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(Target); request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            StoreMaterial = new(cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), cert.Thumbprint);
        }
        private Task<CommandResult> Execute(string cli, string command, string? json, IReadOnlyDictionary<string, string> env, CancellationToken token)
        {
            Commands.Add(command);
            if (Failure == "domain-query" && command == "query domain --json")
                return Task.FromResult(new CommandResult { ExitCode = 1 });
            if (Failure == "malformed-settings" && command == "get SystemSettings --json")
                return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "{\"defaultHostname\":42,\"defaultCertificateId\":[]}" });
            object output = new { };
            if (command == "get SystemSettings --json") output = new { defaultHostname = CurrentHost, defaultCertificateId = CurrentCertificate, defaultDomainId = CurrentDomainId };
            else if (command == "query domain --json") output = new[] { new { id = "valid-domain", name = "unrelated-inventory-first.test" } };
            else if (command == "query Certificate --json") output = certificates.Values;
            else if (command.StartsWith("update SystemSettings"))
            {
                using var doc = JsonDocument.Parse(json!);
                if (doc.RootElement.TryGetProperty("defaultDomainId", out var domain)) CurrentDomainId = domain.GetString()!;
                if (CurrentDomainId != "valid-domain") return Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "defaultDomainId: required" });
                if (doc.RootElement.TryGetProperty("defaultHostname", out var host)) CurrentHost = host.GetString()!;
                if (doc.RootElement.TryGetProperty("defaultCertificateId", out var cert)) CurrentCertificate = cert.GetString()!;
            }
            else if (command == "create Certificate" || command.StartsWith("update Certificate "))
            {
                using var payload = JsonDocument.Parse(json!);
                using var leaf = X509Certificate2.CreateFromPem(payload.RootElement.GetProperty("certificate").GetProperty("value").GetString()!,
                    payload.RootElement.GetProperty("privateKey").GetProperty("secret").GetString()!);
                var host = leaf.GetNameInfo(X509NameType.DnsName, false);
                certificates[host] = new { id = host, subjectAlternativeNames = new Dictionary<string, bool> { [host] = true } };
            }
            else if (command == "create Action")
            {
                if (Failure == "certificate-reload" && json!.Contains("ReloadTlsCertificates"))
                    return Task.FromResult(new CommandResult { ExitCode = 1 });
            }
            else throw new InvalidOperationException(command);
            return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(output) });
        }
        public void Dispose()
        {
            foreach (var db in contexts) db.Dispose();
            connection.Dispose(); area.Dispose();
        }
    }

    public class IdentityProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler = null!;
        public static T Make<T>(Func<string, object?[]?, object?> handler) where T : class
        {
            var proxy = Create<T, IdentityProxy>();
            ((IdentityProxy)(object)proxy).Handler = handler;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args);
    }
}
