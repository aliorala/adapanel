using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Support;
using FluentFTP;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// FTP yedek hedeflerinin şema kararı (<c>ftp</c> düz, <c>ftps</c> açık TLS).
/// </summary>
/// <remarks>
/// <para>
/// <b>Varsayılan <c>ftp</c> (düz) KALIYOR ve gerekçesi:</b> mevcut kurulumların hedef adresleri
/// <c>ftp://…</c> olarak kayıtlı; varsayılanı TLS'e çevirmek TLS konuşmayan (NAS, eski paylaşımlı
/// barındırma) sunucuları bir sonraki gece yedeğinde sessizce kırardı. Bunun yerine yönetici
/// <c>ftps://</c> yazar (arayüzde "FTPS (TLS)" seçeneği) ve o hedefte TLS ZORUNLUDUR: sunucu
/// <c>AUTH TLS</c>'e yanıt vermezse bağlantı kesilir, düz FTP'ye DÜŞÜLMEZ.
/// </para>
/// </remarks>
internal static class BackupFtpScheme
{
    public const string Plain = "ftp";

    public const string Secure = "ftps";

    public static bool Is(Uri? uri) => uri is not null && uri.IsAbsoluteUri && uri.Scheme is Plain or Secure;

    public static bool IsSecure(Uri uri) => string.Equals(uri.Scheme, Secure, StringComparison.Ordinal);

    /// <summary>Şemasız port (ftps için Uri <c>-1</c> verir): açık FTPS da 21'dedir.</summary>
    public static int Port(Uri uri) => uri.Port > 0 ? uri.Port : 21;

    /// <summary>
    /// <c>FtpWebRequest</c> için bağlantı adresi: ad yerine DOĞRULANMIŞ IP. İstek bu adrese
    /// gittiği için ikinci bir DNS çözümlemesi (rebinding) yoktur.
    /// </summary>
    public static Uri PinToAddress(Uri uri, IPAddress address)
        => new UriBuilder(uri) { Scheme = Plain, Host = address.ToString(), Port = Port(uri) }.Uri;
}

/// <summary>
/// Adı BİR KEZ çözüp denetleyen ve bağlanılacak IP'yi veren taraf (G19: DNS rebinding).
/// </summary>
/// <remarks>
/// Her komut/yükleme denemesi kendi çözümlemesini yapar ve DENETLENEN adrese bağlanır; denetim ile
/// bağlantı arasında ikinci bir ad çözümlemesi yoktur. Engelliyse <c>backup.remote_target_forbidden</c>.
/// </remarks>
internal interface IBackupFtpAddressPinner
{
    Task<IPAddress> ResolveAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>Şemaya göre doğru FTP komut uygulamasına yönlendirir.</summary>
internal sealed class RoutingBackupFtpCommands(IBackupFtpCommands plain, IBackupFtpCommands secure) : IBackupFtpCommands
{
    internal IBackupFtpCommands Plain => plain;

    private IBackupFtpCommands For(Uri uri) => BackupFtpScheme.IsSecure(uri) ? secure : plain;

    public Task<FtpCommandResult> DeleteFileAsync(Uri uri, NetworkCredential credential, CancellationToken cancellationToken)
        => For(uri).DeleteFileAsync(uri, credential, cancellationToken);

    public Task<FtpListingResult> ListDirectoryAsync(Uri directoryUri, NetworkCredential credential, CancellationToken cancellationToken)
        => For(directoryUri).ListDirectoryAsync(directoryUri, credential, cancellationToken);

    public Task<FtpCommandResult> RemoveDirectoryAsync(Uri directoryUri, NetworkCredential credential, CancellationToken cancellationToken)
        => For(directoryUri).RemoveDirectoryAsync(directoryUri, credential, cancellationToken);
}

/// <summary>
/// FTPS (açık TLS) istemcisi: IP'ye bağlanır, PASV yanıtındaki adresi yok sayar, sertifikayı özgün
/// ada karşı doğrular. Hiçbir "her sertifikayı kabul et" yolu yoktur.
/// </summary>
internal static class BackupFtpsClient
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Kurulumu ÜRETİM AYARLARIYLA yapar. <c>internal</c>: güvenlik kararlarının BAĞLI olduğu
    /// ölçülebilsin (aynı gerekçe <c>FileMigrationService.FtpIstemcisiOlustur</c>'da).
    /// </summary>
    internal static AsyncFtpClient Create(Uri uri, IPAddress address, NetworkCredential credential, TimeSpan stallTimeout)
    {
        var config = new FtpConfig
        {
            // Explicit: AUTH TLS başarısız olursa FluentFTP bağlantıyı bırakır; kimlik bilgisi
            // HİÇ gönderilmez (Auto düz FTP'ye düşer — migrasyon testlerinde ölçülmüştü).
            EncryptionMode = FtpEncryptionMode.Explicit,
            DataConnectionEncryption = true,
            ValidateAnyCertificate = false,
            // PASV yanıtındaki IP YOK SAYILIR: veri bağlantısı kontrol bağlantısının IP'sine açılır.
            // Aksi hâlde kötü niyetli/ele geçmiş sunucu "227 (10,0,0,5,…)" diyerek paneli iç ağa
            // bağlayabilirdi (FTP bounce / SSRF).
            DataConnectionType = FtpDataConnectionType.PASVEX,
            ConnectTimeout = (int)ConnectTimeout.TotalMilliseconds,
            DataConnectionConnectTimeout = (int)ConnectTimeout.TotalMilliseconds,
            ReadTimeout = (int)stallTimeout.TotalMilliseconds,
            DataConnectionReadTimeout = (int)stallTimeout.TotalMilliseconds
        };

        var client = new AsyncFtpClient(address.ToString(), credential.UserName, credential.Password, BackupFtpScheme.Port(uri), config);
        var expectedHost = uri.DnsSafeHost;
        client.ValidateCertificate += (_, args) => args.Accept = AcceptCertificate(args.Certificate, args.PolicyErrors, expectedHost);
        return client;
    }

