namespace AdaPanel.Core.Dtos.Cache;

public sealed class CacheStatusDto
{
    public CacheRuntimeDto Redis { get; set; } = new();

    public CacheRuntimeDto Memcached { get; set; } = new();
}
