using AdaPanel.Core.Dtos.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebmailDnsOwnershipTests
{
    [Theory]
    [InlineData("alias.test", false, true, true, true)]
    [InlineData(" ALIAS.TEST. ", false, true, true, true)]
    [InlineData("primary.test", false, true, true, false)]
    [InlineData("alias.test", true, true, true, false)]
    [InlineData("alias.test", false, false, true, false)]
    [InlineData("alias.test", false, true, false, false)]
    public async Task LocalDnsOnlyWritesToMatchingAvailablePrimaryZoneWhileExternalHttpsContinues(
        string zone, bool secondary, bool present, bool available, bool shouldWrite)
    {
        using var f = new WebmailPublicationFixture();
        await f.Initialize();
        f.Zone.ZoneName = zone;
        f.Zone.IsSecondary = secondary;
        f.Zone.IsWindowsZonePresent = present;
        f.Zone.IsWindowsDnsAvailable = available;
        var opened = await f.Service().OpenAsync("panel.test", new WebmailOpenRequest { Domain = "alias.test" });
        Assert.Equal("https://webmail.alias.test/", opened.Url);
        Assert.Equal(new[] { "webmail.alias.test" }, f.SslCalls);
        if (shouldWrite)
        {
            var record = Assert.Single(f.DnsWrites);
            Assert.Equal("CNAME", record.Type);
            Assert.Equal("webmail", record.Host);
            Assert.Equal("alias.test.", record.Value);
        }
        else Assert.Empty(f.DnsWrites);
    }
}
