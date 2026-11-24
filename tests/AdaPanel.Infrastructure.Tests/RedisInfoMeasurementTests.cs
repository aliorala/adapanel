using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AdaPanel.Core.Dtos.Cache;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class RedisInfoMeasurementTests : IDisposable
{
    private const string Info = "# Server\r\nredis_version:7.4.1\r\nuptime_in_seconds:123\r\n" +
        "# Clients\r\nconnected_clients:4\r\n# Memory\r\nused_memory_human:1.25M\r\n" +
        "# Stats\r\nkeyspace_hits:80\r\nkeyspace_misses:20\r\n" +
        "# Keyspace\r\ndb0:keys=7,expires=1,avg_ttl=12\r\ndb2:keys=5,expires=0,avg_ttl=0\r\n";
    private readonly AdaPanelDbContext context = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite("Filename=:memory:").Options);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16384)]
    public async Task Complete_RESP_frame_preserves_late_metrics_across_arbitrary_byte_chunks(int chunkSize)
    {
        // The extension deliberately contains multibyte UTF-8: RESP counts bytes,
        // and an individual socket read can end inside a character.
        using var stream = new ResponseStream(Frame(Info + "module_note:測定🙂\r\n"), chunkSize);
        var metrics = await Read(Service(stream));
        AssertMeasured(metrics);
        Assert.Equal("INFO\r\n", Encoding.ASCII.GetString(stream.Written.ToArray()));
    }

    public static IEnumerable<object[]> InvalidFrames()
    {
        yield return ["empty EOF", Array.Empty<byte>()];
        yield return ["null bulk", Encoding.ASCII.GetBytes("$-1\r\n")];
        yield return ["empty bulk", Encoding.ASCII.GetBytes("$0\r\n\r\n")];
        yield return ["error", Encoding.ASCII.GetBytes("-ERR unknown command\r\n")];
        yield return ["simple string", Encoding.ASCII.GetBytes("+OK\r\n")];
        yield return ["array", Encoding.ASCII.GetBytes("*0\r\n")];
        yield return ["unframed INFO", Encoding.UTF8.GetBytes(Info)];
        yield return ["truncated body", Encoding.UTF8.GetBytes("$9999\r\n" + Info)];
        yield return ["length too short", Encoding.UTF8.GetBytes("$5\r\n" + Info + "\r\n")];
        yield return ["missing trailer", Frame(Info)[..^2]];
        yield return ["invalid trailer", Frame(Info)[..^2].Concat("xx"u8.ToArray()).ToArray()];
        yield return ["LF header", Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(Info)}\n{Info}\r\n")];
        yield return ["nondecimal length", Encoding.UTF8.GetBytes("$wat\r\n" + Info + "\r\n")];
        yield return ["negative length", Encoding.UTF8.GetBytes("$-2\r\n" + Info + "\r\n")];
        yield return ["oversize", Encoding.UTF8.GetBytes("$131073\r\n" + Info + "\r\n")];
        yield return ["length overflow", Encoding.UTF8.GetBytes("$99999999999999999999999999\r\n" + Info + "\r\n")];
        var invalidUtf8 = Frame(Info);
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'#')] = 0xff;
        yield return ["invalid UTF-8", invalidUtf8];
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public async Task Incomplete_or_invalid_frames_never_become_zero_metrics(string _, byte[] bytes)
    {
        using var stream = new ResponseStream(bytes);
        Assert.Null(await Read(Service(stream)));
    }

    [Theory]
    [InlineData("redis_version:7.4.1\r\n")]
    [InlineData("uptime_in_seconds:123\r\n")]
    [InlineData("connected_clients:4\r\n")]
    [InlineData("used_memory_human:1.25M\r\n")]
    [InlineData("keyspace_hits:80\r\n")]
    [InlineData("keyspace_misses:20\r\n")]
    [InlineData("# Keyspace\r\n")]
    public async Task Missing_required_measurements_are_not_filled_with_defaults(string missing)
    {
        using var stream = new ResponseStream(Frame(Info.Replace(missing, "", StringComparison.Ordinal)));
        Assert.Null(await Read(Service(stream)));
    }

    [Theory]
    [InlineData("connected_clients:4", "connected_clients:invalid")]
    [InlineData("connected_clients:4", "connected_clients:-1")]
    [InlineData("connected_clients:4", "connected_clients:2147483648")]
    [InlineData("keyspace_hits:80", "keyspace_hits:9223372036854775808")]
    [InlineData("keyspace_hits:80", "keyspace_hits:-1")]
    [InlineData("keyspace_misses:20", "keyspace_misses:invalid")]
    [InlineData("uptime_in_seconds:123", "uptime_in_seconds:invalid")]
    [InlineData("db0:keys=7", "db0:keys=-7")]
    [InlineData("db0:keys=7", "db0:keys=7oops")]
    [InlineData("db0:keys=7", "db0:keys=9223372036854775808")]
    [InlineData("db0:keys=7", "db0:missing=7")]
    [InlineData("db0:keys=7", "db0:keys=7,keys=8")]
    public async Task Invalid_counts_do_not_become_valid_zero_or_negative_measurements(string from, string to)
    {
        using var stream = new ResponseStream(Frame(Info.Replace(from, to, StringComparison.Ordinal)));
        Assert.Null(await Read(Service(stream)));
    }

    [Fact]
    public async Task An_empty_keyspace_and_real_zero_counters_remain_valid()
    {
        var info = Info[..Info.IndexOf("db0:", StringComparison.Ordinal)]
            .Replace("connected_clients:4", "connected_clients:0", StringComparison.Ordinal)
            .Replace("keyspace_hits:80", "keyspace_hits:0", StringComparison.Ordinal)
            .Replace("keyspace_misses:20", "keyspace_misses:0", StringComparison.Ordinal)
            .Replace("uptime_in_seconds:123", "uptime_in_seconds:0", StringComparison.Ordinal);
        using var stream = new ResponseStream(Frame(info));
        var metrics = await Read(Service(stream));
        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.Keys);
        Assert.Equal(0, metrics.ConnectedClients);
        Assert.Equal(0, metrics.Hits);
        Assert.Equal(0, metrics.Misses);
        Assert.Equal(0, metrics.UptimeSeconds);
    }

    [Fact]
    public async Task Unknown_extended_INFO_fields_are_ignored_instead_of_counted_as_databases()
    {
        using var stream = new ResponseStream(Frame(Info + "# Modules\r\ndb_extension:keys=99\r\nmodule:value:with:colons\r\n"));
        AssertMeasured(await Read(Service(stream)));
    }

    [Fact]
    public async Task Database_key_total_overflow_is_unknown()
    {
        using var stream = new ResponseStream(Frame(Info.Replace("db0:keys=7", $"db0:keys={long.MaxValue}", StringComparison.Ordinal)));
        Assert.Null(await Read(Service(stream)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Caller_cancellation_before_or_during_the_read_is_propagated(bool before)
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ResponseStream(Frame(Info), beforeRead: before ? null : cancellation.Cancel);
        var logger = new KayitTutanGunlukcu<CacheService>();
        if (before) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(Service(stream, logger), cancellationToken: cancellation.Token));
        Assert.Empty(logger.Seviyede(LogLevel.Warning));
    }

    [Fact]
    public async Task An_internal_reply_timeout_returns_no_metrics()
    {
        using var stream = new ResponseStream([], blockAtEnd: true);
        Assert.Null(await Read(Service(stream)));
    }

    [Fact]
    public async Task NOAUTH_is_recognized_without_logging_the_response_body()
    {
        const string secret = "private-fixture-marker";
        using var stream = new ResponseStream(Encoding.ASCII.GetBytes("-NOAUTH " + secret + "\r\n"));
        var logger = new KayitTutanGunlukcu<CacheService>();
        Assert.Null(await Read(Service(stream, logger)));
        Assert.True(logger.Icerir(LogLevel.Information, "NOAUTH"));
        Assert.DoesNotContain(secret, logger.TumMetin);
    }

    [Fact]
    public async Task An_ordinary_INFO_value_containing_NOAUTH_is_not_an_authentication_error()
    {
        using var stream = new ResponseStream(Frame(Info + "module_note:NOAUTH is ordinary data\r\n"));
        AssertMeasured(await Read(Service(stream)));
    }

    [Fact]
    public async Task The_real_socket_reader_waits_for_the_entire_INFO_reply()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var peer = ReplyInTwoParts();
        var result = await Read(new CacheService(context), ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        await peer;
        AssertMeasured(result);

        async Task ReplyInTwoParts()
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = client.GetStream();
            var command = new byte[6];
            await stream.ReadExactlyAsync(command, deadline.Token);
            Assert.Equal("INFO\r\n", Encoding.ASCII.GetString(command));
            var frame = Frame(Info);
            var split = Encoding.UTF8.GetString(frame).IndexOf("# Stats", StringComparison.Ordinal);
            await stream.WriteAsync(frame.AsMemory(0, split), deadline.Token);
            await Task.Delay(30, deadline.Token);
            await stream.WriteAsync(frame.AsMemory(split), deadline.Token);
        }
    }

    [Fact]
    public async Task Database_usage_and_restart_identity_are_measured()
    {
        var before = DateTimeOffset.UtcNow;
        var runId = new string('a', 40);
        using var stream = new ResponseStream(Frame(Info.Replace("# Server", "# Server\r\nrun_id:" + runId)));
        var metrics = await Read(Service(stream));
        AssertMeasured(metrics);
        Assert.InRange(metrics!.MeasuredAtUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal(runId, metrics.RunId);
        Assert.Collection(metrics.Databases,
            db => { Assert.Equal(0, db.Index); Assert.Equal(7, db.Keys); Assert.Equal(1, db.Expires); Assert.Equal(12, db.AverageTtlMilliseconds); },
            db => { Assert.Equal(2, db.Index); Assert.Equal(5, db.Keys); Assert.Equal(0, db.Expires); Assert.Equal(0, db.AverageTtlMilliseconds); });
    }

    [Theory]
    [InlineData("expires=-1,avg_ttl=bad")]
    [InlineData("expires=8,avg_ttl=9223372036854775808")]
    [InlineData("expires=1,expires=2,avg_ttl=1,avg_ttl=2")]
    [InlineData("other=1")]
    public async Task Invalid_or_missing_optional_usage_fields_remain_unknown(string fields)
    {
        using var stream = new ResponseStream(Frame(Info.Replace("expires=1,avg_ttl=12", fields)));
        var metrics = await Read(Service(stream));
        AssertMeasured(metrics);
        Assert.Null(metrics!.Databases[0].Expires);
        Assert.Null(metrics.Databases[0].AverageTtlMilliseconds);
    }

    [Theory]
    [InlineData("db2147483648:keys=1")]
    [InlineData("db00:keys=1")]
    public async Task Unrepresentable_or_duplicate_database_indices_are_not_silently_ignored(string row)
    {
        using var stream = new ResponseStream(Frame(Info + row + "\r\n"));
        Assert.Null(await Read(Service(stream)));
    }

    [Fact]
    public async Task Client_projection_excludes_own_socket_and_private_fields()
    {
        using var info = new ResponseStream(Frame(Info));
        using var clients = new ResponseStream(Frame("id=1 addr=127.0.0.1:10 name= db=0 idle=0 cmd=client|list\n" +
            "id=2 addr=192.0.2.1:20 name=worker db=2 idle=17 cmd=secret-command user=private\n"), 1);
        var metrics = await Read(Service(info, clients: clients));
        AssertMeasured(metrics);
        var client = Assert.Single(metrics!.Clients!);
        Assert.Equal("worker", client.Name);
        Assert.Equal(2, client.DatabaseIndex);
        Assert.Equal(17, client.IdleSeconds);
        Assert.Equal("CLIENT LIST\r\n", Encoding.ASCII.GetString(clients.Written.ToArray()));
        var json = System.Text.Json.JsonSerializer.Serialize(metrics.Clients);
        Assert.DoesNotContain("192.0.2", json);
        Assert.DoesNotContain("secret-command", json);
    }

    [Fact]
    public async Task Only_the_panel_connection_is_a_measured_zero_external_clients()
    {
        using var info = new ResponseStream(Frame(Info));
        using var clients = new ResponseStream(Frame("addr=127.0.0.1:10 name= db=0 idle=0\n"));
        var metrics = await Read(Service(info, clients: clients));
        Assert.NotNull(metrics!.Clients);
        Assert.Empty(metrics.Clients);
    }

    [Theory]
    [InlineData("-NOPERM private-response\r\n")]
    [InlineData("$131073\r\n")]
    [InlineData("$100\r\ntruncated")]
    public async Task Client_list_failure_preserves_INFO_without_leaking_response(string response)
    {
        using var info = new ResponseStream(Frame(Info));
        using var clients = new ResponseStream(Encoding.ASCII.GetBytes(response));
        var logger = new KayitTutanGunlukcu<CacheService>();
        var metrics = await Read(Service(info, logger, clients));
        AssertMeasured(metrics);
        Assert.Null(metrics!.Clients);
        Assert.DoesNotContain("private-response", logger.TumMetin);
    }

    [Theory]
    [InlineData("addr=other name= db=0 idle=0\n")]
    [InlineData("addr=127.0.0.1:10\naddr=other name= db=-1 idle=0\n")]
    [InlineData("addr=127.0.0.1:10\naddr=other name= db=0 idle=bad\n")]
    public async Task Unverified_client_rows_do_not_become_a_measured_count(string response)
    {
        using var info = new ResponseStream(Frame(Info));
        using var clients = new ResponseStream(Frame(response));
        var metrics = await Read(Service(info, clients: clients));
        AssertMeasured(metrics);
        Assert.Null(metrics!.Clients);
    }

    [Fact]
    public async Task Client_read_cancellation_propagates_and_timeout_keeps_INFO()
    {
        using var cancellation = new CancellationTokenSource();
        using var info = new ResponseStream(Frame(Info));
        using var clients = new ResponseStream([], beforeRead: cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(Service(info, clients: clients), cancellationToken: cancellation.Token));

        using var secondInfo = new ResponseStream(Frame(Info));
        using var hangingClients = new ResponseStream([], blockAtEnd: true);
        var metrics = await Read(Service(secondInfo, clients: hangingClients));
        AssertMeasured(metrics);
        Assert.Null(metrics!.Clients);
    }

    [Fact]
    public async Task Real_client_socket_filters_itself_after_INFO_socket_is_closed()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var peerTask = Reply();
        var metrics = await Read(new CacheService(context), ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        await peerTask;
        AssertMeasured(metrics);
        Assert.Equal("external", Assert.Single(metrics!.Clients!).Name);

        async Task Reply()
        {
            using var infoPeer = await listener.AcceptTcpClientAsync(deadline.Token);
            var infoStream = infoPeer.GetStream();
            await infoStream.ReadExactlyAsync(new byte[6], deadline.Token);
            await infoStream.WriteAsync(Frame(Info), deadline.Token);
            using var listPeer = await listener.AcceptTcpClientAsync(deadline.Token);
            Assert.Equal(0, await infoStream.ReadAsync(new byte[1], deadline.Token));
            var listStream = listPeer.GetStream();
            var command = new byte[13];
            await listStream.ReadExactlyAsync(command, deadline.Token);
            Assert.Equal("CLIENT LIST\r\n", Encoding.ASCII.GetString(command));
            var body = $"addr={listPeer.Client.RemoteEndPoint} name= db=0 idle=0\naddr=192.0.2.1:15 name=external db=3 idle=9\n";
            var frame = Frame(body);
            foreach (var part in frame.Chunk(7)) await listStream.WriteAsync(part, deadline.Token);
        }
    }

    private CacheService Service(Stream stream, ILogger<CacheService>? logger = null, Stream? clients = null) => new(context, logger)
    {
        OpenRedisInfoStreamAsync = (port, _) =>
        {
            Assert.Equal(0, port);
            return Task.FromResult(stream);
        },
        OpenRedisClientsStreamAsync = clients is null ? null : (_, _) => Task.FromResult((clients, "127.0.0.1:10"))
    };

    private static Task<CacheMetricsDto?> Read(CacheService service, int port = 0, CancellationToken cancellationToken = default)
    {
        var method = typeof(CacheService).GetMethod("ReadRedisInfoAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<CacheMetricsDto?>)method.Invoke(service, [port, cancellationToken])!;
    }

    private static byte[] Frame(string info) => Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(info)}\r\n{info}\r\n");

    private static void AssertMeasured(CacheMetricsDto? metrics)
    {
        Assert.NotNull(metrics);
        Assert.Equal("7.4.1", metrics.Version);
        Assert.Equal("1.25M", metrics.UsedMemory);
        Assert.Equal(123, metrics.UptimeSeconds);
        Assert.Equal(4, metrics.ConnectedClients);
        Assert.Equal(80, metrics.Hits);
        Assert.Equal(20, metrics.Misses);
        Assert.Equal(12, metrics.Keys);
    }

    public void Dispose() => context.Dispose();

    private sealed class ResponseStream(byte[] response, int chunkSize = int.MaxValue, bool blockAtEnd = false, Action? beforeRead = null) : Stream
    {
        private int position;
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (position == response.Length && blockAtEnd) await Task.Delay(Timeout.Infinite, cancellationToken);
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), response.Length - position);
            response.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => Written.WriteAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
