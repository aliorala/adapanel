namespace AdaPanel.Core.Dtos.Waf;

public sealed class ApplyFolderAclRequest
{
    public string Principal { get; set; } = string.Empty;

    public string Rights { get; set; } = "M";
}
