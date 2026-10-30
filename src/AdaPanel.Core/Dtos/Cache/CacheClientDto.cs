namespace AdaPanel.Core.Dtos.Cache;

public sealed class CacheClientDto
{
    public string Name { get; set; } = string.Empty;
    public int DatabaseIndex { get; set; }
    public long IdleSeconds { get; set; }
}
