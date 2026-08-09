using System.Net.Http.Json;
using System.Text.Json;
using AdaPanel.Core.Dtos.Licensing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Licensing;

/// <summary>Doğrulama sunucusuyla konuşan taraf. Ayrı arayüz: kural ağdan bağımsız test edilebilsin.</summary>
public interface ILicenseVerificationClient
{
    /// <summary>
    /// Sunucuya sorar. <b>Yalnızca</b> HTTP 200 + geçerli gövde bir KARAR sayılır; diğer her şey
    /// (<c>null</c>) "ulaşılamadı" demektir.
    /// </summary>
    Task<LicenseVerificationOutcome> VerifyAsync(LicenseVerificationRequest request, CancellationToken cancellationToken);
}

/// <summary>Doğrulama denemesinin sonucu.</summary>
/// <param name="Response">Sunucunun kararı; ulaşılamadıysa null.</param>
/// <param name="Error">Ulaşılamadıysa sebep (teşhis metni); ulaşıldıysa boş.</param>
public readonly record struct LicenseVerificationOutcome(LicenseVerificationResponse? Response, string Error);

/// <summary>
/// HTTP ile doğrulama.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ağ hatası müşteriyi cezalandırmaz.</b> Zaman aşımı, DNS hatası, TLS hatası, 4xx, 5xx ve bozuk
/// gövde — hepsi "ulaşılamadı" olarak döner ve çağıran çevrimdışı toleransa düşer. Sunucu tarafındaki
/// bir yapılandırma hatasının (yanlış yol → 404) müşterinin panelini kısıtlaması kabul edilemez;
/// bu yüzden 404'ü "geçersiz lisans" saymıyoruz.
/// </para>
/// <para>
/// Yalnız <c>status</c> alanı tanınan bir değer ("valid"/"invalid"/"expired") olduğunda karar
/// üretilir. Tanınmayan bir <c>status</c>, ileride sunucu sözleşmesi genişlerse eski panellerin
/// müşteriyi kilitlememesi için yine "ulaşılamadı" sayılır.
/// </para>
/// </remarks>
public sealed class HttpLicenseVerificationClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LicenseOptions> options,
    ILogger<HttpLicenseVerificationClient> logger) : ILicenseVerificationClient
{
    public async Task<LicenseVerificationOutcome> VerifyAsync(
        LicenseVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.VerificationUrl))
        {
            // Buraya normalde hiç gelinmez (servis yapılandırılmamışken sormaz); yine de savunmacı.
            return new LicenseVerificationOutcome(null, "not_configured");
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 2, 60));

            using var response = await client.PostAsJsonAsync(settings.VerificationUrl, request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new LicenseVerificationOutcome(null, $"http_{(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadFromJsonAsync<LicenseVerificationResponse>(cancellationToken);
            if (payload is null)
            {
                return new LicenseVerificationOutcome(null, "empty_body");
            }

            var status = payload.Status?.Trim().ToLowerInvariant() ?? string.Empty;
            if (status is not (LicenseVerdicts.Valid or LicenseVerdicts.Invalid or LicenseVerdicts.Expired))
            {
                return new LicenseVerificationOutcome(null, $"unknown_status:{status}");
            }

            return new LicenseVerificationOutcome(payload, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // İsteği panel iptal etti (kapanış); lisans durumunu bozma.
            throw;
        }
        catch (Exception exception)
        {
            // Log seviyesi bilerek Warning: bu bir hata değil, TOLERE EDİLEN bir durum.
            logger.LogWarning(exception, "Lisans doğrulama sunucusuna ulaşılamadı; çevrimdışı tolerans uygulanacak.");
            return new LicenseVerificationOutcome(null, Describe(exception));
        }
    }

    private static string Describe(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => "timeout",
            HttpRequestException http => $"network:{http.HttpRequestError}",
            JsonException => "invalid_json",
            _ => exception.GetType().Name
        };
    }
}
