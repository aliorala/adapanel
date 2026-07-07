using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentPreflightTests
{
    [Theory]
    [InlineData("mariadb-11", 3306)]
    [InlineData("mysql-8", 3307)]
    [InlineData("postgresql-16", 5432)]
    [InlineData("redis", 6379)]
    public void GetRequiredPorts_KnownEnginesHaveTheirPort(string componentId, int expectedPort)
    {
        var port = Assert.Single(ComponentPreflight.GetRequiredPorts(componentId));
        Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void GetRequiredPorts_UnknownComponentHasNone()
    {
        Assert.Empty(ComponentPreflight.GetRequiredPorts("php-8.3"));
        Assert.Empty(ComponentPreflight.GetConflictingServicePatterns("php-8.3"));
    }

    [Fact]
    public void IsOwnService_PanelServicesAndComponentServiceAreOwn()
    {
        Assert.True(ComponentPreflight.IsOwnService("AdaMariaDB", "AdaMariaDB"));
        Assert.True(ComponentPreflight.IsOwnService("adastalwart", ""));   // Ada* öneki panele aittir
        Assert.False(ComponentPreflight.IsOwnService("MySQL80", "AdaMariaDB"));
        Assert.False(ComponentPreflight.IsOwnService("", "AdaMariaDB"));
    }

    [Fact]
    public void SelectForeignPortConflicts_SkipsOwnServiceKeepsForeign()
    {
        var occupancies = new[]
        {
            new ComponentPreflight.PortOccupancy { Port = 3306, Pid = 10, Process = "mysqld", Service = "AdaMariaDB" },
            new ComponentPreflight.PortOccupancy { Port = 3306, Pid = 20, Process = "mysqld", Service = "MySQL80" },
            new ComponentPreflight.PortOccupancy { Port = 3306, Pid = 30, Process = "mysqld", Service = "" }
        };

        var conflicts = ComponentPreflight.SelectForeignPortConflicts(occupancies, "AdaMariaDB");

        Assert.Equal(2, conflicts.Count); // kendi servisimiz (yeniden kurulum) elenir; yabancı + servissiz süreç kalır
        Assert.DoesNotContain(conflicts, item => item.Service == "AdaMariaDB");
    }

    [Fact]
    public void SelectBlockingServices_BlocksRunningAndAutoSkipsDisabledAndOwn()
    {
        var patterns = ComponentPreflight.GetConflictingServicePatterns("mariadb-11");
        var services = new[]
        {
            new ComponentPreflight.ServiceInfo { Name = "MySQL80", State = "Running", StartMode = "Manual" },
            new ComponentPreflight.ServiceInfo { Name = "MariaDB", State = "Stopped", StartMode = "Auto" },
            new ComponentPreflight.ServiceInfo { Name = "MySQL57", State = "Stopped", StartMode = "Disabled" },
            new ComponentPreflight.ServiceInfo { Name = "AdaMariaDB", State = "Running", StartMode = "Auto" },
            new ComponentPreflight.ServiceInfo { Name = "W3SVC", State = "Running", StartMode = "Auto" }
        };

        var blocking = ComponentPreflight.SelectBlockingServices(services, patterns, "AdaMariaDB");

        Assert.Equal(2, blocking.Count);
        Assert.Contains(blocking, item => item.Name == "MySQL80");   // çalışıyor → engeller
        Assert.Contains(blocking, item => item.Name == "MariaDB");   // durmuş ama otomatik → sonraki açılışta çakışır
        Assert.DoesNotContain(blocking, item => item.Name == "MySQL57"); // devre dışı → engellemez
        Assert.DoesNotContain(blocking, item => item.Name == "AdaMariaDB"); // bizim servis
        Assert.DoesNotContain(blocking, item => item.Name == "W3SVC");     // desen dışı
    }

    [Fact]
    public void BuildProbeScript_ContainsPortsAndEscapedPatterns()
    {
        var script = ComponentPreflight.BuildProbeScript([3306], ["mysql", "maria+db"]);

        Assert.Contains("@(3306)", script);
        Assert.Contains("mysql|maria\\+db", script); // regex kaçışı uygulanır
        Assert.Contains("Get-NetTCPConnection", script);
        Assert.Contains("ConvertTo-Json", script);
    }
}
