using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BootstrapPhpExtensionScopeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-bootstrap-extension-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("PATH=C:/sites/customer", ";extension=curl")]
    [InlineData("PATH=C:/sites/customer", "extension=php_curl.dll")]
    [InlineData("HOST=customer.test", ";extension=curl")]
    [InlineData("HOST=customer.test", "extension=php_curl.dll")]
    public async Task Bootstrap_enables_global_extension_without_changing_tenant_section(string section, string tenantExtension)
    {
        Directory.CreateDirectory(Path.Combine(root, "ext"));
        await File.WriteAllBytesAsync(Path.Combine(root, "ext", "php_curl.dll"), [1]); // existence fixture, not executable
        var tenantLines = new[] { $"[{section}]", tenantExtension, "memory_limit=64M" };
        await File.WriteAllLinesAsync(Path.Combine(root, "php.ini"), new[] { "[PHP]" }.Concat(tenantLines));

        await ComponentBootstrapper.EnsurePhpIniAsync(root, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(Path.Combine(root, "php.ini"));
        var boundary = Array.IndexOf(lines, tenantLines[0]);
        Assert.True(boundary > 0);
        Assert.Contains("extension=curl", lines.Take(boundary));
        Assert.Equal(tenantLines, lines.Skip(boundary));
    }

    [Fact]
    public async Task Existing_global_extension_is_activated_once_and_tenant_settings_remain_unchanged()
    {
        Directory.CreateDirectory(Path.Combine(root, "ext"));
        await File.WriteAllBytesAsync(Path.Combine(root, "ext", "php_curl.dll"), [1]);
        var tenantLines = new[] { "[PATH=C:/sites/customer]", ";extension=curl", "memory_limit=64M" };
        await File.WriteAllLinesAsync(Path.Combine(root, "php.ini"), new[] { "[PHP]", ";extension=php_curl.dll" }.Concat(tenantLines));
        await ComponentBootstrapper.EnsurePhpIniAsync(root, CancellationToken.None);
        var lines = await File.ReadAllLinesAsync(Path.Combine(root, "php.ini"));
        var boundary = Array.IndexOf(lines, tenantLines[0]);
        Assert.Single(lines.Take(boundary), line => line == "extension=curl");
        Assert.Equal(tenantLines, lines.Skip(boundary));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
