using AdaPanel.Core.Dtos.Htaccess;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class HtaccessQueryDiscardTests
{
    [Theory]
    [InlineData("/new?fixed=1", "QSD", "fixed=1")]
    [InlineData("/new?fixed=1", "QSA,QSD", "fixed=1")]
    [InlineData("/new", "QSD", "")]
    [InlineData("/new?", "QSD", "")]
    [InlineData("/new?", "QSA,QSD", "")]
    [InlineData("/new?fixed=1", "QSA", "fixed=1&original=2")]
    [InlineData("/new?fixed=1", "", "fixed=1")]
    [InlineData("/new", "", "original=2")]
    public void Redirect_keeps_the_targets_own_query_when_discarding_the_original(string target, string flags, string expectedQuery)
    {
        using var site = new HtaccessTestSite();
        site.Htaccess($"RewriteEngine On\nRewriteRule ^old$ {target} [R=302,L{(flags.Length == 0 ? "" : "," + flags)}]");
        var result = site.Evaluate("/old", "original=2");
        Assert.False(result.EngineFailed);
        Assert.Equal(HtaccessOutcome.Redirect, result.Outcome);
        Assert.Equal("http://ornek.com/new" + (expectedQuery.Length == 0 ? "" : "?" + expectedQuery), result.RedirectLocation);
    }

    [Theory]
    [InlineData("QSD")]
    [InlineData("QSA,QSD")]
    public void Internal_rewrite_keeps_its_explicit_query(string flags)
    {
        using var site = new HtaccessTestSite();
        site.Htaccess($"RewriteEngine On\nRewriteRule ^old$ /index.php?fixed=1 [{flags},END]");
        var result = site.Evaluate("/old", "original=2");
        Assert.False(result.EngineFailed);
        Assert.Equal(HtaccessOutcome.Rewritten, result.Outcome);
        Assert.Equal("/index.php", result.RewrittenPath);
        Assert.Equal("fixed=1", result.RewrittenQuery);
    }
}
