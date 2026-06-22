using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Backup;

/// <summary>Zip yedeği içindeki tek bir girdi (dizine göre gezinme için).</summary>
public sealed class BackupEntryDto
{
    /// <summary>Bulunulan dizine göre görünen ad.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Arşiv içindeki tam yol (dizinler "/" ile biter).</summary>
    public string Path { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }
}

public sealed class BackupEntriesDto
{
    public string BackupId { get; set; } = string.Empty;

    /// <summary>Gezilen dizin öneki ("" = kök).</summary>
    public string Prefix { get; set; } = string.Empty;

    public BackupEntryDto[] Entries { get; set; } = [];

    /// <summary>Liste üst sınıra takıldıysa true (daha derin dizine inerek daraltın).</summary>
    public bool Truncated { get; set; }
}

public sealed class RestoreBackupItemsRequest
{
    [Required]
    public string BackupId { get; set; } = string.Empty;

    /// <summary>Geri yüklenecek arşiv yolları; dizinler "/" ile bitmelidir.</summary>
    [Required]
    [MinLength(1)]
    public string[] Items { get; set; } = [];

    /// <summary>Boşsa sitenin orijinal kök dizinine geri yüklenir.</summary>
    public string TargetPath { get; set; } = string.Empty;
}

public sealed class RestoreBackupItemsResultDto
{
    public int RestoredCount { get; set; }

    public string TargetRoot { get; set; } = string.Empty;
}
