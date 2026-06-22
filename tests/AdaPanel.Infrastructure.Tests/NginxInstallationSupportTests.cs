using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Infrastructure.Nginx;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxInstallationSupportTests
{
    [Fact]
    public void BuildConfiguration_WritesProxyAndCacheSettings()
    {
        var configuration = NginxInstallationSupport.BuildConfiguration(new NginxConfigurationRequest
        {
            ListenAddress = "127.0.0.1",
            ListenPort = 8088,
            ServerName = "example.test",
            UpstreamUrl = "http://127.0.0.1:80",
            CacheEnabled = true,
            CacheSizeMb = 512
        });

        Assert.Contains("listen 127.0.0.1:8088;", configuration);
        Assert.Contains("server_name example.test;", configuration);
        Assert.Contains("proxy_pass http://127.0.0.1:80;", configuration);
        Assert.Contains("max_size=512m", configuration);
        Assert.Contains("proxy_cache adapanel_cache;", configuration);
    }

    [Theory]
    [InlineData("example.test; include evil.conf")]
    [InlineData("../example.test")]
    [InlineData("example test")]
    public void Validate_RejectsUnsafeServerNames(string serverName)
    {
        var request = new NginxConfigurationRequest
        {
            ServerName = serverName
        };

        var exception = Assert.Throws<AppException>(() => NginxInstallationSupport.Validate(request));

        Assert.Equal("nginx.server_name_invalid", exception.MessageKey);
    }
}
