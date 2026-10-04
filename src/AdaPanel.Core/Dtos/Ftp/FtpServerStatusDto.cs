namespace AdaPanel.Core.Dtos.Ftp;

public sealed class FtpServerStatusDto
{
    public string ServerAddress { get; set; } = string.Empty;

    public int ControlPort { get; set; } = 21;

    public int PassivePortStart { get; set; } = 50000;

    public int PassivePortEnd { get; set; } = 51000;

    /// <summary>
    /// FTP GERÇEKTEN HİZMET VERİYOR MU — "kurulum betiği hata vermedi" DEĞİL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eskiden bu alan koşulsuz <c>true</c> yazılıyordu: <c>EnsureServerAsync</c> altyapı betiği
    /// patlamadıysa "hazır" diyordu. 19.08.2026'da bu makinede ölçülen sonuç: panel
    /// <c>isConfigured=true, statusMessage=ftp.server_ready</c> döndürürken 21 portuna bağlanan
    /// istemciyi <b>FileZilla Server 0.9.59</b> karşılıyordu (ham banner ile doğrulandı) ve panelin
    /// açtığı iki gerçek kullanıcı doğru parolayla bile <c>530</c> alıyordu. Yani panelin tek durum
    /// göstergesi, hiç kimsenin giriş yapamadığı bir sunucu için yeşil yanıyordu.
    /// </para>
    /// <para>
    /// Alanın anlamı bilinçli olarak "yapılandırıldı"dan "yapılandırıldı VE hizmet veriyor"a
    /// genişletildi, çünkü panelin bu değeri okuyan tek tüketicisi onu doğrudan
    /// "hazır / kullanılamıyor" rozetine çeviriyor. Ayrıntı (hangisi bozuk) <see cref="StatusMessage"/>
    /// ve aşağıdaki alanlarda.
    /// </para>
    /// <para>
    /// <b>ÖLÇÜLEMEDİ ≠ BOZUK:</b> ölçüm yapılamazsa (ör. <c>Get-NetTCPConnection</c> yok) bu alan
    /// hazır olduğu iddia edilmez: bu alan <c>false</c>, <see cref="StatusMessage"/> ise
    /// <c>ftp.server_status_unknown</c> olur. Bilinmeyen ölçüm nötr gösterilmeli; ölçülmüş bir
    /// arızayla karıştırılmamalıdır. Önceki kurulum bayrağı güncel sağlık kanıtı değildir.
    /// </para>
    /// </remarks>
    public bool IsConfigured { get; set; }

    /// <summary>Durumun SEBEBİ (i18n anahtarı): <c>ftp.server_ready</c>, <c>ftp.control_port_conflict</c>, …</summary>
    public string StatusMessage { get; set; } = "ftp.server_ready";

    /// <summary>Microsoft FTP Service (FTPSVC) durumu — ölçülemezse boş.</summary>
    public string ServiceState { get; set; } = string.Empty;

    /// <summary>IIS FTP sitesinin durumu (<c>Started</c>/<c>Stopped</c>/…) — ölçülemezse boş.</summary>
    public string SiteState { get; set; } = string.Empty;

    /// <summary>IIS FTP sitesi var ve <c>Started</c> mı.</summary>
    public bool IsSiteRunning { get; set; }

    /// <summary>
    /// Denetim portunu panelin FTP servisi DIŞINDA bir süreç de tutuyor mu.
    /// </summary>
    /// <remarks>
    /// Bu makinede ölçülen durum tam olarak budur ve neden "site çalışıyor mu" sorusunun YETMEDİĞİNİ
    /// gösterir: <c>Get-Website</c> siteyi <c>Started</c> raporluyor, FTPSVC <c>Running</c> ve
    /// 21 portunda GERÇEKTEN dinliyor (pid 5068) — ama aynı portta FileZilla Server da dinliyor
    /// (pid 5036) ve gelen bağlantıyı o karşılıyor. Yani tek arıza belirtisi portun PAYLAŞILMASI.
    /// </remarks>
    public bool HasControlPortConflict { get; set; }

    /// <summary>Çakışmada portu tutan yabancı süreç ("FileZilla Server.exe (PID 5036, …)"); yoksa boş.</summary>
    public string ControlPortOwner { get; set; } = string.Empty;
}
