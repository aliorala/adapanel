namespace AdaPanel.Core.Dtos.Ftp;

public sealed class FtpUserDto
{
    public string UserName { get; set; } = string.Empty;

    public string HomePath { get; set; } = string.Empty;

    public string SiteName { get; set; } = string.Empty;

    public bool ExistsOnSystem { get; set; }

    /*
     * EV KLASÖRÜ ARTIK ÖLÇÜLÜYOR — ve bu, canlı bir sunucuda ölçülmüş bir kör noktanın kapatılmasıdır.
     *
     * OLAY: müşteri FTP'ye bağlanamıyordu. Panel FTP ekranında kullanıcı SAPASAĞLAM görünüyordu;
     * `ExistsOnSystem` true (Windows hesabı gerçekten duruyor), `HomePath` dolu (kayıttan geliyor).
     * Sunucu ise her denemede `530 User cannot log in, home directory inaccessible.` dönüyordu.
     * Panelin gösterdiği hiçbir alan bu durumu yakalayamıyordu, çünkü hiçbiri EV KLASÖRÜNE
     * BAKMIYORDU: `HomePath` bir KAYITTIR, ölçüm değil.
     *
     * IIS FTP kullanıcı izolasyonu (`IsolateAllDirectories`) evi <ftp-kök>\LocalUser\<kullanıcı>
     * yolunda arar; panel oraya siteye giden bir junction koyar. Bu zincirde üç ayrı şey bozulabilir
     * ve üçü de paneli hiç ilgilendirmeden olur: bağlantı hiç oluşmamış olabilir, hedefi silinmiş
     * olabilir (site silindiğinde junction boşa düşer), ya da yol okunamıyor olabilir.
     *
     * ÜÇ DEĞERLİ, ÇÜNKÜ "ÖLÇEMEDİM" İLE "YOK" AYNI ŞEY DEĞİL: `null` = ölçüm yapılamadı (erişim
     * reddi, G/Ç hatası). `false` = ölçüldü ve yok. Bu ayrımı yutmak, panelin bir kez daha
     * bilmediğini biliyormuş gibi göstermesi olurdu — kapatmaya çalıştığımız hatanın ta kendisi.
     */

    /// <summary>IIS FTP'nin bu kullanıcının evi olarak BAKTIĞI yol (kayıttaki hedef değil).</summary>
    public string HomeLinkPath { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="HomeLinkPath"/> diskte var mı. <c>null</c> = ölçülemedi.
    /// </summary>
    public bool? HomeLinkExists { get; set; }

    /// <summary>
    /// Bağlantının gösterdiği hedef diskte var mı. <c>null</c> = ölçülemedi (ya da bağlantı yok).
    /// </summary>
    /// <remarks>
    /// Bağlantı düz bir klasörse hedef kendisidir. Junction ise hedefi AYRI ölçülür: site
    /// silindiğinde junction geride kalır ve "var" görünür, ama gösterdiği yer yoktur.
    /// </remarks>
    public bool? HomeTargetExists { get; set; }

    /// <summary>
    /// Hesabın parola süresi GERÇEKTEN kapatıldı mı. <c>true</c> ölçüldü ve süresiz,
    /// <c>false</c> ölçüldü ve HÂLÂ süre dolumu var, <c>null</c> ölçülemedi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN DTO'DA.</b> <c>EnsurePasswordNeverExpiresAsync</c> bayrağı zaten geri okuyordu ama
    /// sonuç yalnız <c>LogWarning</c>'e gidiyordu; oluşturma yolu her hâlükârda "hesap oluşturuldu"
    /// dönüyordu. Hesap gerçekten oluşturuldu — yalan orada değil. Yalan, <b>söylenmeyende</b>:
    /// makinenin <c>MaxPasswordAge</c>'i (varsayılan 42 gün) hesaba işlerse FTP kırk iki gün sonra
    /// SESSİZCE ölür, müşteri "dün çalışıyordu" der ve kimse paneldeki kaydı şüpheli bulmaz.
    /// Kısmi başarı ADIYLA yazılır: hangi hesap, hangi eksikle açıldı.
    /// </para>
    /// <para>
    /// Listeleme ucunda (<c>GET /ftp/users</c>) <c>null</c>'dır: orası kaydı okur, Windows'a bu
    /// soruyu sormaz. "Sormadık" ile "ölçtük, kapalı" aynı şey değildir.
    /// </para>
    /// </remarks>
    public bool? PasswordNeverExpires { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string ServerAddress { get; set; } = string.Empty;

    public int Port { get; set; } = 21;
}
