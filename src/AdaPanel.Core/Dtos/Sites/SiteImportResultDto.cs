namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteImportResultDto
{
    public string SiteName { get; set; } = string.Empty;

    public int FilesCopied { get; set; }
}
