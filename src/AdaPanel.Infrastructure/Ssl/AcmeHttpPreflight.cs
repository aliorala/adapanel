using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Ssl;

/// <summary>Test the extensionless HTTP.sys challenge on port 80 from outside the origin network.</summary>
internal static class AcmeHttpPreflight
{
    internal const string ChallengePath = "/.well-known/acme-challenge/";
    private static readonly ConcurrencyLock ListenerGate = new();
    internal static Task<CommandResult> ExclusiveAsync(Func<Task<CommandResult>> issue, CancellationToken token) =>
        ListenerGate.RunExclusiveAsync(ConcurrencyKeys.AcmeHttp, issue, token);
    private static readonly HttpClient MeasurementClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("https://api.globalping.io/v1/"),
        MaxResponseContentBufferSize = 256 * 1024,
        DefaultRequestHeaders = { UserAgent = { new("AdaPanel", "1.0") } }
    };

    internal static async Task VerifySelfHostingAsync(IReadOnlyCollection<string> hosts, CancellationToken token)
    {
        foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!AcmeArgumentRules.IsCommandSafeHost(host) || Uri.CheckHostName(host) != UriHostNameType.Dns
                || !host.Contains('.') || host.Contains('*') || OutboundAddressGuard.IsBlockedHostName(host))
                throw Failed(host, "Geçerli bir genel DNS adı gerekiyor.");

            var asciiHost = new UriBuilder("http", host).Uri.IdnHost;
            var path = ChallengePath + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            // The expected body is independent of the URL and is never sent in the measurement request.
            var expected = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var listener = new HttpListener();
            // Match the prefix and precedence used by the win-acme SelfHosting plugin.
            listener.Prefixes.Add($"http://+:80{ChallengePath}");
            try { listener.Start(); }
            catch (HttpListenerException error)
            {
                throw Failed(host, $"HTTP-01 dinleyicisi 80 numaralı portta açılamadı (Windows {error.ErrorCode}).");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            var serving = ServeAsync(listener, asciiHost, path, expected, deadline.Token);
            try
            {
                await VerifyExternalAsync(MeasurementClient, asciiHost, path, expected, deadline.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw Failed(host, "Dış HTTP ölçümü zaman aşımına uğradı.");
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw Failed(host, $"Dış ölçüm hizmetinden geçerli sonuç alınamadı ({error.GetType().Name}).");
            }
            finally
            {
                deadline.Cancel();
                listener.Close();
                await serving;
            }
        }
    }

    internal static async Task ServeAsync(HttpListener listener, string host, string path, string expected, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(expected);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(token);
                try
                {
                    using var response = context.Response;
                    if (context.Request.HttpMethod != "GET" || context.Request.RawUrl != path ||
                        !host.Equals(context.Request.Url?.IdnHost, StringComparison.OrdinalIgnoreCase))
                    {
                        response.StatusCode = 404;
                        continue;
                    }
                    response.ContentType = "text/plain";
                    response.Headers["Cache-Control"] = "no-store";
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes, token);
                }
                catch (HttpListenerException) when (!token.IsCancellationRequested) { /* Client disconnected. */ }
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    internal static async Task VerifyExternalAsync(HttpClient client, string host, string path, string expected, CancellationToken token)
    {
        var url = new UriBuilder("http", host, 80, path).Uri;
        var seen = new HashSet<Uri>();
        while (seen.Count <= 10 && seen.Add(url))
        {
            var result = await MeasureAsync(client, url, token);
            if (!result.TryGetProperty("statusCode", out var code) || !code.TryGetInt32(out var number))
                throw Failed(host, "Dış ölçüm noktası HTTP yanıtı alamadı. DNS ve TCP 80 erişimini kontrol edin.");
            if (number == 200 && result.TryGetProperty("rawBody", out var body) && body.GetString() == expected)
                return;
            if (number is 301 or 302 or 303 or 307 or 308 && result.TryGetProperty("headers", out var headers)
                && headers.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url, location.GetString(), out var next)
                && next.Scheme is "http" or "https" && next.Port is 80 or 443 && next.HostNameType == UriHostNameType.Dns
                && !OutboundAddressGuard.IsBlockedHostName(next.IdnHost) && next.IdnHost.Contains('.')
                && next.UserInfo.Length == 0 && next.Query.Length == 0 && next.Fragment.Length == 0
                && next.AbsolutePath == path)
            {
                url = next;
                continue;
            }
            throw Failed(host, $"Uzantısız HTTP-01 adresi beklenen içeriği döndürmedi (HTTP {number}).");
        }
        throw Failed(host, "HTTP-01 yönlendirme zinciri tamamlanamadı.");
    }

    private static async Task<JsonElement> MeasureAsync(HttpClient client, Uri url, CancellationToken token)
    {
        var host = url.IdnHost;
        using var start = await client.PostAsJsonAsync("measurements", new
        {
            type = "http", target = host, limit = 1,
            measurementOptions = new { protocol = url.Scheme.ToUpperInvariant(), port = url.Port, request = new { method = "GET", path = url.AbsolutePath } }
        }, token);
        if (start.StatusCode != HttpStatusCode.Accepted)
            throw Failed(host, $"Dış ölçüm hizmeti isteği kabul etmedi (HTTP {(int)start.StatusCode}).");
        using var created = JsonDocument.Parse(await start.Content.ReadAsByteArrayAsync(token));
        var id = created.RootElement.GetProperty("id").GetString();
        if (string.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw Failed(host, "Dış ölçüm hizmeti geçerli bir ölçüm kimliği döndürmedi.");
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(550), token);
            using var reply = await client.GetAsync("measurements/" + id, token);
            if (!reply.IsSuccessStatusCode)
                throw Failed(host, $"Dış ölçüm sonucu alınamadı (HTTP {(int)reply.StatusCode}).");
            using var data = JsonDocument.Parse(await reply.Content.ReadAsByteArrayAsync(token));
            var root = data.RootElement;
            var status = root.GetProperty("status").GetString();
            if (status == "in-progress") continue;
            if (status != "finished" || root.GetProperty("results").GetArrayLength() != 1)
                throw Failed(host, "Dış HTTP ölçümü tamamlanamadı.");
            var result = root.GetProperty("results")[0].GetProperty("result");
            if (!result.TryGetProperty("status", out var outcome) || outcome.GetString() != "finished")
                throw Failed(host, "Dış ölçüm noktası HTTP isteğini tamamlayamadı. DNS ve bağlantıyı kontrol edin.");
            return result.Clone();
        }
    }

    private static AppException Failed(string host, string detail) =>
        new("ssl.http01_preflight_failed", 502, 502, $"{host}: {detail}");
}
