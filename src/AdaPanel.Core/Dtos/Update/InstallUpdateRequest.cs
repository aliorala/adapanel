namespace AdaPanel.Core.Dtos.Update;

public sealed class InstallUpdateRequest
{
    public string ExpectedVersion { get; set; } = string.Empty;

    public string ExpectedPackageSha256 { get; set; } = string.Empty;
}
