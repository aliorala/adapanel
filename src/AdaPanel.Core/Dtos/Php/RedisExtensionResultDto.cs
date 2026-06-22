namespace AdaPanel.Core.Dtos.Php;

/// <summary>Native phpredis (redis) PHP uzantısını etkinleştirme sonucu.</summary>
public sealed class RedisExtensionResultDto
{
    public bool Enabled { get; set; }

    /// <summary>already | installed</summary>
    public string Message { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
}
