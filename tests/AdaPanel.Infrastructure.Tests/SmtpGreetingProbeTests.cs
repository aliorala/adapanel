using System.Net;
using System.Net.Sockets;
using System.Text;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SmtpGreetingProbeTests
{
    [Theory]
    [InlineData("220 mail.example.test Ready\r\n", true)]
    [InlineData("220 MAIL.EXAMPLE.TEST. Ready\r\n", true)]
    [InlineData("220-mail.example.test Ready\r\n220-Information\r\n220 Done\r\n", true)]
    [InlineData("220 wrong.test mail.example.test\r\n", false)]
    [InlineData("220 mail.example.test.attacker.test Ready\r\n", false)]
    public async Task Only_the_actual_greeting_hostname_is_compared(string banner, bool expected)
        => Assert.Equal(expected, await Probe(banner));

    [Theory]
    [InlineData("")]
    [InlineData("220\r\n")]
    [InlineData("220 \r\n")]
    [InlineData("421 mail.example.test Unavailable\r\n")]
    [InlineData("220Xmail.example.test\r\n")]
    [InlineData("220-mail.example.test Ready\r\n")]
    [InlineData("220-mail.example.test Ready\r\n250 done\r\n")]
    [InlineData("220 mail.example.test")]
    public async Task Incomplete_or_rejected_greetings_are_not_healthy_or_hostname_mismatches(string banner)
        => await Assert.ThrowsAsync<IOException>(() => Probe(banner));

    [Fact]
    public async Task Oversized_greeting_is_bounded()
        => await Assert.ThrowsAsync<IOException>(() => Probe("220 mail.example.test " + new string('x', 600) + "\r\n"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Silent_server_times_out_but_caller_cancellation_is_preserved(bool cancel)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cts = new CancellationTokenSource();
        var pending = SmtpGreetingProbe.MatchesAsync("mail.example.test", cts.Token,
            ((IPEndPoint)listener.LocalEndpoint).Port, TimeSpan.FromMilliseconds(300));
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) cts.Cancel();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<IOException>(() => pending);
    }

    private static async Task<bool> Probe(string banner)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var pending = SmtpGreetingProbe.MatchesAsync("mail.example.test", default,
            ((IPEndPoint)listener.LocalEndpoint).Port, TimeSpan.FromSeconds(5));
        using (var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes(banner));
        }
        return await pending;
    }
}
