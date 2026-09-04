namespace AdaPanel.Core.Dtos.Components;

public sealed class ComponentInstallResultDto
{
    public string ComponentId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="Version"/> kurulumdan sonra DİSKTEN OKUNDU mu.
    /// </summary>
    /// <remarks>
    /// <c>false</c> ise bu alan bir ölçüm değil, manifest'in İSTEDİĞİ sürümdür. Eskiden ayrım
    /// yoktu: panel her kurulumda istenen sürümü "kuruldu" diye kaydediyor, güncelleme önbelleği
    /// de o değerden besleniyordu — yaması çıkmış bir sürüm "güncel" görünebiliyordu.
    /// Bkz. <c>ComponentVersionProbe</c>.
    /// </remarks>
    public bool VersionMeasured { get; init; }

    public string Status { get; init; } = string.Empty;

    public string InstallDir { get; init; } = string.Empty;

    public string ServiceName { get; init; } = string.Empty;

    public string GeneratedPassword { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

