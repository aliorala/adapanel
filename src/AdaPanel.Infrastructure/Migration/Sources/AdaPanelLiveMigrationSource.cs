using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Migration.Sources;

public sealed class AdaPanelLiveMigrationSource : IAdaPanelLiveMigrationSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly IOptions<PanelRuntimeOptions> runtime;
    private readonly PortableMigrationPackageOptions limits;
    private readonly IMigrationPackageStore packages;
    private readonly OutboundAddressGuard.OutboundHostResolver resolver;
    private readonly Func<RemoteCertificateTrustGate, IPAddress[], HttpMessageHandler>? handlerFactory;

    public AdaPanelLiveMigrationSource(IOptions<PanelRuntimeOptions> runtime, IOptions<PortableMigrationPackageOptions> options, IMigrationPackageStore packages)
        : this(runtime, options.Value, packages, OutboundAddressGuard.SystemResolver) { }
    internal AdaPanelLiveMigrationSource(IOptions<PanelRuntimeOptions> runtime, PortableMigrationPackageOptions limits,
        IMigrationPackageStore packages, OutboundAddressGuard.OutboundHostResolver resolver,
        Func<RemoteCertificateTrustGate, IPAddress[], HttpMessageHandler>? handlerFactory = null)
    { this.runtime = runtime; this.limits = limits; this.packages = packages; this.resolver = resolver; this.handlerFactory = handlerFactory; }

    public async Task<MigrationSourceInventoryDto> DiscoverAsync(AdaPanelSourceConnection connection, CancellationToken cancellationToken = default)
    {
        var trust = new RemoteCertificateTrustGate(connection.AcceptedCertificateFingerprint);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var client = await ClientAsync(connection, trust, deadline.Token);
            using var request = Request(connection, HttpMethod.Get, AdaPanelMigrationSourceProtocol.InventoryPath);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            ValidateResponse(response);
            if (response.Content.Headers.ContentType?.MediaType != "application/json") throw Error("protocol_invalid");
            if (response.Content.Headers.ContentLength > limits.MaxManifestBytes) throw Error("inventory_limit");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var bounded = new BoundedReadStream(source, limits.MaxManifestBytes);
            var envelope = await JsonSerializer.DeserializeAsync<ApiResponse<MigrationSourceInventoryDto>>(bounded, Json, deadline.Token);
            if (envelope is not { Success: true, Data.Provider: "adapanel" }) throw Error("inventory_invalid");
            return envelope.Data;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Error("timeout"); }
        catch (HttpRequestException) { throw TransportError(trust); }
        catch (JsonException) { throw Error("inventory_invalid"); }
    }

    public async Task<PortableMigrationPackage> AcquireAsync(AdaPanelSourceConnection connection, MigrationExportRequest selection, CancellationToken cancellationToken = default)
    {
        var trust = new RemoteCertificateTrustGate(connection.AcceptedCertificateFingerprint);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromHours(6));
        PortableMigrationPackage? uploaded = null;
        try
        {
            using var client = await ClientAsync(connection, trust, deadline.Token);
            using var request = Request(connection, HttpMethod.Post, AdaPanelMigrationSourceProtocol.ExportPath);
            request.Content = JsonContent.Create(selection, options: Json);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            ValidateResponse(response);
            if (response.Content.Headers.ContentType?.MediaType != "application/zip") throw Error("protocol_invalid");
            if (response.Content.Headers.ContentLength is < 22 || response.Content.Headers.ContentLength > limits.MaxUploadBytes) throw Error("artifact_limit");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var bounded = new BoundedReadStream(source, limits.MaxUploadBytes);
            uploaded = await packages.UploadAsync(bounded, deadline.Token);
            if (response.Content.Headers.ContentLength is { } expected && bounded.Position != expected) throw Error("artifact_truncated");
            if (uploaded.Manifest.Provider != PortableMigrationProvider.Adapanel) throw Error("provider_invalid");
            deadline.Token.ThrowIfCancellationRequested();
            return uploaded;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { await CleanupAsync(); throw Error("timeout"); }
        catch (HttpRequestException) { await CleanupAsync(); throw TransportError(trust); }
        catch { await CleanupAsync(); throw; }

        async Task CleanupAsync()
        {
            if (uploaded != null) await packages.DeleteAsync(uploaded.Id, CancellationToken.None);
        }
    }

    private async Task<HttpClient> ClientAsync(AdaPanelSourceConnection connection, RemoteCertificateTrustGate trust, CancellationToken token)
    {
        var origin = Origin(connection.BaseUrl);
        if (string.IsNullOrEmpty(connection.ApiToken) || !connection.ApiToken.StartsWith("adap_", StringComparison.Ordinal) || connection.ApiToken.Length is < 6 or > 4096 || connection.ApiToken.Any(char.IsWhiteSpace) || connection.ApiToken.Any(char.IsControl))
            throw Error("token_invalid");
        var inspection = await OutboundAddressGuard.InspectAsync(origin.Host, resolver,
            OutboundTargetPolicy.Connecting(runtime.Value.AllowInternalOutboundTargets), token);
        if (inspection.BlockedTarget != null || inspection.Addresses.Length == 0)
            throw new AppException("migration.host_forbidden", 403, 403);
        var handler = handlerFactory?.Invoke(trust, inspection.Addresses) ?? BuildHandler(runtime, trust, inspection.Addresses);
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static SocketsHttpHandler BuildHandler(IOptions<PanelRuntimeOptions> runtime, RemoteCertificateTrustGate trust, IPAddress[] inspected)
    {
        // TLS still sees the original URI host (SNI/name validation). Only the socket DNS resolver
        // is replaced by this operation's inspected address snapshot, closing the rebinding gap.
        var handler = OutboundConnectGuard.CreateHandler(
            () => OutboundTargetPolicy.Connecting(runtime.Value.AllowInternalOutboundTargets),
            (_, _) => Task.FromResult(inspected), certificateValidator: trust.AsCallback());
        handler.AllowAutoRedirect = false;
        return handler;
    }
    internal static Uri Origin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || uri.Port < 1) throw Error("https_origin_required");
        return uri;
    }
    private static HttpRequestMessage Request(AdaPanelSourceConnection connection, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(Origin(connection.BaseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiToken);
        request.Headers.Accept.Add(new(path == AdaPanelMigrationSourceProtocol.InventoryPath ? "application/json" : "application/zip"));
        return request;
    }
    private static void ValidateResponse(HttpResponseMessage response)
    {
        // Never read/forward an untrusted failure body: it can echo Authorization or source secrets.
        if (response.StatusCode == HttpStatusCode.NotFound) throw Error("upgrade_required", 409);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) throw Error("unavailable", 503);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw Error("authorization_failed", 403);
        if (response.StatusCode == HttpStatusCode.Conflict) throw Error("export_busy", 409);
        if ((int)response.StatusCode is >= 300 and < 400) throw Error("redirect_forbidden");
        if (!response.IsSuccessStatusCode) throw Error("request_failed");
        if (!response.Headers.TryGetValues(AdaPanelMigrationSourceProtocol.VersionHeader, out var versions)) throw Error("protocol_version_unsupported", 409);
        var advertised = versions.Take(2).ToArray();
        if (advertised.Length != 1 || advertised[0] != AdaPanelMigrationSourceProtocol.Version) throw Error("protocol_version_unsupported", 409);
    }
    private static AppException TransportError(RemoteCertificateTrustGate trust) => trust.Failure is { } failure
        ? new("migration.tls.untrusted", 409, 409, failure.Fingerprint)
        : Error("connection_failed");
    private static AppException Error(string code, int status = 400) => new("migration.source." + code, status, status);

    private sealed class BoundedReadStream(Stream source, long limit) : Stream
    {
        private long consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => consumed; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = source.Read(buffer, offset, count); Count(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = await source.ReadAsync(buffer, token); Count(read); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        private void Count(int count) { if (count > limit - consumed) throw Error("stream_limit"); consumed += count; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // The response owner disposes the source stream; wrapper disposal must not preempt it.
    }
}
