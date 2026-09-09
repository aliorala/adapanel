using System.Net;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Bir FTP komutunun HAM sonucu: geçti mi, sunucu hangi yanıt kodunu verdi, ne dedi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden istisna değil de SONUÇ:</b> silme yolunda <c>550</c>'nin ANLAMI karara girer — RFC 959
/// bu kodu "dosya kullanılamıyor" için verir ve bunun içine hem "dosya yok" hem "izne takıldı"
/// GİRER. Yani sınıflandırma bir iş kuralıdır ve ölçülebilir olmalıdır. Oysa
/// <see cref="FtpWebRequest"/> 550'yi <see cref="WebException"/> olarak fırlatır ve testte 550
/// taşıyan bir <see cref="FtpWebResponse"/> ÜRETİLEMEZ (tipin kurucusu iç). Kodu protokolün TEK
/// dokunma noktasında sonuca çevirmek, sınıflandırmayı ağdan bağımsız test edilebilir kılar.
/// </para>
/// </remarks>
/// <param name="Code">
/// Sunucunun yanıt kodu. Yanıt HİÇ alınamadıysa (DNS/soket/zaman aşımı)
/// <see cref="FtpCommandResult.NoReplyCode"/> olur — "0" ile "550" aynı şey değildir: ilkinde
/// sunucu konuşmadı bile.
/// </param>
internal readonly record struct FtpCommandResult(bool Success, int Code, string Description)
{
    /// <summary>Sunucudan yanıt kodu alınamadı (bağlantı hiç kurulamadı / yarıda koptu).</summary>
    public const int NoReplyCode = 0;

    /// <summary>
    /// RFC 959 "Requested action not taken: file unavailable". <b>İKİ ANLAMI VARDIR</b> (dosya yok
    /// / erişim yok) ve hangisi olduğu koddan OKUNAMAZ; ölçmek gerekir.
    /// </summary>
    public const int FileUnavailable = 550;

    public static FtpCommandResult Ok(int code, string description) => new(true, code, description);

    public static FtpCommandResult Fail(int code, string description) => new(false, code, description);
}

/// <summary>Dizin listelemenin sonucu. Başarısızsa <see cref="Names"/> BOŞTUR ve "dizin boş" ANLAMINA GELMEZ.</summary>
internal readonly record struct FtpListingResult(
    bool Success,
    IReadOnlyCollection<string> Names,
    int Code,
    string Description);

/// <summary>
/// Yedek silme yolunun ihtiyaç duyduğu FTP komutları. Enjekte edilebilir olması, 550
/// sınıflandırmasının GERÇEK bir FTP sunucusu olmadan ölçülebilmesi içindir.
/// </summary>
internal interface IBackupFtpCommands
{
    /// <summary>DELE. Sunucu reddederse istisna DEĞİL, kodu taşıyan bir sonuç döner.</summary>
    Task<FtpCommandResult> DeleteFileAsync(Uri uri, NetworkCredential credential, CancellationToken cancellationToken);

    /// <summary>NLST. Dönen adlar sunucudan geldiği gibidir (kimi sunucu tam yol, kimi düz ad verir).</summary>
    Task<FtpListingResult> ListDirectoryAsync(Uri directoryUri, NetworkCredential credential, CancellationToken cancellationToken);

    /// <summary>
    /// BOŞ bir dizini kaldırır (FTP <c>RMD</c>).
    /// </summary>
    /// <remarks>
    /// Kullanıcı bildirimi: budama dosyaları siliyordu ama GÜN KLASÖRLERİ uzak sunucuda boş olarak
    /// birikiyordu. FTP'de <c>RMD</c> yalnız boş dizinde başarılı olur; yani sunucunun kendisi
    /// "içi doluysa silme" güvencesini zaten veriyor — panel yine de ÖNCE listeliyor, çünkü
    /// "boştu ama silinemedi" ile "dolu olduğu için silinmedi" ayrı şeylerdir.
    /// </remarks>
    Task<FtpCommandResult> RemoveDirectoryAsync(Uri directoryUri, NetworkCredential credential, CancellationToken cancellationToken);
}

