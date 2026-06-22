namespace AdaPanel.Core.Dtos.Store;

public sealed class AppTemplateDto
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Runtime { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public bool RequiresDatabase { get; set; }

    public string DefaultRootFolder { get; set; } = string.Empty;

    public string[] Requirements { get; set; } = [];

    public string[] AutomaticSetup { get; set; } = [];

    public bool Featured { get; set; }
}
