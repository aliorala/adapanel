using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Infrastructure.Nginx;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxUpstreamEncodingTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:80", "http://127.0.0.1")]
    [InlineData("https://example.test/", "https://example.test/")]
    [InlineData("http://example.test/api/", "http://example.test/api/")]
    [InlineData("http://example.test/?token=a//", "http://example.test/?token=a//")]
    [InlineData("http://example.test/a b", "http://example.test/a%20b")]
    [InlineData("http://example.test/a\"b", "http://example.test/a%22b")]
    [InlineData("http://example.test/a;b", "http://example.test/a;b")]
    [InlineData("http://127.0.0.1/; include evil.conf", "http://127.0.0.1/;%20include%20evil.conf")]
    [InlineData("http://example.test/$host?q=$request_uri", "http://example.test/%24host?q=%24request_uri")]
    [InlineData("http://example.test/%22%3B%24", "http://example.test/%22%3B%24")]
    [InlineData("http://[::1]:8080/a", "http://[::1]:8080/a")]
    public void Upstream_is_one_quoted_literal_url_with_its_path_and_query_preserved(string input, string expected)
    {
        var configuration = NginxInstallationSupport.BuildConfiguration(new NginxConfigurationRequest { UpstreamUrl = input });
        var directive = Assert.Single(configuration.Split('\n').Select(line => line.Trim()), line => line.StartsWith("proxy_pass "));
        Assert.Equal($"proxy_pass \"{expected}\";", directive);
        // There is one URL argument and no nginx variable interpolation in user-supplied URLs.
        Assert.Equal(2, directive.Count(character => character == '"'));
        Assert.DoesNotContain('$', directive);
    }

    [Theory]
    [InlineData("http://example.test/a\nb")]
    [InlineData("http://example.test/a\rb")]
    [InlineData("http://example.test/a\tb")]
    public void Control_characters_are_rejected_instead_of_silently_changing_the_url(string input)
    {
        var error = Assert.Throws<AppException>(() => NginxInstallationSupport.BuildConfiguration(new NginxConfigurationRequest { UpstreamUrl = input }));
        Assert.Equal("nginx.upstream_invalid", error.MessageKey);
    }
}
