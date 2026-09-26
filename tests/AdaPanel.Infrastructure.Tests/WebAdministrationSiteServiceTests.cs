using System.Runtime.InteropServices;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebAdministrationSiteServiceTests
{
    [Fact]
    public void Customer_list_does_not_query_HTTP_state_of_internal_FTP_site()
    {
        var service = new WebAdministrationSiteService(
            new ImmediateConcurrencyLock(), NullLogger<WebAdministrationSiteService>.Instance);
        var ftp = new FtpSite();
        var sites = service.MapCustomerSites([ftp, new StaleStateSite()]);
        Assert.False(ftp.StateRead);
        var customer = Assert.Single(sites);
        Assert.Equal("example.test", customer.Name);
        Assert.Equal("Unknown", customer.State);
    }

    private sealed class FtpSite
    {
        public string Name => "AdaPanel FTP";
        public bool StateRead { get; private set; }
        public object State
        {
            get
            {
                StateRead = true;
                throw new COMException("FTP does not support HTTP state.");
            }
        }
    }

    [Fact]
    public void MapSite_UsesUnknownState_WhenIisReturnsAStaleComObject()
    {
        var service = new WebAdministrationSiteService(
            new ImmediateConcurrencyLock(),
            NullLogger<WebAdministrationSiteService>.Instance);

        var site = service.MapSite(new StaleStateSite());

        Assert.Equal(42, site.Id);
        Assert.Equal("example.test", site.Name);
        Assert.Equal("Unknown", site.State);
        Assert.True(site.AutoStart);
        Assert.Equal(@"C:\sites\example.test", site.PhysicalPath);
        Assert.Equal("ExamplePool", site.ApplicationPoolName);
        Assert.Single(site.Bindings);
    }

    // Regresyon: "adapanel.host" gibi paneli çağrıştıran gerçek bir müşteri alan adı, dahili
    // "AdaPanel." önekiyle çakışıp site listesinden gizleniyordu (kullanıcı sitesini panelde göremiyordu).
    [Theory]
    [InlineData("adapanel.host")]
    [InlineData("AdaPanel.host")]
    [InlineData("adapanel.com.tr")]
    [InlineData("mailenable.com")]
    [InlineData("alioral.xyz")]
    public void IsSystemSite_IsFalse_ForCustomerDomainsThatResemblePanelSites(string siteName)
    {
        Assert.False(WebAdministrationSiteService.IsSystemSite(CreateSite(siteName, "*:80:" + siteName)));
    }

    [Theory]
    [InlineData("Default Web Site")]
    [InlineData("AdaPanel FTP")]
    [InlineData("AdaPanel.WebDb.PhpMyAdmin")]
    [InlineData("AdaPanel.WebDb.Adminer")]
    [InlineData("MailEnable WebMail")]
    public void IsSystemSite_IsTrue_ForPanelInternalSites(string siteName)
    {
        Assert.True(WebAdministrationSiteService.IsSystemSite(CreateSite(siteName, "*:8896:")));
    }

    [Fact]
    public void IsSystemSite_IsTrue_WhenEveryBindingIsLoopbackOnly()
    {
        Assert.True(WebAdministrationSiteService.IsSystemSite(CreateSite("internal.tool", "127.0.0.1:9000:")));
    }

    // Panelin kendi "AdaPanel.<Bileşen>" siteleri kendi adlarına bağlanmaz (webmail sitesi
    // *:8899 ve webmail.<alan> ile yayınlanır) -> gizli kalmalı, listeye sızmamalı.
    [Fact]
    public void IsSystemSite_IsTrue_ForPanelComponentSitesThatAreNotBoundToTheirOwnName()
    {
        Assert.True(WebAdministrationSiteService.IsSystemSite(
            CreateSite("AdaPanel.Webmail", "*:8899:", "*:80:webmail.alioral.xyz")));
        Assert.True(WebAdministrationSiteService.IsSystemSite(
            CreateSite("AdaPanel.FutureTool", "*:9100:")));
    }

    // Müşteri sitesi yalnızca www alt alan adına bağlıysa da GÖRÜNMELİ.
    [Fact]
    public void IsSystemSite_IsFalse_WhenACustomerSiteIsBoundOnlyToItsWwwSubdomain()
    {
        Assert.False(WebAdministrationSiteService.IsSystemSite(
            CreateSite("adapanel.host", "*:80:www.adapanel.host")));
    }

    private static SiteDto CreateSite(string name, params string[] bindingInformations)
    {
        return new SiteDto
        {
            Id = 1,
            Name = name,
            State = "Started",
            Bindings = bindingInformations
                .Select(information =>
                {
                    var parts = information.Split(':');
                    return new SiteBindingDto
                    {
                        Protocol = "http",
                        BindingInformation = information,
                        HostName = parts.ElementAtOrDefault(2) ?? string.Empty,
                        Port = int.TryParse(parts.ElementAtOrDefault(1), out var port) ? port : 80,
                        IpAddress = parts.FirstOrDefault() ?? "*"
                    };
                })
                .ToArray()
        };
    }

    private sealed class StaleStateSite
    {
        public long Id => 42;
        public string Name => "example.test";
        public object State => throw new COMException(
            "The object identifier does not represent a valid object.",
            unchecked((int)0x800710D8));
        public bool ServerAutoStart => true;
        public IReadOnlyCollection<FakeBinding> Bindings =>
        [
            new FakeBinding()
        ];
        public IReadOnlyCollection<FakeApplication> Applications =>
        [
            new FakeApplication()
        ];
    }

    private sealed class FakeBinding
    {
        public string Protocol => "http";
        public string BindingInformation => "*:80:example.test";
    }

    private sealed class FakeApplication
    {
        public string Path => "/";
        public string ApplicationPoolName => "ExamplePool";
        public IReadOnlyCollection<FakeVirtualDirectory> VirtualDirectories =>
        [
            new FakeVirtualDirectory()
        ];
    }

    private sealed class FakeVirtualDirectory
    {
        public string Path => "/";
        public string PhysicalPath => @"C:\sites\example.test";
    }

    private sealed class ImmediateConcurrencyLock : IConcurrencyLock
    {
        public Task RunExclusiveAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default) => operation();

        public Task<T> RunExclusiveAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default) => operation();

        public Task RunExclusiveAsync(
            string key,
            Func<Task> operation,
            CancellationToken cancellationToken = default) => operation();

        public Task<T> RunExclusiveAsync<T>(
            string key,
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default) => operation();
    }
}
