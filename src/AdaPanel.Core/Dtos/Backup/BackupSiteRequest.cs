namespace AdaPanel.Core.Dtos.Backup;

public sealed class BackupSiteRequest : BackupArchiveRequest
{
    public string SiteName { get; set; } = string.Empty;

    // --- profesyonel panel benzeri tam-site yedeği kapsamı ---

    /// <summary>Site dosyaları (wwwroot) dahil edilsin mi.</summary>
    public bool IncludeFiles { get; set; } = true;

    /// <summary>IIS/site yapılandırması (binding, app pool, runtime) JSON olarak dahil edilsin mi.</summary>
    public bool IncludeConfig { get; set; } = true;

    /// <summary>Siteye ait mail alan adının hesap/alias listesi (MailEnable) dahil edilsin mi.</summary>
    public bool IncludeMail { get; set; }

    /// <summary>Yedeğe katılacak veritabanları (motor + ad). Boşsa veritabanı eklenmez.</summary>
    public List<BackupDatabaseRef> Databases { get; set; } = [];
}

public sealed class BackupDatabaseRef
{
    public string Engine { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;
}