#pragma warning disable SYSLIB0014
/// <summary>Üretimdeki uygulama: <see cref="FtpWebRequest"/>.</summary>
/// <remarks>
/// <b>SYSLIB0014 bilerek bastırıldı:</b> panelin FTP yükleme yolu da (bkz.
/// <c>BackupService.StoreFtpFileAsync</c>) aynı API'yi kullanıyor; silme yolunu başka bir
/// istemciye taşımak, aynı hedefe iki farklı FTP davranışı (pasif kip, TLS, kimlik) sokardı.
/// </remarks>
internal sealed class FtpWebRequestCommands : IBackupFtpCommands
{
    /// <summary>
    /// Komut zaman aşımı. <see cref="FtpWebRequest"/>'in VARSAYILANI SONSUZDUR ve bu yol otomatik
    /// budamada GLOBAL KİLİDİN içinde çalışır: donmuş bir FTP bağlantısı paneli süresiz kilitler.
    /// Değer, klasör açma (MKD) yolunda kullanılan ölçülmüş süreyle aynı tutuldu.
    /// </summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public async Task<FtpCommandResult> DeleteFileAsync(
        Uri uri,
        NetworkCredential credential,
        CancellationToken cancellationToken)
    {
        var request = Create(uri, WebRequestMethods.Ftp.DeleteFile, credential);
        using var deadline = new FtpRequestDeadline(request, CommandTimeout, cancellationToken);

        try
        {
            using var response = (FtpWebResponse)await request.GetResponseAsync();
            return FtpCommandResult.Ok((int)response.StatusCode, Describe(response.StatusDescription));
        }
        catch (WebException exception)
        {
            // İPTAL, SUNUCU HATASI DEĞİLDİR: Abort() de WebException üretir. Bunu "silinemedi"
            // diye raporlamak, kullanıcının kendi durdurduğu işi hedefin arızası gibi gösterirdi.
            deadline.ThrowIfExpired();
            var (code, description) = Read(exception);
            return FtpCommandResult.Fail(code, description);
        }
    }

    public async Task<FtpCommandResult> RemoveDirectoryAsync(
        Uri directoryUri,
        NetworkCredential credential,
        CancellationToken cancellationToken)
    {
        var request = Create(directoryUri, WebRequestMethods.Ftp.RemoveDirectory, credential);
        using var deadline = new FtpRequestDeadline(request, CommandTimeout, cancellationToken);

        try
        {
            using var response = (FtpWebResponse)await request.GetResponseAsync();
            return FtpCommandResult.Ok((int)response.StatusCode, Describe(response.StatusDescription));
        }
        catch (WebException exception)
        {
            deadline.ThrowIfExpired();
            var (code, description) = Read(exception);
            return FtpCommandResult.Fail(code, description);
        }
    }

    public async Task<FtpListingResult> ListDirectoryAsync(
        Uri directoryUri,
        NetworkCredential credential,
        CancellationToken cancellationToken)
    {
        // NLST (ListDirectory) BİLEREK seçildi: düz ad listesi verir. LIST (ListDirectoryDetails)
        // sunucuya göre değişen insan-okur bir tablo döndürür ve adı oradan ayıklamak, boşluk
        // içeren dosya adlarında yanlış ad üretirdi — yanlış ad "dosya yok" demeye götürür, o da
        // kaydı KAPATIR. Ad çıkarımının yanılabileceği bir yolda "yok" demeyiz.
        var request = Create(directoryUri, WebRequestMethods.Ftp.ListDirectory, credential);
        using var deadline = new FtpRequestDeadline(request, CommandTimeout, cancellationToken);

        try
        {
            using var response = (FtpWebResponse)await request.GetResponseAsync();
            await using var stream = response.GetResponseStream();
            using var reader = new StreamReader(stream);

            var names = new List<string>();
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    names.Add(trimmed);
                }
            }

            return new FtpListingResult(true, names, (int)response.StatusCode, Describe(response.StatusDescription));
        }
        catch (Exception exception) when (exception is WebException or OperationCanceledException)
        {
            deadline.ThrowIfExpired();
            if (exception is not WebException webException) { throw; }
            var (code, description) = Read(webException);
            return new FtpListingResult(false, [], code, description);
        }
    }

    private static FtpWebRequest Create(Uri uri, string method, NetworkCredential credential)
    {
        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = method;
        request.Credentials = credential;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.Timeout = (int)CommandTimeout.TotalMilliseconds;
        return request;
    }

    /// <summary>
    /// Sunucu yanıtını koddan ayırır. Yanıt yoksa kod <see cref="FtpCommandResult.NoReplyCode"/>
    /// kalır: "ulaşamadım" ile "sunucu reddetti" karıştırılmaz.
    /// </summary>
    private static (int Code, string Description) Read(WebException exception)
    {
        if (exception.Response is FtpWebResponse response)
        {
            using (response)
                return ((int)response.StatusCode, Describe(response.StatusDescription, exception.Message));
        }

        return (FtpCommandResult.NoReplyCode, exception.Message.Trim());
    }

    private static string Describe(string? statusDescription, string? fallback = null) =>
        string.IsNullOrWhiteSpace(statusDescription)
            ? (fallback ?? string.Empty).Trim()
            : statusDescription.Trim();
}
#pragma warning restore SYSLIB0014
