namespace AdaPanel.Core.Dtos.Notifications;

public sealed class TestNotificationRequest
{
    public string ExpectedSettingsRevision { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
