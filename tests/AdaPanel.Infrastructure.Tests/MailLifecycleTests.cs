using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dalga 3 (posta): kutu askısı, alias güncelleme, kutu başına yönlendirme ve DKIM rotasyonu.
/// Stalwart CLI'si ve DNS/IIS bellek içi sahtedir; gerçek süreç/servis çalışmaz.
/// </summary>
public sealed class MailLifecycleTests
{
    private const string Address = "alice@example.test";

    [Fact]
    public async Task Hesap_askisi_izinleri_kapatir_GERI_OKUR_ve_yeniden_acar()
    {
        using var f = new Fixture();
        var suspended = await f.Service.SetAccountActiveAsync(new() { Address = Address, Active = false });
        Assert.False(Assert.Single(suspended.Accounts).Active);
        Assert.Equal(new[] { "authenticate", "emailReceive", "emailSend" },
            f.Account["permissions"]!["disabledPermissions"]!.AsObject().Select(item => item.Key));

        var reopened = await f.Service.SetAccountActiveAsync(new() { Address = Address, Active = true });
        Assert.True(Assert.Single(reopened.Accounts).Active);
    }

    [Fact]
    public async Task Sunucu_yazmayi_yoksayarsa_askiya_alma_BASARILI_denmez()
    {
        using var f = new Fixture { IgnoreUpdates = true };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SetAccountActiveAsync(new() { Address = Address, Active = false }));
        Assert.Equal("mail.account_active_not_applied", error.MessageKey);
    }

    [Fact]
    public async Task Olmayan_kutu_askiya_alinamaz()
    {
        using var f = new Fixture();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SetAccountActiveAsync(new() { Address = "yok@example.test", Active = false }));
        Assert.Equal("mail.account_not_found", error.MessageKey);
    }

    [Fact]
    public async Task Yonlendirme_kopyali_ve_kopyasiz_yazilir_okunur_ve_kaldirilir()
    {
        using var f = new Fixture();
        var saved = await f.Service.SetForwardAsync(new() { Address = Address, Enabled = true, Target = "bob@other.test", KeepCopy = true });
        Assert.True(saved.Enabled);
        Assert.True(saved.KeepCopy);
        Assert.Contains("redirect :copy \"bob@other.test\";", f.Scripts.Single().Value["contents"]!.GetValue<string>());

        var noCopy = await f.Service.SetForwardAsync(new() { Address = Address, Enabled = true, Target = "bob@other.test", KeepCopy = false });
        Assert.False(noCopy.KeepCopy);
        Assert.DoesNotContain(":copy", f.Scripts.Single().Value["contents"]!.GetValue<string>());
        Assert.Single(f.Scripts); // güncelleme: ikinci betik oluşmaz

        var read = await f.Service.GetForwardAsync(Address);
        Assert.Equal("bob@other.test", read.Target);

        var removed = await f.Service.SetForwardAsync(new() { Address = Address, Enabled = false });
        Assert.False(removed.Enabled);
        Assert.Empty(f.Scripts);
    }

    [Theory]
    [InlineData("bozuk")]
    [InlineData("a@b.c\"; discard; #")]
    [InlineData("a b@c.d")]
    public async Task Gecersiz_yonlendirme_adresi_reddedilir_ve_hicbir_sey_yazilmaz(string target)
    {
        using var f = new Fixture();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SetForwardAsync(new() { Address = Address, Enabled = true, Target = target }));
        Assert.Equal("mail.forward_invalid_target", error.MessageKey);
        Assert.Empty(f.Scripts);
    }

    [Fact]
    public async Task Kutu_kendisine_yonlendirilemez()
    {
        using var f = new Fixture();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SetForwardAsync(new() { Address = Address, Enabled = true, Target = Address.ToUpperInvariant() }));
        Assert.Equal("mail.forward_to_self", error.MessageKey);
    }

    [Fact]
    public async Task Alias_etkin_bayragi_yerinde_degisir_ve_hedef_baska_kutuya_tasinir()
    {
        using var f = new Fixture();
        f.AddAccount("acc-2", "bob@example.test");
        f.Account["aliases"] = new JsonObject { ["0"] = new JsonObject { ["name"] = "info", ["domainId"] = "domain-id", ["enabled"] = true } };

        var off = await f.Service.UpdateAliasAsync(new() { Address = "info@example.test", Active = false });
        Assert.False(Assert.Single(off.Aliases).Active);

        var moved = await f.Service.UpdateAliasAsync(new() { Address = "info@example.test", Target = "bob@example.test" });
        var alias = Assert.Single(moved.Aliases);
        Assert.Equal("bob@example.test", alias.Target);
        Assert.False(alias.Active); // taşırken etkin durumu korunur
        Assert.Empty(f.Account["aliases"]!.AsObject());
    }

    [Fact]
    public async Task DKIM_rotasyonu_yeni_anahtari_yayinlar_gecirir_eskiyi_bekleme_suresi_sonunda_kaldirir()
    {
        using var f = new Fixture();
        var result = await f.Service.RotateDkimAsync(new() { Domain = "example.test" });

        Assert.Equal("selector", result.OldSelector);
        Assert.StartsWith("r", result.NewSelector);
        Assert.Contains(f.PublishedRecords, r => r.Host == result.NewSelector + "._domainkey" && r.Value.Contains("p=NEWPUBLICKEY"));
        Assert.Equal("active", f.Dkim.Values.Single(d => d["selector"]!.GetValue<string>() == result.NewSelector)["stage"]!.GetValue<string>());
        Assert.Equal("retiring", f.Dkim["dkim-old"]["stage"]!.GetValue<string>());
        Assert.True(f.Dkim.ContainsKey("dkim-old")); // bekleme süresinde eski anahtar KALIR

        // Süre dolmadan temizlik hiçbir şey kaldırmaz.
        Assert.Equal(0, await f.Service.FinalizeDkimRotationsAsync());
        Assert.True(f.Dkim.ContainsKey("dkim-old"));

        // Bekleme süresi dolunca eski anahtar VE DNS kaydı kaldırılır.
        await f.Store.WriteAsync(MailService.DkimRotationStorePath, new List<MailService.DkimRotationEntry>
        {
            new() { Domain = "example.test", OldSelector = "selector", NewSelector = result.NewSelector, RetireAfterUtc = DateTime.UtcNow.AddMinutes(-1) }
        });
        Assert.Equal(1, await f.Service.FinalizeDkimRotationsAsync());
        Assert.False(f.Dkim.ContainsKey("dkim-old"));
        Assert.Contains("selector._domainkey", f.DeletedDnsHosts);
    }

    [Fact]
    public async Task DNS_yayinlanamazsa_gecis_YAPILMAZ_eski_anahtar_aktif_kalir_yeni_anahtar_temizlenir()
    {
        using var f = new Fixture { DnsAvailable = false };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.RotateDkimAsync(new() { Domain = "example.test" }));
        Assert.Equal("mail.dkim_rotation_dns_not_published", error.MessageKey);
        Assert.Equal("active", f.Dkim["dkim-old"]["stage"]!.GetValue<string>());
        Assert.Single(f.Dkim); // beklemedeki yeni anahtar silindi
    }

    [Fact]
    public async Task Gecis_basarisizsa_eski_anahtar_aktif_kalir()
    {
        using var f = new Fixture { FailActivate = true };
        await Assert.ThrowsAsync<AppException>(() => f.Service.RotateDkimAsync(new() { Domain = "example.test" }));
        Assert.Equal("active", f.Dkim["dkim-old"]["stage"]!.GetValue<string>());
        Assert.Single(f.Dkim);
    }

    [Fact]
    public async Task Yeni_anahtar_acik_anahtar_uretmezse_dogrulanamaz_ve_eski_kalir()
    {
        using var f = new Fixture { NoPublicKey = true };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.RotateDkimAsync(new() { Domain = "example.test" }));
        Assert.Equal("mail.dkim_rotation_unverified", error.MessageKey);
        Assert.Equal("active", f.Dkim["dkim-old"]["stage"]!.GetValue<string>());
    }

    [Fact]
    public void MailEnable_icin_yeni_islemler_VARSAYILAN_olarak_desteklenmiyor_der()
    {
        var type = typeof(IMailService);
        foreach (var name in new[] { nameof(IMailService.SetAccountActiveAsync), nameof(IMailService.UpdateAliasAsync), nameof(IMailService.RotateDkimAsync) })
        {
            var method = type.GetMethod(name)!;
            Assert.False(method.IsAbstract, name + " varsayılan gövdeye sahip olmalı (sessiz başarı değil, NotSupported)");
        }
    }

    // ------------------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        private readonly AdaPanelDbContext db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite("Data Source=:memory:").Options);
        internal bool IgnoreUpdates, DnsAvailable = true, FailActivate, NoPublicKey;
        internal readonly Dictionary<string, JsonObject> Accounts = [];
        internal readonly Dictionary<string, JsonObject> Scripts = [];
        internal readonly Dictionary<string, JsonObject> Dkim = [];
        internal readonly List<string> DeletedDnsHosts = [];
        internal IReadOnlyCollection<UpsertDnsRecordRequest> PublishedRecords = [];
        private readonly List<SiteDnsRecordDto> zoneRecords = [];
        internal JsonObject Account => Accounts["acc-1"];
        internal JsonFileStore Store => area.DosyaDeposu;
        internal MailService Service { get; }

        internal void AddAccount(string id, string address) => Accounts[id] = new JsonObject
        {
            ["id"] = id, ["@type"] = "User", ["emailAddress"] = address, ["domainId"] = "domain-id",
            ["permissions"] = new JsonObject { ["@type"] = "Inherit" }, ["aliases"] = new JsonObject()
        };

        internal Fixture()
        {
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            var cli = area.DosyaDeposu.EnsureDirectory("fake-cli");
            File.WriteAllText(Path.Combine(cli, "stalwart-cli.exe"), "fixture only; never executed");
            db.InstalledComponents.Add(new InstalledComponent
            { ComponentId = "stalwart-cli", InstallDir = cli, Status = ComponentInstallStatus.Installed, Type = ComponentType.Zip });
            db.SaveChanges();
            area.DosyaDeposu.WriteAsync("config/mail.json", new { adminPasswordProtected = SahteKoruyucu.Onek + "fixture-password" }).GetAwaiter().GetResult();
            AddAccount("acc-1", Address);
            zoneRecords.Add(new SiteDnsRecordDto { Id = "rec-selector._domainkey", Type = "TXT", Host = "selector._domainkey", Value = "v=DKIM1; k=rsa; p=OLDPUBLICKEY" });
            Dkim["dkim-old"] = new JsonObject { ["id"] = "dkim-old", ["domainId"] = "domain-id", ["stage"] = "active", ["selector"] = "selector", ["publicKey"] = "OLDPUBLICKEY", ["@type"] = "Dkim1RsaSha256" };

            var preferences = MailServerIdentityTests.IdentityProxy.Make<IPanelPreferencesService>((method, _) => method == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto { ServerAddress = "192.0.2.30" })
                : throw new InvalidOperationException(method));
            var iis = MailServerIdentityTests.IdentityProxy.Make<IIisSiteService>((method, _) => method == "GetSitesAsync"
                ? Task.FromResult<IReadOnlyCollection<SiteDto>>([new() { Name = "web-example.test", Bindings = [new() { Protocol = "http", Port = 80, HostName = "example.test" }] }])
                : throw new InvalidOperationException(method));
            var dns = MailServerIdentityTests.IdentityProxy.Make<IDnsService>((method, args) =>
            {
                switch (method)
                {
                    case "GetZoneAsync":
                        return Task.FromResult(new SiteDnsZoneDto { SiteName = "web-example.test", ZoneName = "example.test", Records = zoneRecords.ToArray() });
                    case "UpsertRecordsAsync":
                        PublishedRecords = ((IEnumerable<UpsertDnsRecordRequest>)args![1]!).ToArray();
                        foreach (var request in PublishedRecords)
                            zoneRecords.Add(new SiteDnsRecordDto { Id = "rec-" + request.Host, Type = request.Type, Host = request.Host, Value = request.Value });
                        return Task.FromResult(new SiteDnsZoneDto { SiteName = "web-example.test", ZoneName = "example.test", IsWindowsZonePresent = DnsAvailable, IsWindowsDnsAvailable = DnsAvailable });
                    case "DeleteRecordAsync":
                        var id = (string)args![1]!;
                        DeletedDnsHosts.Add(id["rec-".Length..]);
                        zoneRecords.RemoveAll(item => item.Id == id);
                        return Task.FromResult(new SiteDnsZoneDto { SiteName = "web-example.test", ZoneName = "example.test" });
                    default: throw new InvalidOperationException(method);
                }
            });
            var protector = new SahteKoruyucu();
            Service = new(area.DosyaDeposu, protector, preferences, iis, dns, db,
                new StalwartRuntime(area.DosyaDeposu, protector, db, new SahteIstemciFabrikasi(new SahteBulut())), NullLogger<MailService>.Instance)
            {
                CommandRunner = Run
            };
        }

        private Task<CommandResult> Run(string executable, string command, IReadOnlyDictionary<string, string> environment, CancellationToken token)
        {
            string Ok(object data) => JsonSerializer.Serialize(data);
            CommandResult Good(string output) => new() { ExitCode = 0, StandardOutput = output };
            var bad = new CommandResult { ExitCode = 1, StandardError = "rejected" };
            Task<CommandResult> R(CommandResult result) => Task.FromResult(result);

            JsonObject Payload()
            {
                var file = Regex.Match(command, " --file (.+)$").Groups[1].Value.Trim('"');
                return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            }

            switch (command)
            {
                case "query domain --json": return R(Good("""[{"id":"domain-id","name":"example.test"}]"""));
                case "get domain domain-id --json": return R(Good("""{"id":"domain-id","name":"example.test"}"""));
                case "query account --json": return R(Good(Ok(Accounts.Values.Select(a => new { id = a["id"]!.GetValue<string>(), emailAddress = a["emailAddress"]!.GetValue<string>(), type = "User" }).ToArray())
                    .Replace("\"type\"", "\"@type\"")));
                case "query SieveUserScript --json": return R(Good(Scripts.Count == 0 ? "[]" : "[" + string.Join(",", Scripts.Values.Select(s => s.ToJsonString())) + "]"));
                case "query DkimSignature --json": return R(Good("[" + string.Join(",", Dkim.Values.Select(s => s.ToJsonString())) + "]"));
            }

            var get = Regex.Match(command, @"^get (\w+) (\S+) --json$");
            if (get.Success)
            {
                var (type, id) = (get.Groups[1].Value, get.Groups[2].Value);
                JsonObject? found = type switch
                {
                    "account" => Accounts.GetValueOrDefault(id),
                    "SieveUserScript" => Scripts.GetValueOrDefault(id),
                    "DkimSignature" => Dkim.GetValueOrDefault(id),
                    _ => null
                };
                return R(found is null ? bad : Good(found.ToJsonString()));
            }

            var update = Regex.Match(command, @"^update account (\S+) --(file|field) (.+)$");
            if (update.Success)
            {
                if (IgnoreUpdates) return R(Good("{}"));
                var account = Accounts[update.Groups[1].Value];
                if (update.Groups[2].Value == "file")
                {
                    foreach (var field in Payload()) account[field.Key] = field.Value?.DeepClone();
                }
                else
                {
                    var arg = update.Groups[3].Value.Trim('"').Replace("\\\"", "\"");
                    var split = arg.IndexOf('=');
                    account[arg[..split]] = JsonNode.Parse(arg[(split + 1)..]);
                }

                return R(Good("{}"));
            }

            if (command.StartsWith("create SieveUserScript ", StringComparison.Ordinal) || command.StartsWith("update SieveUserScript ", StringComparison.Ordinal))
            {
                var payload = Payload();
                var id = command.StartsWith("create", StringComparison.Ordinal) ? "script-" + (Scripts.Count + 1) : command.Split(' ')[2];
                payload["id"] = id;
                Scripts[id] = payload;
                return R(Good("{}"));
            }

            var delete = Regex.Match(command, @"^delete (\w+) --ids (\S+)$");
            if (delete.Success)
            {
                if (delete.Groups[1].Value == "SieveUserScript") Scripts.Remove(delete.Groups[2].Value);
                if (delete.Groups[1].Value == "DkimSignature") Dkim.Remove(delete.Groups[2].Value);
                return R(Good("{}"));
            }

            if (command.StartsWith("create DkimSignature/", StringComparison.Ordinal))
            {
                var payload = Payload();
                payload["id"] = "dkim-new";
                payload["@type"] = command.Split(' ')[1]["DkimSignature/".Length..];
                if (!NoPublicKey) payload["publicKey"] = "NEWPUBLICKEY";
                Dkim["dkim-new"] = payload;
                return R(Good("{}"));
            }

            var stage = Regex.Match(command, @"^update DkimSignature (\S+) --field stage=(\w+)$");
            if (stage.Success)
            {
                if (FailActivate && stage.Groups[2].Value == "active") return R(bad);
                Dkim[stage.Groups[1].Value]["stage"] = stage.Groups[2].Value;
                return R(Good("{}"));
            }

            return R(Good("[]"));
        }

        public void Dispose() { db.Dispose(); area.Dispose(); }
    }
}
