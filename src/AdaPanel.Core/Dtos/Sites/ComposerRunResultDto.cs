namespace AdaPanel.Core.Dtos.Sites;

public sealed class ComposerRunResultDto
{
    public int ExitCode { get; set; }

    public string Output { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}
