using System.Net;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentVersionCheckFailureTests
{
    private static ComponentBootstrapper Create() => new(null!, null!, null!, null!,
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions()), null!, null!, NullLogger<ComponentBootstrapper>.Instance);

    public static IEnumerable<object[]> Failures() =>
        from id in new[] { "adminer", "nodejs-lts", "php-8.3", "mariadb-11", "mysql-8", "postgresql-16", "pgadmin4", "nginx", "python-3" }
        from mode in new[] { "503", "timeout", "invalid" }
        select new object[] { id, mode };

    [Theory, MemberData(nameof(Failures))]
    public async Task Failed_sources_preserve_known_update_and_report_failure(string id, string mode)
    {
        using var client = new HttpClient(new Handler(request => mode switch
        {
            "timeout" => throw new TaskCanceledException("Simulated HTTP timeout"),
            "503" => new(HttpStatusCode.ServiceUnavailable),
            _ => request.Method == HttpMethod.Head ? new(HttpStatusCode.Forbidden) : Json("<html>Unavailable</html>")
        }));
        var result = await Create().CheckComponentVersionAsync(new() { Id = id, Version = "8.3.1" }, "8.3.1",
            new() { AvailableVersion = "8.3.9" }, client, CancellationToken.None);
        Assert.True(result.VersionCheckFailed);
        Assert.True(result.VersionCheckSupported);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("8.3.9", result.AvailableVersion);
    }

    [Theory]
    [InlineData("composer")]
    [InlineData("phpmyadmin")]
    [InlineData("docker-engine")]
    [InlineData("vc-redist-x64")]
    [InlineData("url-rewrite")]
    [InlineData("iis-arr")]
    [InlineData("http-platform-handler")]
    public async Task Checksum_or_fixed_url_is_not_a_version_check(string id)
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not request a checksum as a version")));
        var result = await Create().CheckComponentVersionAsync(new() { Id = id, Version = "1.0.0" }, "1.0.0", null, client, CancellationToken.None);
        Assert.False(result.VersionCheckSupported);
        Assert.False(result.VersionCheckFailed);
    }

    [Theory]
    [InlineData("nginx", "nginx-8.3.1.zip")]
    [InlineData("python-3", "<a href=\"8.3.1/\">8.3.1</a>")]
    [InlineData("postgresql-16", "[{\"major\":\"8\",\"latestMinor\":\"3.1\"}]")]
    [InlineData("pgadmin4", "{\"pgadmin4\":{\"version\":\"8.3.1\"}}")]
    [InlineData("mariadb-11", "{\"releases\":{\"8.3.1\":{\"files\":[{\"file_name\":\"mariadb-8.3.1-winx64.zip\",\"checksum\":{\"sha256sum\":\"abc\"}}]}}}")]
    public async Task Valid_current_version_is_success_even_without_a_new_package(string id, string body)
    {
        using var client = new HttpClient(new Handler(_ => Json(body)));
        var result = await Create().CheckComponentVersionAsync(new() { Id = id, Version = "8.3.1" }, "8.3.1",
            new() { AvailableVersion = "8.3.1", VersionCheckFailed = true }, client, CancellationToken.None);
        Assert.False(result.VersionCheckFailed);
        Assert.False(result.UpdateAvailable);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pgadmin4\":{\"version\":\"unknown\"}}")]
    public async Task Missing_or_invalid_version_is_not_current(string body)
    {
        using var client = new HttpClient(new Handler(_ => Json(body)));
        var result = await Create().CheckComponentVersionAsync(new() { Id = "pgadmin4", Version = "9.0" }, "9.0", null, client, CancellationToken.None);
        Assert.True(result.VersionCheckFailed);
    }

    [Fact]
    public async Task Successful_recheck_replaces_failure_with_new_version()
    {
        using var client = new HttpClient(new Handler(_ => Json("{\"tag_name\":\"v6.1.2\",\"assets\":[{\"name\":\"adminer.php\",\"browser_download_url\":\"https://example.invalid/adminer.php\",\"digest\":\"sha256:" + new string('a', 64) + "\"}]}")));
        var result = await Create().CheckComponentVersionAsync(new() { Id = "adminer", Version = "6.1.0" }, "6.1.0",
            new() { AvailableVersion = "6.1.1", VersionCheckFailed = true }, client, CancellationToken.None);
        Assert.False(result.VersionCheckFailed);
        Assert.Equal("6.1.2", result.AvailableVersion);
        Assert.True(result.UpdateAvailable);
    }

    [Fact]
    public async Task Mysql_missing_future_package_is_distinct_from_network_failure()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        var result = await Create().CheckComponentVersionAsync(new() { Id = "mysql-8", Version = "8.4.1" }, "8.4.1", null, client, CancellationToken.None);
        Assert.False(result.VersionCheckFailed);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Legacy_latest_installed_version_cannot_be_compared_as_current()
    {
        using var client = new HttpClient(new Handler(_ => Json("nginx-1.31.1.zip")));
        var result = await Create().CheckComponentVersionAsync(new() { Id = "nginx", Version = "1.31.0" }, "latest", null, client, CancellationToken.None);
        Assert.True(result.VersionCheckFailed);
        Assert.False(result.UpdateAvailable);
        Assert.Equal("1.31.1", result.AvailableVersion);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        using var client = new HttpClient(new Handler(_ => throw new OperationCanceledException(source.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().CheckComponentVersionAsync(
            new() { Id = "adminer", Version = "6.1.0" }, "6.1.0", null, client, source.Token));
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
