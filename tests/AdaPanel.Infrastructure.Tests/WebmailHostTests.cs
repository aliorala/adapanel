using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailHostTests
{
    [Theory]
    [InlineData("*.travianpanel.com", "webmail.travianpanel.com")]
    [InlineData(" Example.COM. ", "webmail.example.com")]
    [InlineData("örnek.com", "webmail.xn--rnek-4qa.com")]
    [InlineData("webmail.example.com", null)]
    [InlineData("webmail.*.example.com", null)]
    [InlineData("foo.*.example.com", null)]
    [InlineData(".example.com", null)]
    [InlineData("example..com", null)]
    [InlineData("-example.com", null)]
    [InlineData("example-.com", null)]
    [InlineData("example.com --option", null)]
    [InlineData("", null)]
    public void CertificateHostOnlyProducesValidWebmailBinding(string input, string? expected)
        => Assert.Equal(expected, WebmailService.ResolveWebmailHost(input));
}
