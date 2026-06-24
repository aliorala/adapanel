using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// B3 — Panel HTTPS reverse-proxy: phpMyAdmin/Adminer'i panelin kimlik-doğrulamalı HTTPS'i arkasında sunar.
// Tarayıcı dış *:8896 düz-HTTP portuna gitmez; panele gelir, panel isteği yerel IIS aracına (127.0.0.1) iletir.
// Auth COOKIE tabanlı olduğundan tarayıcının asset/AJAX istekleri panel cookie'sini taşır → [Authorize] geçer.
// [IgnoreAntiforgeryToken]: phpMyAdmin POST'ları (login/sorgu) panelin CSRF token'ını taşımaz; bu uçta doğrulama kapalı.
[ApiController]
[Authorize(Roles = "Administrator")]
[IgnoreAntiforgeryToken]
[Route("api/v1/dbtools")]
public sealed class DbToolsProxyController(
    IWebDbClientService webDbClientService,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    // RFC 7230 hop-by-hop başlıkları + Host: uçtan uca iletilmez.
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host"
    };

    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS")]
    [Route("{tool}/{**path}")]
    [RequestSizeLimit(1_073_741_824)] // 1 GiB: phpMyAdmin SQL import yüklemeleri büyük olabilir.
    public async Task ProxyAsync(string tool, string? path, CancellationToken cancellationToken)
    {
        var provider = tool.Equals("adminer", StringComparison.OrdinalIgnoreCase) ? "adminer" : "phpmyadmin";
        var port = await webDbClientService.GetDeployedToolPortAsync(provider, cancellationToken);
        if (port <= 0)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsync("DB tool is not deployed.", cancellationToken);
            return;
        }

        var query = Request.QueryString.HasValue ? Request.QueryString.Value : string.Empty;
        var targetUri = new Uri($"http://127.0.0.1:{port}/{path}{query}");

        using var message = new HttpRequestMessage(new HttpMethod(Request.Method), targetUri);

        // Gövde: GET/HEAD dışındaki metotlarda istek gövdesini ilet. KÖK NEDEN DÜZELTMESİ — gövde proxy'ye
        // geldiğinde başa-sarılı/okunabilir olmayabiliyordu → StreamContent 0 byte okuyup, kopyalanan
        // "Content-Length: N" ile çelişiyor: "Sent 0 request content bytes, but Content-Length promised N" →
        // panelin 500'ü (phpMyAdmin'in tüm POST AJAX'ları = navigation, her sekme patlardı). Çözüm: gövde erken
        // EnableBuffering ile tekrar-okunabilir yapılır (Program.cs); burada BAŞA SARILIR → gövde artık dolu,
        // tarayıcının gönderdiği Content-Length ile birebir eşleşir (otoriter uzunluk, çelişki bitti).
        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method))
        {
            if (Request.Body.CanSeek)
            {
                Request.Body.Position = 0;
            }

            message.Content = new StreamContent(Request.Body);
            foreach (var header in Request.Headers)
            {
                if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }
        }

        // İstek başlıkları (hop-by-hop, Host ve Content-* hariç — Content-* gövdeye eklendi).
        foreach (var header in Request.Headers)
        {
            if (HopByHop.Contains(header.Key) || header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            message.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        // phpMyAdmin'in mutlak URL'leri (PmaAbsoluteUri) panel host/şemasından üretebilmesi için.
        // Panel SETs (gelen değeri ezerek) → spoof edilemez.
        message.Headers.Remove("X-Forwarded-Proto");
        message.Headers.Remove("X-Forwarded-Host");
        message.Headers.TryAddWithoutValidation("X-Forwarded-Proto", Request.Scheme);
        message.Headers.TryAddWithoutValidation("X-Forwarded-Host", Request.Host.Value);
        message.Headers.TryAddWithoutValidation("X-Forwarded-For", HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty);

        var client = httpClientFactory.CreateClient("DbToolsProxy");
        client.Timeout = TimeSpan.FromMinutes(30); // büyük import/export uzun sürebilir

        using var backendResponse = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        Response.StatusCode = (int)backendResponse.StatusCode;

        CopyResponseHeaders(backendResponse.Headers);
        CopyResponseHeaders(backendResponse.Content.Headers);
        Response.Headers.Remove("transfer-encoding");

        // Panelin global katı CSP'si phpMyAdmin'i bozar (inline/asset). phpMyAdmin kendi CSP'sini gönderiyorsa
        // o korunur; göndermiyorsa panelinkini KALDIR (proxy aynı-köken, güvenilir yerel araç). X-Frame yeni
        // sekmede sorun değil ama framelemeye gerek kalırsa diye kaldırıyoruz.
        var backendHasCsp = backendResponse.Headers.Contains("Content-Security-Policy") ||
                            backendResponse.Content.Headers.Contains("Content-Security-Policy");
        if (!backendHasCsp)
        {
            Response.Headers.Remove("Content-Security-Policy");
        }

        Response.Headers.Remove("X-Frame-Options");

        await backendResponse.Content.CopyToAsync(Response.Body, cancellationToken);
    }

    private void CopyResponseHeaders(System.Net.Http.Headers.HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            if (HopByHop.Contains(header.Key))
            {
                continue;
            }

            // Kestrel bazı başlıkları doğrudan atamada reddedebilir; güvenli tarafta kal.
            try
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }
            catch
            {
                // atlanabilir başlık (ör. korumalı) — yoksay
            }
        }
    }
}
