namespace AdaPanel.Core.Dtos.Git;

/// <summary>
/// Bir sitenin Git tabanlı dağıtım yapılandırması ve son dağıtım durumu.
/// </summary>
public sealed class GitDeploymentDto
{
    public string SiteName { get; set; } = string.Empty;

    public string RepositoryUrl { get; set; } = string.Empty;

    public string Branch { get; set; } = "main";

    public bool Configured { get; set; }

    public string DeployPath { get; set; } = string.Empty;

    public string LastCommit { get; set; } = string.Empty;

    public DateTime? LastDeployUtc { get; set; }

    public string LastDeployStatus { get; set; } = string.Empty;

    /// <summary>Webhook tetikleme adresi (gizli token dahil değil).</summary>
    public string WebhookPath { get; set; } = string.Empty;

    /// <summary>Webhook doğrulaması için paylaşılan gizli anahtar.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Jetonun konacağı istek başlığının adı — arayüz bunu SUNUCUDAN okur, sabit yazmaz.
    /// </summary>
    /// <remarks>
    /// Arayüz adı kendi tarafında sabitleseydi, sunucu başlığın adını değiştirdiğinde panel
    /// kullanıcıya artık okunmayan bir başlık gösterirdi; sonuç, sebebi görünmeyen bir 401 olurdu.
    /// Değerin tek kaynağı <see cref="AdaPanel.Core.Common.GitWebhookToken.HeaderName"/>.
    /// </remarks>
    public string WebhookHeaderName { get; set; } = AdaPanel.Core.Common.GitWebhookToken.HeaderName;

    /// <summary>SSH dağıtım anahtarı var mı (özel anahtar DPAPI ile korunur ve ASLA dönmez).</summary>
    public bool HasDeployKey { get; set; }

    /// <summary>Depo sağlayıcısına "deploy key" olarak eklenecek GENEL anahtar.</summary>
    public string DeployPublicKey { get; set; } = string.Empty;

    public string DeployKeyFingerprint { get; set; } = string.Empty;

    public DateTime? DeployKeyCreatedUtc { get; set; }

    /// <summary>HTTPS jetonu kayıtlı mı (jetonun kendisi ASLA dönmez).</summary>
    public bool HasHttpsToken { get; set; }

    public string HttpsUsername { get; set; } = string.Empty;

    /// <summary>Seçili derleme sonrası komut kimlikleri (sıralı).</summary>
    public string[] PostDeployCommands { get; set; } = [];

    public int PostDeployTimeoutSeconds { get; set; } = 600;

    /// <summary>Seçilebilecek beyaz liste kimlikleri — arayüz bunu SUNUCUDAN okur.</summary>
    public string[] AllowedPostDeployCommands { get; set; } = [];

    /// <summary>Son dağıtımlar (yeniden eskiye, en çok 15).</summary>
    public GitDeployHistoryDto[] RecentDeployments { get; set; } = [];
}
