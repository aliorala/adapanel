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
        // Accept-Encoding de ATLANIR: upstream'den DAİMA identity (sıkıştırmasız) iste. Böylece HTML yanıt
        // gövdesini güvenle string'e çevirip layout-fix <style>'ını enjekte edebiliriz (gzip'lenmiş gövdeye
        // dokunamazdık) ve gzip/Content-Length uyumsuzluğu riski kalmaz. Loopback'te sıkıştırma kazancı önemsiz.
        foreach (var header in Request.Headers)
        {
            if (HopByHop.Contains(header.Key) ||
                header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
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

        // Araç bu önekin altında yaşıyor; çerezleri de bu yola bağlanmalı.
        var cookiePathPrefix = $"/api/v1/dbtools/{provider}/";
        CopyResponseHeaders(backendResponse.Headers, cookiePathPrefix);
        CopyResponseHeaders(backendResponse.Content.Headers, cookiePathPrefix);
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

        // KALICI LAYOUT DÜZELTMESİ (kök-nedenden bağımsız): phpMyAdmin'in init JS'i (inline AJAX.fireOnload
        // bootstrap) bloklanır/çalışmazsa, içerik margin'i (body margin-left = nav genişliği) JS ile
        // ayarlandığından UYGULANMAZ → ana içerik sabit sol menünün ALTINA kayar (kullanıcı ekran görüntüsü).
        // Proxy'lenen text/html'e küçük bir <style> enjekte ederiz: inline <style> HER CSP altında izinlidir
        // (script DEĞİL), dolayısıyla CSP/yapılandırma/build durumundan BAĞIMSIZ çalışır. :has(#pma_navigation)
        // ile YALNIZ nav'lı tam sayfaya uygulanır (login/hata/AJAX parçası değil); JS çalışırsa kendi inline
        // margin'i (daha yüksek öncelik) bunu sorunsuz ezer → çakışma/çift margin yok, nav resize korunur.
        var mediaType = backendResponse.Content.Headers.ContentType?.MediaType;
        var isHtml = string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase);
        var contentLength = backendResponse.Content.Headers.ContentLength;
        if (ShouldBufferForInjection(isHtml, HttpMethods.IsHead(Request.Method), contentLength))
        {
            await using var upstream = await backendResponse.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await BufferForInjectionAsync(upstream, Response.Body, InjectionBufferLimit, cancellationToken);
            if (bytes is null)
            {
                // Tavan aşıldı: gövde ZATEN akıtıldı, enjeksiyon yapılmadı (ve yapılamazdı).
                return;
            }

            try
            {
                var html = System.Text.Encoding.UTF8.GetString(bytes);
                var injected = InjectLayoutFix(html);
                if (!ReferenceEquals(injected, html))
                {
                    var modified = System.Text.Encoding.UTF8.GetBytes(injected);
                    Response.Headers.Remove("Content-Length");
                    Response.Headers.Remove("Content-Encoding");
                    Response.ContentLength = modified.Length;
                    await Response.Body.WriteAsync(modified, cancellationToken);
                    return;
                }
            }
            catch
            {
                // Beklenmeyen kodlama/biçim → enjeksiyondan vazgeç, orijinal gövdeyi AYNEN gönder.
            }

            await Response.Body.WriteAsync(bytes, cancellationToken);
            return;
        }

        await backendResponse.Content.CopyToAsync(Response.Body, cancellationToken);
    }

    /// <summary>
    /// Layout enjeksiyonu için belleğe alınacak EN ÇOK bayt (16 MiB).
    /// </summary>
    /// <remarks>
    /// Değer değişmedi; değişen, sınırın GERÇEKTEN uygulanmasıdır (aşağıya bakınız).
    /// phpMyAdmin'in en büyük tam sayfası (binlerce tablolu bir veritabanının yapı görünümü)
    /// birkaç megabayttır; 16 MiB geniş bir paydır.
    /// </remarks>
    private const int InjectionBufferLimit = 16 * 1024 * 1024;

    /// <summary>
    /// Yanıt gövdesi enjeksiyon için belleğe ALINMALI mı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>UZUNLUK BİLİNMİYORSA DA ALINIR — ama artık AKARKEN SAYILARAK.</b> Eski koşul
    /// <c>contentLength is null or &lt;= 16 MiB</c> idi ve bu, sınırı bilerek atlanabilir kılıyordu:
    /// <c>Content-Length</c> GÖNDERMEYEN (chunked) bir yanıt <c>null</c> dalına düşüyor ve
    /// <c>ReadAsByteArrayAsync</c> ile SINIRSIZ belleğe alınıyordu. Uç <c>ResponseHeadersRead</c>
    /// ile okuduğu için gövde o ana kadar hiç tüketilmemiş olur; yani 16 MiB'lık sınır, onu aşan
    /// tam da yanıtlarda hiç çalışmıyordu. Sınırı "uzunluğu bilinmeyeni hiç tamponlama" diye
    /// kapatmak yanlış olurdu: phpMyAdmin sayfalarının ÇOĞU chunked gelir, yani layout düzeltmesi
    /// tümüyle ölürdü. Doğru cevap uzunluğa GÜVENMEK değil, OKURKEN SAYMAKTIR.
    /// </para>
    /// </remarks>
    internal static bool ShouldBufferForInjection(bool isHtml, bool isHead, long? contentLength)
        => isHtml && !isHead && contentLength is null or <= InjectionBufferLimit;

    /// <summary>
    /// Gövdeyi en çok <paramref name="limit"/> bayt tamponlar. Sığarsa tamponu döner (çağıran
    /// enjekte edip yazar). SIĞMAZSA <c>null</c> döner ve gövdeyi — okunmuş kısım dahil — olduğu
    /// gibi <paramref name="output"/>'a AKITIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aşan gövde HATA DEĞİLDİR: tarayıcı yanıtı eksiksiz almalıdır. Kaybedilen tek şey layout
    /// <c>&lt;style&gt;</c>'ının enjekte edilmesidir ve o zaten kozmetik bir düzeltmedir — 16 MiB'lık
    /// bir HTML sayfası panelin belleğinden daha pahalıdır.
    /// </para>
    /// <para>
    /// <b>Sınır dışarıdan veriliyor</b> (depodaki <c>FileService</c> bütçe kalıbının aynısı): bir
    /// tavanın gerçekten uygulandığı ancak DOLDURULARAK kanıtlanabilir; üretim değeriyle bu, testte
    /// 16 MiB üretmek demekti.
    /// </para>
    /// </remarks>
    internal static async Task<byte[]?> BufferForInjectionAsync(
        Stream upstream,
        Stream output,
        int limit,
        CancellationToken cancellationToken)
    {
        var tampon = new MemoryStream();
        var parca = new byte[81920];
        while (tampon.Length <= limit)
        {
            var okunan = await upstream.ReadAsync(
                parca.AsMemory(0, (int)Math.Min(parca.Length, limit + 1 - tampon.Length)),
                cancellationToken);
            if (okunan == 0)
            {
                return tampon.ToArray();
            }

            tampon.Write(parca, 0, okunan);
        }

        // Tavan aşıldı: tamponlamaktan VAZGEÇ ve akıt. Okunmuş kısım da yazılır, yoksa gövde
        // başından kırpılmış olurdu.
        tampon.Position = 0;
        await tampon.CopyToAsync(output, cancellationToken);
        await upstream.CopyToAsync(output, cancellationToken);
        return null;
    }

    // phpMyAdmin tam sayfasının margin'ini JS'siz garanti eden inline stil. body margin-left = phpMyAdmin
    // varsayılan NavigationWidth'i (240px). #pma_console (alt SQL konsolu, position:fixed) de aynı şekilde kayar.
    // !important KULLANILMAZ: JS çalışırsa kendi inline margin'i (resize/daralt) bunu ezsin → phpMyAdmin'in
    // nav yeniden-boyutlandırma özelliği korunur; JS ölüyse bu kural devreye girip içeriği menü altından kurtarır.
    private const string LayoutFixStyle =
        "<style id=\"adapanel-pma-layout\">@media screen{" +
        "body:has(#pma_navigation){margin-left:240px}" +
        "body:has(#pma_navigation) #pma_console{margin-left:240px}" +
        "}</style>";

    // Layout-fix <style>'ını </head>'den hemen önce (yoksa </body>) ekler. Zaten varsa ya da uygun kapanış
    // etiketi yoksa (ör. AJAX HTML parçası) girdiyi AYNEN döndürür (referans eşitliği = değişmedi sinyali).
    private static string InjectLayoutFix(string html)
    {
        if (html.Contains("adapanel-pma-layout", StringComparison.Ordinal))
        {
            return html;
        }

        var index = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        }

        return index < 0 ? html : html.Insert(index, LayoutFixStyle);
    }

    private void CopyResponseHeaders(System.Net.Http.Headers.HttpHeaders headers, string? cookiePathPrefix = null)
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
                var values = header.Value.ToArray();

                // ÇEREZ YOLU VEKİL YOLUNA ÇEVRİLİR — yoksa oturum çerezi hiç geri gelmez.
                //
                // Adminer çerez yolunu kendi gördüğü adresten üretiyor:
                //   session_set_cookie_params(0, preg_replace('~\?.*~', '', $_SERVER["REQUEST_URI"]), ...)
                // Vekilin arkasında PHP "/adapanel-signon.php" görür, ama tarayıcı adrese
                // "/api/v1/dbtools/adminer/adapanel-signon.php" olarak gider. İki yol eşleşmediği
                // için tarayıcı çerezi BİR DAHA GÖNDERMEZ: Adminer'ın oturumu her istekte kaybolur
                // ve giriş yaptıktan sonraki ilk sayfa açılmaz.
                //
                // Yolu vekil önekiyle yeniden yazmak, ters vekillerin standart işidir
                // (nginx'te proxy_cookie_path). Aracın kendi kodu değişmez.
                if (cookiePathPrefix is not null && header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    values = values.Select(value => RewriteCookiePath(value, cookiePathPrefix)).ToArray();
                }

                Response.Headers[header.Key] = values;
            }
            catch
            {
                // atlanabilir başlık (ör. korumalı) — yoksay
            }
        }
    }

    /// <summary>
    /// Bir <c>Set-Cookie</c> başlığındaki <c>Path=</c> değerini vekil önekiyle değiştirir.
    /// <para>
    /// Path yoksa eklenir: PHP her zaman yazmayabilir ve o durumda tarayıcı yolu istekten türetir —
    /// aracın alt yolları arasında çerez yine kaybolabilirdi.
    /// </para>
    /// </summary>
    internal static string RewriteCookiePath(string setCookieValue, string pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(setCookieValue))
        {
            return setCookieValue;
        }

        var replaced = System.Text.RegularExpressions.Regex.Replace(
            setCookieValue,
            @"(?i)(;\s*)path\s*=\s*[^;]*",
            $"$1Path={pathPrefix}");

        // Hiç Path yoksa ekle (Regex eşleşmediyse metin aynı kalır).
        return ReferenceEquals(replaced, setCookieValue) || replaced == setCookieValue
            ? $"{setCookieValue}; Path={pathPrefix}"
            : replaced;
    }
}
