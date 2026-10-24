using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableWebmailProvisioningTests
{
    [Theory]
    [InlineData("shared")]
    [InlineData("nested")]
    [InlineData("ambiguous")]
    [InlineData("missing-login")]
    [InlineData("stopped")]
    [InlineData("unreadable")]
    [InlineData("root-escape")]
    [InlineData("foreign-vdir")]
    public async Task Only_one_running_dedicated_trusted_WebMail_site_can_receive_a_new_host(string scenario)
    {
        using var f = new Fixture(); f.ChangeDiscovery(scenario);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.SecureForDomainAsync("example.test", "admin@example.test"));
        Assert.Equal(0, f.Bindings); Assert.Equal(0, f.Issues);
        Assert.False(File.Exists(f.Files.ResolvePath(MailEnableWebmail.ManagedStatePath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reuse_or_selfhosting_issuance_binds_exact_host_and_keeps_other_domain_records(bool issue)
    {
        using var f = new Fixture(); f.HasCertificate = !issue;
        await f.Service.SecureForDomainAsync("example.test", "admin@example.test");
        Assert.Equal(issue ? 1 : 0, f.Issues); Assert.Equal(1, f.Bindings); Assert.Equal(1, f.Probes);
        var records = await f.Records(); Assert.Single(records);
        Assert.Equal("confirmed", records[0].Status); Assert.Equal(f.Thumb, records[0].Thumbprint);
        Assert.Equal("/Mondo/lang/sys/login.aspx", records[0].LoginPath);
        await f.Service.SecureForDomainAsync("second.test", "admin@example.test");
        records = await f.Records(); Assert.Equal(2, records.Length);
        Assert.Contains(f.Sites[0].Bindings, b => b.Host == "webmail.example.test");
        Assert.Contains(f.Sites[0].Bindings, b => b.Host == "webmail.second.test");
        Assert.Equal(f.OtherHash, f.Sites[0].Bindings.Single(b => b.Host == "unrelated.test").Thumbprint);
    }

    [Fact]
    public async Task Healthy_existing_exact_binding_is_adopted_without_replacing_its_certificate()
    {
        using var f = new Fixture(); f.AddEndpoint("webmail.example.test", f.OldHash);
        await f.Service.SecureForDomainAsync("example.test", "");
        Assert.Equal(0, f.Bindings); Assert.Equal(0, f.Issues); Assert.Equal(1, f.Probes);
        Assert.Equal(f.OldHash, (await f.Records()).Single().Thumbprint);
    }

    [Fact]
    public async Task Exact_host_dedicated_site_wins_over_another_dedicated_candidate()
    {
        using var f = new Fixture(); f.AddSecondSite(); f.AddEndpoint("webmail.example.test", f.OldHash);
        await f.Service.SecureForDomainAsync("example.test", "");
        Assert.Equal("ME", (await f.Records()).Single().Site);
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("readback")]
    [InlineData("binding-output")]
    [InlineData("issue")]
    [InlineData("missing-wacs")]
    [InlineData("no-issued-certificate")]
    public async Task Native_or_TLS_failures_never_publish_success(string failure)
    {
        using var f = new Fixture(); f.Failure = failure;
        if (failure is "issue" or "missing-wacs" or "no-issued-certificate") f.HasCertificate = false;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.SecureForDomainAsync("example.test", "admin@example.test"));
        var records = await f.Records();
        Assert.DoesNotContain(records, r => r.Status == "confirmed");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("invalid json")]
    public async Task Corrupt_management_state_is_reported_and_never_treated_as_an_empty_list(string text)
    {
        using var f = new Fixture(); await f.Files.WriteAsync("config/placeholder.json", 1);
        await File.WriteAllTextAsync(f.Files.ResolvePath(MailEnableWebmail.ManagedStatePath), text);
        var errors = await f.Service.SyncManagedAsync(null, default);
        Assert.NotEmpty(errors); Assert.Equal(0, f.Bindings); Assert.Equal(text, await File.ReadAllTextAsync(f.Files.ResolvePath(MailEnableWebmail.ManagedStatePath)));
    }

    [Fact]
    public async Task Restart_managed_sync_applies_a_new_valid_certificate_and_preserves_other_domains()
    {
        using var f = new Fixture(); await f.Service.SecureForDomainAsync("example.test", "admin@example.test");
        f.Thumb = new string('D', 40); f.Service = f.CreateService();
        var approvals = 0;
        var errors = await f.Service.SyncManagedAsync(_ => { approvals++; return Task.CompletedTask; }, default);
        Assert.Empty(errors); Assert.True(approvals > 0); Assert.Equal(2, f.Bindings);
        Assert.Equal(f.Thumb, (await f.Records()).Single().Thumbprint);
    }

    [Theory]
    [InlineData("external-certificate")]
    [InlineData("site-id")]
    [InlineData("application")]
    [InlineData("provider")]
    public async Task Managed_sync_refuses_external_changes_or_provider_switch_without_overwriting(string change)
    {
        using var f = new Fixture(); await f.Service.SecureForDomainAsync("example.test", "admin@example.test");
        var state = await File.ReadAllBytesAsync(f.Files.ResolvePath(MailEnableWebmail.ManagedStatePath));
        if (change == "external-certificate") f.SetEndpointHash("webmail.example.test", new string('E',40));
        if (change == "site-id") f.Sites[0] = f.Sites[0] with { Id=99 };
        if (change == "application") f.Sites[0] = f.Sites[0] with { Applications=[f.Sites[0].Applications[0] with { Pool="changed" }] };
        if (change == "provider") f.Provider = "stalwart";
        f.Thumb = new string('D', 40); var errors = await f.CreateService().SyncManagedAsync(null, default);
        Assert.NotEmpty(errors); Assert.Equal(1, f.Bindings);
        Assert.Equal(state, await File.ReadAllBytesAsync(f.Files.ResolvePath(MailEnableWebmail.ManagedStatePath)));
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("binding-before")]
    public async Task Pending_binding_after_failed_TLS_can_be_recovered_after_restart(string failure)
    {
        using var f = new Fixture(); f.Failure=failure;
        await Assert.ThrowsAsync<AppException>(() => f.Service.SecureForDomainAsync("example.test", "admin@example.test"));
        Assert.Equal("pending", (await f.Records()).Single().Status);
        f.Failure="";
        Assert.Empty(await f.CreateService().SyncManagedAsync(null, default));
        Assert.Equal("confirmed", (await f.Records()).Single().Status);
    }

    [Fact]
    public async Task Before_mutation_rejection_and_real_cancellation_propagate_without_false_confirmation()
    {
        using var f = new Fixture(); await f.Service.SecureForDomainAsync("example.test", "admin@example.test");
        var old = await File.ReadAllBytesAsync(f.Files.ResolvePath(MailEnableWebmail.ManagedStatePath)); f.Thumb=new string('D',40);
        await Assert.ThrowsAsync<AppException>(() => f.CreateService().SyncManagedAsync(_ => throw new AppException("denied",403,403), default));
        Assert.Equal(old, await File.ReadAllBytesAsync(f.Files.ResolvePath(MailEnableWebmail.ManagedStatePath)));
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.CreateService().SyncManagedAsync(_ => { cancel.Cancel(); return Task.CompletedTask; },cancel.Token));
        Assert.Equal(1, f.Bindings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renewal_hooks_run_in_both_wacs_branches_before_binding_measurement_and_report_failures(bool installed)
    {
        using var f = new Fixture(); await f.Service.SecureForDomainAsync("example.test", "admin@example.test");
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options); db.Database.EnsureCreated();
        if (installed) { db.InstalledComponents.Add(new InstalledComponent { ComponentId="win-acme",Status=ComponentInstallStatus.Installed,InstallDir=Path.GetDirectoryName(f.Wacs)! }); db.SaveChanges(); }
        f.Thumb=new string('D',40); var measured=false;
        var ssl = new WinAcmeSslService(db,null!,null!,f.Files,null!,NullLogger<WinAcmeSslService>.Instance,
            bindingReader: _ => { Assert.Equal(2,f.Bindings); measured=true; return Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>([]); },mailEnableWebmail:f.CreateService())
        { RenewalCommand=(_,_,_) => Task.FromResult(new CommandResult { ExitCode=0 }) };
        var approvals=0;
        var result=await ssl.RenewAllAsync(_ => { approvals++; return Task.CompletedTask; });
        Assert.True(measured); Assert.True(approvals>1); Assert.DoesNotContain("webmail synchronization failed",result.FailureReason);
        f.Provider="stalwart";
        result=await ssl.RenewAllAsync();
        Assert.Contains("active provider changed",result.FailureReason); Assert.Contains("active provider changed",result.Output);
    }

    [Fact]
    public async Task Native_discovery_script_executes_against_stateful_MSWA_fixture_and_opens_trusted_nested_endpoint_read_only()
    {
        using var f = new Fixture(); f.ChangeDiscovery("nested"); f.AddEndpoint("webmail.example.test",f.OldHash);
        f.NativePs=true;
        var opened=await f.CreateService().OpenAsync("webmail.example.test",default);
        Assert.Contains("/mail/Mondo/lang/sys/login.aspx", opened.Url);
        Assert.Equal(0,f.Bindings); Assert.Equal(0,f.Issues); Assert.True(f.Discoveries>0);
    }

    [Fact]
    public void ACME_arguments_reject_injection_and_keep_the_old_certificate_for_rollback()
    {
        Assert.Throws<AppException>(() => MailEnableWebmail.IssuanceArguments("example.test --installation iis", "a@example.test"));
        Assert.Throws<AppException>(() => MailEnableWebmail.IssuanceArguments("webmail.example.test", "a@example.test --script bad"));
        var args=MailEnableWebmail.IssuanceArguments("webmail.example.test","admin@example.test");
        Assert.Contains("--installation none",args); Assert.Contains("--validation selfhosting",args); Assert.Contains("--keepexisting",args);
        Assert.DoesNotContain("--installation iis",args);
    }

    [Fact]
    public async Task Reparse_directory_cannot_make_a_customer_root_look_like_a_dedicated_WebMail_root()
    {
        using var f = new Fixture();
        var outside=Path.Combine(f.Container,"outside"); Directory.CreateDirectory(outside);
        var link=Path.Combine(f.Root,"escape");
        var created=await ProcessRunner.RunAsync("cmd.exe", "/c mklink /J \""+link+"\" \""+outside+"\"");
        Assert.True(created.Success,created.StandardError);
        try
        {
            f.Sites[0]=f.Sites[0] with {Applications=[f.Sites[0].Applications[0] with {Directories=[new("/",f.Root),new("/escape",link)]}]};
            await Assert.ThrowsAsync<AppException>(()=>f.Service.SecureForDomainAsync("example.test","admin@example.test"));
            Assert.Equal(0,f.Bindings);
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(f.Root),Path.GetDirectoryName(Path.GetFullPath(link)));
            Directory.Delete(link,recursive:false); // Unlink only the fixture junction, never traverse its target.
        }
    }

    [Theory]
    [InlineData("*:443:webmail.example.test","127.0.0.1")]
    [InlineData("0.0.0.0:443:webmail.example.test","127.0.0.1")]
    [InlineData("[::]:443:webmail.example.test","::1")]
    [InlineData("[::1]:443:webmail.example.test","::1")]
    public void Probe_uses_only_the_native_local_binding_address(string information,string address)
    {
        Assert.Equal(IPAddress.Parse(address),MailEnableWebmail.LocalProbeAddress(new("ME","webmail.example.test",443,"/login.aspx",BindingInformation:information)));
    }

    [Theory]
    [InlineData("203.0.113.9:443:webmail.example.test")]
    [InlineData("*:444:webmail.example.test")]
    [InlineData("*:443:foreign.example.test")]
    public void Probe_refuses_foreign_addresses_or_mismatched_binding_identity(string information)
    {
        Assert.Throws<AppException>(()=>MailEnableWebmail.LocalProbeAddress(new("ME","webmail.example.test",443,"/login.aspx",BindingInformation:information)));
    }

    [Fact]
    public async Task Real_probe_connects_locally_with_requested_SNI_and_rejects_an_untrusted_certificate()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var key=ECDsa.Create();
        var request=new CertificateRequest("CN=webmail.invalid",key,HashAlgorithmName.SHA256);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
        var sni=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Serve()
        {
            using var client=await listener.AcceptTcpClientAsync(deadline.Token);
            using var tls=new SslStream(client.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificateSelectionCallback=(_,host)=> { sni.TrySetResult(host ?? ""); return certificate; }
                },deadline.Token);
            }
            catch (Exception error) when (error is System.Security.Authentication.AuthenticationException or IOException) { }
        }
        var server=Serve();
        await Assert.ThrowsAsync<AppException>(()=>MailEnableWebmail.ProbeEndpointAsync(new("ME","webmail.invalid",port,"/login.aspx",
            Thumbprint:certificate.Thumbprint,BindingInformation:$"127.0.0.1:{port}:webmail.invalid"),deadline.Token));
        Assert.Equal("webmail.invalid",await sni.Task.WaitAsync(deadline.Token));
        await server.WaitAsync(deadline.Token);
    }

    [Theory]
    [InlineData("unchanged",true)]
    [InlineData("new-app",false)]
    [InlineData("foreign-vdir",false)]
    [InlineData("pool",false)]
    [InlineData("missing-login",false)]
    public async Task Native_identity_preflight_rechecks_every_application_and_directory_before_binding(string change,bool allowed)
    {
        using var f=new Fixture();
        var guard=MailEnableWebmail.NativeIdentityGuard(f.Sites[0],Path.Combine(f.Root,"Mondo","lang","sys","login.aspx"));
        if(change=="new-app") f.Sites[0]=f.Sites[0] with { Applications=f.Sites[0].Applications.Append(new("/foreign","OtherPool",[new("/",f.Container)])).ToArray() };
        if(change=="foreign-vdir") f.ChangeDiscovery("foreign-vdir");
        if(change=="pool") f.Sites[0]=f.Sites[0] with { Applications=[f.Sites[0].Applications[0] with {Pool="Changed"}] };
        if(change=="missing-login") f.ChangeDiscovery("missing-login");
        var result=await ProcessRunner.RunPowerShellAsync(f.DiscoveryPreamble()+guard+"\nWrite-Output 'WRITE-REACHED'",default);
        Assert.Equal(allowed,result.Success);
        Assert.Equal(allowed,result.StandardOutput.Contains("WRITE-REACHED",StringComparison.Ordinal));
    }

    [Fact]
    public async Task Issuance_revalidates_original_authority_before_any_following_native_or_state_write()
    {
        using var f=new Fixture(); f.HasCertificate=false;
        var approvals=0;
        await Assert.ThrowsAsync<AppException>(()=>f.Service.SecureForDomainAsync("example.test","admin@example.test",_=> {
            approvals++; if(f.Issues>0) throw new AppException("denied",403,403); return Task.CompletedTask;
        }));
        Assert.True(approvals>=3); Assert.Equal(1,f.Issues); Assert.Equal(0,f.Bindings);
        Assert.False(File.Exists(f.Files.ResolvePath(MailEnableWebmail.ManagedStatePath)));
    }

    [Theory]
    [InlineData("valid",true)]
    [InlineData("name",false)]
    [InlineData("expired",false)]
    [InlineData("client-only",false)]
    [InlineData("no-private-key",false)]
    public void Certificate_selection_requires_name_time_ServerAuth_trust_and_private_key_without_exporting_it(string kind,bool valid)
    {
        using var key=ECDsa.Create();
        var request=new CertificateRequest("CN=webmail.example.test",key,HashAlgorithmName.SHA256);
        var san=new SubjectAlternativeNameBuilder(); san.AddDnsName(kind=="name"?"other.example.test":"webmail.example.test");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new(kind=="client-only"?"1.3.6.1.5.5.7.3.2":"1.3.6.1.5.5.7.3.1")},false));
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3),DateTimeOffset.UtcNow.AddDays(kind=="expired"?-1:30));
        using var bare=new X509Certificate2(cert.Export(X509ContentType.Cert));
        Assert.Equal(valid,MailEnableWebmail.ValidCertificate(kind=="no-private-key"?bare:cert,"webmail.example.test",policy=> {
            policy.TrustMode=X509ChainTrustMode.CustomRootTrust; policy.CustomTrustStore.Add(cert); policy.RevocationMode=X509RevocationMode.NoCheck;
        }));
    }

    [Fact]
    public async Task Empty_contact_uses_the_saved_ACME_contact_without_modifying_HTTP_bindings()
    {
        using var f=new Fixture(); f.HasCertificate=false;
        await f.Files.WriteAsync(AcmeContactSettings.Path,new AcmeContactSettings {Email="fallback@example.test"});
        await f.Service.SecureForDomainAsync("example.test","");
        Assert.Contains("--emailaddress \"fallback@example.test\"",f.IssuedArguments);
        Assert.All(f.Sites[0].Bindings,b=>Assert.Equal("https",b.Protocol));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Container=Path.Combine(Path.GetTempPath(),"adapanel-me-webmail-"+Guid.NewGuid().ToString("N"));
        internal readonly string Root, Wacs;
        internal readonly JsonFileStore Files;
        internal MailEnableWebmail Service;
        internal readonly List<MailEnableWebmail.NativeSite> Sites=[];
        internal string Thumb=new('C',40), OldHash=new('A',40), OtherHash=new('B',40), Failure="", Provider="mailenable", IssuedArguments="";
        internal bool HasCertificate=true, NativePs;
        internal int Bindings, Issues, Probes, Discoveries;
        internal Fixture()
        {
            Root=Path.Combine(Container,"Mail Enable","BIN64","NETWebMail"); Directory.CreateDirectory(Path.Combine(Root,"Mondo","lang","sys"));
            File.WriteAllText(Path.Combine(Root,"Mondo","lang","sys","login.aspx"),"fixture");
            Wacs=Path.Combine(Container,"wacs.exe"); File.WriteAllText(Wacs,"never executed");
            Files=new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot=Path.Combine(Container,"data") }));
            Sites.Add(Site(1,"ME",Root)); AddEndpoint("unrelated.test",OtherHash); Service=CreateService();
        }
        private MailEnableWebmail.NativeSite Site(long id,string name,string root) => new(id,name,"Started",
            [new("/","MEPool",[new("/",root)])],[]);
        internal void AddSecondSite() => Sites.Add(Site(2,"ME2",Root));
        internal void AddEndpoint(string host,string hash) => Sites[0]=Sites[0] with { Bindings=Sites[0].Bindings.Append(new("https",host,443,$"*:443:{host}",hash,1,"My")).ToArray() };
        internal void SetEndpointHash(string host,string hash) => Sites[0]=Sites[0] with { Bindings=Sites[0].Bindings.Select(b=>b.Host==host?b with { Thumbprint=hash }:b).ToArray() };
        internal void ChangeDiscovery(string scenario)
        {
            if(scenario=="ambiguous") AddSecondSite();
            if(scenario=="stopped") Sites[0]=Sites[0] with { State="Stopped" };
            if(scenario=="missing-login") File.Delete(Path.Combine(Root,"Mondo","lang","sys","login.aspx"));
            if(scenario=="unreadable") Failure="discovery";
            if(scenario is "shared" or "nested") {
                var customer=Path.Combine(Container,"customer"); Directory.CreateDirectory(customer);
                Sites[0]=Sites[0] with { Applications=[new("/","CustomerPool",[new("/",customer)]),new("/mail","MEPool",[new("/",Root)])] };
            }
            if(scenario=="foreign-vdir") {
                var outside=Path.Combine(Container,"outside"); Directory.CreateDirectory(outside);
                Sites[0]=Sites[0] with {Applications=[Sites[0].Applications[0] with {Directories=[new("/",Root),new("/customer",outside)]}]};
            }
            if(scenario=="root-escape") {
                var escaped=Path.Combine(Container,"outside"); Directory.CreateDirectory(Path.Combine(escaped,"Mondo","lang","sys"));
                File.WriteAllText(Path.Combine(escaped,"Mondo","lang","sys","login.aspx"),"lookalike customer login");
                Sites[0]=Sites[0] with { Applications=[new("/","MEPool",[new("/",escaped)])] };
            }
        }
        internal MailEnableWebmail CreateService()
        {
            var iis=DispatchProxy.Create<IIisSiteService,Proxy>(); ((Proxy)(object)iis).Call=(method,args)=> {
                if(method.Name!="GetSiteAsync") throw new InvalidOperationException(method.Name);
                var site=Sites.Single(s=>s.Name.Equals((string)args[0]!,StringComparison.OrdinalIgnoreCase));
                var app=site.Applications.Single(a=>a.Path=="/"); var root=app.Directories.Single(d=>d.Path=="/").PhysicalPath;
                return Task.FromResult(new SiteDto { Id=site.Id,Name=site.Name,PhysicalPath=root,ApplicationRootPath=root,ApplicationPoolName=app.Pool });
            };
            return new MailEnableWebmail(Files,iisSiteService:iis)
            {
                InstallationRoot=()=>Path.GetDirectoryName(Path.GetDirectoryName(Root))!, ActiveProvider=()=>Provider,
                DiscoveryCommand=async(script,token)=> {
                    Discoveries++; token.ThrowIfCancellationRequested();
                    if(Failure=="discovery") return new() { ExitCode=1 };
                    if(!NativePs) return new() { ExitCode=0,StandardOutput=JsonSerializer.Serialize(Sites,new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
                    return await ProcessRunner.RunPowerShellAsync(DiscoveryPreamble()+script,token);
                },
                StoreCertificateLookup=(host,preferred)=>!HasCertificate?null:preferred is null?new(Thumb,DateTime.UtcNow.AddDays(90)):
                    Sites.SelectMany(s=>s.Bindings).Any(b=>b.Host==host&&b.Thumbprint.Equals(preferred,StringComparison.OrdinalIgnoreCase))?new(preferred,DateTime.UtcNow.AddDays(30)):null,
                ExecutableLookup=_=>Task.FromResult(Failure=="missing-wacs"?"":Wacs),
                IssueCommand=(_,args,_)=> {
                    Issues++; IssuedArguments=args; Assert.Contains("--installation none",args); Assert.Contains("--validation selfhosting",args); Assert.Contains("--keepexisting",args);
                    if(Failure!="no-issued-certificate") HasCertificate=true;
                    return Task.FromResult(new CommandResult {ExitCode=Failure=="issue"?1:0});
                },
                BindingCommand=(script,_)=> {
                    Bindings++; Assert.DoesNotContain("--installation iis",script);
                    Assert.Contains("WEBMAIL_IDENTITY_GUARD_END",script);
                    Assert.Contains("Approved site changed.",script); Assert.Contains("Approved absent binding changed.",script);
                    Assert.Contains("Approved binding changed.",script); Assert.Contains("$approvedStore = 'My'",script);
                    if(Failure=="binding-before") return Task.FromResult(new CommandResult {ExitCode=1});
                    // This seam tests orchestration. The shared script has separate stateful native-PS tests.
                    if(Failure!="readback") {
                        var host=script.Contains("$dn = 'webmail.second.test'")?"webmail.second.test":"webmail.example.test";
                        if(Sites[0].Bindings.Any(b=>b.Host==host)) SetEndpointHash(host,Thumb); else AddEndpoint(host,Thumb);
                    }
                    return Task.FromResult(new CommandResult { ExitCode=0,StandardOutput=Failure=="binding-output"?"":"OK" });
                },
                EndpointProbe=(endpoint,token)=> {
                    Probes++; token.ThrowIfCancellationRequested(); Assert.StartsWith("/",endpoint.Path); Assert.Equal("https",new UriBuilder("https",endpoint.Host,endpoint.Port,endpoint.Path).Scheme);
                    if(Failure=="tls") throw new AppException("mail.webmail.mailenable_unhealthy",502,502);
                    Assert.Contains(Sites.SelectMany(s=>s.Bindings),b=>b.Host==endpoint.Host&&b.Thumbprint.Equals(endpoint.Thumbprint,StringComparison.OrdinalIgnoreCase));
                    return Task.CompletedTask;
                }
            };
        }
        internal string DiscoveryPreamble()
        {
            var data=Sites.Select(s=>new {s.Id,s.Name,s.State,Applications=s.Applications.Select(a=>new {a.Path,ApplicationPoolName=a.Pool,VirtualDirectories=a.Directories}),
                Bindings=s.Bindings.Select(b=>new {b.Protocol,b.Host,EndPoint=new {b.Port},BindingInformation=b.Information,CertificateHash=b.Thumbprint,SslFlags=b.Flags,CertificateStoreName=b.StoreName})});
            var payload=Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)));
            return "$script:sites=@([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+payload+"'))|ConvertFrom-Json)\n"+
                "function Add-Type { param($Path,$ErrorAction) }\nfunction New-Object { param($TypeName) $m=[pscustomobject]@{Sites=$script:sites}; $m|Add-Member ScriptMethod Dispose {}; return $m }\n";
        }
        internal Task<MailEnableWebmail.ManagedCertificate[]> Records()=>Files.ReadAsync<MailEnableWebmail.ManagedCertificate[]>(MailEnableWebmail.ManagedStatePath,[]);
        public void Dispose()
        {
            var full=Path.GetFullPath(Container); var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if(Path.GetDirectoryName(full)!=temp||!Path.GetFileName(full).StartsWith("adapanel-me-webmail-",StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture cleanup");
            Directory.Delete(full,true);
        }
    }
    public class Proxy:DispatchProxy
    {
        public Func<MethodInfo,object?[],object?> Call {get;set;}=null!;
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>Call(method!,args!);
    }
}
