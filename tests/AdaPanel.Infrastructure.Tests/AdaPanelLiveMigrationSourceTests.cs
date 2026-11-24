using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

[Collection("MigrationSourceApi")]
public sealed class AdaPanelLiveMigrationSourceTests
{
    private const string Token = "adap_THIS-IS-THE-SOURCE-TOKEN";
    [Fact]
    public async Task Discovery_uses_real_bearer_model_fixed_path_and_single_inspected_address_snapshot()
    {
        using var f = new Fixture();
        f.Handler.Response = JsonResponse(ApiResponse<MigrationSourceInventoryDto>.Ok(new() { Complete = true }));
        var inventory = await f.Source.DiscoverAsync(Connection());
        Assert.True(inventory.Complete); Assert.Equal(1, f.Resolutions);
        Assert.Equal("8.8.8.8", Assert.Single(f.Inspected!).ToString());
        Assert.Equal("https://source.example.com:8888" + AdaPanelMigrationSourceProtocol.InventoryPath, f.Handler.Url);
        Assert.Equal("Bearer " + Token, f.Handler.Authorization); Assert.Equal("GET", f.Handler.Method);
    }

    [Fact]
    public async Task Acquisition_streams_real_validated_zip_into_private_package_store()
    {
        using var f = new Fixture(); var zip = Package();
        f.Handler.Response = ZipResponse(zip);
        var package = await f.Source.AcquireAsync(Connection(), new() { SiteIds = ["site:one"], IncludeMail = false });
        Assert.Equal(PortableMigrationProvider.Adapanel, package.Manifest.Provider);
        Assert.Equal("POST", f.Handler.Method); Assert.EndsWith(AdaPanelMigrationSourceProtocol.ExportPath, f.Handler.Url);
        Assert.Contains("site:one", f.Handler.Body); Assert.DoesNotContain(Token, f.Handler.Body);
        var stored = await f.Store.ReadManifestAsync(package.Id); Assert.Single(stored.Sites);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Root, "migration-packages"), "staging-*"));
    }

    [Theory]
    [InlineData("http://source.example.com")]
    [InlineData("https://user:password@source.example.com")]
    [InlineData("https://source.example.com/other")]
    [InlineData("https://source.example.com/?token=secret")]
    [InlineData("https://source.example.com/#fragment")]
    public async Task Only_an_https_origin_is_accepted_before_any_transport(string url)
    {
        using var f = new Fixture();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Source.DiscoverAsync(new() { BaseUrl = url, ApiToken = Token }));
        Assert.Equal("migration.source.https_origin_required", error.MessageKey); Assert.Equal(0, f.Handler.Calls);
    }

    [Fact]
    public async Task Internal_destination_is_blocked_before_authentication_headers_reach_transport()
    {
        using var f = new Fixture(IPAddress.Loopback);
        var error = await Assert.ThrowsAsync<AppException>(() => f.Source.DiscoverAsync(Connection()));
        Assert.Equal("migration.host_forbidden", error.MessageKey); Assert.Equal(0, f.Handler.Calls);
    }

    [Theory]
    [InlineData(404, "upgrade_required")]
    [InlineData(503, "unavailable")]
    [InlineData(401, "authorization_failed")]
    [InlineData(403, "authorization_failed")]
    [InlineData(409, "export_busy")]
    [InlineData(302, "redirect_forbidden")]
    [InlineData(500, "request_failed")]
    public async Task Failure_bodies_are_never_read_or_echoed_and_old_version_is_distinct(int status, string code)
    {
        using var f = new Fixture(); var body = new TrackingStream(Encoding.UTF8.GetBytes(Token));
        f.Handler.Response = new((HttpStatusCode)status) { Content = new StreamContent(body) };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Source.DiscoverAsync(Connection()));
        Assert.Equal("migration.source." + code, error.MessageKey); Assert.Equal(0, body.Reads);
        Assert.DoesNotContain(Token, error.Message + error.Details);
    }

    [Fact]
    public async Task Successful_http_without_the_protocol_header_cannot_masquerade_as_new_source_api()
    {
        using var f = new Fixture(); f.Handler.Response = JsonResponse(ApiResponse<MigrationSourceInventoryDto>.Ok(new()));
        f.Handler.Response.Headers.Remove(AdaPanelMigrationSourceProtocol.VersionHeader);
        Assert.Equal("migration.source.protocol_version_unsupported", (await Assert.ThrowsAsync<AppException>(() => f.Source.DiscoverAsync(Connection()))).MessageKey);
    }

    [Fact]
    public async Task Inventory_json_has_a_streaming_byte_budget()
    {
        using var f = new Fixture(limits: new() { MaxManifestBytes = 128 });
        f.Handler.Response = JsonResponse(ApiResponse<MigrationSourceInventoryDto>.Ok(new() { Issues = [new() { Detail = new string('x', 500) }] }));
        await Assert.ThrowsAsync<AppException>(() => f.Source.DiscoverAsync(Connection()));
    }

    [Fact]
    public async Task Oversized_zip_is_rejected_before_package_publication()
    {
        using var f = new Fixture(limits: new() { MaxUploadBytes = 100 }); f.Handler.Response = ZipResponse(Package());
        Assert.Equal("migration.source.artifact_limit", (await Assert.ThrowsAsync<AppException>(() => f.Source.AcquireAsync(Connection(), new()))).MessageKey);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Root, "migration-packages")));
    }

    [Fact]
    public async Task Wrong_provider_is_deleted_after_normal_package_validation()
    {
        using var f = new Fixture(); f.Handler.Response = ZipResponse(Package(PortableMigrationProvider.Cpanel));
        Assert.Equal("migration.source.provider_invalid", (await Assert.ThrowsAsync<AppException>(() => f.Source.AcquireAsync(Connection(), new()))).MessageKey);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Root, "migration-packages")));
    }

    [Fact]
    public async Task Declared_length_mismatch_cannot_leave_a_published_package()
    {
        using var f = new Fixture(); var zip = Package(); f.Handler.Response = ZipResponse(zip);
        f.Handler.Response.Content.Headers.ContentLength = zip.Length + 1;
        Assert.Equal("migration.source.artifact_truncated", (await Assert.ThrowsAsync<AppException>(() => f.Source.AcquireAsync(Connection(), new()))).MessageKey);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Root, "migration-packages")));
    }

    [Fact]
    public async Task Cancellation_during_copy_disposes_response_and_package_staging()
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        var body = new TrackingStream(Package()) { OnRead = cancellation.Cancel };
        f.Handler.Response = ZipResponse([]); f.Handler.Response.Content = new StreamContent(body);
        f.Handler.Response.Content.Headers.ContentType = new("application/zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Source.AcquireAsync(Connection(), new(), cancellation.Token));
        Assert.True(body.Disposed); Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(f.Root, "migration-packages")));
    }

    [Fact]
    public void Native_handler_disables_redirects_and_preserves_guarded_connect_and_tls_callbacks()
    {
        using var handler = AdaPanelLiveMigrationSource.BuildHandler(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions()), new(""), [IPAddress.Parse("8.8.8.8")]);
        Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseProxy); Assert.NotNull(handler.ConnectCallback);
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void Tls_rejects_unknown_certificate_and_accepts_only_an_explicit_exact_sha256_exception()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=source.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var unknown = new RemoteCertificateTrustGate(""); Assert.False(unknown.Evaluate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        var exact = new RemoteCertificateTrustGate(TrustFingerprint.FromCertificate(certificate));
        Assert.True(exact.Evaluate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(new RemoteCertificateTrustGate("SHA256:different").Evaluate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_local_tls_preserves_original_sni_and_sends_credentials_only_after_trust(bool acceptedPin)
    {
        using var f = new Fixture(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=source.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("source.example.com");
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel server authentication needs a key-backed certificate, rather than
        // CreateSelfSigned's ephemeral key. No certificate is installed into a trust store.
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string sni = "", authorization = "";
        Exception? serverFailure = null;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var ssl = new SslStream(socket.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificateSelectionCallback = (_, name) => { sni = name ?? ""; return certificate; } }, deadline.Token);
                using var reader = new StreamReader(ssl, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token)))
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) authorization = line[14..].Trim();
                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ApiResponse<MigrationSourceInventoryDto>.Ok(new() { Complete = true }), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\n" + AdaPanelMigrationSourceProtocol.VersionHeader + ": 1\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(headers, deadline.Token); await ssl.WriteAsync(payload, deadline.Token); await ssl.FlushAsync(deadline.Token);
            }
            catch (Exception error) when (error is AuthenticationException or IOException or OperationCanceledException) { serverFailure = error; }
        }, deadline.Token);
        var resolutions = 0;
        var runtime = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = f.Root, AllowInternalOutboundTargets = true });
        var native = new AdaPanelLiveMigrationSource(runtime, new(), f.Store,
            (_, _) => { resolutions++; return Task.FromResult(new[] { IPAddress.Loopback }); });
        var connection = new AdaPanelSourceConnection
        {
            BaseUrl = "https://source.example.com:" + port, ApiToken = Token,
            AcceptedCertificateFingerprint = acceptedPin ? TrustFingerprint.FromCertificate(certificate) : ""
        };
        try
        {
            if (acceptedPin) Assert.True((await native.DiscoverAsync(connection, deadline.Token)).Complete);
            else
            {
                var error = await Assert.ThrowsAsync<AppException>(() => native.DiscoverAsync(connection, deadline.Token));
                Assert.Equal("migration.tls.untrusted", error.MessageKey);
                Assert.Equal(TrustFingerprint.FromCertificate(certificate), error.Details);
            }
            await server.WaitAsync(deadline.Token);
            Assert.Equal("source.example.com", sni); Assert.Equal(1, resolutions);
            Assert.Equal(acceptedPin ? "Bearer " + Token : "", authorization);
        }
        catch (AppException error) when (error.MessageKey == "migration.source.connection_failed")
        {
            await server.WaitAsync(deadline.Token);
            throw new InvalidOperationException("Local TLS server failure: " + serverFailure?.ToString(), error);
        }
        finally { deadline.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } }
    }

    private static AdaPanelSourceConnection Connection() => new() { BaseUrl = "https://source.example.com:8888", ApiToken = Token };
    private static HttpResponseMessage JsonResponse<T>(T payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
        response.Headers.Add(AdaPanelMigrationSourceProtocol.VersionHeader, AdaPanelMigrationSourceProtocol.Version); return response;
    }
    private static HttpResponseMessage ZipResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        response.Headers.Add(AdaPanelMigrationSourceProtocol.VersionHeader, AdaPanelMigrationSourceProtocol.Version); return response;
    }
    private static byte[] Package(PortableMigrationProvider provider = PortableMigrationProvider.Adapanel)
    {
        var manifest = new PortableMigrationManifest
        {
            PackageId = Guid.NewGuid(), Provider = provider, CreatedAtUtc = DateTimeOffset.UtcNow, SourceName = "fixture-source", Complete = true,
            Sites = [new() { Id = "site:one", Name = "example.test", ContentPath = "sites/one", FileCount = 0, HostNames = ["example.test"] }]
        };
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return bytes.ToArray();
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "AdaPanel.NativeLive." + Guid.NewGuid().ToString("N"));
        internal readonly Handler Handler = new();
        internal readonly PortableMigrationPackageStore Store;
        internal readonly AdaPanelLiveMigrationSource Source;
        internal int Resolutions;
        internal IPAddress[]? Inspected;
        internal Fixture(IPAddress? address = null, PortableMigrationPackageOptions? limits = null)
        {
            var runtime = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root });
            var options = limits ?? new();
            Store = new(runtime, Microsoft.Extensions.Options.Options.Create(options));
            Source = new(runtime, options, Store, (_, _) => { Resolutions++; return Task.FromResult(new[] { address ?? IPAddress.Parse("8.8.8.8") }); },
                (_, addresses) => { Inspected = addresses; return Handler; });
        }
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal HttpResponseMessage Response = new(HttpStatusCode.InternalServerError);
        internal int Calls;
        internal string Url = "", Authorization = "", Method = "", Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Url = request.RequestUri!.AbsoluteUri; Authorization = request.Headers.Authorization!.ToString(); Method = request.Method.Method;
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token); return Response;
        }
    }
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int Reads;
        internal bool Disposed;
        internal Action? OnRead;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Reads++; var read = base.ReadAsync(buffer, token); OnRead?.Invoke(); return read; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
