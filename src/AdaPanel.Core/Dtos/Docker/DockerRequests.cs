using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Docker;

public sealed class PullImageRequest
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Image { get; set; } = string.Empty;
}

public sealed class RunContainerRequest
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Image { get; set; } = string.Empty;

    [StringLength(128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Port eslemeleri, virgulle: "8080:80,8443:443".</summary>
    public string Ports { get; set; } = string.Empty;

    /// <summary>Ortam degiskenleri, satir/virgulle: "KEY=VALUE".</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>--restart unless-stopped uygulansin mi.</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>Konteyneri bir siteyle ilişkilendirir (etiket <c>adapanel.site</c>); site silinince konteyner de temizlenir.</summary>
    [StringLength(260)]
    public string SiteName { get; set; } = string.Empty;

    /// <summary>CPU sınırı, çekirdek sayısı olarak ("0.5", "2"). Boş = sınırsız.</summary>
    [StringLength(8)]
    public string CpuLimit { get; set; } = string.Empty;

    /// <summary>Bellek sınırı ("256m", "2g"). Boş = sınırsız.</summary>
    [StringLength(8)]
    public string MemoryLimit { get; set; } = string.Empty;
}

public sealed class UpdateContainerLimitsRequest
{
    [StringLength(8)]
    public string CpuLimit { get; set; } = string.Empty;

    [StringLength(8)]
    public string MemoryLimit { get; set; } = string.Empty;
}

public sealed class DockerContainerLimitsDto
{
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>Çekirdek sayısı; 0 = sınırsız.</summary>
    public double Cpus { get; set; }

    /// <summary>Bayt; 0 = sınırsız.</summary>
    public long MemoryBytes { get; set; }
}

/// <summary>Site silinirken konteyner temizliğinin ölçülmüş sonucu.</summary>
public sealed class DockerSiteCleanupResult
{
    public bool Installed { get; set; }

    public bool DaemonRunning { get; set; }

    public List<string> Removed { get; set; } = [];

    /// <summary>Silinemeyenler: "ad: sebep".</summary>
    public List<string> Failed { get; set; } = [];
}

public sealed class DockerLogsDto
{
    public string ContainerId { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
}
