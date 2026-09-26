namespace AdaPanel.Infrastructure.Ssl;

/// <summary>
/// Az önce kurulan HTTPS bağlamasının GERÇEKTEN bizim sertifikamızı taşıyıp taşımadığının ölçümü.
///
/// <para>
/// <b>NEDEN VAR.</b> <c>MeasureHttpsBinding</c> yalnız <c>binding.Protocol == "https"</c> ve host
/// adına bakıyordu; <c>SiteBindingDto</c>'da sertifika alanı zaten YOK, yani ölçüm yapısal olarak
/// sertifikayı göremiyordu. Oysa <c>EnsureHttpsBindingAsync</c> betiği bağlamayı ÖNCE kurar
/// (<c>New-WebBinding</c>), sertifikayı SONRA iliştirir (<c>$b.AddSslCertificate</c>). Bu sıralama
/// tek başına şu sonucu doğurur: sertifika iliştirme düşerse geriye <b>sertifikasız bir HTTPS
/// bağlaması</b> kalır — tarayıcı bağlantıyı kapatır (ERR_SSL...), site 443'te ölüdür — ve
/// panelin ölçümü "HTTPS bağlaması var" der, <c>ssl.json</c>'a da öyle yazılır.
/// </para>
///
/// <para>
/// İkinci sessiz yol: betik sertifika deposunu <c>My</c> diye VARSAYAR ve yalnız
/// <c>WebHosting</c>'i alternatif olarak dener. Sertifika ikisinde de bulunamazsa <c>My</c>
/// varsayımıyla bağlama yapılır. Bunun tutup tutmadığını yalnız geri okuma söyler.
/// </para>
///
/// <para>
/// <b>TEK KAYNAK.</b> Ölçüm, deponun ZATEN sahip olduğu sertifika-farkındalıklı okuyucudan
/// beslenir: <c>ReadHttpsBindingsAsync</c> (<c>certificateHash</c> + <c>certificateStoreName</c>).
/// Kaldırma yolu o okuyucuyu doğru kullanıyordu; kurma yolu kullanmıyordu. İkinci bir okuyucu
/// yazmak, iki tarafın sessizce ayrışması demek olurdu.
/// </para>
///
/// <para>
/// IIS'e DOKUNMAZ: saf karar mantığıdır, IIS kurulu olmayan bir makinede de sınanabilir
/// (<see cref="SslRemovalPlan"/> ile aynı gerekçe).
/// </para>
/// </summary>
public static class SslBindingVerification
{
    internal static void Refresh(IEnumerable<AdaPanel.Core.Dtos.Ssl.SslCertificateDto> certificates,
        IReadOnlyCollection<IisHttpsBinding>? bindings)
    {
        foreach (var certificate in certificates)
        {
            var host = certificate.HostName.StartsWith("*.", StringComparison.Ordinal) ? "" : certificate.HostName;
            var result = Olc(bindings, certificate.SiteName, host,
                certificate.Port > 0 ? certificate.Port : 443, certificate.Thumbprint);
            certificate.IsHttpsBindingPresent = result.BindingPresent;
            certificate.IsCertificateBound = result.CertificateBound;
            certificate.BindingDetail = result.Detail;
        }
    }

    /// <summary>
    /// <param name="bindings">
    /// IIS'ten okunan HTTPS bağlamalarının TAMAMI. <c>null</c> = OKUNAMADI (boş liste ile aynı şey
    /// değildir: boş liste "hiç HTTPS bağlaması yok" demektir).
    /// </param>
    /// <param name="siteName">Bağlamanın ait olduğu IIS sitesi.</param>
    /// <param name="hostName">Aranan host; boş ise (joker sertifika) sitedeki herhangi bir bağlama.</param>
    /// <param name="port">Beklenen port.</param>
    /// <param name="thumbprint">Az önce kurduğumuz sertifikanın parmak izi.</param>
    /// </summary>
    /// <returns>
    /// <c>BindingPresent</c>: sitede eşleşen bir HTTPS bağlaması var mı (<c>null</c> = ölçülemedi).
    /// <c>CertificateBound</c>: o bağlama BİZİM sertifikamızı taşıyor mu (<c>null</c> = ölçülemedi).
    /// </returns>
    public static (bool? BindingPresent, bool? CertificateBound, string Detail) Olc(
        IReadOnlyCollection<IisHttpsBinding>? bindings,
        string siteName,
        string hostName,
        int port,
        string thumbprint)
    {
        if (bindings is null)
        {
            // Okuyamadık. "Bağlama yok" DEMEYİZ, "sertifika bağlı değil" hiç demeyiz.
            return (null, null, "IIS baglama listesi okunamadi.");
        }

        var herhangiBiri = string.IsNullOrWhiteSpace(hostName);

        var eslesenler = bindings
            .Where(binding => string.Equals(binding.SiteName, siteName, StringComparison.OrdinalIgnoreCase))
            .Where(binding => binding.Port == port)
            .Where(binding => herhangiBiri
                || string.IsNullOrWhiteSpace(binding.HostName)
                || string.Equals(binding.HostName, hostName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (eslesenler.Length == 0)
        {
            // ÖLÇÜLDÜ ve YOK. Bağlama hiç kurulmamış.
            return (false, false, $"'{siteName}' sitesinde {port} portunda eslesen HTTPS baglamasi bulunamadi.");
        }

        var bizimki = Normalize(thumbprint);

        // Bizim parmak izimizi taşıyan bir bağlama VARSA cevap nettir.
        if (eslesenler.Any(binding => string.Equals(Normalize(binding.Thumbprint), bizimki, StringComparison.OrdinalIgnoreCase)
                && bizimki.Length > 0))
        {
            return (true, true, string.Empty);
        }

        // Bağlama var ama bizim sertifikamız üzerinde değil. Sertifika alanı OKUNABİLDİ mi?
        // Okunamadıysa hiçbir şey iddia etmeyiz — "farklı sertifika" ile "sertifikaya bakamadım"
        // aynı görüntüye düşmemeli.
        if (eslesenler.All(binding => !binding.CertificateReadable))
        {
            return (true, null, $"'{siteName}' baglamasi bulundu ama sertifika alani okunamadi.");
        }

        var okunanlar = eslesenler
            .Where(binding => binding.CertificateReadable)
            .Select(binding => string.IsNullOrEmpty(binding.Thumbprint) ? "(sertifika YOK)" : binding.Thumbprint)
            .ToArray();

        // ÖLÇÜLDÜ: bağlama duruyor ama üzerinde bizim sertifikamız yok. Sertifikasız bir HTTPS
        // bağlaması siteyi 443'te ÖLÜ bırakır; farklı bir sertifika ise tarayıcıda ad uyuşmazlığı
        // verir. İkisi de "SSL kuruldu" demek DEĞİLDİR.
        return (
            true,
            false,
            $"'{siteName}' HTTPS baglamasi bulundu ama sertifika beklenen degil "
            + $"(beklenen={bizimki}, okunan={string.Join(", ", okunanlar)}).");
    }

    private static string Normalize(string? thumbprint)
        => (thumbprint ?? string.Empty).Replace(" ", string.Empty).Replace(":", string.Empty).Trim();
}
