using System.Net;
using System.Net.Sockets;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupFtpDeadlineTests
{
    [Theory]
    [InlineData("list", 421, 3)]
    [InlineData("delete", 421, 3)]
    [InlineData("rmdir", 421, 3)]
    [InlineData("list", 530, 1)]
    [InlineData("delete", 550, 1)]
    public async Task Ftp_commands_retry_temporary_server_rejection_only(string command, int rejection, int expectedAttempts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var attempts = 0;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < expectedAttempts; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                attempts++;
                using var reader = new StreamReader(client.GetStream());
                await using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true, NewLine = "\r\n" };
                if (i < 2)
                {
                    await writer.WriteLineAsync($"{rejection} Could not create socket");
                    continue;
                }
                await writer.WriteLineAsync("220 Test FTP");
                using var dataListener = new TcpListener(IPAddress.Loopback, 0);
                dataListener.Start();
                while (await reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    var verb = line.Split(' ')[0];
                    switch (verb)
                    {
                        case "USER": await writer.WriteLineAsync("331 Password"); break;
                        case "PASS": await writer.WriteLineAsync("230 Logged in"); break;
                        case "PWD": await writer.WriteLineAsync("257 \"/\" current directory"); break;
                        case "CWD": await writer.WriteLineAsync("250 OK"); break;
                        case "PASV":
                            var port = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                            await writer.WriteLineAsync($"227 Passive (127,0,0,1,{port / 256},{port % 256})");
                            break;
                        case "NLST":
                            await writer.WriteLineAsync("150 Listing");
                            using (var data = await dataListener.AcceptTcpClientAsync(timeout.Token))
                            {
                                await data.GetStream().WriteAsync("archive.zip\r\n"u8.ToArray(), timeout.Token);
                            }
                            await writer.WriteLineAsync("226 Done");
                            break;
                        case "DELE": case "RMD": await writer.WriteLineAsync("250 Done"); break;
                        case "QUIT": await writer.WriteLineAsync("221 Bye"); return;
                        default: await writer.WriteLineAsync("200 OK"); break;
                    }
                }
            }
        }, timeout.Token);

        var commands = new FtpWebRequestCommands();
        var uri = new Uri($"ftp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/archive");
        var credential = new NetworkCredential("test", "test");
        if (command == "list")
        {
            var result = await commands.ListDirectoryAsync(uri, credential, timeout.Token);
            Assert.Equal(rejection == 421, result.Success);
            if (result.Success) { Assert.Equal("archive.zip", Assert.Single(result.Names)); }
            else { Assert.Equal(rejection, result.Code); }
        }
        else
        {
            var result = command == "delete"
                ? await commands.DeleteFileAsync(uri, credential, timeout.Token)
                : await commands.RemoveDirectoryAsync(uri, credential, timeout.Token);
            Assert.Equal(rejection == 421, result.Success);
            if (!result.Success) { Assert.Equal(rejection, result.Code); }
        }
        await server.WaitAsync(timeout.Token);
        Assert.Equal(expectedAttempts, attempts);
    }

    [Fact]
    public async Task Asynchronous_request_is_aborted_when_server_never_sends_greeting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
#pragma warning disable SYSLIB0014
        var request = (FtpWebRequest)WebRequest.Create($"ftp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/backup");
#pragma warning restore SYSLIB0014
        request.Method = WebRequestMethods.Ftp.MakeDirectory;
        var pending = request.GetResponseAsync();
        using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new FtpRequestDeadline(request, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Assert.ThrowsAsync<WebException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var error = Assert.Throws<WebException>(deadline.ThrowIfExpired);
        Assert.Equal(WebExceptionStatus.Timeout, error.Status);
    }

    [Fact]
    public async Task Cancellation_during_retry_wait_does_not_start_another_attempt()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupFtpRetry.RunAsync(_ =>
        {
            attempts++;
            cancellation.Cancel();
            throw new WebException("Connection lost", WebExceptionStatus.ConnectionClosed);
        }, cancellation.Token, TimeSpan.Zero));
        Assert.Equal(1, attempts);
    }
}
