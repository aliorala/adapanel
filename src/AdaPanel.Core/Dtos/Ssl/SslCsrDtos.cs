namespace AdaPanel.Core.Dtos.Ssl;

public sealed class CreateSslCsrRequest
{
    public string HostName { get; set; } = string.Empty;

    /// <summary>Ek alan adlari (SAN). Ana host her zaman SAN'a eklenir.</summary>
    public string[] AltNames { get; set; } = [];

    public string Organization { get; set; } = string.Empty;

    /// <summary>Iki harfli ISO ulke kodu (istege bagli).</summary>
    public string Country { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Locality { get; set; } = string.Empty;

    /// <summary>2048, 3072 veya 4096.</summary>
    public int KeySize { get; set; } = 2048;
}

/// <summary>
/// Sertifika imzalama istegi kaydi. OZEL ANAHTAR ASLA bu DTO'da yer almaz: sunucuda
/// DataProtection ile korunarak kalir ve imzali sertifika icerik aktarilinca depoya gecer.
/// </summary>
public sealed class SslCsrDto
{
    public string Id { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string[] AltNames { get; set; } = [];

    public string Subject { get; set; } = string.Empty;

    public int KeySize { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>"pending" (imzali sertifika bekleniyor) ya da "imported".</summary>
    public string Status { get; set; } = "pending";

    public string ImportedThumbprint { get; set; } = string.Empty;

    /// <summary>PEM bicimli istek; CA'ya gonderilecek metin.</summary>
    public string CsrPem { get; set; } = string.Empty;
}

public sealed class ImportSignedCertificateRequest
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>CA'nin imzaladigi sertifika (PEM metni, DER ya da P7B) Base64 olarak; zincir ayni pakette olabilir.</summary>
    public string Base64Content { get; set; } = string.Empty;

    public int Port { get; set; } = 443;
}

public sealed class SslRenewalActionRequest
{
    public string SiteName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public int Port { get; set; } = 443;

    /// <summary>pause | resume | cancel</summary>
    public string Action { get; set; } = string.Empty;
}
