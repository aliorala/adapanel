using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Databases;

public sealed class GrantDatabaseAccessRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string Engine { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string DatabaseName { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string UserName { get; init; } = string.Empty;

    [StringLength(255)]
    public string AllowedHost { get; init; } = "%";

    public string[] Privileges { get; init; } = [];
}
