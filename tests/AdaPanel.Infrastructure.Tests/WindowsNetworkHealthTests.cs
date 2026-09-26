using System.Net;
using System.Net.Sockets;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WindowsNetworkHealthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_tables_find_real_TCP_and_UDP_listeners_only_for_their_owner(bool ipv6)
    {
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var tcp = new TcpListener(address, 0);
        if (ipv6) tcp.Server.DualMode = false;
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var udp = new UdpClient(address.AddressFamily);
        if (ipv6) udp.Client.DualMode = false;
        udp.Client.Bind(new IPEndPoint(address, port));
        var measured = WindowsNetworkHealth.ReadListeners((uint)Environment.ProcessId, port);
        Assert.Contains(address.ToString(), measured.Tcp);
        Assert.Contains(address.ToString(), measured.Udp);
        var other = WindowsNetworkHealth.ReadListeners(uint.MaxValue, port);
        Assert.Empty(other.Tcp);
        Assert.Empty(other.Udp);
        udp.Dispose();
        measured = WindowsNetworkHealth.ReadListeners((uint)Environment.ProcessId, port);
        Assert.Contains(address.ToString(), measured.Tcp);
        Assert.DoesNotContain(address.ToString(), measured.Udp);
    }

    [Fact]
    public void Missing_service_is_distinct_from_a_failed_query()
    {
        var state = WindowsNetworkHealth.ReadService("AdaPanelMissing_" + Guid.NewGuid().ToString("N"));
        Assert.Equal("NotInstalled", state.Status);
        Assert.Equal(0u, state.ProcessId);
    }

    [Fact]
    public void SCM_reads_existing_service_without_changing_it()
    {
        var state = WindowsNetworkHealth.ReadService("EventLog");
        Assert.NotEqual("Unknown", state.Status);
        Assert.NotEqual("NotInstalled", state.Status);
        if (state.Status == "Running") Assert.True(state.ProcessId > 0);
    }
}
