namespace AdaPanel.Core.Dtos.Update;

public sealed class ApplyUpdateRequest
{
    public string DownloadUrl { get; set; } = string.Empty;

    public string PackageSha256 { get; set; } = string.Empty;
}
