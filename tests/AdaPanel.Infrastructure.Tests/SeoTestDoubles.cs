using System.Net;
using System.Text;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SEO tarayıcısının testlerinde kullanılan SAHTE sunucu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerçek ağ yok:</b> tarayıcıdan beklenen davranışların çoğu (istek atmama, sınırlar,
/// iptal) ancak "hangi isteklerin ATILDIĞI" ölçülerek doğrulanabilir. Sahte işleyici bunu kayıt
/// altına alır; ayrıca test, ağın/IIS'in kurulu olmasına bağlı kalmaz.
/// </para>
/// <para>
/// Eşzamanlılık ölçümü için <see cref="MaxConcurrent"/> tutulur: aynı anda kaç isteğin uçuşta
/// olduğu, sınırın gerçekten uygulandığının tek dürüst kanıtıdır.
/// </para>
/// </remarks>
internal sealed class SeoFakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly object gate = new();
    private int active;

    public List<Uri> Requests { get; } = [];

    /// <summary>Aynı anda uçuşta olan en fazla istek sayısı.</summary>
    public int MaxConcurrent { get; private set; }

    /// <summary>Her isteğe yapay gecikme (zaman aşımı/eşzamanlılık ölçümleri için).</summary>
    public TimeSpan Delay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (gate)
        {
            Requests.Add(uri);
            active++;
            MaxConcurrent = Math.Max(MaxConcurrent, active);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return responder(request, uri.AbsolutePath);
        }
        finally
        {
            lock (gate)
            {
                active--;
            }
        }
    }

    public static HttpResponseMessage Html(string html) => Text(html, "text/html");

    public static HttpResponseMessage Text(string body, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };

        return response;
    }

    public static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(string.Empty) };
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode code)
        => new(code) { Content = new StringContent(string.Empty) };
}
