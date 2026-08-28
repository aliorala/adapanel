namespace AdaPanel.Core.Dtos.Files;

public sealed class WriteFileRequest
{
    public string Path { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> ise dosya YALNIZCA YOKSA yazılır; varsa istek <c>files.already_exists</c> ile
    /// REDDEDİLİR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gerekti:</b> "yeni dosya" düğmesi bu uca bağlı ve uç var olan dosyanın üstüne
    /// yazıyor. Panelde adın kullanılıp kullanılmadığına bakan denetim İSTEMCİDE: kullanıcının
    /// gördüğü liste eskimişse ya da ikinci bir oturum aynı anda dosyayı oluşturmuşsa,
    /// "oluştur" demek var olan dosyayı SIFIRLAMAK olur. Yarışı kapatabilecek tek yer, dosyayı
    /// yaratan işlemin kendisidir.
    /// </para>
    /// <para>
    /// Varsayılan <c>false</c>: düzenleyicinin "kaydet" akışı aynı ucu kullanıyor ve orada
    /// üstüne yazmak İSTENEN davranıştır. Bayrağı yalnız oluşturma akışı gönderir.
    /// </para>
    /// </remarks>
    public bool FailIfExists { get; set; }
}
