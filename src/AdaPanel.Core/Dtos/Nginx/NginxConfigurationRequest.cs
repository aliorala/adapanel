using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Nginx;

public sealed class NginxConfigurationRequest
{
    [Required]
    [StringLength(64)]
    public string ListenAddress { get; set; } = "127.0.0.1";

    [Range(1, 65535)]
    public int ListenPort { get; set; } = 8088;

    [Required]
    [StringLength(253)]
    public string ServerName { get; set; } = "_";

    [Required]
    [StringLength(2048)]
    public string UpstreamUrl { get; set; } = "http://127.0.0.1:80";

    public bool CacheEnabled { get; set; }

    [Range(16, 8192)]
    public int CacheSizeMb { get; set; } = 256;
}
