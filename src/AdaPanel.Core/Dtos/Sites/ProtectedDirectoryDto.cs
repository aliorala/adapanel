namespace AdaPanel.Core.Dtos.Sites;

/// <summary>panel "Password-Protected Directories" benzeri korumalı dizin kaydı.</summary>
public sealed class ProtectedDirectoryDto
{
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Site köküne göre yol (örn. "yonetim" veya "alt/klasor").</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Realm { get; set; } = string.Empty;

    /// <summary>
    /// Koruma ŞU AN klasörde GERÇEKTEN duruyor mu. <c>null</c> = ÖLÇÜLEMEDİ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ölçülüyor:</b> koruma tek bir <c>web.config</c> dosyasında yaşar ve o dosyayı
    /// silebilecek yollar panelin kendi içinde sıralanabiliyor: FTP kullanıcısı (klasörde Modify
    /// izni var), dosya yöneticisi, <c>git reset --hard</c> ile dağıtım, SiteBuilder yayını,
    /// WordPress yeniden kurulumu. Panel bunların hiçbirini görmeden kendi kaydına bakıp "korumalı"
    /// diyordu. Bu bir GÜVENLİK YANLIŞ-GÜVENCESİDİR: ekran yeşil görünürken klasör herkese açıktır
    /// ve kullanıcının bunu fark etmesi için bir sebebi yoktur.
    /// </para>
    /// <para>
    /// <b>false ile null aynı şey değildir.</b> false = klasör okundu, koruma dosyası YOK ya da
    /// başkasının dosyasıyla değiştirilmiş. null = ölçüm yapılamadı (site kökü okunamadı, dosya
    /// kilitli); bu durumda kaydı geçersiz saymak, geçici bir arızayı "korumanız gitti" alarmına
    /// çevirmek olurdu.
    /// </para>
    /// </remarks>
    public bool? IsActive { get; set; }
}

public sealed class ProtectDirectoryRequest
{
    public string RelativePath { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Realm { get; set; } = string.Empty;
}
