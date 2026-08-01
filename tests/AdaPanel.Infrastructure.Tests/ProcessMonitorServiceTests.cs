using AdaPanel.Infrastructure.Monitoring;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Canlı süreç izleyicisinin saf mantığı: CPU% delta hesabı, app pool adı → site adı ve
/// süreç → etiket eşlemesi. Ölçüm kodu makineye bağlıdır, bu üç parça ise saftır ve test edilir.
/// </summary>
public sealed class ProcessMonitorServiceTests
{
    [Fact]
    public void CalculateCpuPercent_NormalizesByProcessorCount()
    {
        // 1 sn'lik pencerede 1 sn CPU zamanı harcayan süreç 4 çekirdekli makinede %25'tir.
        var percent = ProcessMonitorService.CalculateCpuPercent(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(11),
            TimeSpan.FromSeconds(1),
            processorCount: 4);

        Assert.Equal(25d, percent);
    }

    [Fact]
    public void CalculateCpuPercent_ClampsToHundred()
    {
        var percent = ProcessMonitorService.CalculateCpuPercent(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1),
            processorCount: 2);

        Assert.Equal(100d, percent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CalculateCpuPercent_ReturnsZeroForInvalidProcessorCount(int processorCount)
    {
        var percent = ProcessMonitorService.CalculateCpuPercent(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            processorCount);

        Assert.Equal(0d, percent);
    }

    [Fact]
    public void CalculateCpuPercent_ReturnsZeroWhenNoElapsedTimeOrNegativeDelta()
    {
        Assert.Equal(0d, ProcessMonitorService.CalculateCpuPercent(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.Zero, 4));

        // Süreç yeniden başlamış / sayaç geri gitmiş olabilir → negatif fark 0 sayılır.
        Assert.Equal(0d, ProcessMonitorService.CalculateCpuPercent(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4));
    }

    [Fact]
    public void CalculateTotalCpuPercent_SumsProcessDeltas()
    {
        // 2 çekirdek × 1 sn = 2000 ms kapasite; 500 ms harcanmışsa %25.
        Assert.Equal(25d, ProcessMonitorService.CalculateTotalCpuPercent(500d, TimeSpan.FromSeconds(1), 2));
        Assert.Equal(0d, ProcessMonitorService.CalculateTotalCpuPercent(0d, TimeSpan.FromSeconds(1), 2));
    }

    [Theory]
    [InlineData("c:\\windows\\system32\\inetsrv\\w3wp.exe -ap \"AdaPanel.alioral.xyz\" -v \"v4.0\" -l \"webengine4.dll\"", "AdaPanel.alioral.xyz")]
    [InlineData("w3wp.exe -ap AdaPanel.alioral.xyz -v v4.0", "AdaPanel.alioral.xyz")]
    [InlineData("c:\\windows\\system32\\inetsrv\\w3wp.exe -ap \"DefaultAppPool\" -v \"v4.0\"", "DefaultAppPool")]
    public void ExtractAppPoolName_ReadsWorkerProcessArgument(string commandLine, string expected)
    {
        Assert.Equal(expected, ProcessMonitorService.ExtractAppPoolName(commandLine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("c:\\php\\php-cgi.exe")]
    public void ExtractAppPoolName_ReturnsNullWhenAbsent(string? commandLine)
    {
        Assert.Null(ProcessMonitorService.ExtractAppPoolName(commandLine));
    }

    [Theory]
    [InlineData("AdaPanel.alioral.xyz", "alioral.xyz")]
    [InlineData("adapanel.example-site.com", "example-site.com")]
    public void ResolveSiteNameFromPool_StripsManagedPrefix(string appPool, string expected)
    {
        Assert.Equal(expected, ProcessMonitorService.ResolveSiteNameFromPool(appPool));
    }

    [Theory]
    [InlineData("DefaultAppPool")]
    [InlineData("AdaPanel.")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolveSiteNameFromPool_ReturnsNullForUnmanagedPool(string? appPool)
    {
        Assert.Null(ProcessMonitorService.ResolveSiteNameFromPool(appPool));
    }

    [Fact]
    public void ResolveLabel_MapsWorkerProcessToSite()
    {
        var label = ProcessMonitorService.ResolveLabel("w3wp", "AdaPanel.alioral.xyz", null);

        Assert.Equal("site", label.Category);
        Assert.Equal("alioral.xyz", label.Label);
        Assert.Equal("AdaPanel.alioral.xyz", label.Detail);
    }

    [Fact]
    public void ResolveLabel_MapsForeignAppPoolToPoolCategory()
    {
        var label = ProcessMonitorService.ResolveLabel("w3wp", "DefaultAppPool", null);

        Assert.Equal("apppool", label.Category);
        Assert.Equal("DefaultAppPool", label.Label);
    }

    [Fact]
    public void ResolveLabel_MapsPhpFastCgiToOwningSite()
    {
        // php-cgi'nin app pool'u EBEVEYN w3wp'den gelir; etiket yine siteyi göstermelidir.
        var label = ProcessMonitorService.ResolveLabel("php-cgi", "AdaPanel.alioral.xyz", null);

        Assert.Equal("php", label.Category);
        Assert.Equal("alioral.xyz", label.Label);
    }

    [Theory]
    [InlineData("AdaMariaDB", "database", "MariaDB")]
    [InlineData("AdaMySQL", "database", "MySQL")]
    [InlineData("AdaPostgreSQL", "database", "PostgreSQL")]
    [InlineData("AdaRedis", "cache", "Redis")]
    [InlineData("AdaMemcached", "cache", "Memcached")]
    [InlineData("AdaStalwart", "mail", "Stalwart")]
    [InlineData("AdaPanel", "panel", "AdaPanel")]
    [InlineData("W3SVC", "web", "IIS")]
    [InlineData("FTPSVC", "ftp", "FTP")]
    public void ResolveLabel_MapsPanelWindowsServices(string serviceName, string category, string expectedLabel)
    {
        var label = ProcessMonitorService.ResolveLabel("someexe", null, serviceName);

        Assert.Equal(category, label.Category);
        Assert.Equal(expectedLabel, label.Label);
        Assert.Equal(serviceName, label.Detail);
    }

    [Theory]
    [InlineData("mariadbd", "database", "MariaDB")]
    [InlineData("stalwart", "mail", "Stalwart")]
    [InlineData("nginx", "web", "Nginx")]
    public void ResolveLabel_FallsBackToExecutableNameWhenServiceLookupFails(string processName, string category, string expectedLabel)
    {
        // Stalwart zamanlanmış görevle çalışabilir (servis PID'i yok) → exe adı yedek yol olmalı.
        var label = ProcessMonitorService.ResolveLabel(processName, null, null);

        Assert.Equal(category, label.Category);
        Assert.Equal(expectedLabel, label.Label);
    }

    [Fact]
    public void ResolveLabel_ClassifiesWindowsCoreAndUnknownProcesses()
    {
        Assert.Equal("windows", ProcessMonitorService.ResolveLabel("svchost", null, null).Category);
        Assert.Equal("other", ProcessMonitorService.ResolveLabel("notepad", null, null).Category);
        Assert.Equal("notepad", ProcessMonitorService.ResolveLabel("notepad", null, null).Label);
    }

    [Fact]
    public void ParseMetadata_ReadsAppPoolsParentsAndServices()
    {
        const string json = """
        {"processes":[
            {"id":4321,"parentId":900,"commandLine":"c:\\windows\\system32\\inetsrv\\w3wp.exe -ap \"AdaPanel.alioral.xyz\" -v \"v4.0\""},
            {"id":4400,"parentId":4321,"commandLine":"c:\\php\\php-cgi.exe"}],
         "services":[{"id":1500,"name":"AdaMariaDB"}]}
        """;

        var metadata = ProcessMonitorService.ParseMetadata(json);

        Assert.NotNull(metadata);
        Assert.Equal("AdaPanel.alioral.xyz", metadata!.AppPoolFor(4321));
        Assert.Null(metadata.AppPoolFor(4400));
        // php-cgi kendi komut satırından siteyi bilmez; ebeveyn w3wp üzerinden çözülür.
        Assert.Equal("AdaPanel.alioral.xyz", metadata.AppPoolForParentOf(4400));
        Assert.Equal("AdaMariaDB", metadata.ServiceFor(1500));
        Assert.Contains(4321, metadata.KnownProcessIds);
    }

    [Fact]
    public void ParseMetadata_AcceptsSingleObjectInsteadOfArray()
    {
        // PowerShell 5.1 tek elemanlı diziyi nesneye indirger; ayrıştırma bunu tolere etmeli.
        const string json = """
        {"processes":{"id":77,"parentId":1,"commandLine":"w3wp.exe -ap \"AdaPanel.demo.local\""},"services":{"id":88,"name":"AdaRedis"}}
        """;

        var metadata = ProcessMonitorService.ParseMetadata(json);

        Assert.NotNull(metadata);
        Assert.Equal("AdaPanel.demo.local", metadata!.AppPoolFor(77));
        Assert.Equal("AdaRedis", metadata.ServiceFor(88));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void ParseMetadata_ReturnsNullForUnusableOutput(string? json)
    {
        Assert.Null(ProcessMonitorService.ParseMetadata(json));
    }
}
