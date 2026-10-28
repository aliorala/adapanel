namespace AdaPanel.Core.Dtos.Databases;

public sealed class DatabaseDumpRequest
{
    public string Engine { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    /// <summary>Opt-in: bind stored objects to the authenticated import account, without changing SQL SECURITY.</summary>
    public bool AdaptDefiners { get; set; }

    /// <summary>Export a separate portable dump whose explicit DEFINER accounts are CURRENT_USER.</summary>
    public bool PortableDefiners { get; set; }
}
