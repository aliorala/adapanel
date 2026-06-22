using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteBindingRequest
{
    [Required]
    [RegularExpression("^(http|https)$")]
    public string Protocol { get; init; } = "http";

    [StringLength(255)]
    public string HostName { get; init; } = string.Empty;

    [StringLength(64)]
    public string IpAddress { get; init; } = "*";

    [Range(1, 65535)]
    public int Port { get; init; } = 80;
}
