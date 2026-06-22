using System.Xml.Linq;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebAppPublisherTests
{
    [Fact]
    public void EnsurePhpEntry_PreservesExistingPhpHandler()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adapanel-web-publisher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.php"), "<?php echo 'ok';");
        File.WriteAllText(
            Path.Combine(root, "web.config"),
            """
            <configuration>
              <system.webServer>
                <handlers>
                  <add name="AdaPanel.Php.php-8.3" path="*.php" modules="FastCgiModule" scriptProcessor="C:\php\php-cgi.exe" />
                </handlers>
              </system.webServer>
            </configuration>
            """);

        try
        {
            WebAppPublisher.EnsurePhpEntry(root);

            var document = XDocument.Load(Path.Combine(root, "web.config"));
            Assert.Equal(
                "FastCgiModule",
                (string?)document.Descendants("handlers").Descendants("add").Single().Attribute("modules"));
            Assert.Equal(
                "index.php",
                (string?)document.Descendants("defaultDocument").Descendants("add").First().Attribute("value"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
