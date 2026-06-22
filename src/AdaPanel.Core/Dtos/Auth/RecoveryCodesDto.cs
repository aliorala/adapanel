namespace AdaPanel.Core.Dtos.Auth;

public sealed class RecoveryCodesDto
{
    /// <summary>Yeni üretilen kurtarma kodları (yalnızca bu yanıtta gösterilir).</summary>
    public IReadOnlyCollection<string> Codes { get; set; } = [];
}
