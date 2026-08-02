namespace AdaPanel.Core.Dtos.Settings;

public sealed class PanelPreferencesDto
{
    public string PanelName { get; set; } = "AdaPanel";

    public string DefaultLanguage { get; set; } = "tr";

    public string ThemeMode { get; set; } = "light";

    public string UiDensity { get; set; } = "compact";

    public string DefaultSiteRoot { get; set; } = "C:\\inetpub\\wwwroot";

    public string DefaultBackupRoot { get; set; } = "%ProgramData%\\AdaPanel\\backups";

    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>
    /// Panelin DIŞARIYA açıldığı alan adı (ör. panel.ornek.com). BOŞ = özellik pasif.
    /// <para>
    /// Ne işe yarar: panel varsayılan olarak KENDİNDEN İMZALI bir sertifikayla çalışır. Tarayıcı bu
    /// uyarıyı "devam et" ile geçebilir ama yapay zeka istemcileri (panele /mcp üzerinden kendi
    /// altyapılarından bağlananlar) geçemez — bağlantı sessizce reddedilir. Bu alan doluysa panel,
    /// bu ad için IIS'te bir ters vekil sitesi kurar (443 → yerel panel portu) ve Let's Encrypt
    /// sertifikası alıp bağlar; böylece panel geçerli bir sertifikayla dışarıya açılır.
    /// </para>
    /// <para>
    /// KURAL: burada ÜRÜNE GÖMÜLÜ hiçbir varsayılan alan adı yoktur. Değer daima yöneticiden gelir;
    /// boşsa panel dışarıya açılmaya çalışmaz. Bu alanı IPanelPublicAccessService (panel erişimi
    /// ekranı) yönetir; Ayarlar &gt; Genel formu değerin ÜZERİNE YAZMAZ.
    /// </para>
    /// </summary>
    public string PanelHostName { get; set; } = string.Empty;

    public int PreferredHttpsPort { get; set; } = 8888;

    public bool AllowHttpFallback { get; set; } = true;

    public int LoginLockoutMinutes { get; set; } = 15;

    public string DefaultRuntimeProfile { get; set; } = "auto";

    public bool InstallRuntimeIfMissing { get; set; } = true;

    public bool RestartRequired { get; set; }
}
