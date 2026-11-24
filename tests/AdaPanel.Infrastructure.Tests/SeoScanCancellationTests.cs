using System.Net;
using System.Reflection;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Seo;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SeoScanCancellationTests
{
    [Theory]
    [InlineData("/next")]
    [InlineData("/robots.txt")]
    [InlineData("/sitemap.xml")]
    public async Task Cancellation_preserves_measured_pages_and_records_cancelled(string cancelPath)
    {
        var directory = Directory.CreateTempSubdirectory("adapanel-seo-cancel-");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var requests = new List<string>();
            var store = Store(directory.FullName);
            var sites = DispatchProxy.Create<IIisSiteService, SiteProxy>();
            var runner = new SeoScanRunner(sites, store, new SeoScanQueue(),
                NullLogger<SeoScanRunner>.Instance, _ => new HttpClient(new Handler(request =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    requests.Add(path);
                    if (path == cancelPath)
                    {
                        cancellation.Cancel();
                        throw new OperationCanceledException(cancellation.Token);
                    }
                    var html = cancelPath == "/next" ? "<html><a href='/next'>Next</a></html>" : "<html><title>Page</title></html>";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(path == "/" ? html : "User-agent: *\nAllow: /", System.Text.Encoding.UTF8,
                            path == "/" ? "text/html" : "text/plain")
                    };
                })));

            await runner.RunAsync(new("scan", "example.test", "/", 50, null), cancellation.Token);

            var report = Assert.Single((await store.ReadAsync("example.test", CancellationToken.None)).Reports);
            Assert.Equal("cancelled", report.Status);
            Assert.Equal(1, report.PagesCrawled);
            Assert.Single(report.Pages);
            Assert.Empty(report.Error);
            Assert.Equal(cancelPath, requests[^1]);
            Assert.DoesNotContain(report.Findings, finding => finding.Key == "time_limit_reached");
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(true, "cancelled")]
    [InlineData(false, "failed")]
    public async Task Cancellation_before_lookup_is_distinguished_from_an_unrelated_failure(bool cancel, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("adapanel-seo-cancel-");
        try
        {
            using var cancellation = new CancellationTokenSource();
            if (cancel) cancellation.Cancel();
            var sites = DispatchProxy.Create<IIisSiteService, SiteProxy>();
            ((SiteProxy)(object)sites).LookupFailure = new OperationCanceledException(cancellation.Token);
            var store = Store(directory.FullName);
            var runner = new SeoScanRunner(sites, store, new SeoScanQueue(), NullLogger<SeoScanRunner>.Instance,
                _ => throw new InvalidOperationException("No HTTP request is expected."));
            await runner.RunAsync(new("scan", "example.test", "/", 50, null), cancellation.Token);
            var report = Assert.Single((await store.ReadAsync("example.test", CancellationToken.None)).Reports);
            Assert.Equal(expected, report.Status);
            Assert.Equal(0, report.PagesCrawled);
        }
        finally { directory.Delete(true); }
    }

    private static SeoReportStore Store(string path) => new(new JsonFileStore(
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = path })));

    public class SiteProxy : DispatchProxy
    {
        public Exception? LookupFailure { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IIisSiteService.GetSiteAsync)) throw new NotSupportedException();
            if (LookupFailure is not null) return Task.FromException<SiteDto>(LookupFailure);
            return Task.FromResult(new SiteDto
            {
                Name = "example.test", Bindings = [new SiteBindingDto { Protocol = "http", HostName = "example.test", Port = 80 }]
            });
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
