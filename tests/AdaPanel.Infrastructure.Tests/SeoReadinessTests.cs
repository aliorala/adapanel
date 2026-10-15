using System.Net;
using System.Text.Json;
using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Seo;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SeoReadinessTests
{
    [Theory]
    [InlineData("Mozilla/5.0 OAI-SearchBot/1.0", "openai_search")]
    [InlineData("Mozilla/5.0 GPTBot/1.2", "openai_training")]
    [InlineData("Mozilla/5.0 ChatGPT-User/1.0", "openai_user")]
    public void LogAnalysisSeparatesSearchTrainingAndUserRequests(string userAgent, string key)
        => Assert.Equal(key, SeoBotCatalog.Match(userAgent)!.Key);

    [Fact]
    public void ReadsGraphArraysAndHtmlEvidenceWithoutExecutingScripts()
    {
        var result = SeoContentAnalyzer.Analyze("""
            <html lang="tr"><head><title>Example</title>
            <link rel="alternate canonical" href="https://example.test/a?b=1&amp;c=2">
            <script type="application/ld+json">{"@context":"https://schema.org","@graph":[{"@type":["Article","WebPage"],"author":{"@type":"Person"}}]}</script>
            <script>let fake = '<h2>Fake</h2>';</script>
            <!-- <script type="application/ld+json">bad</script> -->
            </head><body><h1>Answer</h1><h2>Details</h2><p>Useful evidence.</p></body></html>
            """);
        Assert.Equal("tr", result.Language);
        Assert.Equal("https://example.test/a?b=1&c=2", result.Canonical);
        Assert.Equal(1, result.CanonicalCount);
        Assert.Equal(1, result.SectionHeadingCount);
        Assert.Equal(1, result.JsonLdBlocks);
        Assert.Equal(0, result.InvalidJsonLdBlocks);
        Assert.Equal(["Article", "Person", "WebPage"], result.SchemaTypes);
        Assert.Equal("Answer Details Useful evidence.".Length, result.TextCharacters);
    }

    [Theory]
    [InlineData("{bad}")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{\"@type\":\"Article\",}")]
    public void InvalidOrScalarJsonIsNotCountedAsUsableMarkup(string json)
    {
        var signals = SeoContentAnalyzer.Analyze($"<script type='application/ld+json'>{json}</script>");
        Assert.Equal(1, signals.InvalidJsonLdBlocks);
        Assert.Contains(SeoContentFindingRules.Evaluate(new SeoPageDto { ContentSignals = signals }), finding => finding.Key == "invalid_jsonld");
    }

    [Fact]
    public void StructuredDataWorkIsBoundedAndLimitationIsReported()
    {
        var many = string.Concat(Enumerable.Repeat("<script type='application/ld+json'>{}</script>", 33));
        var large = "<script type='application/ld+json'>" + new string(' ', SeoContentAnalyzer.MaxJsonLdLength + 1) + "</script>";
        Assert.True(SeoContentAnalyzer.Analyze(many).StructuredDataLimited);
        Assert.True(SeoContentAnalyzer.Analyze(large).StructuredDataLimited);
        var deep = "<script type='application/ld+json'>" + new string('[', 40) + "{}" + new string(']', 40) + "</script>";
        Assert.Equal(1, SeoContentAnalyzer.Analyze(deep).InvalidJsonLdBlocks);
    }

    [Fact]
    public void MicrodataIsNotReportedAsMissingStructuredData()
    {
        var signals = SeoContentAnalyzer.Analyze("<div itemscope itemtype='https://schema.org/Product'>Product</div>");
        Assert.True(signals.OtherStructuredData);
        Assert.DoesNotContain(SeoContentFindingRules.Evaluate(new SeoPageDto { ContentSignals = signals }), finding => finding.Key == "structured_data_review");
    }

    [Theory]
    [InlineData("noindex", true)]
    [InlineData("none", true)]
    [InlineData("x-noindex-something", false)]
    [InlineData("index, follow", false)]
    public void RobotsTokensHaveExactSemantics(string directive, bool expected)
        => Assert.Equal(expected, SeoHtmlAnalyzer.Parse($"<meta name=robots content='{directive}'>").NoIndex);

    [Theory]
    [InlineData("nosnippet", true)]
    [InlineData("max-snippet: 0", true)]
    [InlineData("max-snippet: -1", false)]
    [InlineData("max-snippet: 100", false)]
    public void SnippetControlsAreMeasured(string value, bool expected)
        => Assert.Equal(expected, SeoContentAnalyzer.Analyze($"<meta name='robots' content='{value}'>").NoSnippet);

    [Fact]
    public void SpecificHeaderScopesAreNotMistakenForGenericNoindex()
    {
        Assert.Equal("", SeoContentAnalyzer.GenericHeaderDirectives(["googlebot: noindex, nofollow"]));
        Assert.Equal("noindex, nosnippet", SeoContentAnalyzer.GenericHeaderDirectives(["noindex, googlebot: nofollow", "nosnippet"]));
        Assert.Equal("max-snippet: 0", SeoContentAnalyzer.GenericHeaderDirectives(["max-snippet: 0"]));
    }

    [Fact]
    public void GroupedAgentsAndSpecificOverridesAreRespected()
    {
        var text = "User-agent: *\nDisallow: /\nUser-agent: Googlebot\nUser-agent: OAI-SearchBot\nAllow: /\nUser-agent: GPTBot\nDisallow: /";
        var access = SeoBotAccessAnalyzer.Analyze(text);
        Assert.Equal("allowed", access.Single(bot => bot.Agent == "Googlebot").Status);
        Assert.Equal("allowed", access.Single(bot => bot.Agent == "OAI-SearchBot").Status);
        Assert.Equal("blocked", access.Single(bot => bot.Agent == "Bingbot").Status);
        Assert.Equal("training", access.Single(bot => bot.Agent == "GPTBot").Purpose);
        Assert.True(SeoRobotsAnalyzer.ParseRobots("User-agent: *\nUser-agent: OtherBot\nDisallow: /").DisallowsAll);
    }

    [Theory]
    [InlineData("User-agent: *\nDisallow: /\nAllow: /blog", "restricted")]
    [InlineData("User-agent: *\nDisallow: /private", "restricted")]
    [InlineData("User-agent: *\nDisallow: /\nAllow: /", "allowed")]
    [InlineData("User-agent: *\nDisallow:", "allowed")]
    [InlineData("User-agent: *\nDisallow: /*", "blocked")]
    [InlineData("User-agent: Googlebot\nDisallow: /\nUser-agent: Googlebot\nAllow: /public", "restricted")]
    public void PolicySummaryDoesNotPretendToKnowEveryPath(string text, string expected)
        => Assert.Equal(expected, SeoBotAccessAnalyzer.Analyze(text).Single(bot => bot.Agent == "Googlebot").Status);

    [Theory]
    [InlineData(503, false, "unknown")]
    [InlineData(429, false, "unknown")]
    [InlineData(302, false, "unknown")]
    [InlineData(200, true, "unknown")]
    [InlineData(404, false, "allowed")]
    [InlineData(410, false, "allowed")]
    public void UnreadablePoliciesAreNotShownAsAllowed(int status, bool truncated, string expected)
        => Assert.All(SeoBotAccessAnalyzer.Analyze(null, status, truncated), bot => Assert.Equal(expected, bot.Status));

    [Fact]
    public void TrainingRestrictionDoesNotCreateSearchWarning()
    {
        var robots = new SeoRobotsDto { Found = true, BotAccess = SeoBotAccessAnalyzer.Analyze("User-agent: GPTBot\nDisallow: /") };
        Assert.DoesNotContain(SeoFindingRules.Evaluate([], robots, new SeoSitemapDto { Found = true }, false), finding => finding.Key == "search_bot_blocked");
    }

    [Fact]
    public void OldReportsRemainUnmeasuredAndNewSignalsRoundTrip()
    {
        var old = JsonSerializer.Deserialize<SeoScanReportDto>("{\"Pages\":[{\"Path\":\"/\"}]}")!;
        Assert.Equal(0, old.AnalysisVersion);
        Assert.Null(Assert.Single(old.Pages).ContentSignals);
        Assert.Empty(SeoContentFindingRules.Evaluate(Assert.Single(old.Pages)));
        var report = new SeoScanReportDto { AnalysisVersion = 1, Pages = [new SeoPageDto { ContentSignals = SeoContentAnalyzer.Analyze("<html lang=tr><body>Hello</body></html>") }] };
        var restored = JsonSerializer.Deserialize<SeoScanReportDto>(JsonSerializer.Serialize(report))!;
        Assert.Equal(1, restored.AnalysisVersion);
        Assert.Equal("tr", Assert.Single(restored.Pages).ContentSignals!.Language);
    }

    [Fact]
    public async Task CrawlerCarriesHttpAndHtmlEvidenceIntoFindings()
    {
        using var client = new HttpClient(new SeoFakeHandler((_, _) =>
        {
            var response = SeoFakeHandler.Html("<html lang=tr><head><title>A</title><script type='application/ld+json'>{bad}</script></head><body><p>Answer</p></body></html>");
            response.Headers.TryAddWithoutValidation("X-Robots-Tag", "noindex, max-snippet: 0");
            return response;
        }));
        var crawl = await new SeoCrawler(client, SeoCrawlLimits.Default).CrawlAsync(Target(), "/", null, CancellationToken.None);
        var page = Assert.Single(crawl.Pages);
        Assert.True(page.NoIndex);
        Assert.True(page.ContentSignals!.NoSnippet);
        Assert.Contains(page.ContentSignals.RobotDirectives, value => value.StartsWith("X-Robots-Tag:"));
        var findings = SeoFindingRules.Evaluate(crawl.Pages, new SeoRobotsDto { Found = true }, new SeoSitemapDto { Found = true }, false);
        Assert.Contains(findings, finding => finding.Key == "invalid_jsonld");
        Assert.Contains(findings, finding => finding.Key == "nosnippet");
        Assert.Contains(findings, finding => finding.Key == "noindex");
    }

    [Fact]
    public async Task TruncatedHtmlIsNotDiagnosedAsMissingContent()
    {
        using var client = new HttpClient(new SeoFakeHandler((_, _) => SeoFakeHandler.Html(new string(' ', 101))));
        var crawl = await new SeoCrawler(client, SeoCrawlLimits.Default with { MaxResponseBytes = 100 }).CrawlAsync(Target(), "/", null, CancellationToken.None);
        Assert.True(Assert.Single(crawl.Pages).ContentSignals!.BodyTruncated);
        var findings = SeoFindingRules.Evaluate(crawl.Pages, new SeoRobotsDto { Found = true }, new SeoSitemapDto { Found = true }, false);
        Assert.Contains(findings, finding => finding.Key == "html_truncated");
        Assert.DoesNotContain(findings, finding => finding.Key is "missing_title" or "no_html_text" or "invalid_jsonld");
    }

    [Fact]
    public async Task ExactBodyLimitIsNotTruncation()
    {
        using var client = new HttpClient(new SeoFakeHandler((_, _) => SeoFakeHandler.Html(new string('a', 100))));
        var fetched = await new SeoCrawler(client, SeoCrawlLimits.Default with { MaxResponseBytes = 100 }).FetchAsync(Target(), "/", CancellationToken.None);
        Assert.False(fetched.BodyTruncated);
        Assert.Equal(100, fetched.Body.Length);
    }

    [Fact]
    public void RemainingRedirectDoesNotProduceHtmlMissingWarnings()
    {
        var findings = SeoFindingRules.Evaluate([new SeoPageDto { StatusCode = 302, ContentType = "text/html" }], new SeoRobotsDto { Found = true }, new SeoSitemapDto { Found = true }, false);
        Assert.DoesNotContain(findings, finding => finding.Key is "missing_title" or "missing_h1");
    }

    [Fact]
    public void MainWebDomainWinsOverMailAndPreviewBindings()
    {
        var site = new SiteDto
        {
            Name = "example.test",
            Bindings = [new SiteBindingDto { HostName = "preview.127.0.0.1.nip.io", Port = 443 },
                new SiteBindingDto { HostName = "mail.example.test", Port = 443 },
                new SiteBindingDto { HostName = "example.test", Port = 80 }]
        };
        Assert.Equal("example.test", SeoTargetResolver.Resolve(site).Target!.Host);
    }

    private static SeoCrawlTarget Target() => new() { Host = "example.test", Port = 80, UseTls = false, AllowedHosts = SeoLinkPolicy.BuildAllowedHosts(["example.test"]) };
}
