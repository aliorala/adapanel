using System.Reflection;
using AdaPanel.Infrastructure.Php;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PhpExtensionScopeTests
{
    [Theory]
    [InlineData("[PATH=C:/site]")]
    [InlineData("[HOST=site.test]")]
    public void Site_extension_directory_does_not_suppress_the_global_directory(string section)
    {
        var scopedLines = new[] { section, "extension_dir = \"C:/site/extensions\"" };
        var lines = new List<string> { "[PHP]", "; Keep this comment" };
        lines.AddRange(scopedLines);

        var changed = Invoke("EnsureExtensionDir", lines, @"C:\PHP\ext");

        Assert.True(changed);
        Assert.Contains("extension_dir = \"C:\\PHP\\ext\"", PhpIniSiteIsolation.GlobalLines(lines));
        Assert.Equal(scopedLines, lines.Skip(PhpIniSiteIsolation.GlobalInsertIndex(lines)));
    }

    [Theory]
    [InlineData("extension=openssl")]
    [InlineData(";extension=openssl")]
    [InlineData("extension=php_openssl.dll")]
    public void Site_extension_directive_does_not_suppress_or_receive_global_activation(string directive)
    {
        var scopedLines = new[] { "[PATH=C:/site]", directive };
        var lines = new List<string> { "[PHP]", "; Keep this comment" };
        lines.AddRange(scopedLines);

        var changed = Invoke("EnablePhpIniExtension", lines, "openssl");

        Assert.True(changed);
        Assert.Contains("extension=openssl", PhpIniSiteIsolation.GlobalLines(lines));
        Assert.Equal(scopedLines, lines.Skip(PhpIniSiteIsolation.GlobalInsertIndex(lines)));
    }

    [Fact]
    public void Existing_global_configuration_is_preserved_and_repeated_enabling_is_idempotent()
    {
        var lines = new List<string> { "extension_dir = \"D:/custom/ext\"", ";extension=openssl", "[PATH=C:/site]", ";extension=openssl" };

        Assert.False(Invoke("EnsureExtensionDir", lines, @"C:\PHP\ext"));
        Assert.True(Invoke("EnablePhpIniExtension", lines, "openssl"));
        Assert.False(Invoke("EnablePhpIniExtension", lines, "openssl"));

        Assert.Equal("extension_dir = \"D:/custom/ext\"", lines[0]);
        Assert.Equal("extension=openssl", lines[1]);
        Assert.Equal(";extension=openssl", lines[^1]);
    }

    // Only the in-memory conversion is invoked: no IIS access or php-cgi process renewal.
    private static bool Invoke(string method, List<string> lines, string value)
        => (bool)typeof(PhpService).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [lines, value])!;
}
