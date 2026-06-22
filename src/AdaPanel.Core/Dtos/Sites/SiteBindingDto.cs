namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteBindingDto
{
    public string Protocol { get; init; } = string.Empty;

    public string HostName { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public int Port { get; init; }

    public string BindingInformation { get; init; } = string.Empty;
}
