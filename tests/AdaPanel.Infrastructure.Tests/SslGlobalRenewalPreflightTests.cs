using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslGlobalRenewalPreflightTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task OneFailedHttpTargetDoesNotBlockOtherRenewalsButRevokedAuthorityDoes(bool revokeAuthority)
    {
        using var area = new BulutAlani();
        using var connection = new SqliteConnection("DataSource=:memory:"); await connection.OpenAsync();
        using var db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var install = area.DosyaDeposu.EnsureDirectory("fake-winacme");
        await File.WriteAllTextAsync(Path.Combine(install,"wacs.exe"),"Never executed");
        db.InstalledComponents.Add(new InstalledComponent { ComponentId="win-acme", InstallDir=install, Status=ComponentInstallStatus.Installed });
        await db.SaveChangesAsync();
        var hosts = new[] {"failed.example.test","healthy.example.test","*.wild.test","staging.example.test","import.example.test"};
        var certificates = hosts.Select(CreateCertificate).ToArray();
        try
        {
            var records = hosts.Select((host,index) => new SslCertificateDto {
                SiteName="site-"+index, HostName=host, Port=443, Thumbprint=certificates[index].Thumbprint,
                Source="letsencrypt", ManagedRenewalId=index==4 ? null : "renewal-"+index,
                ManagedRenewalSiteId=index==4 ? null : index+1, ManagedRenewalStaging=index==3,
                NotAfterUtc=certificates[index].NotAfter.ToUniversalTime()
            }).ToArray();
            await area.DosyaDeposu.WriteAsync("config/ssl.json",records);
            var plans = records.Take(3).Select((record,index) => Plan(record,index==2,area.DosyaDeposu.ResolvePath("tools/acme-dns01.ps1"))).ToArray();
            var sites = DispatchProxy.Create<IIisSiteService,SiteProxy>();
            ((SiteProxy)sites).Sites = records.Select((record,index)=>new SiteDto {Id=index+1,Name=record.SiteName}).ToArray();
            var calls = new List<string>(); var revoked=false; var commands=0; var approvals=0;
            var service = new WinAcmeSslService(db,sites,null!,area.DosyaDeposu,null!,NullLogger<WinAcmeSslService>.Instance,
                _=>Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>(records.Select(record=>new IisHttpsBinding {
                    SiteName=record.SiteName,BindingInformation="*:443:"+record.HostName.Replace("*.",""),Thumbprint=record.Thumbprint,
                    CertificateReadable=true,StoreName="My"
                }).ToArray())) {
                HttpPreflightAsync=(targets,_) => {
                    var host=Assert.Single(targets); calls.Add(host);
                    if (host==hosts[0]) { revoked=revokeAuthority; throw new AppException("ssl.http01_preflight_failed",502,502,"TCP 80 timed out at external probe"); }
                    return Task.CompletedTask;
                },
                RenewalRecordsAsync=(_,_,_)=>Task.FromResult<IReadOnlyList<JsonElement>>(plans),
                RenewalCertificateLookup=thumb=>certificates.FirstOrDefault(cert=>cert.Thumbprint==thumb) is {} cert ? new X509Certificate2(cert) : null,
                CertificateProbeAsync=(record,_)=>Task.FromResult(SslStoreProbe.Found(record.Thumbprint,record.NotAfterUtc,record.Source)),
                RenewalCommand=(_,args,_)=> {
                    Assert.Equal("--renew --baseuri https://acme-v02.api.letsencrypt.org/",args); commands++;
                    Assert.Equal(hosts.Take(2),calls);
                    return Task.FromResult(new CommandResult {ExitCode=0,StandardOutput="Renewal succeeded"});
                }
            };
            Task Approve(CancellationToken _) { approvals++; if(revoked) throw new AppException("auth.forbidden",403,403); return Task.CompletedTask; }
            if(revokeAuthority)
            {
                var error=await Assert.ThrowsAsync<AppException>(()=>service.RenewAllAsync(Approve,area.Iptal));
                Assert.Equal("auth.forbidden",error.MessageKey); Assert.Equal(0,commands); Assert.Equal(new[]{hosts[0]},calls);
            }
            else
            {
                var result=await service.RenewAllAsync(Approve,area.Iptal);
                Assert.Equal(1,commands); Assert.Equal(1,result.RenewedCount); Assert.True(result.Measured);
                Assert.Contains(hosts[0],result.FailureReason); Assert.Contains("TCP 80 timed out",result.FailureReason);
                Assert.DoesNotContain(hosts[1],result.FailureReason);
                Assert.Contains("vadesi kesin bilinmiyor",result.Output);
                Assert.DoesNotContain("yenileme kaydi dogrulanamadi",result.FailureReason);
                Assert.DoesNotContain(hosts[2],calls); Assert.DoesNotContain(hosts[3],calls); Assert.DoesNotContain(hosts[4],calls);
            }
            Assert.True(approvals>=2);
        }
        finally { foreach(var certificate in certificates) certificate.Dispose(); }
    }
    private static JsonElement Plan(SslCertificateDto record,bool wildcard,string script) => JsonSerializer.SerializeToElement(new {
        Id=record.ManagedRenewalId,
        TargetPluginOptions=wildcard ? new {Plugin=SslRenewalVerification.ManualSource,AlternativeNames=new[]{record.HostName,record.HostName[2..]}} as object
            : new {Plugin=SslRenewalVerification.IisSource,IncludeHosts=new[]{record.HostName},IncludeSiteIds=new[]{record.ManagedRenewalSiteId}},
        ValidationPluginOptions=wildcard ? new {Plugin=SslRenewalVerification.DnsScript,Script=script} as object : new {Plugin=SslRenewalVerification.SelfHosting},
        InstallationPluginOptions=new[]{new {Plugin=SslRenewalVerification.IisInstallation,SiteId=record.ManagedRenewalSiteId}},
        History=new[]{new {Success=true,OrderResults=new[]{new {Success=true,Thumbprint=record.Thumbprint}}}}
    });
    private static X509Certificate2 CreateCertificate(string host)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN=fixture",key,HashAlgorithmName.SHA256);
        var san=new SubjectAlternativeNameBuilder(); san.AddDnsName(host); if(host.StartsWith("*."))san.AddDnsName(host[2..]);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(30));
    }
    public class SiteProxy : DispatchProxy
    {
        public SiteDto[] Sites=[];
        protected override object? Invoke(MethodInfo? method,object?[]? args) => method!.Name=="GetSiteAsync"
            ? Task.FromResult(Sites.Single(site=>site.Name==(string)args![0]!)) : throw new NotSupportedException(method.Name);
    }
}
