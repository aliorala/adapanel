using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Core.Dtos.Common;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Cluster;

/// <summary>Tek bir düğüme yapılan çağrının sonucu. Başarısızlık bir DEĞERDİR, istisna değil.</summary>
/// <param name="ErrorKey">Boşsa çağrı başarılı.</param>
/// <param name="Detail">Teşhis metni (zaman aşımı, HTTP kodu, TLS hatası). Belirteç İÇERMEZ.</param>
public readonly record struct ClusterCallResult<T>(T? Value, string ErrorKey, string Detail, long DurationMs)
{
    public bool Succeeded => ErrorKey.Length == 0;
}

/// <summary>Uzak düğümle konuşan taraf. Ayrı arayüz: toplu görünüm ağdan bağımsız test edilebilsin.</summary>
public interface IClusterNodeClient
{
    /// <summary>Düğümün kendi raporunu çeker. HİÇBİR koşulda istisna fırlatmaz (çağıran iptali hariç).</summary>
    Task<ClusterCallResult<ClusterNodeInfoDto>> FetchInfoAsync(
        ClusterNodeRecord node,
        string token,
        CancellationToken cancellationToken);

    /// <summary>Düğüme durum değiştiren bir çağrı yapar (ör. site oluşturma).</summary>
    Task<ClusterCallResult<JsonElement>> PostAsync(
        ClusterNodeRecord node,
        string token,
        string relativePath,
        object payload,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ClusterNodeClient(
    ClusterHttpClientFactory clientFactory,
    ILogger<ClusterNodeClient> logger) : IClusterNodeClient
{
    /// <summary>
    /// Düğüm başına zaman aşımı.
    /// </summary>
    /// <remarks>
    /// Varsayılan <c>HttpClient</c> 100 saniye bekler. Merkez panosunu bir düğümün ölümü yüzünden
    /// bir buçuk dakika kilitlemek, panelin kendisini çökmüş göstermek olurdu. Sekiz saniye,
    /// yavaş ama çalışan bir sunucuyu yakalamaya yeter, ölü bir sunucuda ise kullanıcıyı bekletmez.
    /// </remarks>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Durum değiştiren çağrılar için zaman aşımı. Site oluşturma uzak sunucuda IIS/DNS/PHP
    /// işlerini tetikler; sekiz saniye yetmez.
    /// </summary>
    public static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(90);

    private const string NodeInfoPath = "api/v1/cluster/node-info";

    public async Task<ClusterCallResult<ClusterNodeInfoDto>> FetchInfoAsync(
        ClusterNodeRecord node,
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);

        return await SendAsync<ClusterNodeInfoDto>(
            node,
            token,
            () => new HttpRequestMessage(HttpMethod.Get, NodeInfoPath),
            ProbeTimeout,
            cancellationToken);
    }

    public async Task<ClusterCallResult<JsonElement>> PostAsync(
        ClusterNodeRecord node,
        string token,
        string relativePath,
        object payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);

        return await SendAsync<JsonElement>(
            node,
            token,
            () => new HttpRequestMessage(HttpMethod.Post, relativePath)
            {
                Content = JsonContent.Create(payload)
            },
            MutationTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Ortak gönderim. Her hata yolu bir DEĞERE çevrilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden istisna fırlatmıyor:</b> bu metodun çağıranı, on düğümü paralel yoklayan toplu
    /// görünümdür. Tek bir düğümün istisnası <c>Task.WhenAll</c> üzerinden yukarı çıksaydı BİR ölü
    /// düğüm, dokuz sağlam düğümün satırını da götürürdü — yani panonun varlık sebebini yok ederdi.
    /// </para>
    /// <para>
    /// <b>Zaman aşımı iki katmanlı:</b> <c>HttpClient.Timeout</c> ve ayrıca bağlı bir
    /// <c>CancellationTokenSource</c>. Yalnız birincisi olsaydı, gövdeyi damla damla gönderen bir
    /// sunucuda okuma aşaması sürerken iptal çalışmazdı; yalnız ikincisi olsaydı çağıran isteği
    /// iptal ettiğinde ayrımı yapamazdık.
    /// </para>
    /// </remarks>
    private async Task<ClusterCallResult<T>> SendAsync<T>(
        ClusterNodeRecord node,
        string token,
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ClusterCallResult<T>(default, ClusterMessages.TokenRequired, string.Empty, 0);
        }

        var saat = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        try
        {
            using var client = clientFactory.Create(node.Address, node.VerifyTlsCertificate, timeout);
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            if (!response.IsSuccessStatusCode)
            {
                // Uzak panelin kendi mesaj anahtarını taşıyoruz: "neden reddedildi" sorusunun cevabı
                // ORADA. Gövde okunamazsa yalnız HTTP kodu döner (uydurma yapılmaz).
                var remoteKey = await TryReadMessageAsync(response, linked.Token);
                var detail = $"HTTP {(int)response.StatusCode}"
                    + (remoteKey.Length > 0 ? $" ({remoteKey})" : string.Empty);

                return new ClusterCallResult<T>(default, ClusterMessages.RemoteRejected, detail, saat.ElapsedMilliseconds);
            }

            var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(linked.Token);
            if (envelope is null || !envelope.Success)
            {
                return new ClusterCallResult<T>(
                    default,
                    ClusterMessages.RemoteRejected,
                    envelope?.Message ?? "empty_body",
                    saat.ElapsedMilliseconds);
            }

            return new ClusterCallResult<T>(envelope.Data, string.Empty, string.Empty, saat.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Merkez paneli kapanıyor / kullanıcı sayfadan ayrıldı: bu bir düğüm arızası DEĞİL.
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ClusterCallResult<T>(default, ClusterMessages.NodeUnreachable, "timeout", saat.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Düğüme ulaşılamadı: {Label} ({Address}).", node.Label, node.Address);
            return new ClusterCallResult<T>(
                default,
                ClusterMessages.NodeUnreachable,
                Describe(exception),
                saat.ElapsedMilliseconds);
        }
    }

    private static async Task<string> TryReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>(cancellationToken);
            return envelope?.Message ?? string.Empty;
        }
        catch
        {
            // Uzak taraf JSON dönmediyse (ör. ters vekilin HTML hata sayfası) sessizce geç.
            return string.Empty;
        }
    }

    /// <summary>
    /// İstisnayı KISA ve sır içermeyen bir teşhis metnine çevirir.
    /// </summary>
    /// <remarks>
    /// Ham <c>exception.Message</c> yazılmıyor: TLS ve soket hatalarının iç metinleri sunucu
    /// adları, sertifika konuları ve iç IP'ler içerebilir; bu metin denetim kaydına ve arayüze gider.
    /// SSRF reddi ayrıca ayırt ediliyor — "engellendi" ile "ulaşılamadı" farklı olaylardır.
    /// </remarks>
    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException http when http.Message == ClusterMessages.AddressForbidden => "blocked_address",
        HttpRequestException http => $"network:{http.HttpRequestError}",
        JsonException => "invalid_json",
        _ => exception.GetType().Name
    };
}
