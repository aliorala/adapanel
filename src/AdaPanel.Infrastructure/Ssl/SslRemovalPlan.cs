namespace AdaPanel.Infrastructure.Ssl;

/// <summary>
/// IIS'ten OKUNMUŞ tek bir HTTPS bağlaması. <see cref="BindingInformation"/> IIS'in kendi biçimidir:
/// <c>ip:port:host</c> (host boş olabilir — host başlıksız bağlama).
/// </summary>
public sealed class IisHttpsBinding
{
    public string SiteName { get; init; } = string.Empty;

    public string BindingInformation { get; init; } = string.Empty;

    /// <summary>
    /// Bağlamaya iliştirilmiş sertifikanın parmak izi (küçük harf, ayraçsız).
    /// Boş = sertifika YOK <b>ya da</b> okunamadı; ayrımı <see cref="CertificateReadable"/> yapar.
    /// </summary>
    public string Thumbprint { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="Thumbprint"/> alanı GERÇEKTEN okunabildi mi.
    /// </summary>
    /// <remarks>
    /// Okuma betiği parmak izini <c>try { ... } catch { }</c> içinde alıyordu ve hem "sertifika
    /// iliştirilmemiş" hem de "okuyamadım" durumu aynı BOŞ dizeye düşüyordu — bu deponun
    /// tekrarlanan hata sınıfının (hata ile boş sonucun aynı görüntüye düşmesi) sertifika
    /// tarafındaki örneği. Ayrım burada taşınır: <c>true</c> + boş parmak izi = bağlamada
    /// sertifika YOK (kesin), <c>false</c> = hiçbir şey iddia edilemez.
    /// <para>
    /// Varsayılan <c>false</c>'tur (fail-safe): alanı doldurmayan bir okuyucu, yanlışlıkla
    /// "ölçtüm" demiş olmaz.
    /// </para>
    /// </remarks>
    public bool CertificateReadable { get; init; }

    /// <summary>Sertifikanın bulunduğu depo ("My" / "WebHosting").</summary>
    public string StoreName { get; init; } = string.Empty;

    public string HostName => Parca(2);

    public int Port => int.TryParse(Parca(1), out var port) ? port : 0;

    private string Parca(int index)
    {
        // IPv6 addresses contain colons; only the last two separators delimit port/host.
        var hostStart = BindingInformation.LastIndexOf(':');
        var portStart = hostStart > 0 ? BindingInformation.LastIndexOf(':', hostStart - 1) : -1;
        if (portStart < 0) return string.Empty;
        return index == 2 ? BindingInformation[(hostStart + 1)..].Trim()
            : BindingInformation[(portStart + 1)..hostStart].Trim();
    }
}

/// <summary>
/// HTTPS bağlaması kaldırılırken verilecek kararlar. IIS'e DOKUNMAYAN saf mantık: hangi bağlama
/// hedeflenecek, siteyi erişilemez bırakır mı, sertifikayı başkası kullanıyor mu.
/// <para>
/// Ayrı durmasının sebebi: bu kararlar panelin en yıkıcı SSL işlemini yönetiyor ve IIS kurulu
/// olmayan bir makinede de SINANABİLMELİ. "Başka site kullanıyorsa silme" kuralı tahmin değil
/// ÖLÇÜM olmalıdır; ölçümün doğru yorumlandığını da ancak test kanıtlar.
/// </para>
/// </summary>
public static class SslRemovalPlan
{
    /// <summary>Hedef bağlama bulunamadı.</summary>
    public const string HostNotFoundKey = "ssl.binding_not_found";

    /// <summary>Sitede birden çok HTTPS bağlaması var; hangisinin kaldırılacağı belirtilmeli.</summary>
    public const string HostNameRequiredKey = "ssl.host_name_required";

    /// <summary>
    /// Kaldırılacak bağlamayı seçer.
    /// <para>
    /// Host adı verilmişse ONA göre; verilmemişse yalnızca sitede o portta TEK BİR HTTPS bağlaması
    /// varsa ona. Birden fazlaysa seçim yapılmaz: yıkıcı bir işlemde "herhalde bunu kastetti" demek,
    /// yanlış alan adının HTTPS'ini kapatmak demektir.
    /// </para>
    /// </summary>
    public static (IisHttpsBinding? Target, string ErrorKey) SelectTarget(
        IReadOnlyCollection<IisHttpsBinding> siteBindings,
        string? hostName,
        int port)
    {
        var portlular = siteBindings.Where(binding => binding.Port == port).ToArray();
        if (portlular.Length == 0)
        {
            return (null, HostNotFoundKey);
        }

        var istenen = (hostName ?? string.Empty).Trim();
        if (istenen.Length == 0)
        {
            return portlular.Length == 1
                ? (portlular[0], string.Empty)
                : (null, HostNameRequiredKey);
        }

        var eslesen = portlular.FirstOrDefault(binding =>
            string.Equals(binding.HostName, istenen, StringComparison.OrdinalIgnoreCase));

        return eslesen is null ? (null, HostNotFoundKey) : (eslesen, string.Empty);
    }

    /// <summary>
    /// Bu bağlama kaldırılırsa sitede HTTPS bağlaması KALMAZ mı? Kalmıyorsa ve "Force HTTPS"
    /// yönlendirmesi açıksa site tamamen erişilemez hâle gelir (HTTP → HTTPS'e yollanır, HTTPS
    /// dinlenmez): tarayıcı "bağlantı kurulamadı" der ve kullanıcı sebebini panelde göremez.
    /// </summary>
    public static bool IsLastHttpsBindingOfSite(IReadOnlyCollection<IisHttpsBinding> siteBindings, IisHttpsBinding target) =>
        !siteBindings.Any(binding => !AynıBağlama(binding, target));

    /// <summary>
    /// Aynı sertifikayı (parmak izi) kullanan DİĞER bağlamalar — hedef bağlama hariç, TÜM siteler
    /// dahil. Boş değilse sertifika depodan SİLİNMEZ: silmek, o siteleri anında bozardı.
    /// </summary>
    public static string[] FindOtherUsersOfCertificate(
        IReadOnlyCollection<IisHttpsBinding> allBindings,
        IisHttpsBinding target)
    {
        if (string.IsNullOrWhiteSpace(target.Thumbprint))
        {
            // Parmak izini ÖLÇEMEDİK. "Kimse kullanmıyor" DEMEYİZ: ölçemediğimiz şeye dayanarak
            // sertifika silmek, bilmediğimiz bir siteyi bozmak olurdu.
            return ["(parmak izi okunamadi)"];
        }

        return allBindings
            .Where(binding => !AynıBağlama(binding, target))
            .Where(binding => !binding.CertificateReadable ||
                string.Equals(binding.Thumbprint, target.Thumbprint, StringComparison.OrdinalIgnoreCase))
            .Select(binding => $"{binding.SiteName} ({binding.BindingInformation})")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool AynıBağlama(IisHttpsBinding left, IisHttpsBinding right) =>
        string.Equals(left.SiteName, right.SiteName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.BindingInformation, right.BindingInformation, StringComparison.OrdinalIgnoreCase);
}
