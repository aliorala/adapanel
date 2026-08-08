namespace AdaPanel.Core.Dtos.Backup;

public sealed class BackupRecordDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public long Size { get; set; }

    public string Status { get; set; } = string.Empty;

    public string TargetType { get; set; } = "local";

    public string RemoteUri { get; set; } = string.Empty;

    /// <summary>
    /// Son geri yüklemenin YARIM KALAN adımları. Boş değilse <see cref="Status"/> da
    /// <c>restored_with_errors</c> olur — "restored" yazıp sorunu yutmak yasaktır.
    /// </summary>
    public List<BackupRestoreWarningDto> Warnings { get; set; } = [];

    /// <summary>
    /// Geri yükleme üzerine yazmadan önce sitenin O ANKİ içeriğinin taşındığı kardeş klasör.
    /// Kullanıcının "geri yükleme yanlış yedekti" dediğinde döneceği tek yer burasıdır.
    /// </summary>
    public string PreservedPath { get; set; } = string.Empty;
}
