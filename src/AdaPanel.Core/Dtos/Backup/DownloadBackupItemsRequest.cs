using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Backup;

/// <summary>Download selected entries without writing them into any site or restore directory.</summary>
public sealed class DownloadBackupItemsRequest
{
    [Required]
    public string BackupId { get; set; } = string.Empty;

    [Required, MinLength(1)]
    public string[] Items { get; set; } = [];

    public string EncryptionPassword { get; set; } = string.Empty;
}

/// <summary>The caller owns this private temporary ZIP stream and must dispose it.</summary>
public sealed class BackupItemsDownload(Stream content, string fileName, int itemCount) : IDisposable
{
    public Stream Content { get; } = content;
    public string FileName { get; } = fileName;
    public int ItemCount { get; } = itemCount;
    public void Dispose() => Content.Dispose();
}
