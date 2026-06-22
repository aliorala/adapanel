namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteDto
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public bool AutoStart { get; init; }

    public string PhysicalPath { get; init; } = string.Empty;

    public string ApplicationRootPath { get; init; } = string.Empty;

    public string ApplicationPoolName { get; init; } = string.Empty;

    public IReadOnlyCollection<SiteBindingDto> Bindings { get; init; } = [];
}
