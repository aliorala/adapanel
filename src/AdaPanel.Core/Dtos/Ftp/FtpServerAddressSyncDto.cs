namespace AdaPanel.Core.Dtos.Ftp;

public sealed class FtpServerAddressSyncDto
{
    public bool Succeeded { get; set; }
    public bool Skipped { get; set; }
    public bool Changed { get; set; }
    public string Error { get; set; } = string.Empty;
    public string? CurrentAddress { get; set; }
}
