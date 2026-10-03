namespace AdaPanel.Core.Dtos.Diagnostics;

public sealed class SendErrorReportsRequest
{
    public string ExpectedSettingsRevision { get; set; } = string.Empty;
}
