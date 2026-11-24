using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Php;

public sealed class SetSitePhpVersionRequest
{
    // Provisioning grants the final tree ACL after selecting a handler. Do not create the
    // protected log marker before that grant. HTTP callers cannot set this internal handoff.
    [global::System.Text.Json.Serialization.JsonIgnore]
    public bool DeferErrorLogging { get; init; }
    [Required]
    [StringLength(128, MinimumLength = 2)]
    public string ComponentId { get; init; } = string.Empty;
}
