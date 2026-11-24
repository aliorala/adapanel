using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using MailKit.Net.Imap;
using MailKit;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationMailImapTests
{
    [Theory]
    [InlineData("")]
    [InlineData("date")]
    [InlineData("flags")]
    [InlineData("body")]
    [InlineData("count")]
    public async Task Real_IMAP_APPEND_and_FETCH_verify_message_content_date_flags_and_count(string fault)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "AdaPanel.LocalImap." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            var body = Path.Combine(scratch, "source.eml");
            await File.WriteAllTextAsync(body, "From: a@example.test\r\nTo: b@example.test\r\nDate: Tue, 1 Jan 2019 01:02:03 +0000\r\nSubject: source message\r\n\r\nThe body is preserved.\r\n", Encoding.ASCII);
            var date = new DateTimeOffset(2022, 2, 3, 4, 5, 6, TimeSpan.Zero);
            var data = new MigrationMailDataset(); data.Folders.Add(new("INBOX", "inbox", true));
            data.Messages.Add(new(body, await MigrationMailArchiveReader.CanonicalHashAsync(body, default), date, MessageFlags.Seen | MessageFlags.Answered, [], ["INBOX"]));
            await using var server = new LocalServer { AcceptAuthentication = true, ReadbackFault = fault };
            var transport = TrustedFixtureTransport(server);
            await using var session = await transport.OpenAsync(server.Endpoint, "alice@target.test", "fixture-private-password", scratch, default);
            if (fault.Length == 0) await session.ImportAndVerifyAsync(data, default);
            else await Assert.ThrowsAsync<AppException>(() => session.ImportAndVerifyAsync(data, default));
            Assert.Equal(1, server.AppendCalls); Assert.Equal(date, server.StoredDate);
            Assert.Contains("\\Seen", server.StoredFlags); Assert.Contains("\\Answered", server.StoredFlags);
            Assert.Contains("The body is preserved.", server.StoredBody);
        }
        finally { Directory.Delete(scratch, true); }
    }
    [Fact]
    public async Task Real_loopback_TLS_preserves_mail_identity_and_default_validation_rejects_untrusted_certificate()
    {
        await using var server = new LocalServer();
        var transport = new MigrationMailImap();
        await Assert.ThrowsAnyAsync<Exception>(() => transport.ProbeAsync(server.Endpoint, default));
        Assert.Equal("mail.target.test", server.ServerName);
    }
    [Fact]
    public async Task Real_TLS_capability_probe_and_explicit_authentication_rejection_verify_disabled_state()
    {
        await using var server = new LocalServer();
        var transport = TrustedFixtureTransport(server);
        await transport.ProbeAsync(server.Endpoint, default);
        await transport.VerifyDisabledAsync(server.Endpoint, "alice@target.test", "fixture-private-password", default);
        Assert.Equal("mail.target.test", server.ServerName);
        Assert.Equal(1, server.AuthenticationAttempts);
    }
    [Theory]
    [InlineData("accepted")]
    [InlineData("disconnected")]
    [InlineData("capability")]
    public async Task Accepted_login_network_failure_or_missing_OBJECTID_is_not_disabled_proof(string fault)
    {
        await using var server = new LocalServer { AcceptAuthentication = fault == "accepted", DisconnectAuthentication = fault == "disconnected", MissingObjectId = fault == "capability" };
        var transport = TrustedFixtureTransport(server);
        await Assert.ThrowsAnyAsync<Exception>(() => transport.VerifyDisabledAsync(server.Endpoint, "alice@target.test", "fixture-private-password", default));
        if (fault == "capability") Assert.Equal(0, server.AuthenticationAttempts);
    }
    private static MigrationMailImap TrustedFixtureTransport(LocalServer server) => new(() => new ImapClient
    {
        // Test-only leaf trust. Production constructor creates a client without callbacks.
        ServerCertificateValidationCallback = (_, certificate, _, _) => certificate != null && certificate.GetCertHashString() == server.Certificate.GetCertHashString()
    });
    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        private readonly List<Task> connections = [];
        private readonly Task accept;
        internal X509Certificate2 Certificate { get; }
        internal MigrationMailEndpoint Endpoint { get; }
        internal string? ServerName;
        internal bool AcceptAuthentication, DisconnectAuthentication, MissingObjectId;
        internal int AuthenticationAttempts;
        internal int AppendCalls;
        internal string ReadbackFault = "", StoredFlags = "", StoredBody = "";
        internal DateTimeOffset StoredDate;
        internal LocalServer()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=mail.target.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("mail.target.test"); request.CertificateExtensions.Add(names.Build());
            using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Certificate = new X509Certificate2(temporary.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
            listener.Start(); Endpoint = new("mail.target.test", ((IPEndPoint)listener.LocalEndpoint).Port, false);
            accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var socket = await listener.AcceptTcpClientAsync(stop.Token);
                    connections.Add(ServeAsync(socket));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        private async Task ServeAsync(TcpClient socket)
        {
            using (socket)
            using (var tls = new SslStream(socket.GetStream()))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, host) => { ServerName = host; return Certificate; },
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }, stop.Token);
                    using var read = new StreamReader(tls, Encoding.ASCII, false, 4096, true);
                    using var write = new StreamWriter(tls, Encoding.ASCII, 4096, true) { AutoFlush = true, NewLine = "\r\n" };
                    string Capabilities() => "IMAP4rev1 UIDPLUS UNSELECT AUTH=PLAIN SASL-IR" + (MissingObjectId ? "" : " OBJECTID");
                    await write.WriteLineAsync("* OK [CAPABILITY " + Capabilities() + "] Fixture ready");
                    while (!stop.IsCancellationRequested)
                    {
                        var line = await read.ReadLineAsync(stop.Token); if (line == null) return;
                        var split = line.IndexOf(' '); if (split < 0) throw new InvalidDataException();
                        var tag = line[..split]; var command = line[(split + 1)..];
                        if (command.StartsWith("CAPABILITY")) { await write.WriteLineAsync("* CAPABILITY " + Capabilities()); await write.WriteLineAsync(tag + " OK capability"); }
                        else if (command.StartsWith("AUTHENTICATE"))
                        {
                            Interlocked.Increment(ref AuthenticationAttempts);
                            if (DisconnectAuthentication) return;
                            await write.WriteLineAsync(tag + (AcceptAuthentication ? " OK authenticated" : " NO [AUTHENTICATIONFAILED] disabled"));
                        }
                        else if (command.StartsWith("LIST"))
                        {
                            await write.WriteLineAsync(command == "LIST \"\" \"\"" ? "* LIST (\\Noselect) \"/\" \"\"" : "* LIST (\\HasNoChildren \\Subscribed) \"/\" \"INBOX\"");
                            await write.WriteLineAsync(tag + " OK listed");
                        }
                        else if (command.StartsWith("EXAMINE") || command.StartsWith("SELECT"))
                        {
                            var count = ReadbackFault == "count" && AppendCalls > 0 ? 0 : AppendCalls;
                            await write.WriteLineAsync("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
                            await write.WriteLineAsync("* " + count + " EXISTS"); await write.WriteLineAsync("* 0 RECENT");
                            await write.WriteLineAsync("* OK [UIDVALIDITY 1] valid"); await write.WriteLineAsync("* OK [UIDNEXT " + (AppendCalls + 1) + "] next");
                            await write.WriteLineAsync("* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] flags");
                            await write.WriteLineAsync(tag + (command.StartsWith("SELECT") ? " OK [READ-WRITE] selected" : " OK [READ-ONLY] selected"));
                        }
                        else if (command.StartsWith("SUBSCRIBE") || command.StartsWith("UNSUBSCRIBE") || command.StartsWith("UNSELECT")) await write.WriteLineAsync(tag + " OK done");
                        else if (command.StartsWith("APPEND"))
                        {
                            var match = Regex.Match(command, @"^APPEND .*? \((?<flags>.*?)\) ""(?<date>[^""]+)"" \{(?<length>\d+)\}$");
                            if (!match.Success) throw new InvalidDataException("Unexpected APPEND fixture shape");
                            StoredFlags = match.Groups["flags"].Value;
                            var sourceDate = match.Groups["date"].Value; sourceDate = sourceDate.Insert(sourceDate.Length - 2, ":");
                            StoredDate = DateTimeOffset.ParseExact(sourceDate, "dd-MMM-yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture);
                            var count = int.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture); var text = new char[count];
                            await write.WriteLineAsync("+ continue");
                            await read.ReadBlockAsync(text.AsMemory(), stop.Token); StoredBody = new string(text);
                            if (await read.ReadLineAsync(stop.Token) != "") throw new InvalidDataException("Unexpected literal terminator");
                            AppendCalls++; await write.WriteLineAsync(tag + " OK [APPENDUID 1 " + AppendCalls + "] appended");
                        }
                        else if (command.Contains("FETCH") && command.Contains("BODY"))
                        {
                            var content = ReadbackFault == "body" ? StoredBody.Replace("preserved", "tampered!") : StoredBody;
                            await write.WriteLineAsync("* 1 FETCH (UID 1 BODY[] {" + Encoding.ASCII.GetByteCount(content) + "}");
                            await write.WriteAsync(content); await write.WriteLineAsync(")"); await write.WriteLineAsync(tag + " OK fetched");
                        }
                        else if (command.Contains("FETCH"))
                        {
                            var reportedDate = (ReadbackFault == "date" ? StoredDate.AddSeconds(1) : StoredDate).ToString("dd-MMM-yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture); reportedDate = reportedDate.Remove(reportedDate.Length - 3, 1);
                            var flags = ReadbackFault == "flags" ? "\\Seen" : StoredFlags;
                            await write.WriteLineAsync("* 1 FETCH (UID 1 FLAGS (" + flags + ") INTERNALDATE \"" + reportedDate + "\" EMAILID (M123))");
                            await write.WriteLineAsync(tag + " OK fetched");
                        }
                        else if (command.StartsWith("LOGOUT")) { await write.WriteLineAsync("* BYE closing"); await write.WriteLineAsync(tag + " OK logout"); return; }
                        else await write.WriteLineAsync(tag + " BAD unsupported fixture command");
                    }
                }
                catch (AuthenticationException) { }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop(); await accept;
            await Task.WhenAll(connections); Certificate.Dispose(); stop.Dispose();
        }
    }
}
