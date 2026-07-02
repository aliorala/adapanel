namespace AdaPanel.Core.Dtos.Databases;

public sealed class DatabaseRecordDto
{
    public string Engine { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Owner { get; init; } = string.Empty;

    public string Encoding { get; init; } = string.Empty;

    public string Collation { get; init; } = string.Empty;

    /// <summary>Bu veritabanına yetkili (bağlı) kullanıcı adları. Boş = kullanıcısı olmayan (yetim) DB.</summary>
    public IReadOnlyList<string> Users { get; init; } = [];
}
