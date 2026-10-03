using System.Text;
using AdaPanel.Infrastructure.Php;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpRedisActivationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-php-redis-" + Guid.NewGuid().ToString("N"));
    private readonly string iniPath;
    private readonly string sourceDll;
    private readonly string targetDll;

    public PhpRedisActivationTests()
    {
        Directory.CreateDirectory(root);
        iniPath = Path.Combine(root, "php.ini");
        sourceDll = Path.Combine(root, "downloaded.dll");
        targetDll = Path.Combine(root, "php_redis.dll");
        File.WriteAllBytes(sourceDll, [7, 8, 9]);
    }

    [Theory]
    [InlineData(";extension=redis")]
    [InlineData(";extension=php_redis.dll")]
    public async Task Commented_redis_is_activated_globally_without_changing_site_configuration(string directive)
    {
        await File.WriteAllLinesAsync(iniPath, ["[PHP]", directive, "[PATH=C:/site]", "extension=redis"]);
        var verified = await PhpService.ActivateRedisFilesAsync(iniPath, sourceDll, targetDll, async _ =>
        {
            var updated = await File.ReadAllLinesAsync(iniPath);
            return PhpIniSiteIsolation.GlobalLines(updated).Contains("extension=redis");
        });

        Assert.True(verified);
        var lines = await File.ReadAllLinesAsync(iniPath);
        Assert.Equal("extension=redis", lines[^1]);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(targetDll));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_verification_restores_original_ini_bytes_and_existing_dll(bool existingDll)
    {
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("[PHP]\r\n;extension=redis\r\n[PATH=C:/site]\r\nextension=redis")).ToArray();
        await File.WriteAllBytesAsync(iniPath, original);
        if (existingDll) await File.WriteAllBytesAsync(targetDll, [1, 2, 3]);

        Assert.False(await PhpService.ActivateRedisFilesAsync(iniPath, sourceDll, targetDll, _ => Task.FromResult(false)));

        Assert.Equal(original, await File.ReadAllBytesAsync(iniPath));
        if (existingDll) Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(targetDll));
        else Assert.False(File.Exists(targetDll));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_verification_restores_original_files(bool cancelled)
    {
        var original = Encoding.UTF8.GetBytes("[PHP]\r\nmemory_limit=256M\r\n");
        await File.WriteAllBytesAsync(iniPath, original);
        await File.WriteAllBytesAsync(targetDll, [1, 2, 3]);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<Exception>(() => PhpService.ActivateRedisFilesAsync(iniPath, sourceDll, targetDll, _ =>
        {
            if (cancelled) { cts.Cancel(); throw new OperationCanceledException(cts.Token); }
            throw new IOException("Verification process failed");
        }, cts.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(iniPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(targetDll));
    }

    [Fact]
    public async Task Verified_activation_preserves_custom_lines_and_keeps_the_new_files()
    {
        await File.WriteAllLinesAsync(iniPath, ["[PHP]", "session.name=CUSTOM", "[PATH=C:/site]", "memory_limit=64M"]);
        Assert.True(await PhpService.ActivateRedisFilesAsync(iniPath, sourceDll, targetDll, async _ =>
        {
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(targetDll));
            Assert.Contains("extension=redis", PhpIniSiteIsolation.GlobalLines(await File.ReadAllLinesAsync(iniPath)));
            return true;
        }));
        var lines = await File.ReadAllLinesAsync(iniPath);
        Assert.Contains("session.name=CUSTOM", lines);
        Assert.Equal("memory_limit=64M", lines[^1]);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
