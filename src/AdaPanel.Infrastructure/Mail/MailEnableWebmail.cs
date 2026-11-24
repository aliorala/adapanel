using System.Net;
using System.Net.Security;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableWebmail(JsonFileStore? fileStore = null, AdaPanelDbContext? dbContext = null,
    IIisSiteService? iisSiteService = null, IConcurrencyLock? concurrencyLock = null, MailProviderSelection? providerSelection = null)
{
    internal sealed record Endpoint(string Site, string Host, int Port, string Path, long SiteId = 0,
        string Thumbprint = "", string BindingInformation = "");
    internal Func<string?> InstallationRoot { get; init; } = MailEnableInstallation.DiscoverRoot;
    internal Func<string, CancellationToken, Task<CommandResult>> DiscoveryCommand { get; init; } = ProcessRunner.RunPowerShellAsync;
    internal Func<Endpoint, CancellationToken, Task> EndpointProbe { get; init; } = ProbeEndpointAsync;

    internal static Endpoint Select(IReadOnlyList<Endpoint> endpoints, string publicHost)
    {
        var matching = endpoints.Where(e => e.Host.Equals(publicHost, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length == 1) return matching[0];
        if (endpoints.Count == 1) return endpoints[0];
        throw new AppException(endpoints.Count == 0 ? "mail.webmail.mailenable_not_ready" : "mail.webmail.mailenable_ambiguous", 409, 409);
    }

    public async Task<WebmailOpenDto> OpenAsync(string publicHost, CancellationToken token)
    {
        var inventory = await ReadInventoryAsync(token);
        var endpoints = inventory.Where(s => s.State == "Started").SelectMany(s => TrustedEndpoints(s)).ToArray();
        var endpoint = Select(endpoints, publicHost);
        await EndpointProbe(endpoint, token);
        return new() { Url = EndpointUri(endpoint).AbsoluteUri };
    }

    private static Uri EndpointUri(Endpoint endpoint) => new UriBuilder("https", endpoint.Host, endpoint.Port, endpoint.Path).Uri;

    internal static IPAddress LocalProbeAddress(Endpoint endpoint)
    {
        var information = endpoint.BindingInformation;
        var hostSeparator = information.LastIndexOf(':');
        var portSeparator = hostSeparator > 0 ? information.LastIndexOf(':', hostSeparator - 1) : -1;
        if (portSeparator < 0 || !information[(hostSeparator + 1)..].Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(information[(portSeparator + 1)..hostSeparator], out var port) || port != endpoint.Port) throw NotReady();
        var text = information[..portSeparator].Trim('[', ']');
        IPAddress address;
        if (text is "*" or "0.0.0.0") address = IPAddress.Loopback;
        else if (!IPAddress.TryParse(text, out address!)) throw NotReady();
        if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
        if (!IPAddress.IsLoopback(address) && !NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(local => local.Address.Equals(address))) throw NotReady();
        return address;
    }

    internal static async Task ProbeEndpointAsync(Endpoint endpoint, CancellationToken token)
    {
        var address = LocalProbeAddress(endpoint);
        var certificateMatched = false;
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        handler.ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        // The request URI retains the hostname for TLS SNI, certificate-name validation and HTTP Host.
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            certificateMatched = errors == SslPolicyErrors.None && certificate is not null
                && certificate.GetCertHashString().Equals(endpoint.Thumbprint, StringComparison.OrdinalIgnoreCase);
            return certificateMatched;
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await client.GetAsync(EndpointUri(endpoint), HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK || !certificateMatched)
                throw new AppException("mail.webmail.mailenable_unhealthy", 502, 502);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        { throw new AppException("mail.webmail.mailenable_unhealthy", 502, 502); }
    }

    public async Task<WebmailDiagnosticsDto> DiagnoseAsync(string publicHost, CancellationToken token)
    {
        try
        {
            var opened = await OpenAsync(publicHost, token);
            return new() { Healthy = true, Url = opened.Url, Report = "MailEnable Web Mail: IIS HTTPS login endpoint and certificate validation succeeded." };
        }
        catch (AppException error) { return new() { Healthy = false, Report = error.MessageKey }; }
    }
}
