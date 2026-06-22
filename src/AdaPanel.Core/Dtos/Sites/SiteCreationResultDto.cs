namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Plesk benzeri birleşik site oluşturma sonucu: oluşturulan site + onunla birlikte
/// kurulan yan kaynaklar (veritabanı, mail domaini, FTP) ve kurulamayan/atlanan uyarılar.
/// </summary>
public sealed class SiteCreationResultDto
{
    public SiteDto Site { get; set; } = new();

    /// <summary>Site ile birlikte başarıyla oluşturulan yan kaynaklar.</summary>
    public List<string> Created { get; set; } = [];

    /// <summary>Oluşturulamayan yan kaynaklar (site yine de kuruldu; best-effort).</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// Otomatik oluşturulan hesapların yalnızca bu yanıtta gösterilen erişim bilgileri.
    /// Parolalar panel ayarlarına veya loglara düz metin olarak yazılmaz.
    /// </summary>
    public List<GeneratedCredentialDto> GeneratedCredentials { get; set; } = [];
}

public sealed class GeneratedCredentialDto
{
    public string Service { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
