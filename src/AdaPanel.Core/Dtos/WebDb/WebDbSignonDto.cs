namespace AdaPanel.Core.Dtos.WebDb;

/// <summary>phpMyAdmin'i tek seferlik token ile (signon) açacak URL.</summary>
public sealed class WebDbSignonDto
{
    public string Url { get; set; } = string.Empty;
}
