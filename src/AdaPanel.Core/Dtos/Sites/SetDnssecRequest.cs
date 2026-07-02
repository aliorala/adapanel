namespace AdaPanel.Core.Dtos.Sites;

/// <summary>Bölge DNSSEC'ini aç/kapat isteği.</summary>
public sealed class SetDnssecRequest
{
    public bool Enabled { get; init; }
}
