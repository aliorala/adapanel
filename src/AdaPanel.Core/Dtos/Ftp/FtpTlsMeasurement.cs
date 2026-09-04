namespace AdaPanel.Core.Dtos.Ftp;

/// <summary>
/// Tek bir FTP TLS ayarının ÖLÇÜLEN durumu. Üç değerlidir ve üçü de ayrı anlam taşır.
/// </summary>
public enum FtpTlsPolicyState
{
    /// <summary>Geri okuma yapılamadı. "Uygulanmadı" DEĞİLDİR.</summary>
    Unmeasured = 0,

    /// <summary>IIS'ten okundu ve istenen değerle aynı.</summary>
    Applied = 1,

    /// <summary>IIS'ten okundu ve istenen değerde DEĞİL — yazma sessizce düşmüş.</summary>
    NotApplied = 2
}

/// <summary>
/// FTPS zorunluluğunun (SslAllow/SslRequire) IIS'te GERÇEKTEN yürürlükte olup olmadığının ölçümü.
///
/// <para>
/// <b>NEDEN VAR.</b> <c>SetRequireSslAsync</c> ISTENEN bayrağı geri döndürüyordu; uç de o bayraktan
/// beslenip kullanıcıya <c>panel.ftp.tlsRequired</c> ("FTPS artık zorunlu; düz FTP bağlantıları
/// reddedilecek") diyordu. Oysa altyapı betiği GEVŞEK koşucuyla çalışıyor
/// (<c>RunPowerShellAsync</c>): <c>Set-ItemProperty</c> sonlandırıcı olmayan bir hata verdiğinde
/// betik akmaya devam eder ve <c>powershell.exe</c> 0 ile çıkar. Betiğin içinde zaten bir geri
/// okuma VARDI ama yalnız <c>Write-Warning</c> yazıyordu ve C# tarafı çıktıya HİÇ bakmıyordu —
/// yani ölçüm yapılıp ATILIYORDU (bu deponun tekrarlanan hata sınıfı).
/// </para>
///
/// <para>
/// Bedeli iki yönlüdür ve ikisi de sessizdir:
/// <list type="bullet">
///   <item>Açma düşerse: yönetici "düz FTP reddedilecek" yazısını görür, sunucu düz FTP'yi kabul
///   etmeye DEVAM eder, bütün müşteri parolaları ağda düz metin akar.</item>
///   <item>Kapatma düşerse: site <c>SslRequire</c>'da kalır, istemciler "534 Policy requires SSL"
///   ile kilitlenir, panel "artık isteğe bağlı" der ve yönetici arızayı başka yerde arar.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>SERTİFİKA AYRI ÖLÇÜLÜR.</b> <c>controlChannelPolicy=1</c> (SslRequire) iken
/// <c>serverCertHash</c> boşsa IIS "AUTH TLS"i reddeder: düz bağlantı da yasak, TLS de imkânsız →
/// FTP TÜMDEN kapanır. Altyapı betiğinde sertifika bağlama <c>catch { }</c> içindedir, yani bu
/// durum sessizce oluşabilir. Bu yüzden politika ile sertifika AYRI iki alandır.
/// </para>
///
/// <para>
/// <b>YANLIŞ ALARM ÜRETMEZ.</b> Bazı IIS sürümlerinde <c>Get-ItemProperty</c> bu dotted provider
/// yollarında <c>.Value</c>'yu boş/farklı tipte döndürüyor; eskiden bu yüzden atılan bir istisna
/// çalışan FTP'yi tümden bloke ediyordu (betikteki 2026-06-21 notu). Okunan değer tanınmazsa
/// sonuç <see cref="FtpTlsPolicyState.NotApplied"/> DEĞİL <see cref="FtpTlsPolicyState.Unmeasured"/>
/// olur: bilmediğimizi söyleriz, uydurmayız.
/// </para>
/// </summary>
public sealed record FtpTlsMeasurement(
    FtpTlsPolicyState ControlChannel,
    FtpTlsPolicyState DataChannel,
    FtpTlsPolicyState Certificate,
    string Detail)
{
    /// <summary>Betiğin STDOUT'a bastığı damga. Damga yoksa geri okuma HİÇ çalışmamıştır.</summary>
    public const string Stamp = "ADAPANEL-FTP-TLS=";

    /// <summary>Hiçbir şey ölçülemedi (hızlı yol / betik çıktısı okunamadı).</summary>
    public static FtpTlsMeasurement Olculemedi(string detail = "")
        => new(FtpTlsPolicyState.Unmeasured, FtpTlsPolicyState.Unmeasured, FtpTlsPolicyState.Unmeasured, detail);

    /// <summary>
    /// Politika GERÇEKTEN yürürlükte mi. Yalnız her iki kanal da ölçülüp istenen değerde
    /// bulunduğunda, VE (FTPS zorunluysa) sertifika bağlıyken true olur.
    /// </summary>
    public bool Enforced => ControlChannel == FtpTlsPolicyState.Applied
        && DataChannel == FtpTlsPolicyState.Applied
        && Certificate != FtpTlsPolicyState.NotApplied;

    /// <summary>Ölçüm bir yerde "uygulanmadı" dedi mi (kesin kötü haber).</summary>
    public bool Failed => ControlChannel == FtpTlsPolicyState.NotApplied
        || DataChannel == FtpTlsPolicyState.NotApplied
        || Certificate == FtpTlsPolicyState.NotApplied;

    /// <summary>
    /// Kullanıcıya gösterilecek üç değerli cevap: <c>true</c> yürürlükte, <c>false</c> uygulanmadı,
    /// <c>null</c> ölçülemedi. Panelin sabit yeşil cümlesi artık BUNA bakar.
    /// </summary>
    public bool? Verified => Enforced ? true : Failed ? false : null;

    /// <summary>
    /// Betik çıktısındaki damgadan ölçümü çıkarır.
    /// </summary>
    /// <param name="requireSsl">İstenen politika (true → SslRequire/1, false → SslAllow/0).</param>
    /// <param name="output">Betiğin STDOUT'u.</param>
    public static FtpTlsMeasurement Olc(bool requireSsl, string? output)
    {
        var satir = DamgaSatiri(output);
        if (satir is null)
        {
            // Damga yok: geri okuma hiç çalışmadı. "Uygulanmadı" DEMEYİZ.
            return Olculemedi("FTP TLS geri okuma damgasi bulunamadi.");
        }

        var parcalar = satir.Split('|');
        var ham = new string[3];
        for (var i = 0; i < 3; i++)
        {
            ham[i] = i < parcalar.Length ? parcalar[i].Trim() : string.Empty;
        }

        var beklenenSayi = requireSsl ? "1" : "0";
        var beklenenAd = requireSsl ? "SslRequire" : "SslAllow";

        var control = PolitikaOku(ham[0], beklenenSayi, beklenenAd);
        var data = PolitikaOku(ham[1], beklenenSayi, beklenenAd);
        var certificate = SertifikaOku(ham[2], requireSsl);

        var detay = $"controlChannelPolicy='{ham[0]}', dataChannelPolicy='{ham[1]}', "
            + $"serverCertHash='{(string.IsNullOrEmpty(ham[2]) ? "(okunamadi)" : ham[2])}', beklenen='{beklenenSayi}/{beklenenAd}'";

        return new FtpTlsMeasurement(control, data, certificate, detay);
    }

    private static string? DamgaSatiri(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var satir in output.Split('\n'))
        {
            var kirpik = satir.Trim();
            if (kirpik.StartsWith(Stamp, StringComparison.Ordinal))
            {
                return kirpik[Stamp.Length..];
            }
        }

        return null;
    }

    private static FtpTlsPolicyState PolitikaOku(string ham, string beklenenSayi, string beklenenAd)
    {
        if (string.IsNullOrEmpty(ham))
        {
            // Betik değeri okuyamadı. Bu, ayarın uygulanmadığı anlamına GELMEZ.
            return FtpTlsPolicyState.Unmeasured;
        }

        if (string.Equals(ham, beklenenSayi, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ham, beklenenAd, StringComparison.OrdinalIgnoreCase))
        {
            return FtpTlsPolicyState.Applied;
        }

        // TANINAN bir karşı değer mi? IIS bu alanda yalnız SslAllow(0)/SslRequire(1)/SslAllowUnencrypted...
        // benzeri sayı ya da ad döndürür. Tanımadığımız bir metin (ör. nesne ToString'i) ölçüm
        // sayılmaz — eski "her farklı değer hatadır" davranışı yanlış alarm üretiyordu.
        var tanidik = ham is "0" or "1" or "2"
            || ham.StartsWith("Ssl", StringComparison.OrdinalIgnoreCase);

        return tanidik ? FtpTlsPolicyState.NotApplied : FtpTlsPolicyState.Unmeasured;
    }

    private static FtpTlsPolicyState SertifikaOku(string ham, bool requireSsl)
    {
        if (string.Equals(ham, "yok", StringComparison.OrdinalIgnoreCase))
        {
            // Sertifika okundu ve BAĞLI DEĞİL. FTPS zorunluyken bu, FTP'nin tümden kapanması
            // demektir; isteğe bağlıyken yalnız "AUTH TLS çalışmaz" demektir.
            return requireSsl ? FtpTlsPolicyState.NotApplied : FtpTlsPolicyState.Unmeasured;
        }

        if (string.IsNullOrEmpty(ham))
        {
            return FtpTlsPolicyState.Unmeasured;
        }

        return FtpTlsPolicyState.Applied;
    }
}
