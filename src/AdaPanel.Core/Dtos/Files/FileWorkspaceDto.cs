namespace AdaPanel.Core.Dtos.Files;

public sealed class FileWorkspaceDto
{
    public string Path { get; set; } = string.Empty;

    public string ParentPath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PhysicalPath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public string Content { get; set; } = string.Empty;

    public bool ContentLoaded { get; set; }

    public string ContentUnavailableReasonKey { get; set; } = string.Empty;

    /// <summary>ACL command processing only; not a measurement of effective Windows access.</summary>
    public bool? PermissionsVerified { get; set; }

    public string PermissionsDetail { get; set; } = string.Empty;

    public FileEntryDto[] Entries { get; set; } = [];
}
