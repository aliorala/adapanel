namespace AdaPanel.Infrastructure.Seo;

/// <summary>
/// Taranacak hedefin TAM tanımı. Bu nesne yalnızca <c>SeoScanRunner</c> tarafından, kapsam
/// doğrulamasından geçmiş bir sitenin IIS bağlamalarından üretilir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir tip:</b> "neyi tarayabiliriz" sorusunun cevabı tek yerde durmalı. Konak adı,
/// port ve izinli konak kümesi crawler'ın içine dağılsaydı, birini güncelleyip diğerini unutmak
/// (klasik SSRF açığı) mümkün olurdu.
/// </para>
/// <para>
/// <b>Bağlantı adresi burada YOK — bilerek.</b> Soket her zaman loopback'e bağlanır
/// (<see cref="SeoHttpTransport"/>). Yani hedef nesnesinde bir "adres" alanı olsaydı bile
/// taşımayı etkilemezdi; bu, hedef çözümlemesinde bir hata olsa dahi isteğin makineden
/// ÇIKAMAYACAĞI anlamına gelir.
/// </para>
/// </remarks>
public sealed class SeoCrawlTarget
{
    /// <summary>İsteğin <c>Host</c> başlığı ve TLS SNI değeri — sitenin IIS bağlamasındaki ad.</summary>
    public required string Host { get; init; }

    /// <summary>80 ya da 443 (bkz. <see cref="SeoLinkPolicy.IsAllowedPort"/>).</summary>
    public required int Port { get; init; }

    public required bool UseTls { get; init; }

    /// <summary>
    /// Sitenin SAHİPLENDİĞİ konak adları (tüm bağlamalarındaki host adları). Bir bağlantının
    /// "iç bağlantı" sayılması için konağı bu kümede olmalıdır.
    /// </summary>
    public required IReadOnlySet<string> AllowedHosts { get; init; }

    public string Scheme => UseTls ? "https" : "http";
}
