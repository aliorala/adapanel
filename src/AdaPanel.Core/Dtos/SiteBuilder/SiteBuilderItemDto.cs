namespace AdaPanel.Core.Dtos.SiteBuilder;

/// <summary>Bir hizmet kartı ya da galeri görseli.</summary>
public sealed class SiteBuilderItemDto
{
    public string Title { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    /// <summary>Yüklenmiş görselin panel içindeki dosya adı. Boş = görselsiz.</summary>
    public string ImageName { get; set; } = string.Empty;
}