    /// <summary>
    /// Sertifika kararı: zincir ve süre geçerli, ad ÖZGÜN adla eşleşmeli. Sabitleme/istisna yok.
    /// </summary>
    internal static bool AcceptCertificate(X509Certificate? certificate, SslPolicyErrors errors, string expectedHost)
        => certificate is not null
           && CertificateHostMatcher.ForgiveNameMismatchFor(certificate, errors, expectedHost) == SslPolicyErrors.None;

    internal static string RemotePath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return path.Length == 0 ? "/" : path;
    }

    internal static async Task UploadAsync(
        Uri uri,
        IPAddress address,
        NetworkCredential credential,
        string localPath,
        TimeSpan totalTimeout,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(totalTimeout);
        await using var client = Create(uri, address, credential, stallTimeout);
        try
        {
            await client.Connect(total.Token);
            var status = await client.UploadFile(
                localPath,
                RemotePath(uri),
                FtpRemoteExists.Overwrite,
                createRemoteDir: true,
                FtpVerify.None,
                progress: null,
                token: total.Token);
            if (status != FtpStatus.Success)
            {
                throw new AppException("backup.remote_upload_failed", 502, 502, $"Url={Sanitized(uri)}\r\nFTPS upload status={status}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new WebException("FTPS transfer timed out.", WebExceptionStatus.Timeout);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or System.Net.Sockets.SocketException)
        {
            // Geçici ağ hatası: ortak yeniden deneme döngüsü WebException ile çalışır.
            throw new WebException(exception.Message, exception, WebExceptionStatus.ConnectionClosed, null);
        }
        catch (Exception exception)
        {
            // TLS reddi (sertifika), kimlik doğrulama, AUTH TLS yok… Sebep teşhis metninde kalır.
            throw new AppException("backup.remote_upload_failed", 502, 502, $"Url={Sanitized(uri)}\r\n{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string Sanitized(Uri uri)
        => new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.ToString();
}

/// <summary>FTPS ile silme/listeleme/dizin kaldırma (FluentFTP). Yanıt kodu korunur; istisna fırlatmaz.</summary>
internal sealed class FluentFtpBackupCommands(IBackupFtpAddressPinner pinner) : IBackupFtpCommands
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    public Task<FtpCommandResult> DeleteFileAsync(Uri uri, NetworkCredential credential, CancellationToken cancellationToken)
        => RunAsync(uri, credential, "DELE", cancellationToken);

    public Task<FtpCommandResult> RemoveDirectoryAsync(Uri directoryUri, NetworkCredential credential, CancellationToken cancellationToken)
        => RunAsync(directoryUri, credential, "RMD", cancellationToken);

    private Task<FtpCommandResult> RunAsync(Uri uri, NetworkCredential credential, string verb, CancellationToken cancellationToken)
        => BackupFtpRetry.RunAsync(token => RunOnceAsync(uri, credential, verb, token), cancellationToken,
            result => !result.Success && BackupFtpRetry.IsTransientCode(result.Code));

    private async Task<FtpCommandResult> RunOnceAsync(Uri uri, NetworkCredential credential, string verb, CancellationToken cancellationToken)
    {
        // Engelli/çözülemeyen hedef ISTISNA olarak çıkar (AppException): bu bir "sunucu reddetti"
        // değil, panelin KENDİ kararıdır ve yutulmamalı.
        var address = await pinner.ResolveAsync(uri, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CommandTimeout);
        await using var client = BackupFtpsClient.Create(uri, address, credential, CommandTimeout);
        try
        {
            await client.Connect(deadline.Token);
            var reply = await client.Execute($"{verb} {BackupFtpsClient.RemotePath(uri)}", deadline.Token);
            var code = int.TryParse(reply.Code, out var parsed) ? parsed : FtpCommandResult.NoReplyCode;
            return reply.Success ? FtpCommandResult.Ok(code, reply.Message ?? string.Empty) : FtpCommandResult.Fail(code, reply.Message ?? string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FtpCommandResult.Fail(FtpCommandResult.NoReplyCode, exception.Message.Trim());
        }
    }

    public Task<FtpListingResult> ListDirectoryAsync(Uri directoryUri, NetworkCredential credential, CancellationToken cancellationToken)
        => BackupFtpRetry.RunAsync(token => ListOnceAsync(directoryUri, credential, token), cancellationToken,
            result => !result.Success && BackupFtpRetry.IsTransientCode(result.Code));

    private async Task<FtpListingResult> ListOnceAsync(Uri uri, NetworkCredential credential, CancellationToken cancellationToken)
    {
        var address = await pinner.ResolveAsync(uri, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CommandTimeout);
        await using var client = BackupFtpsClient.Create(uri, address, credential, CommandTimeout);
        try
        {
            await client.Connect(deadline.Token);
            // NLST (düz ad listesi) — gerekçe FtpWebRequestCommands.ListDirectoryOnceAsync'te.
            var names = await client.GetNameListing(BackupFtpsClient.RemotePath(uri), deadline.Token);
            return new FtpListingResult(true, names.Select(name => name.Trim()).Where(name => name.Length > 0).ToArray(), 226, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var code = exception is FluentFTP.Exceptions.FtpCommandException command && int.TryParse(command.CompletionCode, out var parsed)
                ? parsed
                : FtpCommandResult.NoReplyCode;
            return new FtpListingResult(false, [], code, exception.Message.Trim());
        }
    }
}
