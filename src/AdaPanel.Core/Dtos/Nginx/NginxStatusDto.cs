namespace AdaPanel.Core.Dtos.Nginx;

public sealed class NginxStatusDto
{
    public bool IsInstalled { get; set; }

    public bool IsRunning { get; set; }

    public bool IsStartupTaskRegistered { get; set; }

    public string Status { get; set; } = "NotInstalled";

    public string Version { get; set; } = string.Empty;

    public string InstallDir { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    public string ListenAddress { get; set; } = "127.0.0.1";

    public int ListenPort { get; set; } = 8088;

    public string ServerName { get; set; } = "_";

    public string UpstreamUrl { get; set; } = "http://127.0.0.1:80";

    public bool CacheEnabled { get; set; }

    public int CacheSizeMb { get; set; } = 256;

    public string LastConfigTest { get; set; } = string.Empty;

    public string AccessUrl { get; set; } = string.Empty;
}
