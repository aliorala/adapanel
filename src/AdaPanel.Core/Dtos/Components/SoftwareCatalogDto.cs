namespace AdaPanel.Core.Dtos.Components;

public sealed class SoftwareCatalogDto
{
    public IReadOnlyCollection<ComponentManifestItemDto> Manifest { get; init; } = [];

    public IReadOnlyCollection<ComponentManifestItemDto> Defaults { get; init; } = [];

    public IReadOnlyCollection<InstalledComponentDto> Installed { get; init; } = [];
}

