namespace AdaPanel.Core.Dtos.Databases;

public sealed class DatabaseTransferResultDto
{
    public string Engine { get; set; } = string.Empty;

    public string SourceDatabase { get; set; } = string.Empty;

    public string TargetDatabase { get; set; } = string.Empty;

    /// <summary>Aktarılan dökümün bayt cinsinden boyutu (bilgi amaçlı).</summary>
    public long Size { get; set; }
}
