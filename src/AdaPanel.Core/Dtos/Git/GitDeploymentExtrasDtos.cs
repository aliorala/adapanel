using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Git;

/// <summary>Bir dağıtımın geçmiş kaydı. Sır içermez.</summary>
public sealed class GitDeployHistoryDto
{
    public string Id { get; set; } = string.Empty;

    public DateTime DeployedAtUtc { get; set; }

    /// <summary>manual | webhook | rollback</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>Dağıtımdan ÖNCEKİ HEAD (tam SHA); geri alma hedefi budur. İlk dağıtımda boş.</summary>
    public string PreviousCommit { get; set; } = string.Empty;

    public string NewCommit { get; set; } = string.Empty;

    public bool Success { get; set; }

    /// <summary>Dağıtım öncesi çalışma ağacında izlenen dosyalarda kaç yerel değişiklik vardı (reset bunları ezerdi).</summary>
    public int LocalChangeCount { get; set; }

    /// <summary>İlk 20 yerel değişiklik ("M yol").</summary>
    public string[] LocalChanges { get; set; } = [];

    /// <summary>Yerel değişikliklerin yedeklendiği git referansı (refs/adapanel/saved/...); yedeklenmediyse boş.</summary>
    public string SavedChangesRef { get; set; } = string.Empty;

    /// <summary>Derleme sonrası komutların özeti ("composer-install: ok"); komut yoksa boş.</summary>
    public string PostDeploy { get; set; } = string.Empty;
}

/// <summary>Özel depo HTTPS jetonu. Jeton YALNIZ yazılır; hiçbir yanıtta geri dönmez.</summary>
public sealed class SetGitHttpsTokenRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Username { get; set; } = "x-access-token";

    [Required]
    [StringLength(512, MinimumLength = 1)]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Derleme sonrası komutlar: SERBEST KABUK YOK; yalnız sunucudaki beyaz listeden kimlikler
/// (composer-install, npm-ci, npm-run-build).
/// </summary>
public sealed class SetGitPostDeployRequest
{
    [MaxLength(5)]
    public string[] Commands { get; set; } = [];

    [Range(30, 1800)]
    public int TimeoutSeconds { get; set; } = 600;
}

public sealed class RollbackGitRequest
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string DeploymentId { get; set; } = string.Empty;
}

/// <summary>Yapılandırmayı kaldırma: YIKICI (geçmiş + yedeklenen yerel değişiklikler gider), site adı onayı gerekir.</summary>
public sealed class RemoveGitDeploymentRequest
{
    [Required]
    [StringLength(260)]
    public string ConfirmSiteName { get; set; } = string.Empty;
}

public sealed class GitRemovalResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public string[] Removed { get; set; } = [];

    /// <summary>Sunucuda kalan her şey; boş değilse kaldırma TAM değildir.</summary>
    public string[] Leftovers { get; set; } = [];

    /// <summary>Bilinçli bırakılanlar (site dosyaları, eski kiracı .git kopyaları).</summary>
    public string[] Kept { get; set; } = [];
}
