using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Security;

public sealed class UnbanIpRequest
{
    [Required]
    public string IpAddress { get; set; } = string.Empty;
}
