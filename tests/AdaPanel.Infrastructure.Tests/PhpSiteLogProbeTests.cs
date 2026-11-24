using System.Diagnostics;
using System.Text.Json;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Php;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpSiteLogProbeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-php-probe-" + Guid.NewGuid().ToString("N"));
    private SiteDto Site => new() { Name = "fixture.test", PhysicalPath = root, ApplicationPoolName = "fixture-pool" };
    private string Log => Path.Combine(root, ".adapanel-logs", "php-errors.log");
    private PhpSiteLogPlan Plan => new(Log, Path.GetDirectoryName(Log), true, null);
    public PhpSiteLogProbeTests() { Directory.CreateDirectory(root); Directory.CreateDirectory(Path.GetDirectoryName(Log)!); Directory.CreateDirectory(Path.Combine(root, "api")); }

    [Fact]
    public async Task Unmarked_existing_directory_never_claims_automatic_archiving()
    {
        var result = await Fake().VerifyAsync(Site, Plan, default);
        Assert.Equal("verified", result.Status);
        Assert.False(result.Managed);
        Assert.Contains("arşivlenmiyor", result.Detail);
    }

    [Fact]
    public async Task True_without_file_readback_is_failure_and_temporary_scripts_are_removed()
    {
        var probe = Fake(write: false);
        var result = await probe.VerifyAsync(Site, Plan, default);
        Assert.Equal("failed", result.Status);
        Assert.Empty(Directory.GetFiles(root, "adapanel-log-check-*.php", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(root, "adapanel-log-probe-*"));
    }

    [Theory]
    [InlineData(200, "failed")]
    [InlineData(302, "failed")]
    [InlineData(500, "failed")]
    [InlineData(404, "verified")]
    [InlineData(403, "verified")]
    public async Task Success_requires_actual_log_and_HTTP_denial(int http, string expected)
    {
        var paths = new List<string>();
        var result = await Fake(http: http, paths: paths).VerifyAsync(Site, Plan, default);
        Assert.Equal(expected, result.Status);
        Assert.Contains(paths, path => path.StartsWith("/api/"));
        Assert.Contains(paths, path => path.StartsWith("/adapanel-log-probe-"));
    }

    [Fact]
    public async Task Recent_ini_mismatch_is_pending_then_becomes_failed_after_cache_TTL()
    {
        var ini = Path.Combine(root, ".user.ini");
        await File.WriteAllTextAsync(ini, "display_errors=Off");
        Assert.Equal("pending", (await Fake(actualLog: "C:/wrong/php.log").VerifyAsync(Site, Plan, default)).Status);
        File.SetLastWriteTimeUtc(ini, DateTime.UtcNow.AddMinutes(-10));
        Assert.Equal("failed", (await Fake(actualLog: "C:/wrong/php.log").VerifyAsync(Site, Plan, default)).Status);
    }

    [Fact]
    public async Task Api_override_is_not_hidden_by_root_success()
    {
        Assert.Equal("failed", (await Fake(apiOverride: true).VerifyAsync(Site, Plan, default)).Status);
    }

    [Fact]
    public async Task Permission_failure_short_circuits_without_creating_a_PHP_endpoint()
    {
        var paths = new List<string>();
        var result = await Fake(paths: paths).VerifyAsync(Site, Plan with { ConfigurationError = "Access denied" }, default);
        Assert.Equal("failed", result.Status); Assert.Empty(paths);
    }

    [Fact]
    public async Task Real_CGI_loads_root_and_api_user_ini_and_reads_its_markers()
    {
        var php = PhpExecutable();
        await File.WriteAllTextAsync(Path.Combine(root, ".user.ini"), $"log_errors=On\ndisplay_errors=Off\nerror_log=\"{Log.Replace('\\', '/')}\"\n");
        var count = 0;
        var probe = new PhpSiteLogProbe { Send = async (_, path, secret, ct) =>
        {
            if (path.EndsWith(".log")) return new(404, "fixture HTTP denial; real IIS tested separately");
            count++;
            return await RunCgiAsync(php, path, secret, ct);
        } };
        var result = await probe.VerifyAsync(Site, Plan, default);
        Assert.True(result.Status == "verified", result.Detail); Assert.Equal(3, count);
        Assert.Equal(3, (await File.ReadAllLinesAsync(Log)).Count(line => line.Contains("AdaPanel-PHP-log-check-")));
    }

    [Fact]
    public async Task Real_CGI_can_read_own_log_but_open_basedir_rejects_another_site()
    {
        var neighbor = Path.Combine(Path.GetTempPath(), "adapanel-neighbor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(neighbor);
        try
        {
            var otherLog = Path.Combine(neighbor, "php-errors.log");
            await File.WriteAllTextAsync(otherLog, "other-site-test-marker");
            await File.WriteAllTextAsync(Log, "own-site-test-marker");
            await File.WriteAllTextAsync(Path.Combine(root, ".user.ini"), $"display_errors=Off\nopen_basedir=\"{root.Replace('\\', '/')}\"\n");
            await File.WriteAllTextAsync(Path.Combine(root, "isolation.php"),
                "<?php echo json_encode(['own'=>@file_get_contents('" + Log.Replace('\\', '/') + "'), 'other'=>@file_get_contents('" + otherLog.Replace('\\', '/') + "')]);");
            using var result = JsonDocument.Parse((await RunCgiAsync(PhpExecutable(), "/isolation.php", "", default)).Body);
            Assert.Equal("own-site-test-marker", result.RootElement.GetProperty("own").GetString());
            Assert.False(result.RootElement.GetProperty("other").GetBoolean());
        }
        finally { Directory.Delete(neighbor, true); }
    }

    [Fact]
    public async Task Real_CGI_error_log_true_does_not_prove_a_file_was_written()
    {
        // An inaccessible destination is represented without modifying host ACLs or creating users.
        // PHP reports true despite not being able to create a log in a nonexistent directory.
        var missing = Path.Combine(root, "missing", "runtime.log").Replace('\\', '/');
        await File.WriteAllTextAsync(Path.Combine(root, ".user.ini"), $"log_errors=On\ndisplay_errors=Off\nerror_log=\"{missing}\"\n");
        await File.WriteAllTextAsync(Path.Combine(root, "return-value.php"), "<?php echo json_encode(['returned'=>error_log('proof'), 'exists'=>file_exists(ini_get('error_log'))]);");
        var response = await RunCgiAsync(PhpExecutable(), "/return-value.php", "", default);
        using var json = JsonDocument.Parse(response.Body);
        Assert.True(json.RootElement.GetProperty("returned").GetBoolean());
        Assert.False(json.RootElement.GetProperty("exists").GetBoolean());
    }

    private PhpSiteLogProbe Fake(bool write = true, int http = 404, string? actualLog = null, bool apiOverride = false, List<string>? paths = null) => new()
    {
        Send = async (_, path, secret, ct) =>
        {
            paths?.Add(path);
            if (path.EndsWith(".log")) return new(http, "");
            var source = await File.ReadAllTextAsync(Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)), ct);
            var marker = "AdaPanel-PHP-log-check-" + source.Split("AdaPanel-PHP-log-check-", StringSplitOptions.None)[1][..32];
            if (write) await File.AppendAllTextAsync(Log, marker + Environment.NewLine, ct);
            return new(200, JsonSerializer.Serialize(new { marker, sapi = "cgi-fcgi", pool = "fixture-pool", errorLog = actualLog ?? (apiOverride && path.StartsWith("/api/") ? "C:/different.log" : Log), logErrors = true, displayErrors = false, cacheTtl = 300, readBack = true }));
        }
    };
    private async Task<PhpSiteLogProbe.Reply> RunCgiAsync(string php, string path, string secret, CancellationToken ct)
    {
        var info = new ProcessStartInfo(php) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
        info.ArgumentList.Add("-n"); info.ArgumentList.Add("-d"); info.ArgumentList.Add("cgi.force_redirect=0");
        foreach (var (key, value) in new Dictionary<string,string> { ["DOCUMENT_ROOT"] = root, ["SCRIPT_FILENAME"] = Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)), ["SCRIPT_NAME"] = path, ["REQUEST_METHOD"] = "GET", ["REDIRECT_STATUS"] = "200", ["GATEWAY_INTERFACE"] = "CGI/1.1", ["SERVER_PROTOCOL"] = "HTTP/1.1", ["HTTP_X_ADAPANEL_PROBE"] = secret, ["APP_POOL_ID"] = "fixture-pool" }) info.Environment[key] = value;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct); var output = await stdout; _ = await stderr;
        Assert.Equal(0, process.ExitCode);
        var split = output.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (split >= 0) return new(200, output[(split + 4)..]);
        split = output.IndexOf("\n\n", StringComparison.Ordinal);
        return new(200, split >= 0 ? output[(split + 2)..] : output);
    }
    private static string PhpExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, ".tools", "php-usage-tests", "php-cgi.exe");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Real CGI test requires .tools/php-usage-tests/php-cgi.exe; not a mock success.");
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
