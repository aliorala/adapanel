using System.Net;
using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class GitHubComponentVersionMetadataTests
{
    private static ComponentBootstrapper Create() => new(null!, null!, null!, null!,
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions()), null!, null!, NullLogger<ComponentBootstrapper>.Instance);

    public static IEnumerable<object[]> Packages() =>
    [
        ["redis", "5.0.14.1", "Redis-x64-5.0.14.1.zip", "tporadowski/redis"],
        ["win-acme", "2.2.9.1701", "win-acme.v2.2.9.1701.x64.trimmed.zip", "win-acme/win-acme"]
    ];

    [Theory, MemberData(nameof(Packages))]
    public async Task Legacy_release_without_digest_can_verify_current_or_newer_version(string id, string version, string asset, string repository)
    {
        foreach (var installed in new[] { version, "1.0.0" })
        foreach (var digest in new string?[] { null, "" })
        {
            using var client = Client(_ => Body(version, asset, digest), repository);
            var result = await Create().CheckComponentVersionAsync(Component(id), installed,
                new() { AvailableVersion = "1.0.0", VersionCheckFailed = true }, client, CancellationToken.None);
            Assert.True(result.VersionCheckSupported);
            Assert.False(result.VersionCheckFailed);
            Assert.Equal(version, result.AvailableVersion);
            Assert.Equal(installed != version, result.UpdateAvailable);
        }
    }

    [Theory, MemberData(nameof(Packages))]
    public async Task Version_metadata_does_not_weaken_install_checksum_requirement(string id, string version, string asset, string repository)
    {
        var component = Component(id);
        using var client = Client(_ => Body(version, asset, null), repository);
        var method = typeof(ComponentBootstrapper).GetMethod("ResolveInstallComponentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var resolved = await (Task<ComponentManifestItemDto>)method.Invoke(Create(), [component, client, CancellationToken.None])!;
        Assert.Same(component, resolved);
        Assert.Equal(new string('a', 64), resolved.Sha256);
    }

    public static IEnumerable<object[]> Sources() => Packages().Select(package => new[] { package[0], package[1], package[3] });

    [Theory, MemberData(nameof(Packages))]
    public async Task Unknown_installed_version_cannot_be_reported_as_current(string id, string version, string asset, string repository)
    {
        using var client = Client(_ => Body(version, asset, null), repository);
        var result = await Create().CheckComponentVersionAsync(Component(id), "latest", null, client, CancellationToken.None);
        Assert.True(result.VersionCheckFailed);
        Assert.False(result.UpdateAvailable);
        Assert.Equal(version, result.AvailableVersion);
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task Unverified_responses_preserve_failure_and_previous_update(string id, string version, string repository)
    {
        foreach (var mode in new[] { "rate-limit", "503", "timeout", "invalid-json", "missing-tag", "missing-assets", "wrong-asset" })
        {
            using var client = Client(_ => mode switch
            {
                "rate-limit" => new(HttpStatusCode.Forbidden),
                "503" => new(HttpStatusCode.ServiceUnavailable),
                "timeout" => throw new TaskCanceledException("HTTP timeout"),
                "invalid-json" => Json("<html>error</html>"),
                "missing-tag" => Json("{\"assets\":[]}"),
                "missing-assets" => Json("{\"tag_name\":\"v" + version + "\"}"),
                _ => Body(version, "linux.tar.gz", null)
            }, repository);
            var result = await Create().CheckComponentVersionAsync(Component(id), "1.0.0",
                new() { AvailableVersion = version }, client, CancellationToken.None);
            Assert.True(result.VersionCheckFailed);
            Assert.True(result.UpdateAvailable);
            Assert.Equal(version, result.AvailableVersion);
        }
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task Caller_cancellation_propagates(string id, string version, string repository)
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var client = Client(_ => throw new OperationCanceledException(source.Token), repository);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().CheckComponentVersionAsync(
            Component(id), version, null, client, source.Token));
    }

    private static ComponentManifestItemDto Component(string id) => new()
    { Id = id, Version = "latest", DownloadUrl = "https://example.invalid/pinned.zip", Sha256 = new string('a', 64) };

    private static HttpResponseMessage Body(string version, string asset, string? digest) => Json(JsonSerializer.Serialize(new
    {
        tag_name = "v" + version,
        assets = new[] { new { name = asset, browser_download_url = "https://github.com/owner/repo/releases/download/v" + version + "/" + asset, digest } }
    }));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, string repository) => new(new Handler(request =>
    {
        Assert.Equal("https://api.github.com/repos/" + repository + "/releases/latest", request.RequestUri!.AbsoluteUri);
        return respond(request);
    }));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
