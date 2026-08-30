using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using FluentFTP;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace AdaPanel.Infrastructure.Migration;

/// <summary>
/// Uzak sunucudan site dosyalarını çeker: FTP/FTPS için FluentFTP, SFTP için SSH.NET (her ikisi de
/// listeleme/özyinelemeyi güvenli yapar — elle FTP listesi ayrıştırmaya gerek yok). Hedef = sitenin
/// IIS fiziksel kök dizini. Sertifika/host-key doğrulaması göç senaryosu için gevşektir (kullanıcı kendi
/// dosyalarını çekiyor).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden günlükçü (ILogger) var:</b> göç TEK SEFERLİK ve GERİ ALINAMAZ bir iştir — kullanıcı
/// bittiğini sanıp DNS'i çevirdikten sonra eksik gelen dosyalar geri getirilemez. Buradaki en
/// tehlikeli durum "hepsi patladı" değil, KISMİ BAŞARI: FluentFTP tek tek öğeler için hata
/// döndürebilir; bir dosya bile indiyse <see cref="EnsureFilesDownloaded"/> geçer ve panel yeşil
/// "Dosyalar çekildi" gösterir. Kaç öğenin düştüğü ve hangileri olduğu HİÇBİR YERE yazılmıyordu.
/// Aynı sessizlik SFTP'de de var: dizin ve normal dosya dışındaki girdiler (sembolik bağlantı,
/// soket) sayaca hiç girmeden atlanıyor.
/// </para>
/// <para>
/// <b>Sır sızıntısı:</b> bu servisin isteği FTP/SFTP KULLANICI ADI ve PAROLASI taşır. Günlüğe
/// asla <c>request</c> nesnesi ya da kütüphane bağlantı dizesi yazılmaz; adres
/// <see cref="GunlugeUygunHedef"/> ile YENİDEN KURULUR — şema, konak, port ve uzak yol; kimlik
/// bilgisi parçası hiç eklenmez.
/// </para>
/// <para>
/// Günlükçü İSTEĞE BAĞLI: yeni bir zorunlu parametre mevcut dar kapsamlı test kurulumlarını
/// kırardı; gerekçe <c>BackupService</c>'te yazılı. Üretimde DI daima verir.
/// </para>
/// </remarks>
internal sealed class FileMigrationService(
    IIisSiteService iisSiteService,
    ILogger<FileMigrationService>? logger = null,
    // İSTEĞE BAĞLI: "iç ağ hedeflerine izin ver" açık kapısı. Verilmemişse KAPALI sayılır.
    Microsoft.Extensions.Options.IOptions<Options.PanelRuntimeOptions>? runtimeOptions = null,
    // İSTEĞE BAĞLI: adres çözümleyici. Testte sahte — birim testi gerçek DNS'e çıkmamalı.
    OutboundAddressGuard.OutboundHostResolver? outboundResolver = null) : IFileMigrationService
{
    public async Task<RemoteFileMigrationResult> PullAsync(RemoteFileMigrationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw new AppException("migration.host_required", 400, 400);
        }

        // KONAK DENETİMİ BAĞLANMADAN ÖNCE.
        //
        // Yüzey: `request.Host` + `request.Port` HTTP istek gövdesinden gelir ve aşağıdaki iki yol
        // (FluentFTP `AsyncFtpClient`, SSH.NET `SftpClient`) bunlara DOĞRUDAN bağlanır. Denetimsiz
        // bırakıldığında bu uç, panelin ağından KEYFİ host:port'a TCP bağlantısı açan bir araçtır:
        // iç ağ port taraması (bağlanabildi mi / bağlanamadı mı ayırt edilebiliyor, çünkü hata
        // mesajı kullanıcıya dönüyor) ve loopback'teki servislere erişim.
        //
        // ADRES BURADA ÇIPLAK BİR DİZEDİR, URL DEĞİL. Ölçüldü: `IPAddress.TryParse` çıplak
        // "2130706433", "0x7f000001", "017700000001" ve "127.1" girdilerinin dördünü de kabul edip
        // 127.0.0.1'e normalleştiriyor — yani Uri'den geçmeyen bu yolda da sayısal yazımlar
        // yakalanıyor. Ayrıca çözülemeyen ad REDDEDİLİR: çözülemeyen bir konağa FluentFTP/SSH.NET
        // de bağlanamaz, yani reddetmek hiçbir çalışan göçü kapatmaz.
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            request.Host,
            outboundResolver ?? OutboundAddressGuard.SystemResolver,
            OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false),
            cancellationToken);

        if (engellenen is not null)
        {
            throw new AppException("migration.host_forbidden", 403, 403, engellenen);
        }

        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        var target = site.PhysicalPath;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            throw new AppException("migration.target_missing", 400, 400);
        }

        var protocol = (request.Protocol ?? "ftp").Trim().ToLowerInvariant();
        var remotePath = string.IsNullOrWhiteSpace(request.RemotePath) ? "/" : request.RemotePath.Trim();
        var hedefEtiket = GunlugeUygunHedef(protocol, request.Host, request.Port, remotePath);

        logger?.LogInformation(
            "Goc dosya cekme basliyor: {Site} <- {UzakHedef} (yerel hedef {YerelYol}).",
            request.SiteName,
            hedefEtiket,
            target);

        try
        {
            var sonuc = protocol switch
            {
                "sftp" => await PullSftpAsync(request, target, remotePath, cancellationToken),
                _ => await PullFtpAsync(request, target, remotePath, protocol == "ftps", cancellationToken)
            };

            logger?.LogInformation(
                "Goc dosya cekme bitti: {Site} <- {UzakHedef}, {Dosya} dosya / {Bayt} bayt -> {YerelYol}.",
                request.SiteName,
                hedefEtiket,
                sonuc.FilesDownloaded,
                sonuc.BytesDownloaded,
                sonuc.TargetPath);
            return sonuc;
        }
        catch (AppException exception)
        {
            // Buraya "sıfır dosya indi" koruması da düşer. İSTİSNA HTTP yanıtına gider ve orada
            // biter; sunucuda iz kalmazsa "göç neden boş geldi" sorusu sonradan incelenemez.
            logger?.LogWarning(
                "Goc dosya cekme BASARISIZ: {Site} <- {UzakHedef}. Anahtar {Anahtar}. {Detay}",
                request.SiteName,
                hedefEtiket,
                exception.MessageKey,
                exception.Details);
            throw;
        }
        catch (Exception exception)
        {
            // Ham FTP/SSH istisnası kullanıcıya SINIFLANDIRILMIŞ hâliyle gider; yığın izi (asıl
            // teşhis malzemesi) yalnız günlükte kalır. Kimlik bilgisi kütüphane istisna
            // metinlerinde geçmez — parola hiçbir mesaja yazılmaz, kullanıcı adı da bilinçli
            // olarak `hedefEtiket`in dışında bırakılmıştır.
            logger?.LogError(
                exception,
                "Goc dosya cekme HATASI: {Site} <- {UzakHedef}.",
                request.SiteName,
                hedefEtiket);
            throw ClassifyFileError(exception);
        }
    }

    /// <summary>
    /// Günlüğe yazılabilir uzak adres: şema, konak, port ve uzak yol. KİMLİK BİLGİSİ İÇERMEZ.
    /// </summary>
    /// <remarks>
    /// Adres, isteği olduğu gibi biçimlendirmek yerine PARÇALARINDAN yeniden kurulur. Sebebi
    /// tek bir kural: kullanıcı adı/parola alanları buraya HİÇ girmez, dolayısıyla ileride biri
    /// biçimi değiştirse bile sızdıracak bir alan yoktur. (Denetim kaydındaki merkezî temizleyici
    /// <c>AuditSecretRedactor</c> ILogger yolunda çalışmaz; koruma çağrı yerinde kurulur.)
    /// </remarks>
    internal static string GunlugeUygunHedef(string protocol, string? host, int port, string remotePath)
    {
        var sema = string.IsNullOrWhiteSpace(protocol) ? "ftp" : protocol.Trim().ToLowerInvariant();
        var konak = (host ?? string.Empty).Trim();

        // Kullanıcı konak alanına "kullanici:parola@sunucu" yazmış olabilir; bu biçim FTP
        // adreslerinde yaygındır ve olduğu gibi yazılırsa PAROLA günlüğe düşerdi.
        var atIsareti = konak.LastIndexOf('@');
        if (atIsareti >= 0)
        {
            konak = konak[(atIsareti + 1)..];
        }

        var portParcasi = port > 0 ? $":{port}" : string.Empty;
        return $"{sema}://{konak}{portParcasi}{remotePath}";
    }

    // Ham FTP/SFTP istisnasını anlaşılır bir başlığa (yetkilendirme / uzak yol / bağlantı) eşler ve gerçek teknik
    // mesajı Details'te taşır. Tür adı + mesaj üzerinden anahtar-kelime sınıflandırması (kütüphaneye sıkı bağlı
    // değil; FluentFTP ve SSH.NET'in tipik metinlerini kapsar). Eşleşme yoksa generic "migration.failed".
    private static AppException ClassifyFileError(Exception exception)
    {
        var detail = exception.Message;
        var probe = $"{exception.GetType().Name} {detail}".ToLowerInvariant();

        if (probe.Contains("auth") || probe.Contains("login") || probe.Contains("permission denied") ||
            probe.Contains("password") || probe.Contains("530"))
        {
            return new AppException("migration.files.auth_failed", 502, 502, detail);
        }

        if (probe.Contains("no such file") || probe.Contains("not found") || probe.Contains("pathnotfound") ||
            probe.Contains("could not find") || probe.Contains("no such directory") || probe.Contains("550"))
        {
            return new AppException("migration.files.path_not_found", 502, 502, detail);
        }

        if (probe.Contains("socket") || probe.Contains("connect") || probe.Contains("timeout") ||
            probe.Contains("timed out") || probe.Contains("resolve") || probe.Contains("refused") ||
            probe.Contains("unreachable") || probe.Contains("host"))
        {
            return new AppException("migration.files.connect_failed", 502, 502, detail);
        }

        return new AppException("migration.failed", 502, 500, detail);
    }

    private async Task<RemoteFileMigrationResult> PullFtpAsync(
        RemoteFileMigrationRequest request,
        string target,
        string remotePath,
        bool secure,
        CancellationToken cancellationToken)
    {
        var config = new FtpConfig
        {
            EncryptionMode = secure ? FtpEncryptionMode.Auto : FtpEncryptionMode.None,
            ValidateAnyCertificate = true
        };

        await using var client = new AsyncFtpClient(
            request.Host,
            request.UserName,
            request.Password,
            request.Port > 0 ? request.Port : 21,
            config);

        await client.Connect(cancellationToken);
        var results = await client.DownloadDirectory(
            target,
            remotePath,
            FtpFolderSyncMode.Update,
            FtpLocalExists.Overwrite,
            FtpVerify.None,
            token: cancellationToken);
        await client.Disconnect(cancellationToken);

        var files = results.Where(item => item.IsSuccess && item.Type == FtpObjectType.File).ToList();
        var basarisizlar = results.Where(item => !item.IsSuccess).ToList();
        var failed = basarisizlar.Count;

        // KISMİ BAŞARI — "bazıları oldu, bazıları olmadı".
        //
        // Aşağıdaki koruma yalnız SIFIR dosyayı yakalar; 900 dosyadan 40'ı düştüğünde akış geçer
        // ve panel "Dosyalar çekildi" der. Göç geri alınamaz olduğu için eksik dosyanın hangisi
        // olduğu SONRADAN sorulur — o an tek kayıt bu satırdır. Yol adları sır değildir,
        // kimlik bilgisi ise bu listede hiç bulunmaz.
        if (failed > 0)
        {
            logger?.LogWarning(
                "FTP goc KISMEN basarisiz: {Site} icin {Basarisiz} oge aktarilamadi, {Basarili} dosya indi. Atlanan ilk ogeler: {Ogeler}",
                request.SiteName,
                failed,
                files.Count,
                string.Join(", ", basarisizlar.Take(20).Select(item => item.RemotePath)));
        }

        var result = new RemoteFileMigrationResult
        {
            SiteName = request.SiteName,
            Protocol = secure ? "ftps" : "ftp",
            FilesDownloaded = files.Count,
            BytesDownloaded = files.Sum(item => Math.Max(0, item.Size)),
            TargetPath = target
        };

        EnsureFilesDownloaded(result, remotePath, failed);
        return result;
    }

    /// <summary>
    /// SIFIR DOSYA BAŞARI DEĞİLDİR.
    /// <para>
    /// Eski hâlde bu kontrol hiç yoktu: yanlış "Uzak yol" (en sık göç hatası — /httpdocs yerine /,
    /// ya da kullanıcının ev dizini boş) verildiğinde FluentFTP/SSH.NET istisna atmaz, sadece boş
    /// bir liste döner. Panel de "Dosyalar çekildi" diye YEŞİL bir adım gösterirdi; kullanıcı
    /// göçün bittiğini sanıp DNS'i çevirir ve karşısına boş site çıkardı.
    /// </para>
    /// <para>
    /// Mesaj anahtarı bilerek <c>migration.files.path_not_found</c>: kullanıcıya yapması gereken
    /// şeyi söyler ("Uzak yol'u kontrol et: /httpdocs, /public_html, /").
    /// </para>
    /// </summary>
    internal static void EnsureFilesDownloaded(RemoteFileMigrationResult result, string remotePath, int failedCount)
    {
        if (result.FilesDownloaded > 0)
        {
            return;
        }

        var detay = $"'{remotePath}' yolundan hicbir dosya indirilmedi.";
        if (failedCount > 0)
        {
            detay += $" {failedCount} ogenin aktarimi basarisiz oldu (yetki ya da baglanti).";
        }
        else
        {
            detay += " Uzak dizin bos olabilir ya da yol yanlis olabilir.";
        }

        throw new AppException("migration.files.path_not_found", 502, 502, detay);
    }

    private async Task<RemoteFileMigrationResult> PullSftpAsync(
        RemoteFileMigrationRequest request,
        string target,
        string remotePath,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var client = new SftpClient(
                request.Host,
                request.Port > 0 ? request.Port : 22,
                request.UserName,
                request.Password);

            client.Connect();
            var counter = new MigrationCounter();
            DownloadSftpDirectory(client, remotePath, target, counter, cancellationToken);
            client.Disconnect();

            // KISMİ BAŞARI'nın SFTP'deki karşılığı: dizin de normal dosya da olmayan girdiler
            // (sembolik bağlantı, soket, aygıt) sayaca hiç girmeden ATLANIR. Sembolik bağlantı
            // paylaşımlı barındırmada yaygındır (ör. wp-content/uploads başka bir birime bağlı);
            // atlanınca göç "başarılı" görünüp site eksik gelir. Sayı burada kayda geçer.
            if (counter.Skipped > 0)
            {
                logger?.LogWarning(
                    "SFTP goc: {Site} icin {Atlanan} girdi dosya/dizin olmadigi icin ATLANDI (sembolik baglanti, soket vb.). Ilk ornekler: {Ornekler}",
                    request.SiteName,
                    counter.Skipped,
                    string.Join(", ", counter.SkippedSamples));
            }

            var result = new RemoteFileMigrationResult
            {
                SiteName = request.SiteName,
                Protocol = "sftp",
                FilesDownloaded = counter.Files,
                BytesDownloaded = counter.Bytes,
                TargetPath = target
            };

            // FTP ile aynı gerçek: SFTP'de de boş/yanlış uzak yol istisna atmaz, sessizce 0 dosya döner.
            EnsureFilesDownloaded(result, remotePath, failedCount: 0);
            return result;
        }, cancellationToken);
    }

    private static void DownloadSftpDirectory(
        SftpClient client,
        string remoteDir,
        string localDir,
        MigrationCounter counter,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localDir);
        foreach (var entry in client.ListDirectory(remoteDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var localPath = Path.Combine(localDir, entry.Name);
            if (entry.IsDirectory)
            {
                DownloadSftpDirectory(client, entry.FullName, localPath, counter, cancellationToken);
            }
            else if (entry.IsRegularFile)
            {
                using var stream = File.Create(localPath);
                client.DownloadFile(entry.FullName, stream);
                counter.Files++;
                counter.Bytes += entry.Length;
            }
            else
            {
                // Sessizce atlanan girdi. Günlüğe TEK TEK yazmak binlerce satır üretebilirdi;
                // sayaç ve ilk birkaç örnek üst katmanda BİR kez yazılır.
                counter.Skipped++;
                if (counter.SkippedSamples.Count < 10)
                {
                    counter.SkippedSamples.Add(entry.FullName);
                }
            }
        }
    }

    private sealed class MigrationCounter
    {
        public int Files;
        public long Bytes;
        public int Skipped;
        public List<string> SkippedSamples { get; } = [];
    }
}
