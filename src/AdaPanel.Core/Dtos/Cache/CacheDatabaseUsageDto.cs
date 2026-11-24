namespace AdaPanel.Core.Dtos.Cache;

public sealed class CacheDatabaseUsageDto
{
    public int Index { get; set; }
    public long Keys { get; set; }
    public long? Expires { get; set; }
    public long? AverageTtlMilliseconds { get; set; }
}
