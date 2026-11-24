using System.Text.Json;
using AdaPanel.Core.Common;
using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AdaPanel.Infrastructure.Ssl.SslBindingRepairService;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslBindingRepairTests
{
    const string Old = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", New = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    static readonly SslRepairTarget Target = new("site", "example.test", 443);

    [Fact]
    public async Task Preview_is_read_only_and_exposes_both_certificates_for_explicit_selection()
    {
        using var f = await Fixture.Create();
        var plan = await f.Service.PlanAsync(Target, "admin", f.Authorize, default);
        var option = Assert.Single(plan.Options);
        Assert.Equal(Old, option.Current!.Thumbprint); Assert.Equal(New, option.Candidate.Thumbprint);
        Assert.NotEmpty(option.PlanToken); Assert.Equal(0, f.Writes); Assert.Equal(0, f.Probes);
        Assert.Equal(New, (await f.Read())[0].Thumbprint);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Controller_records_verified_or_unverified_repair_for_the_measured_site_owner_not_the_admin(bool tls, bool owned)
    {
        using var f = await Fixture.Create(); f.Tls = tls;
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var admin = Guid.NewGuid(); var tenant = Guid.NewGuid();
        db.Users.Add(new PanelUser { Id=admin, UserName="repair-admin", Email="admin@example.test", Role=UserRole.Administrator, PasswordHash="fixture" });
        await db.SaveChangesAsync();
        var scope = DispatchProxy.Create<ICustomerScopeService,AuditProxy>();
        var ownerReads = 0;
        ((AuditProxy)(object)scope).Call = (method,args) =>
        {
            Assert.Equal(nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync),method.Name);
            Assert.Equal("site",args[0]); ownerReads++;
            return Task.FromResult<Guid?>(owned ? tenant : null);
        };
        var audit = DispatchProxy.Create<IAuditService,AuditProxy>();
        var entries = new List<object?[]>();
        ((AuditProxy)(object)audit).Call = (method,args) =>
        {
            Assert.Equal(nameof(IAuditService.WriteForSubscriptionAsync),method.Name);
            entries.Add(args); return Task.CompletedTask;
        };
        var controller = new SslRepairController(f.Service,db,scope,audit)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier,admin.ToString()),new Claim(ClaimTypes.Role,"Administrator")],"fixture"))
            } }
        };
        var preview = await controller.PlanAsync(Target,default);
        var plan = Assert.IsType<ApiResponse<SslRepairPlan>>(Assert.IsType<OkObjectResult>(preview.Result).Value).Data!;
        Assert.Empty(entries); Assert.Equal(0,f.Writes); Assert.Equal(0,ownerReads);
        var token = Assert.Single(plan.Options).PlanToken;
        await controller.ApplyAsync(new SslRepairApplyRequest(token),default);
        Assert.Equal(1,f.Writes); Assert.Equal(1,ownerReads);
        var entry = Assert.Single(entries);
        Assert.Equal(tls ? "ssl.binding_repaired" : "ssl.binding_repair_unverified",entry[0]);
        Assert.Equal("ssl:site",entry[1]); Assert.Equal(admin,entry[3]);
        Assert.Equal(owned ? (Guid?)tenant : null,(Guid?)entry[4]);
        Assert.NotEqual(admin,(Guid?)entry[4]);
        Assert.Contains($"tlsVerified={tls}",Assert.IsType<string>(entry[2]));
    }

    public class AuditProxy : DispatchProxy
    {
        public Func<MethodInfo,object?[],object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!,args!);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tampered")]
    [InlineData("record")]
    [InlineData("binding")]
    [InlineData("site")]
    [InlineData("revoked")]
    [InlineData("candidate")]
    public async Task Stale_or_unauthorized_plans_never_mutate(string change)
    {
        using var f = await Fixture.Create();
        var plan = Assert.Single((await f.Service.PlanAsync(Target, "admin", f.Authorize, default)).Options);
        if (change == "record") await f.Files.WriteAsync("config/ssl.json", new[] { Record(Old) });
        if (change == "binding") f.Bindings[0] = f.Bindings[0] with { Thumbprint = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC" };
        if (change == "site") f.Sites.Site = new SiteDto { Id = 8, Name = "site", PhysicalPath = "C:\\replacement", ApplicationPoolName = "pool", ApplicationRootPath = "/" };
        if (change == "revoked") f.Revoked = true;
        if (change == "candidate") f.CandidateValid = false;
        await Assert.ThrowsAsync<AppException>(() => f.Service.ApplyAsync(change == "tampered" ? plan.PlanToken+"tampered" : plan.PlanToken,
            change == "actor" ? "another" : "admin", f.Authorize, default));
        Assert.Equal(0, f.Writes); Assert.Equal(0, f.Probes);
    }

    [Theory]
    [InlineData("ccs")]
    [InlineData("non-sni")]
    [InlineData("shared")]
    [InlineData("no-rollback")]
    [InlineData("invalid-candidate")]
    public async Task Unsafe_targets_have_no_apply_token(string reason)
    {
        using var f = await Fixture.Create();
        if (reason == "ccs") f.Bindings[0] = f.Bindings[0] with { Flags = 3 };
        if (reason == "non-sni") f.Bindings[0] = f.Bindings[0] with { Flags = 0 };
        if (reason == "shared") f.Bindings.Add(f.Bindings[0] with { SiteName = "other", Ip = "192.0.2.8", Information = "192.0.2.8:443:example.test" });
        if (reason == "no-rollback") f.HasOldKey = false;
        if (reason == "invalid-candidate") f.CandidateValid = false;
        var option = Assert.Single((await f.Service.PlanAsync(Target, "admin", f.Authorize, default)).Options);
        Assert.Empty(option.PlanToken); Assert.NotEmpty(option.BlockedReason); Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task Wildcard_preview_uses_concrete_covered_hosts_and_excludes_default_and_other_hosts()
    {
        using var f = await Fixture.Create();
        await f.Files.WriteAsync("config/ssl.json", new[] { Record(New, "*.example.test") });
        f.Bindings.Add(f.Bindings[0] with { HostName = "unrelated.test", Information = "*:443:unrelated.test" });
        f.Bindings.Add(f.Bindings[0] with { HostName = "", Information = "*:443:" });
        var plan = await f.Service.PlanAsync(Target with { HostName = "*.example.test" }, "admin", f.Authorize, default);
        Assert.Equal("example.test", Assert.Single(plan.Options).HostName); Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_a_real_TLS_success_is_reported_as_verified_and_neighbors_are_preserved(bool tls)
    {
        using var f = await Fixture.Create(); f.Tls = tls;
        await f.Files.WriteAsync("config/ssl.json", new[] { Record(New), new SslCertificateDto { SiteName="other", HostName="other.test", Thumbprint=Old, Port=443 } });
        var plan = Assert.Single((await f.Service.PlanAsync(Target, "admin", f.Authorize, default)).Options);
        var result = await f.Service.ApplyAsync(plan.PlanToken, "admin", f.Authorize, default);
        Assert.True(result.BindingApplied); Assert.Equal(tls, result.TlsVerified);
        Assert.Equal(tls ? "ssl.repair.verified" : "ssl.repair.tls_failed", result.Detail);
        Assert.Equal(1, f.Writes); Assert.Equal(1, f.Probes);
        var records = await f.Read(); Assert.Equal(Old, records[1].Thumbprint); Assert.Null(records[1].IsCertificateBound);
        Assert.Contains("Approved binding changed", f.LastScript);
        Assert.Contains("Approved site changed", f.LastScript);
        Assert.DoesNotContain("--installation", f.LastScript);
        await Assert.ThrowsAsync<AppException>(() => f.Service.ApplyAsync(plan.PlanToken, "admin", f.Authorize, default));
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("output")]
    [InlineData("readback")]
    public async Task Failed_command_or_readback_never_claims_success(string mode)
    {
        using var f = await Fixture.Create(); f.Failure = mode;
        var plan = Assert.Single((await f.Service.PlanAsync(Target, "admin", f.Authorize, default)).Options);
        if (mode == "readback")
        {
            var result = await f.Service.ApplyAsync(plan.PlanToken, "admin", f.Authorize, default);
            Assert.False(result.BindingApplied); Assert.False(result.TlsVerified);
        }
        else await Assert.ThrowsAsync<AppException>(() => f.Service.ApplyAsync(plan.PlanToken, "admin", f.Authorize, default));
        Assert.Null((await f.Read())[0].IsCertificateBound);
    }

    [Fact]
    public async Task Revocation_immediately_before_mutation_and_cancellation_propagate()
    {
        using var f = await Fixture.Create();
        var plan = Assert.Single((await f.Service.PlanAsync(Target, "admin", f.Authorize, default)).Options);
        f.RevokeDuringInventory = true;
        await Assert.ThrowsAsync<AppException>(() => f.Service.ApplyAsync(plan.PlanToken, "admin", f.Authorize, default));
        Assert.Equal(0, f.Writes);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ApplyAsync(plan.PlanToken,"admin", f.Authorize, cts.Token));
    }

    static SslCertificateDto Record(string hash, string host="example.test") => new() { SiteName="site",HostName=host,Port=443,Thumbprint=hash };
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wildcard_and_apex_refresh_together_without_hiding_another_covered_binding(bool secondBinding)
    {
        using var f = await Fixture.Create();
        await f.Files.WriteAsync("config/ssl.json", new[] { Record(New), Record(New,"*.example.test") });
        if(secondBinding) f.Bindings.Add(f.Bindings[0] with { HostName="www.example.test",Information="*:443:www.example.test" });
        var plan = Assert.Single((await f.Service.PlanAsync(Target,"admin",f.Authorize,default)).Options);
        var result = await f.Service.ApplyAsync(plan.PlanToken,"admin",f.Authorize,default);
        Assert.True(result.TlsVerified);
        var records = await f.Read();
        Assert.True(records[0].IsCertificateBound);
        Assert.Equal(!secondBinding, records[1].IsCertificateBound);
        Assert.NotNull(records[1].BindingCheckedAtUtc);
    }

    [Fact]
    public async Task Actual_current_store_is_used_even_when_another_store_has_the_same_hash()
    {
        using var f = await Fixture.Create();
        f.Bindings[0] = f.Bindings[0] with { StoreName="WebHosting" };
        var plan = Assert.Single((await f.Service.PlanAsync(Target,"admin",f.Authorize,default)).Options);
        Assert.Empty(plan.BlockedReason); Assert.NotEmpty(plan.PlanToken);
        var result = await f.Service.ApplyAsync(plan.PlanToken,"admin",f.Authorize,default);
        Assert.True(result.TlsVerified);
        Assert.Contains("WebHosting", f.LastScript);
    }
    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanelSslRepair-"+Guid.NewGuid().ToString("N"));
        public JsonFileStore Files { get; }
        public FakeSites Sites { get; } = new();
        public List<BindingSnapshot> Bindings { get; } = [new("site","example.test","*:443:example.test","*",443,Old,"My",1)];
        public SslBindingRepairService Service { get; }
        public bool Revoked, RevokeDuringInventory, Tls=true, CandidateValid=true, HasOldKey=true;
        public int Writes, Probes;
        public string Failure="", LastScript="";
        Fixture()
        {
            Files = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot=root }));
            Service = new(Files, Sites, new ConcurrencyLock(), new EphemeralDataProtectionProvider()) {
                Command = (script,ct) => {
                    ct.ThrowIfCancellationRequested();
                    if(script == InventoryScript) {
                        if(RevokeDuringInventory) Revoked=true;
                        return Task.FromResult(new CommandResult { StandardOutput=JsonSerializer.Serialize(Bindings, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
                    }
                    Writes++; LastScript=script;
                    if(Failure != "readback") Bindings[0]=Bindings[0] with { Thumbprint=New, StoreName="My" };
                    return Task.FromResult(new CommandResult { ExitCode=Failure=="exit"?1:0, StandardOutput=Failure=="output"?"not OK":"OK" });
                },
                CertificateReader = (hash,host,store) => new(new(hash, "CN="+host, DateTime.UtcNow.AddDays(20), hash==Old || CandidateValid), store.Length > 0 ? store : "My", host is "example.test" or "www.example.test", hash!=Old || HasOldKey),
                Probe = (_,_,_) => { Probes++; return Task.FromResult(Tls); }
            };
        }
        public static async Task<Fixture> Create() { var f=new Fixture(); await f.Files.WriteAsync("config/ssl.json",new[]{Record(New)}); return f; }
        public Task Authorize(SiteDto site, CancellationToken token) { token.ThrowIfCancellationRequested(); if(Revoked) throw new AppException("auth.forbidden",403,403); return Task.CompletedTask; }
        public Task<SslCertificateDto[]> Read() => Files.ReadAsync("config/ssl.json",Array.Empty<SslCertificateDto>());
        public void Dispose() { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
    sealed class FakeSites : IIisSiteService
    {
        public SiteDto Site = new() { Id=7, Name="site",PhysicalPath="C:\\site",ApplicationRootPath="/",ApplicationPoolName="pool" };
        public Task<SiteDto> GetSiteAsync(string site, CancellationToken ct=default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Site); }
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyCollection<SiteDto>>([Site]);
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string s,string p,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task DeleteSiteAsync(string s,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string s,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string s,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string s,SiteBindingRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string s,RemoveSiteBindingRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken ct=default)=>throw new NotSupportedException();
    }
}
