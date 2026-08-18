using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Backup;

public sealed class BackupService(
    IIisSiteService iisSiteService,
    IDatabaseService databaseService,
    IMailService mailService,
    IPanelPreferencesService preferencesService,
    IFtpService ftpService,
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory,
    // İSTEĞE BAĞLI: şifreleme kasası. Verilmezse (dar kapsamlı testler) panelde kayıtlı parola
    // yok sayılır; istekle gelen parola yine çalışır.
    BackupEncryptionStore? encryptionStore = null,
    // İSTEĞE BAĞLI: dar kapsamlı testler bu servisi doğrudan kuruyor; günlükçüyü zorunlu yapmak
    // onlarca test kurulumunu kırardı. Üretimde DI her zaman verir.
    ILogger<BackupService>? logger = null,
    // İSTEĞE BAĞLI: ABONELİK yedeği için gerekenler. Yeni bağımlılıklar SONA ve varsayılanlı
    // eklendi — mevcut dar kapsamlı testler (site/şifreleme/budama) olduğu gibi derlenmeye devam
    // etsin diye. Bunlar verilmemişse abonelik yedeği AÇIKÇA reddedilir (sessizce eksik yedek
    // üretmek yerine).
    ICustomerScopeService? customerScopes = null,
    IDatabaseOwnershipService? databaseOwnership = null) : IBackupService
{
    private const string IndexPath = "config/backups.json";

    /// <summary>
    /// Uzak hedefe yükleme için ÜST SINIR. Sonsuz bekleme kabul edilemez: yükleme, otomatik
    /// yedeklemede global kilidin içinde çalışıyor ve aynı kilidi giriş akışı da alıyor.
    /// </summary>
    private static readonly TimeSpan RemoteUploadTimeout = TimeSpan.FromHours(2);

    /// <summary>Veri akışının DURMASINA tahammül süresi (yalnız FTP; HttpClient'ta ayrı ayarı yok).</summary>
    private static readonly TimeSpan RemoteUploadStallTimeout = TimeSpan.FromMinutes(5);

    private static readonly System.Text.Json.JsonSerializerOptions BackupJsonOptions =
        new() { WriteIndented = true };

    public async Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        var records = await fileStore.ReadAsync(IndexPath, Array.Empty<BackupRecordDto>(), cancellationToken);
        return records
            .OrderByDescending(record => record.CreatedAtUtc)
            .ToArray();
    }

    // =====================================================================================
    // ŞİFRELEME (tek kaynak). Yedekler FTP/S3/WebDAV gibi ÜÇÜNCÜ TARAF depolara gidiyor;
    // şifresiz bir arşiv müşterinin veritabanı dökümünü ve postasını o depoya erişen herkese
    // açar. Biçim ve kripto BackupArchiveCipher'da; burada yalnızca "ne zaman, hangi parolayla"
    // kararı verilir. Şifreleme ile yükleme/kayıt akışı tek yerde birleştirilmiştir
    // (FinalizeBackupAsync): dört yedek türünün her birinde ayrı ayrı yazılsaydı, biri
    // unutulduğunda o tür sessizce şifresiz kalırdı.
    // =====================================================================================

    /// <summary>
    /// Kullanılacak şifreleme parolası: önce istekteki, yoksa panelde KAYITLI olan. İkisi de yoksa
    /// boş döner = yedek şifresiz alınır. Kayıtlı parola, zamanlanmış (kimse ekranda değilken
    /// çalışan) yedeklerin şifrelenebilmesinin tek yoludur.
    /// </summary>
    private async Task<string> ResolveEncryptionPasswordAsync(string? requestPassword, CancellationToken cancellationToken)
    {
        var password = (requestPassword ?? string.Empty).Trim();
        if (password.Length > 0)
        {
            return password;
        }

        return encryptionStore is null ? string.Empty : await encryptionStore.GetPasswordAsync(cancellationToken);
    }

    /// <summary>
    /// Üretilmiş yedek dosyasını (gerekiyorsa) şifreler. Şifreleme başarısızsa hem yarım şifreli
    /// dosya hem de DÜZ METİN kopya silinir ve hata yükselir: kullanıcı "şifreli yedek" istediyse
    /// sessizce şifresiz bir dosya bırakmak, korumayı yalan söylemek olurdu.
    /// </summary>
    private static async Task<(string Path, string Name, bool Encrypted)> ProtectArchiveAsync(
        string localPath,
        string fileName,
        string password,
        CancellationToken cancellationToken)
    {
        if (password.Length == 0)
        {
            return (localPath, fileName, false);
        }

        var encryptedPath = localPath + BackupArchiveCipher.EncryptedExtension;
        try
        {
            await BackupArchiveCipher.EncryptFileAsync(localPath, encryptedPath, password, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDeleteFile(encryptedPath);
            TryDeleteFile(localPath);
            throw new AppException("backup.encryption_failed", 500, 500, exception.Message);
        }

        // Düz metin kopya diskte KALMAZ; kalsaydı şifreleme yalnızca uzak kopyayı korur, sunucuda
        // duran asıl dosya korumasız olurdu.
        TryDeleteFile(localPath);
        return (encryptedPath, fileName + BackupArchiveCipher.EncryptedExtension, true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temizlik best-effort.
        }
    }

    /// <summary>
    /// Üretilmiş yedek dosyasını (gerekiyorsa) sıkıştırır. Hangi biçimin sıkıştırılacağı kararı
    /// <see cref="BackupDumpCompression.ShouldCompress"/>'tedir — burada "ne zaman" değil yalnızca
    /// "nasıl" vardır.
    /// </summary>
    /// <remarks>
    /// <b>Sıkıştırma başarısızsa YEDEK ÖLMEZ:</b> düz dosya olduğu gibi korunur, kayda görünür bir
    /// uyarı düşer. Bu, <see cref="ProtectArchiveAsync"/>'in tam TERSİ davranıştır ve fark bilinçlidir:
    /// şifreleme bir GÜVENLİK SÖZÜDÜR, tutulamıyorsa sessizce şifresiz dosya bırakmak yalan olur;
    /// sıkıştırma ise yalnızca bir BOYUT iyileştirmesidir — disk dolduğu için gzip yazılamadı diye
    /// o gecenin yedeğini hiç almamak, korumasız değil YEDEKSİZ bırakmak olurdu. Geri yükleme her
    /// iki durumda da çalışır: biçim kararı dosya başlığından verilir.
    /// </remarks>
    private async Task<(string Path, string Name, bool Compressed)> CompressArchiveAsync(
        string localPath,
        string fileName,
        string format,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        if (!BackupDumpCompression.ShouldCompress(format))
        {
            return (localPath, fileName, false);
        }

        var compressedPath = localPath + BackupDumpCompression.CompressedExtension;
        try
        {
            await BackupDumpCompression.CompressFileAsync(localPath, compressedPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDeleteFile(compressedPath);
            logger?.LogWarning(exception, "Yedek sikistirilamadi, duz dosya korundu: {File}", fileName);
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.compression_failed",
                Target = format,
                Detail = exception.Message
            });
            return (localPath, fileName, false);
        }

        // Düz kopya diskte KALMAZ; kalsaydı kazanç sıfır olurdu (iki dosya birden dururdu).
        TryDeleteFile(localPath);
        return (compressedPath, fileName + BackupDumpCompression.CompressedExtension, true);
    }

    /// <summary>
    /// Her yedek akışının ORTAK son adımı: sıkıştır → şifrele → uzak hedefe yükle → kaydı oluştur
    /// ve listeye ekle. (Sıkıştırmanın şifrelemeden ÖNCE gelmesi zorunludur; gerekçe aşağıda.)
    /// </summary>
    /// <param name="chain">
    /// Artımsal zincir damgası (zincirsiz yedeklerde <c>null</c>). Damganın kayda yazılması TEK
    /// yerde olur: dört yedek akışının her birinde ayrı ayrı yazılsaydı, biri unutulduğunda o tür
    /// zincire ait olduğunu bilmeyen — ve bu yüzden budamada zinciri kıran — kayıtlar üretirdi.
    /// </param>
    /// <param name="fileIndex">
    /// Arşivin dosya indeksi. Diskteki yan kayıt (<c>backups/index/&lt;id&gt;.json</c>) bir sonraki
    /// artımsal halkanın karşılaştırma temelidir; ŞİFRELİ arşivde indeksi arşivden okumak bütün
    /// dosyayı çözmeyi gerektirdiği için yan kayıt olmadan artımsal fiilen çalışmazdı.
    /// </param>
    /// <param name="placement">
    /// Yedeğin YERİ (gün + kategori). ZORUNLU tutuldu: isteğe bağlı olsaydı yeni bir yedek türü
    /// eklendiğinde parametreyi vermemek DERLENİR ve o tür sessizce uzak hedefin köküne düz
    /// yüklenirdi — düzeltilen hatanın ta kendisi. Şimdi vermeyi unutmak derleme hatasıdır.
    /// </param>
    private async Task<BackupRecordDto> FinalizeBackupAsync(
        string localPath,
        string fileName,
        string type,
        string source,
        string format,
        BackupTargetRequest request,
        BackupPlacement placement,
        CancellationToken cancellationToken,
        ChainStamp? chain = null,
        BackupFileIndex? fileIndex = null,
        string subscriptionUserId = "",
        int itemCount = 0)
    {
        // SIRA KİLİTLİ: ÖNCE SIKIŞTIR, SONRA ŞİFRELE.
        //
        // Şifrelenmiş veri rastgeleye yakındır ve SIKIŞMAZ; ters sırada dosya küçülmez, yalnızca
        // CPU harcanırdı. Bu yüzden sıkıştırma tam burada — ProtectArchiveAsync'ten ÖNCE — durur ve
        // sıra bir testle korunur (BackupDumpCompressionTests).
        //
        // İkisi de FinalizeBackupAsync'te toplanmıştır: dört yedek akışının her birinde ayrı ayrı
        // yazılsaydı, biri unutulduğunda o tür sessizce ham (ve/veya şifresiz) kalırdı.
        var warnings = new List<BackupRestoreWarningDto>();
        var (compressedPath, compressedName, compressed) =
            await CompressArchiveAsync(localPath, fileName, format, warnings, cancellationToken);

        var password = await ResolveEncryptionPasswordAsync(request.EncryptionPassword, cancellationToken);
        var (finalPath, finalName, encrypted) = await ProtectArchiveAsync(compressedPath, compressedName, password, cancellationToken);

        var target = ResolveTarget(request.TargetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);

        // YEDEK HER ZAMAN ÖNCE YEREL DİSKE ALINIR; uzak hedef bunun YERİNE değil ÜSTÜNE gelir.
        //
        // Uzak yükleme hatası ESKİDEN İSTİSNA OLARAK YUKARI ÇIKIYORDU ve kayıt bu satırdan sonra
        // yazıldığı için hiç oluşmuyordu. Sonuç: alınmış, sağlam, YEREL bir yedek diskte duruyor
        // ama panelde GÖRÜNMÜYOR — listede yok, geri yüklenemiyor, saklama temizliğine de
        // takılmadığı için sonsuza kadar yer kaplıyor. FTP'nin bir gecelik arızası, o gecenin
        // yedeğini fiilen yok ediyordu.
        //
        // Artık yerel kopya HER KOŞULDA kaydedilir; uzak kopya başarısızsa bu, kaydın durumunda
        // ve uyarısında AÇIKÇA söylenir. Bir hedefin başarısızlığı diğerini götürmez.
        var remoteUri = string.Empty;
        string? remoteFailure = null;
        if (target.Type != "local")
        {
            try
            {
                remoteUri = await UploadToRemoteAsync(finalPath, finalName, target, placement, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                remoteFailure = DescribeRemoteFailure(exception);
                logger?.LogWarning(
                    exception,
                    "Yedek uzak hedefe gönderilemedi ({Target}); yerel kopya korundu: {File}",
                    target.Type,
                    finalName);
            }
        }

        var record = new BackupRecordDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = finalName,
            Type = type,
            Source = source,
            Path = finalPath,
            // Biçim ANLAMSAL kalır ("zip"/"sql"/"bak"): şifreleme ve sıkıştırma birer SARMALAYICIDIR,
            // arşivin türünü değiştirmez. Geri yükleme yolları bu alana bakmaya devam eder
            // (sarmalayıcılar dosya BAŞLIĞINDAN tanınır, bu etiketten değil).
            Format = format,
            CreatedAtUtc = DateTime.UtcNow,
            Size = new FileInfo(finalPath).Length,
            Status = DescribeDestinationStatus(remoteUri, remoteFailure),
            TargetType = target.Type,
            RemoteUri = remoteUri,
            Encrypted = encrypted,
            Compressed = compressed,
            ChainId = chain?.ChainId ?? string.Empty,
            ChainIndex = chain?.ChainIndex ?? 0,
            BackupMode = chain?.Mode ?? BackupChainPlanner.ModeFull,
            BaseBackupId = chain?.BaseBackupId ?? string.Empty,
            SubscriptionUserId = subscriptionUserId,
            ItemCount = itemCount
        };

        // İNDEKS YAN KAYDI kayıttan ÖNCE yazılır: kayıt listede görünüp indeksi olmayan bir halka,
        // bir sonraki turda "temel okunamadı" diye zorunlu tam yedeğe düşürür (zararsız ama
        // gereksiz). Yazamazsak da yedek geçerlidir; yalnızca sonraki tur tam yedek olur.
        if (fileIndex is not null)
        {
            try
            {
                await fileStore.WriteAsync(IndexSidecarPath(record.Id), fileIndex, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(exception, "Yedek dosya indeksi diske yazilamadi: {File}", finalName);
            }
        }

        // Sıkıştırma denendi ama başarısız olduysa (disk dolu vb.) SESSİZ KALINMAZ: yedek geçerlidir
        // ama beklenenden ~4 kat büyüktür ve sebebi ancak burada görünür.
        record.Warnings.AddRange(warnings);

        // Uzak kopya alınamadıysa SEBEBİ kayda taşınır. "Uzak hedefe gönderilemedi" demeden
        // sessizce yerel yedekle yetinmek, kullanıcının off-site kopyası olduğunu sanmasına yol
        // açar — felaket anında öğrenilecek en kötü şey budur.
        if (remoteFailure is not null)
        {
            record.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.remote_upload_failed",
                Target = target.Type,
                Detail = remoteFailure
            });
        }

        // Şifresiz yedek üçüncü taraf depoya çıktıysa bunu SÖYLERİZ. Sessiz kalmak, müşteri
        // verisinin S3/FTP'de düz metin durduğunu kimsenin fark etmemesi demekti (KVKK/GDPR).
        if (!encrypted && target.Type != "local")
        {
            record.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.remote_unencrypted",
                Target = target.Type,
                Detail = finalName
            });
        }

        await AppendRecordAsync(record, cancellationToken);
        return record;
    }

    /// <summary>
    /// Yedeğin hangi hedeflere ULAŞTIĞINI anlatan durum.
    /// </summary>
    /// <remarks>
    /// Üç durum vardır ve üçü de GERÇEK bir sonucu anlatır:
    /// <list type="bullet">
    /// <item><c>created</c> — yalnız yerel disk istendi, alındı.</item>
    /// <item><c>created_remote_synced</c> — yerel disk + uzak hedef, İKİSİ de tamam.</item>
    /// <item><c>created_remote_failed</c> — yerel disk tamam, uzak hedef başarısız.
    /// Yedek VARDIR ama off-site kopyası YOKTUR; ikisini aynı kefeye koymak, kullanıcının
    /// olmayan bir off-site kopyaya güvenmesi demek olurdu.</item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// Site dışı arşivlerin çıkarılacağı klasör adı: tür + kaynak + zaman damgası.
    /// </summary>
    /// <remarks>
    /// Kaynak alanı kullanıcı verisidir (site adı, abonelik kimliği) ve doğrudan yola girerse
    /// dizin kaçışına açık olur; bu yüzden temizlik <see cref="BackupPathName"/> üzerinden yapılır
    /// — yedek klasörü (<see cref="BackupLayout"/>) ile AYNI kural, tek kaynaktan. İkisi ayrı
    /// yazıldığı sürece biri düzeltilip diğeri unutulabilirdi. Zaman damgası, aynı yedekten ikinci
    /// kez seçerek geri yüklemenin öncekinin üzerine yazmasını engeller.
    /// </remarks>
    internal static string BuildRestoreFolderName(BackupRecordDto record)
    {
        var tur = BackupPathName.Sanitize(record.Type, "yedek");
        var kaynak = BackupPathName.Sanitize(record.Source, "yedek");
        return $"{tur}-{kaynak}-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    /// <summary>
    /// Uzak yükleme hatasının KAYDA yazılacak hâli.
    /// </summary>
    /// <remarks>
    /// <see cref="AppException"/>'ın <c>Message</c>'ı yalnızca çeviri ANAHTARIDIR
    /// ("backup.remote_upload_failed"); sunucunun gerçekten ne dediği (<c>550 Permission denied</c>,
    /// hangi klasör, hangi adres) <c>Details</c> içinde durur. Kayda yalnız anahtarı yazmak,
    /// kullanıcıya "gönderilemedi" deyip SEBEBİ yutmaktı — hatayı görüp de nedenini asla
    /// öğrenememek, düzeltilemeyen bir arıza demektir.
    /// </remarks>
    internal static string DescribeRemoteFailure(Exception exception) =>
        exception is AppException app && !string.IsNullOrWhiteSpace(app.Details)
            ? $"{app.MessageKey}: {app.Details}"
            : exception.Message;

    internal static string DescribeDestinationStatus(string? remoteUri, string? remoteFailure)
    {
        if (!string.IsNullOrWhiteSpace(remoteFailure))
        {
            return "created_remote_failed";
        }

        return string.IsNullOrWhiteSpace(remoteUri) ? "created" : "created_remote_synced";
    }

    /// <summary>
    /// Yedeğin OKUNABİLİR hâli: sarmalayıcısı yoksa dosyanın kendisi; şifreli ve/veya sıkıştırılmış
    /// ise çözülmüş/açılmış GEÇİCİ kopya. Dispose edildiğinde geçici kopyaların HEPSİ silinir.
    /// </summary>
    private sealed class BackupContent(string filePath, IReadOnlyList<string> temporaryPaths) : IDisposable
    {
        public string FilePath { get; } = filePath;

        public void Dispose()
        {
            // Sarmalayıcı başına bir geçici dosya olabilir (çözülmüş kopya + açılmış kopya);
            // hepsi silinir, yoksa çözülmüş DÜZ METİN müşteri verisi %TEMP%'te kalırdı.
            foreach (var temporaryPath in temporaryPaths)
            {
                TryDeleteFile(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Kaydın arşivini okumaya hazırlar: gerekiyorsa ÇÖZER, gerekiyorsa AÇAR.
    /// <para>
    /// Sarmalayıcıların varlığı DOSYANIN BAŞLIĞINDAN okunur, kayıttaki bayraklardan değil: eski
    /// (şifresiz ve sıkıştırmasız) yedekler çalışmaya devam etmeli, dosyası elle taşınmış/kaydı
    /// bozulmuş bir yedek de doğru tanınmalıdır. Etikete güvenip başlığa bakmamak, "yedek var
    /// görünüyor ama geri gelmiyor" sınıfının ta kendisidir.
    /// </para>
    /// <para>
    /// <b>Sıra, yazma sırasının TERSİDİR:</b> yazarken sıkıştır→şifrele, okurken çöz→aç. Bütün
    /// okuma yolları (geri yükleme, gözatma, ayrıntılı geri yükleme, zincir halkaları) tek elden
    /// buradan geçer; ayrı ayrı yazılsaydı biri unutulduğunda o yol sıkıştırılmış baytları ham SQL
    /// sanıp veritabanına aktarmaya kalkardı.
    /// </para>
    /// </summary>
    private async Task<BackupContent> OpenContentAsync(BackupRecordDto record, string? password, CancellationToken cancellationToken)
    {
        var currentPath = record.Path;
        var temporaryPaths = new List<string>();

        try
        {
            if (BackupArchiveCipher.IsEncryptedFile(currentPath))
            {
                var parola = await ResolveEncryptionPasswordAsync(password, cancellationToken);
                if (parola.Length == 0)
                {
                    // "Bozuk dosya" DEMEYİZ: dosya sağlam, parola eksik. Kullanıcı ne yapacağını bilmeli.
                    throw new AppException("backup.password_required", 400, 400);
                }

                var decryptedPath = Path.Combine(Path.GetTempPath(), $"adabk-open-{Guid.NewGuid():N}.tmp");
                temporaryPaths.Add(decryptedPath);
                await BackupArchiveCipher.DecryptFileAsync(currentPath, decryptedPath, parola, cancellationToken);
                currentPath = decryptedPath;
            }

            // SIKIŞTIRILMIŞ DÖKÜM GERİ YÜKLENİRKEN ÖNCE AÇILMALI: ImportDumpAsync gerçek bir .sql
            // DOSYASI bekler (mysql istemcisine "source <yol>" olarak verilir). Bu adım atlanırsa
            // özellik "yedek küçüldü" der ama geri yükleme çalışmaz — yedek fiilen yok olur.
            if (BackupDumpCompression.IsCompressedFile(currentPath))
            {
                var decompressedPath = Path.Combine(Path.GetTempPath(), $"adabk-open-{Guid.NewGuid():N}.{SafeTempExtension(record.Format)}");
                temporaryPaths.Add(decompressedPath);
                await BackupDumpCompression.DecompressFileAsync(currentPath, decompressedPath, cancellationToken);
                currentPath = decompressedPath;
            }

            return new BackupContent(currentPath, temporaryPaths);
        }
        catch
        {
            // Yarım kalan geçici kopyalar %TEMP%'te ÇÖZÜLMÜŞ müşteri verisi olarak kalmaz.
            foreach (var temporaryPath in temporaryPaths)
            {
                TryDeleteFile(temporaryPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Açılmış geçici kopyanın uzantısı. Kayıttaki biçim etiketi kullanıcı verisi sayılır: yola
    /// giren her şey temizlenir, tanınmayan/boş değer <c>tmp</c>'ye düşer.
    /// </summary>
    private static string SafeTempExtension(string? format)
    {
        var temiz = new string((format ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        return temiz.Length is > 0 and <= 8 ? temiz.ToLowerInvariant() : "tmp";
    }

    public async Task<BackupEncryptionSettingsDto> GetEncryptionSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (encryptionStore is null)
        {
            return new BackupEncryptionSettingsDto { Algorithm = EncryptionAlgorithmSummary };
        }

        var state = await encryptionStore.GetStateAsync(cancellationToken);
        var password = await encryptionStore.GetPasswordAsync(cancellationToken);
        return new BackupEncryptionSettingsDto
        {
            Enabled = state.Enabled,
            // ÖLÇÜLMÜŞ değer: kayıt var ama çözülemiyorsa (panel taşınmış, DataProtection anahtarları
            // gitmiş) "parola hazır" demek, zamanlanmış yedeklerin sessizce şifresiz alınmasıydı.
            PasswordUsable = password.Length > 0,
            UpdatedAtUtc = state.UpdatedAtUtc,
            Algorithm = EncryptionAlgorithmSummary
        };
    }

    public async Task<BackupEncryptionSettingsDto> SetEncryptionSettingsAsync(SetBackupEncryptionRequest request, CancellationToken cancellationToken = default)
    {
        if (encryptionStore is null)
        {
            throw new AppException("backup.encryption_unavailable", 503, 503);
        }

        await encryptionStore.SetPasswordAsync(request.Password, cancellationToken);
        return await GetEncryptionSettingsAsync(cancellationToken);
    }

    private static readonly string EncryptionAlgorithmSummary =
        $"AES-256-GCM / PBKDF2-SHA256 ({BackupArchiveCipher.Pbkdf2Iterations:N0} tur)";

    /// <summary>
    /// Tek bir sitenin tam yedeği: <c>files/</c> + <c>config/</c> + <c>databases/</c> + <c>mail/</c>
    /// tek bir zip içinde.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Arşiv siteye ait HER ŞEYİ bir arada tutar</b> — istekte seçilen veritabanlarının dökümü
    /// de aynı zip'in <c>databases/</c> klasörüne girer; ayrı bir dosya aramak gerekmez.
    /// </para>
    /// <para>
    /// <b>UYARI — <c>mail/</c> POSTA KUTUSU DEĞİLDİR.</b> Bu klasördeki JSON, alan adının
    /// hesap/alias/yönlendirme ENVANTERİDİR; hiçbir İLETİ içermez. İletiler Stalwart'ın RocksDB
    /// deposunda yaşar; tek bir alan adını o depodan dosya kopyalayarak ayırmak mümkün değildir.
    /// Posta kutusu içeriği YALNIZCA sunucu geneli posta yedeğiyle
    /// (<see cref="BackupMailAsync"/>) korunur. Bu ayrım arşivin <c>manifest.json</c> dosyasına da
    /// yazılır: "mail" parçasını görüp postasının yedeklendiğini sanan biri, geri getirilemeyeceğini
    /// ancak felaket anında öğrenirdi.
    /// </para>
    /// </remarks>
    public async Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        var preferences = await preferencesService.GetAsync(cancellationToken);
        // YER kararı bir kez verilir; hem yerel klasör hem UZAK yol bundan türetilir.
        //
        // Kaynak seviyesi SİTE ADIDIR ve bilinçli olarak zincirin kaynak kimliğiyle (aşağıdaki
        // ResolveChainPlanAsync çağrısının ikinci parametresi) AYNI değerdir: böylece bir zincirin
        // bütün halkaları aynı gün içinde aynı klasörde toplanır, elle bakan biri de halkaları
        // dosya adı ayıklamadan görür.
        var placement = BackupLayout.Ensure(preferences.DefaultBackupRoot, "sites", site.Name);
        var backupsDirectory = placement.LocalDirectory;

        // ZİNCİR KARARI ARŞİVDEN ÖNCE VERİLİR: hangi dosyaların yazılacağı buna bağlı.
        var plan = await ResolveChainPlanAsync("site", site.Name, request, cancellationToken);
        var artimsal = plan.Stamp is { } damga && damga.Mode == BackupChainPlanner.ModeIncremental;

        // DOSYA ADI DA aynı temizlikten geçmiş değeri kullanır (placement.Source): klasörü
        // korurken dosya adını ham bırakmak kapıyı açık bırakmaktı — Path.Combine, ayırıcı içeren
        // bir dosya adını yol olarak kabul eder ve yedek hedef klasörün DIŞINA yazılırdı.
        var fileName = $"{placement.Source}-{DateTime.UtcNow:yyyyMMddHHmmss}{(artimsal ? "-inc" : string.Empty)}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        var applicationRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
            ? site.PhysicalPath
            : site.ApplicationRootPath;

        // Dosya listesi bir kez taranır: hem indeksin hem de arşive girecek kümenin kaynağı budur.
        var taranan = request.IncludeFiles ? ScanFiles(applicationRoot, "files") : [];
        var indeks = BuildIndex(plan.Stamp, taranan);
        var fark = BackupChainPlanner.Diff(plan.PreviousIndex, indeks);
        var yazilacak = fark.Changed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // profesyonel panel benzeri tam-site yedeği: tek .zip içinde files/ + config/ + databases/ + mail/.
        var includedParts = new List<string>();
        await using (var zipStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var yazilan = AddFilesToArchive(archive, taranan, yazilacak);
            if (yazilan > 0)
            {
                includedParts.Add(artimsal ? $"files(+{yazilan})" : "files");
            }

            if (request.IncludeConfig)
            {
                var configJson = System.Text.Json.JsonSerializer.Serialize(site, BackupJsonOptions);
                AddTextEntry(archive, "config/site.json", configJson);
                includedParts.Add("config");
            }

            foreach (var db in request.Databases ?? [])
            {
                if (string.IsNullOrWhiteSpace(db.Engine) || string.IsNullOrWhiteSpace(db.DatabaseName))
                {
                    continue;
                }

                if (await TryAddDatabaseDumpAsync(archive, db.Engine, db.DatabaseName, "databases", cancellationToken))
                {
                    includedParts.Add($"db:{db.DatabaseName}");
                }
            }

            if (request.IncludeMail)
            {
                try
                {
                    // DİKKAT — BU BİR POSTA KUTUSU YEDEĞİ DEĞİLDİR.
                    //
                    // Yazılan şey hesap/alias/yönlendirme ENVANTERİDİR; TEK BİR İLETİ içermez.
                    // İletiler Stalwart'ın RocksDB deposunda yaşar ve o depodan tek bir alan adını
                    // dosya kopyalayarak ayırmak MÜMKÜN DEĞİLDİR (tek depo, paylaşılan indeksler);
                    // sunucu geneli posta yedeği bu yüzden ayrı bir akıştır (BackupMailAsync).
                    //
                    // Bu ayrım arşivin manifest'ine de yazılır: "mail" parçasını görüp postasının
                    // yedeklendiğini sanan biri, felaket anında geri getirilemeyecek olanı ancak o
                    // an öğrenirdi. Yarım bir söz, hiç söz vermemekten kötüdür.
                    var mailDomain = site.Name.ToLowerInvariant();
                    var mailWorkspace = await mailService.GetWorkspaceAsync(mailDomain, cancellationToken);
                    var mailJson = System.Text.Json.JsonSerializer.Serialize(mailWorkspace, BackupJsonOptions);
                    AddTextEntry(archive, $"mail/{mailDomain}.json", mailJson);
                    includedParts.Add("mail-inventory");
                }
                catch
                {
                    // Mail kurulu değilse/alan adı yoksa sessizce atla — yedek yine de tamam.
                }
            }

            WriteChainEntries(archive, plan.Stamp, indeks, fark);

            var manifest = System.Text.Json.JsonSerializer.Serialize(new
            {
                site = site.Name,
                createdAtUtc = DateTime.UtcNow,
                parts = includedParts,
                mode = plan.Stamp?.Mode ?? BackupChainPlanner.ModeFull,
                chainId = plan.Stamp?.ChainId ?? string.Empty,
                chainIndex = plan.Stamp?.ChainIndex ?? 0,
                note = artimsal
                    ? "Incremental link: files/ carries CHANGED files only; database dumps are always FULL."
                    : "Full backup.",
                // Arşivi açan kişi "mail/" klasörünü görüp postasının yedeklendiğini SANMAMALI:
                // burada yalnız hesap/alias envanteri var, ileti YOK. Yanlış güven, felaket anında
                // geri getirilemeyen posta demektir.
                mailNote = includedParts.Contains("mail-inventory")
                    ? "mail/ holds the ACCOUNT AND ALIAS INVENTORY only. It contains NO messages/mailbox data. Mailbox contents live in the Stalwart store and are covered ONLY by the server-wide mail backup."
                    : string.Empty
            }, BackupJsonOptions);
            AddTextEntry(archive, "manifest.json", manifest);
        }

        return await FinalizeBackupAsync(
            targetPath,
            fileName,
            "site",
            site.Name,
            "zip",
            request,
            placement,
            cancellationToken,
            plan.Stamp,
            plan.Stamp is null ? null : indeks);
    }

    // =====================================================================================
    // ARTIMSAL (INCREMENTAL) YEDEK ALTYAPISI
    //
    // Model Plesk'ten alınmıştır: ilk yedek her zaman TAM'dır; sonrakiler yalnızca son yedekten
    // beri DEĞİŞENİ taşır ve tam yedekle birlikte bir ZİNCİR oluşturur. Değişiklik kararı bir
    // İNDEKS üzerinden verilir (dosya arşivde var mıydı, boyutu/değişiklik tarihi aynı mı).
    //
    // ŞİFRELEME + ARTIMSAL: bir zincirin bütün halkaları aynı politikayla şifrelenir. Parola
    // kaybolursa TEK bir halka değil ZİNCİRİN TAMAMI (tam yedek dahil) geri getirilemez.
    // =====================================================================================

    /// <summary>Arşive girecek tek bir dosya: arşiv adı + disk yolu + indeks ölçüleri.</summary>
    private sealed record ArchiveFile(string EntryName, string FullPath, long Size, long ModifiedUtcTicks);

    /// <summary>Kayda basılacak zincir damgası.</summary>
    private sealed record ChainStamp(string ChainId, int ChainIndex, string Mode, string BaseBackupId);

    /// <summary>
    /// Bu yedeğin zincirdeki yeri + karşılaştırma temeli. <see cref="Stamp"/> <c>null</c> ise
    /// yedek zincire ait değildir (klasik, kendi başına yeterli tam yedek).
    /// </summary>
    private sealed record ChainPlan(ChainStamp? Stamp, BackupFileIndex? PreviousIndex);

    /// <summary>Arşiv içindeki indeks girdisi (zincirin kendi kendini anlatması için).</summary>
    private const string ArchiveIndexEntry = "index.json";

    /// <summary>Arşiv içindeki "bu halkada SİLİNMİŞ dosyalar" girdisi.</summary>
    private const string ArchiveDeletedEntry = "deleted.json";

    private static string IndexSidecarPath(string backupId) => $"backups/index/{backupId}.json";

    /// <summary>
    /// Yeni yedeğin TAM mı ARTIMSAL mı olacağını çözer ve artımsalsa karşılaştırma temelini yükler.
    /// </summary>
    private async Task<ChainPlan> ResolveChainPlanAsync(
        string type,
        string source,
        BackupArchiveRequest request,
        CancellationToken cancellationToken)
    {
        // Artımsal istenmiyorsa zincir de kurulmaz: zincirsiz yedek tek başına geri yüklenir ve
        // hiçbir budama kuralını kısıtlamaz. (Eski davranış birebir korunur.)
        if (!request.Incremental)
        {
            return new ChainPlan(null, null);
        }

        var kayitlar = await GetBackupsAsync(cancellationToken);
        var sonZincir = kayitlar
            .Where(record => string.Equals(record.Type, type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.Source, source, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(record.ChainId))
            .OrderByDescending(record => record.CreatedAtUtc)
            .Select(record => record.ChainId)
            .FirstOrDefault();

        BackupRecordDto? sonHalka = null;
        DateTime? tamYedekZamani = null;
        BackupFileIndex? oncekiIndeks = null;

        if (!string.IsNullOrWhiteSpace(sonZincir))
        {
            var zincir = BackupChainPlanner.GetChain(kayitlar, sonZincir);
            sonHalka = zincir.LastOrDefault();
            tamYedekZamani = zincir.FirstOrDefault(record => record.ChainIndex == 0)?.CreatedAtUtc;

            // Son halkanın DOSYASI yerinde değilse üstüne artımsal koymak, doğduğu anda kopuk bir
            // zincir üretmek olurdu. Ölçülmeyen şeye güvenilmez.
            if (sonHalka is not null && IsUsableBackup(sonHalka))
            {
                oncekiIndeks = await ReadFileIndexAsync(sonHalka, cancellationToken);
            }
        }

        var tamGerekli = BackupChainPlanner.TamYedekGerekli(
            sonHalka,
            tamYedekZamani,
            request.FullBackupEveryDays,
            oncekiIndeks is not null,
            DateTime.UtcNow);

        if (tamGerekli)
        {
            return new ChainPlan(
                new ChainStamp(Guid.NewGuid().ToString("N"), 0, BackupChainPlanner.ModeFull, string.Empty),
                null);
        }

        return new ChainPlan(
            new ChainStamp(sonHalka!.ChainId, sonHalka.ChainIndex + 1, BackupChainPlanner.ModeIncremental, sonHalka.Id),
            oncekiIndeks);
    }

    /// <summary>
    /// Bir halkanın dosya indeksini okur: önce diskteki yan kayıt, olmazsa (ŞİFRESİZ) arşivin
    /// içindeki <c>index.json</c>. İkisi de yoksa <c>null</c> → çağıran TAM yedeğe düşer.
    /// </summary>
    private async Task<BackupFileIndex?> ReadFileIndexAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        try
        {
            var sidecar = await fileStore.ReadAsync<BackupFileIndex?>(IndexSidecarPath(record.Id), null, cancellationToken);
            if (sidecar is not null)
            {
                return sidecar;
            }
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            logger?.LogWarning(exception, "Yedek indeksi okunamadi: {Backup}", record.Name);
        }

        // Yan kayıt yoksa arşivden okumayı dene. ŞİFRELİ arşivde bu, bütün dosyayı geçici diske
        // çözmek demektir (50 GB'lık bir yedekte kabul edilemez) → şifreli arşivde denenmez ve
        // sonuç zorunlu tam yedektir. Bu, güvenlik ile maliyetin ölçülmüş dengesidir.
        if (record.Encrypted || BackupArchiveCipher.IsEncryptedFile(record.Path))
        {
            return null;
        }

        try
        {
            using var archive = ZipFile.OpenRead(record.Path);
            var entry = archive.GetEntry(ArchiveIndexEntry);
            if (entry is null)
            {
                return null;
            }

            await using var stream = entry.Open();
            return await System.Text.Json.JsonSerializer.DeserializeAsync<BackupFileIndex>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger?.LogWarning(exception, "Yedek arsivinden indeks okunamadi: {Backup}", record.Name);
            return null;
        }
    }

    /// <summary>
    /// Bir dizini tarar ve indeks ölçülerini (boyut + değişiklik tarihi) toplar.
    /// Okunamayan dosya ATLANIR: erişilemeyen tek bir dosya bütün yedeği düşürmemeli.
    /// </summary>
    private static List<ArchiveFile> ScanFiles(string root, string entryPrefix)
    {
        var sonuc = new List<ArchiveFile>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return sonuc;
        }

        var tam = Path.GetFullPath(root);
        IEnumerable<string> dosyalar;
        try
        {
            dosyalar = Directory.EnumerateFiles(tam, "*", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return sonuc;
        }

        foreach (var dosya in dosyalar)
        {
            try
            {
                var bilgi = new FileInfo(dosya);
                if (!bilgi.Exists)
                {
                    continue;
                }

                var goreli = Path.GetRelativePath(tam, dosya).Replace('\\', '/');
                sonuc.Add(new ArchiveFile(
                    $"{entryPrefix}/{goreli}",
                    dosya,
                    bilgi.Length,
                    bilgi.LastWriteTimeUtc.Ticks));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Kilitli/erişilemeyen dosya atlanır (mevcut davranışın aynısı).
            }
        }

        return sonuc;
    }

    private static BackupFileIndex BuildIndex(ChainStamp? stamp, IReadOnlyCollection<ArchiveFile> files) => new()
    {
        ChainId = stamp?.ChainId ?? string.Empty,
        ChainIndex = stamp?.ChainIndex ?? 0,
        CreatedAtUtc = DateTime.UtcNow,
        Entries = files
            .Select(file => new BackupFileIndexEntry
            {
                Path = file.EntryName,
                Size = file.Size,
                ModifiedUtcTicks = file.ModifiedUtcTicks
            })
            .ToList()
    };

    /// <summary>Seçili dosyaları arşive ekler ve GERÇEKTEN eklenen sayısını döner.</summary>
    private static int AddFilesToArchive(ZipArchive archive, IReadOnlyCollection<ArchiveFile> files, HashSet<string> selected)
    {
        var eklenen = 0;
        foreach (var file in files)
        {
            if (!selected.Contains(file.EntryName))
            {
                continue;
            }

            try
            {
                archive.CreateEntryFromFile(file.FullPath, file.EntryName, CompressionLevel.Optimal);
                eklenen++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Kilitli/erişilemeyen dosyayı atla; yedek geri kalanıyla devam eder.
            }
        }

        return eklenen;
    }

    /// <summary>
    /// Zincir girdilerini arşive yazar: TAM indeks + bu halkada silinen dosyalar.
    /// </summary>
    /// <remarks>
    /// İndeks artımsal halkada da TAM tutulur; aksi hâlde bir sonraki halka "neyin silindiğini"
    /// hesaplayamazdı. Silinenler taşınmazsa geri yükleme, kullanıcının bilerek kaldırdığı
    /// dosyaları (ör. temizlenmiş bir zararlı eklentiyi) geri getirirdi.
    /// </remarks>
    private static void WriteChainEntries(
        ZipArchive archive,
        ChainStamp? stamp,
        BackupFileIndex index,
        BackupChainPlanner.IndexDiff diff)
    {
        if (stamp is null)
        {
            return;
        }

        AddTextEntry(archive, ArchiveIndexEntry, System.Text.Json.JsonSerializer.Serialize(index, BackupJsonOptions));
        if (diff.Deleted.Count > 0)
        {
            AddTextEntry(archive, ArchiveDeletedEntry, System.Text.Json.JsonSerializer.Serialize(diff.Deleted, BackupJsonOptions));
        }
    }

    /// <summary>
    /// Bir veritabanının dökümünü arşive ekler. Dökümü alınamayan veritabanı için <c>false</c> döner
    /// (çağıran bunu uyarıya çevirir); yedeğin geri kalanı durmaz.
    /// </summary>
    private async Task<bool> TryAddDatabaseDumpAsync(
        ZipArchive archive,
        string engine,
        string databaseName,
        string entryPrefix,
        CancellationToken cancellationToken)
    {
        var ext = engine.Equals("mssql", StringComparison.OrdinalIgnoreCase) ||
                  engine.Equals("sqlserver", StringComparison.OrdinalIgnoreCase) ? "bak" : "sql";
        var tempDump = Path.Combine(Path.GetTempPath(), $"adabk-{Guid.NewGuid():N}.{ext}");
        try
        {
            await databaseService.ExportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
            {
                Engine = engine,
                DatabaseName = databaseName,
                FilePath = tempDump
            }, cancellationToken);

            if (!File.Exists(tempDump))
            {
                return false;
            }

            archive.CreateEntryFromFile(tempDump, $"{entryPrefix}/{engine}-{databaseName}.{ext}", CompressionLevel.Optimal);
            return true;
        }
        finally
        {
            if (File.Exists(tempDump))
            {
                File.Delete(tempDump);
            }
        }
    }

    public async Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request, CancellationToken cancellationToken = default)
    {
        var preferences = await preferencesService.GetAsync(cancellationToken);
        var safeDatabaseName = string.IsNullOrWhiteSpace(request.DatabaseName) ? "all" : request.DatabaseName.Trim();

        // KAYNAK KLASÖRÜ dosya adının önekiyle AYNI ("mysql-shop_db"): diskten bakan biri klasör
        // adıyla dosya adı arasında zihinsel çeviri yapmak zorunda kalmasın.
        var kaynakKlasoru = $"{request.Engine}-{safeDatabaseName}";

        // VERİTABANI BİR SİTEYE AİTSE onun klasörüne girer: kullanıcının isteği "bir sitenin her
        // şeyi bir arada dursun". Sahiplik ÖLÇÜLEREK okunur (aşağıya bakınız); okunamıyorsa yedek
        // kategorinin kendi klasörüne düşer — yanlış siteye koymaktansa gruplamamak yeğdir.
        var sahipSite = await ResolveDatabaseSiteAsync(request.Engine, request.DatabaseName, cancellationToken);
        var placement = sahipSite.Length == 0
            ? BackupLayout.Ensure(preferences.DefaultBackupRoot, "databases", kaynakKlasoru)
            : BackupLayout.Ensure(preferences.DefaultBackupRoot, "sites", sahipSite, "databases", kaynakKlasoru);
        var backupsDirectory = placement.LocalDirectory;
        var extension = request.Engine.Equals("mssql", StringComparison.OrdinalIgnoreCase) ||
                        request.Engine.Equals("sqlserver", StringComparison.OrdinalIgnoreCase)
            ? "bak"
            : "sql";
        // Klasörle AYNI (temizlenmiş) önek: veritabanı adı da kullanıcı verisidir, ham hâliyle
        // Path.Combine'a verilmesi yedeği hedef klasörün dışına yazabilirdi.
        var fileName = $"{placement.Source}-{DateTime.UtcNow:yyyyMMddHHmmss}.{extension}";
        var targetPath = Path.Combine(backupsDirectory, fileName);
        var dump = await databaseService.ExportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
        {
            Engine = request.Engine,
            DatabaseName = request.DatabaseName,
            FilePath = targetPath
        }, cancellationToken);

        return await FinalizeBackupAsync(
            targetPath,
            fileName,
            "database",
            $"{dump.Engine}:{dump.DatabaseName}",
            extension,
            request,
            placement,
            cancellationToken);
    }

    /// <summary>
    /// Veritabanının bağlı olduğu site (yoksa boş metin). Yalnızca YEDEĞİN KLASÖRÜNÜ seçmek için
    /// kullanılır; erişim/kapsam kararı DEĞİLDİR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden sahiplik kaydı, ad benzerliği değil:</b> "shop_db şu siteye aittir" tahmini bu
    /// depoda daha önce gerçek bir veri kaybına yol açtı. Bağ, panelin kendi OTORİTE kaydından
    /// (<see cref="IDatabaseOwnershipService"/>, <c>config/db-ownership.json</c>) okunur; kayıt
    /// yoksa bağ da YOKTUR.
    /// </para>
    /// <para>
    /// <b>Neden hata yutulmuyor ama yedek de durdurulmuyor:</b> sahiplik okunamazsa bilinen tek
    /// gerçek "hangi siteye ait olduğunu bilmiyoruz"tur. Bu yüzden yedek eski düzendeki
    /// <c>databases/</c> klasörüne alınır (yedek KAYBOLMAZ) ve sebep günlüğe yazılır — sessizce
    /// bir siteye koymak, o siteyi geri yükleyen kişiye ait olmayan bir veritabanını sunardı.
    /// </para>
    /// <para>
    /// <b>Boş veritabanı adı</b> (motorun tamamının dökümü) hiçbir siteye ait olamaz; sahiplik hiç
    /// sorgulanmaz.
    /// </para>
    /// </remarks>
    private async Task<string> ResolveDatabaseSiteAsync(string engine, string? databaseName, CancellationToken cancellationToken)
    {
        if (databaseOwnership is null || string.IsNullOrWhiteSpace(databaseName))
        {
            return string.Empty;
        }

        try
        {
            var kayitlar = await databaseOwnership.GetAllAsync(cancellationToken);
            var ad = databaseName.Trim();
            foreach (var kayit in kayitlar)
            {
                if (string.Equals(kayit.Engine, engine?.Trim(), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(kayit.DatabaseName, ad, StringComparison.OrdinalIgnoreCase))
                {
                    return (kayit.SiteName ?? string.Empty).Trim();
                }
            }

            return string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(
                exception,
                "Veritabani sahipligi okunamadi; yedek site klasorune degil 'databases' klasorune alinacak: {Engine}/{Database}",
                engine,
                databaseName);
            return string.Empty;
        }
    }

    public async Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request, CancellationToken cancellationToken = default)
    {
        var preferences = await preferencesService.GetAsync(cancellationToken);
        // KAYNAK KLASÖRÜ YOK, bilerek: panel yedeğinin kaynağı TEKTİR (panelin kendisi).
        // "panel\panel\" gibi bir seviye hiçbir şeyi ayırmaz, yalnızca bir tık daha derine gömer.
        var placement = BackupLayout.Ensure(preferences.DefaultBackupRoot, "panel");
        var backupsDirectory = placement.LocalDirectory;
        var fileName = $"panel-config-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        // Panelin KENDİ durumu: adapanel.db (kullanıcı/2FA) + config/*.json (planlar, scope'lar, otomasyon...).
        // DataProtection ANAHTARLARI (keys/) ve sertifikalar BİLEREK DIŞARIDA — makineye bağlı/hassas, taşınamaz.
        var configDirectory = fileStore.ResolvePath("config");
        var dbPath = fileStore.ResolvePath("data/adapanel.db");
        var includedParts = new List<string>();

        await using (var zipStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            // adapanel.db (+ WAL/SHM) — çalışan panel kilitleyebilir → paylaşımlı okuma ile ekle.
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file))
                {
                    AddSharedFileToArchive(archive, file, $"data/{Path.GetFileName(file)}");
                    includedParts.Add(Path.GetFileName(file));
                }
            }

            // config/ tüm JSON durum dosyaları (backups.json dahil — zararsız).
            if (Directory.Exists(configDirectory))
            {
                AddDirectoryToArchive(archive, configDirectory, "config");
                includedParts.Add("config");
            }

            // Planlı görevler (\AdaPanel\ klasörü): Windows Görev Zamanlayıcı'da yaşarlar, panel dosyalarında DEĞİL —
            // ham görev XML'i (principal/tetikleyici/eylem birebir) yedeğe girer; panel geri yüklemesinde otomatik kaydedilir.
            var exportedTasks = await TryExportScheduledTasksAsync(cancellationToken);
            if (exportedTasks.Count > 0)
            {
                var index = new List<object>();
                for (var i = 0; i < exportedTasks.Count; i++)
                {
                    var entryName = $"tasks/{i:000}.xml";
                    AddTextEntry(archive, entryName, exportedTasks[i].Xml);
                    index.Add(new { name = exportedTasks[i].Name, entry = entryName });
                }

                AddTextEntry(archive, "tasks/index.json", System.Text.Json.JsonSerializer.Serialize(index, BackupJsonOptions));
                includedParts.Add($"tasks({exportedTasks.Count})");
            }

            // Tam-sunucu DR: IIS site envanteri (ad + fiziksel yol + havuz + bağlamalar). Geri yüklemede
            // eksik siteler bu envanterden yeniden kurulur; içerik site yedeklerinden döner. Best-effort.
            try
            {
                var sites = await iisSiteService.GetSitesAsync(cancellationToken);
                if (sites.Count > 0)
                {
                    AddTextEntry(archive, "iis/sites.json", System.Text.Json.JsonSerializer.Serialize(sites, BackupJsonOptions));
                    includedParts.Add($"iis-sites({sites.Count})");
                }
            }
            catch
            {
                // IIS okunamıyorsa (rol yok vb.) panel yedeği yine de oluşur.
            }

            var manifest = System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "panel-config",
                createdAtUtc = DateTime.UtcNow,
                parts = includedParts,
                note = "DataProtection keys and certificates are intentionally excluded (machine-bound)."
            }, BackupJsonOptions);
            AddTextEntry(archive, "manifest.json", manifest);
        }

        return await FinalizeBackupAsync(targetPath, fileName, "panel", "panel", "zip", request, placement, cancellationToken);
    }

    // =====================================================================================
    // ABONELİK YEDEĞİ (Plesk paritesi)
    //
    // Plesk site başına değil ABONELİK başına yedek alır: bir müşterinin bütün siteleri +
    // veritabanları + posta alan adları + FTP hesapları TEK arşivde. Bir müşteriyi başka
    // sunucuya taşımak ya da tek hamlede geri getirmek ancak böyle mümkündür.
    //
    // KİRACI İZOLASYONU buradaki tek gerçek risktir ve kararı SubscriptionBackupPlanner verir:
    // arşive giren her kaynak, aboneliğin kapsamından ÖLÇÜLEREK seçilir; ad benzerliğine
    // güvenilmez. Arşivi indiren müşterinin eline başkasının verisi geçemez.
    // =====================================================================================

    /// <summary>Abonelik arşivindeki FTP hesabı kaydı (parola YEDEĞE GİREMEZ, dışa aktarılamaz).</summary>
    private sealed record SubscriptionFtpEntry(string UserName, string SiteName, string HomePath);

    public async Task<BackupRecordDto> BackupSubscriptionAsync(BackupSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        if (customerScopes is null || databaseOwnership is null)
        {
            // Kapsam/sahiplik okunamıyorsa hangi verinin bu aboneliğe ait olduğunu ÖLÇEMEYİZ.
            // Ölçemediğimiz hâlde arşiv üretmek, başkasının verisini içeren bir yedek üretme
            // riskidir; açıkça reddetmek tek doğru cevaptır.
            //
            // Bu bir KULLANICI HATASI DEĞİL, kapsayıcı yapılandırma hatasıdır: üretimde DI bu iki
            // servisi her zaman verir, yalnızca dar kapsamlı testlerde boş kalabilir. Bu yüzden
            // kullanıcıya "şunu düzelt" diyen bir mesaj anahtarı üretilmez; ortak beklenmeyen-hata
            // yoluna (common.unexpected_error) düşer.
            throw new InvalidOperationException(
                "BackupService abonelik yedegi icin ICustomerScopeService ve IDatabaseOwnershipService gerektirir.");
        }

        var scope = await customerScopes.GetScopeAsync(request.UserId, cancellationToken);
        var mailDomains = request.IncludeMail
            ? await customerScopes.GetAllowedMailDomainsAsync(request.UserId, cancellationToken)
            : [];

        var ownership = request.IncludeDatabases
            ? await databaseOwnership.GetAllAsync(cancellationToken)
            : [];

        IReadOnlyCollection<(string UserName, string SiteName, string HomePath)> ftpUsers = [];
        if (request.IncludeFtp)
        {
            try
            {
                ftpUsers = (await ftpService.GetFtpUsersAsync(cancellationToken))
                    .Select(user => (user.UserName, user.SiteName, user.HomePath))
                    .ToArray();
            }
            catch (Exception exception)
            {
                // FTP rolü kurulu değilse abonelik yedeği yine alınır.
                logger?.LogWarning(exception, "Abonelik yedegi icin FTP hesaplari okunamadi.");
            }
        }

        var plan = SubscriptionBackupPlanner.BuildPlan(request.UserId, scope.Sites, mailDomains, ownership, ftpUsers);
        var kaynak = request.UserId.ToString("N");

        var preferences = await preferencesService.GetAsync(cancellationToken);
        // KAYNAK KLASÖRÜ: aboneliğin TAM kimliği. Dosya adındaki kısaltılmış (8 haneli) önek
        // BİLEREK kullanılmadı — klasör bir zincirin halkalarını topluyor; iki aboneliğin kimliği
        // ilk 8 hanede çakışırsa halkaları aynı klasörde karışırdı.
        var placement = BackupLayout.Ensure(preferences.DefaultBackupRoot, "subscriptions", kaynak);
        var backupsDirectory = placement.LocalDirectory;
        var chainPlan = await ResolveChainPlanAsync("subscription", kaynak, request, cancellationToken);
        var artimsal = chainPlan.Stamp is { } damga && damga.Mode == BackupChainPlanner.ModeIncremental;

        // Dosya adında kullanıcı kimliğinin ilk 8 hanesi: aynı gün içinde alınan farklı
        // aboneliklerin arşivleri diskte birbirinden ayırt edilebilir olmalı.
        var fileName = $"abonelik-{kaynak[..8]}-{DateTime.UtcNow:yyyyMMddHHmmss}{(artimsal ? "-inc" : string.Empty)}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        // Bütün sitelerin dosyaları TEK bir indekste toplanır: zincir kararı abonelik bütünü
        // üzerinden verilir, site site değil. Aksi hâlde bir sitenin tam yedeği başka bir siteyi
        // artımsal bırakır ve zincir anlamını kaybederdi.
        var taranan = new List<ArchiveFile>();
        var siteBilgileri = new Dictionary<string, AdaPanel.Core.Dtos.Sites.SiteDto>(StringComparer.OrdinalIgnoreCase);
        var uyarilar = new List<BackupRestoreWarningDto>();

        foreach (var siteAdi in plan.Sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var site = await iisSiteService.GetSiteAsync(siteAdi, cancellationToken);
                siteBilgileri[siteAdi] = site;

                if (request.IncludeFiles)
                {
                    var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
                    taranan.AddRange(ScanFiles(kok, $"sites/{siteAdi}/files"));
                }
            }
            catch (Exception exception)
            {
                // Site IIS'te yoksa/okunamıyorsa SESSİZ GEÇİLMEZ: kullanıcı yedeğinde o sitenin
                // OLMADIĞINI bilmelidir.
                uyarilar.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.site_unavailable",
                    Target = siteAdi,
                    Detail = exception.Message
                });
            }
        }

        var indeks = BuildIndex(chainPlan.Stamp, taranan);
        var fark = BackupChainPlanner.Diff(chainPlan.PreviousIndex, indeks);
        var yazilacak = fark.Changed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var parcalar = new List<string>();
        await using (var zipStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var yazilanDosya = AddFilesToArchive(archive, taranan, yazilacak);
            if (yazilanDosya > 0)
            {
                parcalar.Add(artimsal ? $"files(+{yazilanDosya})" : $"files({yazilanDosya})");
            }

            foreach (var (siteAdi, site) in siteBilgileri)
            {
                AddTextEntry(
                    archive,
                    $"sites/{siteAdi}/config/site.json",
                    System.Text.Json.JsonSerializer.Serialize(site, BackupJsonOptions));
            }

            if (siteBilgileri.Count > 0)
            {
                parcalar.Add($"sites({siteBilgileri.Count})");
            }

            // VERİTABANLARI: artımsal halkada bile HER ZAMAN TAM döküm. Bir SQL dökümünü "değişen
            // dosyalar" gibi parçalamak, geri yüklenmeyen bir yedek üretirdi.
            var dbSayisi = 0;
            foreach (var db in plan.Databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await TryAddDatabaseDumpAsync(archive, db.Engine, db.DatabaseName, "databases", cancellationToken))
                    {
                        dbSayisi++;
                    }
                    else
                    {
                        uyarilar.Add(new BackupRestoreWarningDto
                        {
                            MessageKey = "backup.database_dump_empty",
                            Target = $"{db.Engine}/{db.DatabaseName}"
                        });
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.database_dump_failed",
                        Target = $"{db.Engine}/{db.DatabaseName}",
                        Detail = exception.Message
                    });
                }
            }

            if (dbSayisi > 0)
            {
                parcalar.Add($"databases({dbSayisi})");
            }

            // POSTA: burada taşınan şey posta KUTUSU ENVANTERİDİR (hesaplar, alias'lar, kotalar),
            // iletilerin kendisi DEĞİL. İletiler mail sunucusunun veri dizinindedir ve sunucu
            // geneli posta yedeğiyle (BackupMailAsync) alınır. Bu ayrım manifeste de yazılır;
            // "posta yedeklendi" deyip iletilerin gelmediğini geri yüklerken öğrenmek kabul edilemez.
            var postaSayisi = 0;
            foreach (var domain in plan.MailDomains)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var workspace = await mailService.GetWorkspaceAsync(domain, cancellationToken);
                    AddTextEntry(
                        archive,
                        $"mail/{domain}.json",
                        System.Text.Json.JsonSerializer.Serialize(workspace, BackupJsonOptions));
                    postaSayisi++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_domain_failed",
                        Target = domain,
                        Detail = exception.Message
                    });
                }
            }

            if (postaSayisi > 0)
            {
                parcalar.Add($"mail({postaSayisi})");
            }

            if (plan.FtpUsers.Count > 0)
            {
                var ftpKayitlari = plan.FtpUsers
                    .Select(user => new SubscriptionFtpEntry(user.UserName, user.SiteName, user.HomePath))
                    .ToArray();
                AddTextEntry(archive, "ftp/accounts.json", System.Text.Json.JsonSerializer.Serialize(ftpKayitlari, BackupJsonOptions));
                parcalar.Add($"ftp({ftpKayitlari.Length})");
            }

            AddTextEntry(archive, "subscription.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                userId = kaynak,
                planId = scope.PlanId,
                planName = scope.PlanName,
                expiresAtUtc = scope.ExpiresAtUtc,
                maxSites = scope.MaxSites,
                maxDiskMb = scope.MaxDiskMb,
                sites = plan.Sites,
                databases = plan.Databases.Select(db => $"{db.Engine}:{db.DatabaseName}"),
                mailDomains = plan.MailDomains,
                ftpUsers = plan.FtpUsers.Select(user => user.UserName)
            }, BackupJsonOptions));

            WriteChainEntries(archive, chainPlan.Stamp, indeks, fark);

            AddTextEntry(archive, "manifest.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "subscription",
                userId = kaynak,
                createdAtUtc = DateTime.UtcNow,
                parts = parcalar,
                items = plan.ItemCount,
                mode = chainPlan.Stamp?.Mode ?? BackupChainPlanner.ModeFull,
                chainId = chainPlan.Stamp?.ChainId ?? string.Empty,
                chainIndex = chainPlan.Stamp?.ChainIndex ?? 0,
                note = "Mail entries are MAILBOX INVENTORY (accounts/aliases/quotas), not messages. " +
                       "Database dumps are always FULL, even in incremental links. " +
                       "FTP and mailbox PASSWORDS cannot be exported and must be reset after restore."
            }, BackupJsonOptions));
        }

        var record = await FinalizeBackupAsync(
            targetPath,
            fileName,
            "subscription",
            kaynak,
            "zip",
            request,
            placement,
            cancellationToken,
            chainPlan.Stamp,
            chainPlan.Stamp is null ? null : indeks,
            kaynak,
            plan.ItemCount);

        // Yedek ALINIRKEN atlanan her şey kayda taşınır. "Yedeklendi" deyip içinde olmayan bir
        // veritabanını geri yüklerken keşfetmek, panelin verebileceği en pahalı sürprizdir.
        if (uyarilar.Count > 0)
        {
            record.Warnings.AddRange(uyarilar);

            // Uzak hedef durumu ZATEN bir hata anlatıyorsa (created_remote_failed) onu EZMEYİZ:
            // off-site kopyanın olmadığını söylemek, eksik öğe uyarısından daha az önemli değildir.
            // İkisi de kayıtta durur; biri durumda, diğeri uyarı listesinde.
            if (record.Status == "created" || record.Status == "created_remote_synced")
            {
                record.Status = "created_with_errors";
            }

            await UpsertRecordAsync(record, cancellationToken);
        }

        return record;
    }

    // Çalışan süreç kilitlese bile (ör. SQLite adapanel.db) paylaşımlı okuma ile zip'e ekler.
    private static void AddSharedFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        try
        {
            using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            source.CopyTo(entryStream);
        }
        catch (IOException)
        {
            // Kilitli/erişilemeyen dosyayı atla; yedek geri kalanıyla devam eder.
        }
    }

    private sealed record ScheduledTaskExport(string Name, string Xml);

    private sealed record ScheduledTaskIndexEntry(string Name, string Entry);

    // Planlı görevlerin ham XML dökümü (best-effort: dışa aktarılamazsa panel yedeği yine de oluşur).
    private static async Task<List<ScheduledTaskExport>> TryExportScheduledTasksAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string script = @"$list = Get-ScheduledTask -TaskPath '\AdaPanel\' -ErrorAction SilentlyContinue | ForEach-Object {
  [pscustomobject]@{ Name = $_.TaskName; Xml = [string](Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath) }
}
$list | ConvertTo-Json -Depth 3";
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return [];
            }

            using var document = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            ScheduledTaskExport[] items = root.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Array => System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskExport[]>(root) ?? [],
                System.Text.Json.JsonValueKind.Object => [System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskExport>(root)!],
                _ => []
            };

            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Xml))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // Panel yedeğindeki görevleri Görev Zamanlayıcı'ya geri kaydeder (XML: principal/tetikleyici birebir).
    private static async Task TryRestoreScheduledTasksAsync(string extractedRoot, CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(extractedRoot, "tasks", "index.json");
            if (!File.Exists(indexPath))
            {
                return;
            }

            var index = System.Text.Json.JsonSerializer.Deserialize<List<ScheduledTaskIndexEntry>>(
                await File.ReadAllTextAsync(indexPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

            foreach (var item in index)
            {
                var xmlPath = Path.Combine(extractedRoot, item.Entry.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(item.Name) || !File.Exists(xmlPath))
                {
                    continue;
                }

                // XML'i base64 taşı: tırnak/özel karakter kaçış derdi tamamen ortadan kalkar.
                var xml = await File.ReadAllTextAsync(xmlPath, cancellationToken);
                var xmlBase64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(xml));
                var script = $@"$xml = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{xmlBase64}'))
Register-ScheduledTask -TaskName '{item.Name.Replace("'", "''")}' -TaskPath '\AdaPanel\' -Xml $xml -Force | Out-Null";
                await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            }
        }
        catch
        {
            // Best-effort: bir görev geri yüklenemese de yedek çıkarma işlemi tamamlanır.
        }
    }

    /// <summary>
    /// Tam-sunucu DR: panel yedeğindeki IIS envanterinden (iis/sites.json) eksik siteleri yeniden kurar.
    /// Best-effort — site başına hata diğerlerini durdurmaz; sonuç, çıkarma klasörüne
    /// iis-restore-report.txt olarak yazılır (kullanıcı ne kurulup ne atlandığını görür).
    /// HTTPS bağlamaları bilerek kurulmaz (sertifika makineye bağlıdır; SSL ekranından yeniden verilir).
    /// </summary>
    private async Task TryRestoreIisSitesAsync(string extractedRoot, List<string> report, CancellationToken cancellationToken)
    {
        try
        {
            var snapshotPath = Path.Combine(extractedRoot, "iis", "sites.json");
            if (!File.Exists(snapshotPath))
            {
                return;
            }

            var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<AdaPanel.Core.Dtos.Sites.SiteDto>>(
                await File.ReadAllTextAsync(snapshotPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (snapshot.Count == 0)
            {
                return;
            }

            var existing = (await iisSiteService.GetSitesAsync(cancellationToken)).Select(site => site.Name);
            var missing = IisSnapshotRestore.GetMissingSites(snapshot, existing);
            report.Add($"Envanter: {snapshot.Count} site; eksik: {missing.Count}.");

            foreach (var site in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (create, extraBindings) = IisSnapshotRestore.ToProvisioningPlan(site);
                    if (!string.IsNullOrWhiteSpace(create.PhysicalPath))
                    {
                        Directory.CreateDirectory(create.PhysicalPath);
                    }

                    await iisSiteService.CreateSiteAsync(create, cancellationToken);
                    foreach (var binding in extraBindings)
                    {
                        try
                        {
                            await iisSiteService.AddBindingAsync(site.Name, binding, cancellationToken);
                        }
                        catch (Exception bindingException)
                        {
                            report.Add($"  {site.Name}: ek bağlama {binding.HostName}:{binding.Port} eklenemedi — {bindingException.Message}");
                        }
                    }

                    report.Add($"  {site.Name}: yeniden kuruldu ({1 + extraBindings.Count} http bağlama). İçerik için site yedeğini geri yükleyin; HTTPS'i SSL ekranından yeniden verin.");
                }
                catch (Exception siteException)
                {
                    report.Add($"  {site.Name}: KURULAMADI — {siteException.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            report.Add($"IIS envanteri geri yüklenemedi: {exception.Message}");
        }
    }

    private sealed class FtpUserSnapshot
    {
        public string UserName { get; set; } = string.Empty;
        public string HomePath { get; set; } = string.Empty;
        public string SiteName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Tam-sunucu DR: yedekteki config/ftp-users.json kayıtlarından FTP hesaplarını yeniden kurar.
    /// Windows yerel hesap PAROLALARI yedeğe giremez (dışa aktarılamaz) → hesap RASTGELE parolayla
    /// açılır ve rapora "parolayı FTP ekranından sıfırlayın" düşülür. Hesap zaten varsa parolasına
    /// dokunulmaz (CreateFtpUserAsync mevcut hesapta yalnız ACL/ana dizini tazeler — idempotent).
    /// </summary>
    private async Task TryRestoreFtpUsersAsync(string extractedRoot, List<string> report, CancellationToken cancellationToken)
    {
        try
        {
            var snapshotPath = Path.Combine(extractedRoot, "config", "ftp-users.json");
            if (!File.Exists(snapshotPath))
            {
                return;
            }

            var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<FtpUserSnapshot>>(
                await File.ReadAllTextAsync(snapshotPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (snapshot.Count == 0)
            {
                return;
            }

            report.Add($"FTP envanteri: {snapshot.Count} hesap.");
            foreach (var user in snapshot.Where(item => !string.IsNullOrWhiteSpace(item.UserName)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ftpService.CreateFtpUserAsync(new AdaPanel.Core.Dtos.Ftp.CreateFtpUserRequest
                    {
                        UserName = user.UserName,
                        Password = $"Dr{Guid.NewGuid():N}aA1",
                        SiteName = user.SiteName,
                        HomePath = user.HomePath
                    }, cancellationToken);
                    report.Add($"  FTP {user.UserName}: hazır — parolayı FTP ekranından sıfırlayın (parolalar yedeğe giremez).");
                }
                catch (Exception exception)
                {
                    report.Add($"  FTP {user.UserName}: KURULAMADI — {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            report.Add($"FTP envanteri geri yüklenemedi: {exception.Message}");
        }
    }

    private static async Task TryWriteRestoreReportAsync(string extractedRoot, List<string> report, CancellationToken cancellationToken)
    {
        if (report.Count == 0)
        {
            return;
        }

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(extractedRoot, "iis-restore-report.txt"), report, cancellationToken);
        }
        catch
        {
            // rapor yazılamazsa akış bozulmaz
        }
    }

    private const string StalwartTaskName = "AdaStalwart";

    /// <summary>
    /// Stalwart mail verisinin yedeği. RocksDB canlı kopyalanamayacağından servis
    /// (zamanlanmış görev) kısa süre durdurulur; zip alınır; her durumda yeniden başlatılır.
    /// </summary>
    public async Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request, CancellationToken cancellationToken = default)
    {
        var installDirectory = ResolveStalwartInstallDirectory();
        var dataDirectory = Path.Combine(installDirectory, "data");
        if (!Directory.Exists(dataDirectory))
        {
            throw new AppException("backup.mail_not_installed", 404, 404);
        }

        var preferences = await preferencesService.GetAsync(cancellationToken);
        // KAYNAK KLASÖRÜ YOK, bilerek: bu yedeğin kaynağı TEKTİR — Stalwart'ın veri deposunun
        // TAMAMI. Alan adı başına bir kaynak klasörü açmak, olmayan bir ayrımı varmış gibi
        // gösterirdi (bkz. BackupMailAsync özeti).
        var placement = BackupLayout.Ensure(preferences.DefaultBackupRoot, "mail");
        var backupsDirectory = placement.LocalDirectory;
        var fileName = $"stalwart-mail-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);

        await StopStalwartAsync(installDirectory, cancellationToken);
        try
        {
            ZipFile.CreateFromDirectory(dataDirectory, targetPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        finally
        {
            await StartStalwartAsync(cancellationToken);
        }

        return await FinalizeBackupAsync(targetPath, fileName, "mail", "stalwart", "zip", request, placement, cancellationToken);
    }

    private async Task RestoreMailAsync(string archivePath, CancellationToken cancellationToken)
    {
        var installDirectory = ResolveStalwartInstallDirectory();
        var dataDirectory = Path.Combine(installDirectory, "data");

        await StopStalwartAsync(installDirectory, cancellationToken);
        try
        {
            // Mevcut veri yana alınır; geri yükleme başarısız olursa elle dönülebilir.
            if (Directory.Exists(dataDirectory))
            {
                var sideline = dataDirectory + $".old-{DateTime.UtcNow:yyyyMMddHHmmss}";
                Directory.Move(dataDirectory, sideline);
            }

            // Yedek "data/..." girdileri içerir; kurulum köküne çıkarılır.
            ZipFile.ExtractToDirectory(archivePath, installDirectory, overwriteFiles: true);
        }
        finally
        {
            await StartStalwartAsync(cancellationToken);
        }
    }

    private static string ResolveStalwartInstallDirectory()
    {
        var root = Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\stalwart");
        if (!Directory.Exists(root))
        {
            throw new AppException("backup.mail_not_installed", 404, 404);
        }

        return root;
    }

    /// <summary>Süreç gerçekten durdu mu diye kaç tur denenir (her tur arası 1 sn).</summary>
    private const int StalwartStopAttempts = 10;

    /// <summary>
    /// Stalwart'ı durdurur ve DURDUĞUNU DOĞRULAR.
    /// <para>
    /// Eski hâlde <c>schtasks /End</c> sonucu okunmuyor, <c>Kill</c> çıplak bir <c>catch { }</c> ile
    /// yutuluyor ve ardından yalnızca 1 saniye bekleniyordu. Süreç ayaktayken RocksDB dosyaları
    /// kopyalanırsa GERİ YÜKLENEMEYEN bir posta yedeği üretilir — üstelik "yedeklendi" denerek.
    /// Bozuk yedeği başarılı saymaktansa yedek almayı reddetmek gerekir: kullanıcı o zaman gerçek
    /// bir yedeği olmadığını bilir.
    /// </para>
    /// </summary>
    private static async Task StopStalwartAsync(string installDirectory, CancellationToken cancellationToken)
    {
        // schtasks başarısız olabilir (görev yok, zaten durmuş). TEK BAŞINA KARAR VERMEZ; asıl ölçüm
        // aşağıdaki süreç doğrulamasıdır, bu yüzden sonucu yalnızca teşhis metnine taşınır.
        var end = await ProcessRunner.RunAsync(
            "schtasks.exe",
            $"/End /TN \"{StalwartTaskName}\"",
            cancellationToken: cancellationToken);

        var prefix = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var blockers = Array.Empty<string>();
        for (var attempt = 0; attempt < StalwartStopAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            blockers = KillStalwartProcesses(prefix);
            if (blockers.Length == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var teshis = $"schtasks /End exit={end.ExitCode} {end.StandardError}".Trim() +
                     $"\r\nHalen calisan/dogrulanamayan surecler: {string.Join(", ", blockers)}";
        throw new AppException("backup.mail_stop_failed", 409, 409, teshis);
    }

    /// <summary>
    /// Kurulum dizininden çalışan stalwart süreçlerini sonlandırmayı dener ve denemeden SONRA hâlâ
    /// engel olanları tanımlarıyla döner. Boş dizi = "dizin gerçekten serbest".
    /// <para>
    /// Yolu okunamayan bir süreç de engel sayılır: onun bizim örneğimiz OLMADIĞINI ölçemiyoruz ve
    /// ölçemediğimiz şeye "sorun yok" demek, bu hatanın ta kendisiydi.
    /// </para>
    /// </summary>
    private static string[] KillStalwartProcesses(string prefix)
    {
        var blockers = new List<string>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("stalwart"))
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception exception)
                {
                    blockers.Add($"pid {process.Id} (yol okunamadi: {exception.Message})");
                    continue;
                }

                if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // başka bir kurulumun süreci — bizim veri dizinimizi tutmuyor
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
                catch (Exception exception)
                {
                    blockers.Add($"pid {process.Id} sonlandirilamadi: {exception.Message}");
                    continue;
                }

                if (!process.HasExited)
                {
                    blockers.Add($"pid {process.Id} halen calisiyor");
                }
            }
            catch (InvalidOperationException)
            {
                // Süreç bu arada çıkmış; engel değil.
            }
            finally
            {
                process.Dispose();
            }
        }

        return blockers.ToArray();
    }

    private static Task StartStalwartAsync(CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync("schtasks.exe", $"/Run /TN \"{StalwartTaskName}\"", cancellationToken: cancellationToken);

    public async Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default)
    {
        var backups = (await GetBackupsAsync(cancellationToken)).ToArray();
        var record = backups.FirstOrDefault(item => item.Id == request.BackupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        if (!File.Exists(record.Path))
        {
            throw new AppException("backup.not_found", 404, 404);
        }

        // Önceki bir geri yüklemeden kalan uyarılar bu çalışmanın sonucu sanılmasın.
        record.Warnings = [];
        record.PreservedPath = string.Empty;

        // ZİNCİR SAĞLAMLIĞI CANLI İÇERİĞE DOKUNMADAN ÖNCE ÖLÇÜLÜR.
        //
        // Artımsal bir halka, tam yedek + aradaki bütün halkalar uygulanmadan anlam ifade etmez.
        // Eksik halkayla "elimizdekini uygulayalım" demek, sitenin hangi dosyalarının eski
        // kaldığını kimsenin bilmemesi demektir. Plesk kopuk zinciri işaretler ve geri yüklerken
        // eksik yedeklerin ADINI söyler; panel de aynısını yapar ama işi hiç başlatmaz.
        var chain = BackupChainPlanner.Resolve(backups, record.Id, IsUsableBackup);
        if (chain.Broken)
        {
            throw new AppException(
                "backup.chain_broken",
                409,
                409,
                $"Eksik halkalar: {string.Join(", ", chain.MissingLinks)}");
        }

        // Şifreliyse ÖNCE çözülür: yanlış parola / kurcalanmış arşiv burada, canlı içeriğe hiç
        // dokunulmadan durur. Çözülmüş geçici kopya iş bitince silinir.
        using var content = await OpenContentAsync(record, request.EncryptionPassword, cancellationToken);

        if (string.Equals(record.Type, "mail", StringComparison.OrdinalIgnoreCase))
        {
            await RestoreMailAsync(content.FilePath, cancellationToken);
            record.Status = "restored";
            await UpsertRecordAsync(record, cancellationToken);
            return record;
        }

        // ABONELİK yedeği: sitelerin dosyaları kendi köklerine, veritabanları içe aktarılarak,
        // posta/FTP hesapları yeniden kurularak geri yüklenir. Zincirin bütün halkaları sırayla.
        if (string.Equals(record.Type, "subscription", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            var sonuc = await RestoreSubscriptionChainAsync(
                record,
                content.FilePath,
                chain,
                backups,
                request.EncryptionPassword,
                cancellationToken);

            record.Warnings = sonuc.Warnings;
            record.PreservedPath = sonuc.PreservedPath;
            record.Status = sonuc.Warnings.Count == 0 ? "restored" : "restored_with_errors";
            await UpsertRecordAsync(record, cancellationToken);
            return record;
        }

        // profesyonel panel benzeri tam-site yedeği (files/ + databases/ + config/ içeren birleşik .zip):
        // dosyaları sitenin köküne, veritabanlarını içe aktararak gerçek geri yükleme yap.
        if (string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase) &&
            IsCombinedSiteArchive(content.FilePath))
        {
            var sonuc = await RestoreSiteChainAsync(
                record,
                content.FilePath,
                chain,
                backups,
                request.TargetPath,
                request.EncryptionPassword,
                cancellationToken);

            record.Warnings = sonuc.Warnings;
            record.PreservedPath = sonuc.PreservedPath;

            // YARIM KALAN İŞE "restored" DENMEZ. Dosyalar canlı kökün üzerine zaten yazıldığı için
            // bu durumda kullanıcının elinde ne eski hâl ne de dönmüş bir veritabanı vardır;
            // farkında olmadan devam etmesi en pahalı sonuçtur.
            record.Status = sonuc.Warnings.Count == 0 ? "restored" : "restored_with_errors";
            await UpsertRecordAsync(record, cancellationToken);
            return record;
        }

        if (string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            var destination = string.IsNullOrWhiteSpace(request.TargetPath)
                ? fileStore.EnsureDirectory($"restore/{record.Source}-{DateTime.UtcNow:yyyyMMddHHmmss}")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.TargetPath.Trim()));

            Directory.CreateDirectory(destination);
            ZipFile.ExtractToDirectory(content.FilePath, destination, overwriteFiles: true);

            // Panel yedeği: içindeki planlı görevleri (tasks/*.xml) Görev Zamanlayıcı'ya otomatik geri kaydet
            // + tam-sunucu DR: eksik IIS siteleri + FTP hesapları yeniden kurulur (rapor: iis-restore-report.txt).
            if (string.Equals(record.Type, "panel", StringComparison.OrdinalIgnoreCase))
            {
                await TryRestoreScheduledTasksAsync(destination, cancellationToken);
                var report = new List<string>();
                await TryRestoreIisSitesAsync(destination, report, cancellationToken);
                await TryRestoreFtpUsersAsync(destination, report, cancellationToken);
                await TryWriteRestoreReportAsync(destination, report, cancellationToken);
            }

            record.Status = "restored";
            record.Path = destination;
            // Kayıt artık ÇIKARILMIŞ dizini gösteriyor; orası şifreli değildir. Bayrağı "true"
            // bırakmak, bir sonraki geri yüklemede olmayan bir şifre için parola sordururdu.
            record.Encrypted = false;
        }
        else if (string.Equals(record.Format, "sql", StringComparison.OrdinalIgnoreCase))
        {
            var sourceParts = record.Source.Split(':', 2);
            await databaseService.ImportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
            {
                Engine = sourceParts[0],
                DatabaseName = sourceParts.Length > 1 ? sourceParts[1] : string.Empty,
                FilePath = content.FilePath
            }, cancellationToken);
            record.Status = "restored";
        }

        await UpsertRecordAsync(record, cancellationToken);
        return record;
    }

    private const int MaxEntriesPerListing = 500;

    /// <summary>
    /// Zip yedeğinin içinde dizin bazlı gezinme: verilen önekin hemen altındaki
    /// klasörler ve dosyalar döner. Girdi adları '\\' veya '/' ayırıcılı olabilir.
    /// </summary>
    public async Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default)
    {
        var record = await FindZipRecordAsync(backupId, cancellationToken);
        var normalizedPrefix = NormalizeArchivePath(prefix ?? string.Empty);
        if (normalizedPrefix.Length > 0 && !normalizedPrefix.EndsWith('/'))
        {
            normalizedPrefix += "/";
        }

        var directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<BackupEntryDto>();
        var truncated = false;

        // GÖZATMA yalnız panelde KAYITLI parolayı kullanır. Parolayı bu GET ucuna sorgu dizesiyle
        // taşımak, onu IIS erişim günlüklerine ve tarayıcı geçmişine yazmak olurdu.
        using var content = await OpenContentAsync(record, null, cancellationToken);
        using var archive = ZipFile.OpenRead(content.FilePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeArchivePath(entry.FullName);
            if (!name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) || name.Length == normalizedPrefix.Length)
            {
                continue;
            }

            var rest = name[normalizedPrefix.Length..];
            var slashIndex = rest.IndexOf('/');
            if (slashIndex >= 0)
            {
                directories.Add(rest[..slashIndex]);
            }
            else if (files.Count < MaxEntriesPerListing)
            {
                files.Add(new BackupEntryDto
                {
                    Name = rest,
                    Path = name,
                    IsDirectory = false,
                    Size = entry.Length
                });
            }
            else
            {
                truncated = true;
            }
        }

        var entries = directories
            .Select(directory => new BackupEntryDto
            {
                Name = directory,
                Path = normalizedPrefix + directory + "/",
                IsDirectory = true
            })
            .Concat(files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            .Take(MaxEntriesPerListing)
            .ToArray();

        return new BackupEntriesDto
        {
            BackupId = record.Id,
            Prefix = normalizedPrefix,
            Entries = entries,
            Truncated = truncated || entries.Length >= MaxEntriesPerListing
        };
    }

    /// <summary>
    /// Seçilen arşiv öğelerini geri yükler. Hedef verilmemişse sitenin orijinal kökü
    /// kullanılır: yedek includeBaseDirectory ile alındığından girdiler kök klasör adını
    /// içerir; bu yüzden çıkarma, kökün ÜST dizinine göre yapılır.
    /// </summary>
    public async Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default)
    {
        var record = await FindZipRecordAsync(request.BackupId, cancellationToken);
        var items = request.Items
            .Select(NormalizeArchivePath)
            .Where(item => item.Length > 0)
            .ToArray();
        if (items.Length == 0)
        {
            throw new AppException("backup.items_required", 400, 400);
        }

        string targetRoot;
        if (!string.IsNullOrWhiteSpace(request.TargetPath))
        {
            targetRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.TargetPath.Trim()));
        }
        else
        {
            if (!string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase))
            {
                // SITE DIŞI ARŞİVLER (abonelik/panel/mail) İÇİN AYRILMIŞ BİR ÇIKARMA KLASÖRÜ.
                //
                // Eskiden burada koşulsuz `backup.target_path_required` fırlatılıyordu. Sonuç:
                // kapsamlı kullanıcı (müşteri/bayi) hedef yol VEREMEDİĞİ için — güvenlik gereği
                // o alan zorla boşaltılır — abonelik arşivinden seçerek geri yükleme kiracıda
                // HİÇBİR ZAMAN çalışmıyordu. Özellik vardı, kapısı kilitliydi.
                //
                // Tek bir "doğru" canlı hedef YOKTUR: abonelik arşivi birden çok sitenin
                // dosyalarını, veritabanı dökümlerini ve posta envanterini bir arada taşır.
                // Bu yüzden canlı içeriğin üzerine yazmaya ÇALIŞMIYORUZ (site arşivinde de
                // yazılmıyor; oraya da sitenin YANINA çıkarılıyor). İçerik, panelin yedek kökü
                // altında tarih damgalı ayrı bir klasöre arşivdeki yapısıyla çıkarılır;
                // kullanıcı ne aldığını görür ve istediğini oradan taşır.
                var preferences = await preferencesService.GetAsync(cancellationToken);
                var restoreRoot = BackupLayout.Ensure(preferences.DefaultBackupRoot, "restored").LocalDirectory;
                targetRoot = Path.Combine(restoreRoot, BuildRestoreFolderName(record));
                Directory.CreateDirectory(targetRoot);
            }
            else
            {
                AdaPanel.Core.Dtos.Sites.SiteDto site;
                try
                {
                    site = await iisSiteService.GetSiteAsync(record.Source, cancellationToken);
                }
                catch (AppException)
                {
                    // Site artık yoksa kullanıcı açık bir hedef yol vermelidir.
                    throw new AppException("backup.target_path_required", 404, 404);
                }

                var applicationRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                    ? site.PhysicalPath
                    : site.ApplicationRootPath;
                targetRoot = Path.GetDirectoryName(Path.GetFullPath(applicationRoot))
                    ?? throw new AppException("backup.target_path_required", 400, 400);
            }
        }

        Directory.CreateDirectory(targetRoot);
        var restored = 0;

        using var content = await OpenContentAsync(record, request.EncryptionPassword, cancellationToken);
        using var archive = ZipFile.OpenRead(content.FilePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeArchivePath(entry.FullName);
            if (name.Length == 0 || name.EndsWith('/'))
            {
                continue;
            }

            var matched = items.Any(item => item.EndsWith('/')
                ? name.StartsWith(item, StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, item, StringComparison.OrdinalIgnoreCase));
            if (!matched)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(targetRoot, name.Replace('/', Path.DirectorySeparatorChar)));
            // Zip-slip koruması: hedef her zaman kök altında kalmalı (tek doğru cevap: PathContainment).
            if (!PathContainment.IsInsideRoot(targetRoot, destination))
            {
                throw new AppException("backup.invalid_entry_path", 400, 400, name);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            restored++;
        }

        if (restored == 0)
        {
            throw new AppException("backup.items_not_found", 404, 404);
        }

        return new RestoreBackupItemsResultDto
        {
            RestoredCount = restored,
            TargetRoot = targetRoot
        };
    }

    private async Task<BackupRecordDto> FindZipRecordAsync(string backupId, CancellationToken cancellationToken)
    {
        var record = (await GetBackupsAsync(cancellationToken)).FirstOrDefault(item => item.Id == backupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        if (!string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("backup.not_browsable", 400, 400);
        }

        if (!File.Exists(record.Path))
        {
            throw new AppException("backup.not_found", 404, 404);
        }

        return record;
    }

    private static string NormalizeArchivePath(string value) =>
        value.Replace('\\', '/').TrimStart('/');

    public async Task<int> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var records = (await GetBackupsAsync(cancellationToken)).ToArray();
        return await PruneRecordsAsync(records, retentionDays, keepCount: 0, remote, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> PruneSubscriptionAsync(
        Guid userId,
        int retentionDays,
        int keepCount,
        BackupPruneRemoteOptions? remote = null,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0 && keepCount <= 0)
        {
            return 0;
        }

        var kimlik = userId.ToString("N");

        // KAPSAM DARALTILIR: bir aboneliğin saklama politikası YALNIZCA kendi yedeklerini
        // etkileyebilir. Bu daraltma olmadan bir müşterinin "3 gün sakla" ayarı, başka bir
        // müşterinin yedeklerini silen bir düğmeye dönerdi.
        var records = (await GetBackupsAsync(cancellationToken))
            .Where(record => string.Equals(record.Type, "subscription", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.SubscriptionUserId, kimlik, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return await PruneRecordsAsync(records, retentionDays, keepCount, remote, cancellationToken);
    }

    /// <summary>
    /// Budamanın TEK uygulaması: kararı <see cref="BackupPrunePlanner"/> verir, burada yalnızca
    /// silme ve kayıt güncelleme yapılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kararın ayrı olmasının sebebi: budama yalnızca tarihe bakarsa, yedekler HİÇ alınamazken de
    /// çalışır. Gecelik otomasyon her kaynağı ayrı try/catch ile yedekler ve hepsi patlasa bile
    /// buraya gelir: MariaDB 14 gün kapalı kalırsa her gece yedek başarısız olur, naif bir budama
    /// eskiyi silmeye devam eder ve 15. gün geri yüklenecek TEK bir yedek kalmaz.
    /// </para>
    /// <para>
    /// Artımsal zincirlerde ikinci bir tuzak vardır: zincirin ortasındaki (özellikle 0 numaralı)
    /// yedeği silmek, sonraki halkaları sessizce çöpe çevirir. Planlayıcı bu yüzden zinciri TEK
    /// bir geri yükleme noktası sayar ve ancak en yeni halkası da süresini doldurduğunda budar.
    /// </para>
    /// </remarks>
    private async Task<int> PruneRecordsAsync(
        IReadOnlyCollection<BackupRecordDto> records,
        int retentionDays,
        int keepCount,
        BackupPruneRemoteOptions? remote,
        CancellationToken cancellationToken)
    {
        // retentionDays <= 0 → süreye göre silme yok; yalnızca sayı limiti çalışsın diye cutoff
        // ULAŞILAMAZ bir geçmişe alınır (hiçbir kayıt "süresi dolmuş" sayılmaz).
        var cutoff = retentionDays > 0 ? DateTime.UtcNow.AddDays(-retentionDays) : DateTime.MinValue;
        var silinecekler = BackupPrunePlanner.Select(records, cutoff, keepCount, IsUsableBackup);
        var removedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in silinecekler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sebep = await TryDeleteBackupFilesAsync(record, remote, cancellationToken);
            if (sebep is null)
            {
                removedIds.Add(record.Id);
            }
            else
            {
                logger?.LogWarning("Suresi dolmus yedek silinemedi, kayit korundu: {Sebep}", sebep);
            }
        }

        if (removedIds.Count > 0)
        {
            // ATOMİK: güncel listeden YALNIZ silinen ID'leri çıkar → eşzamanlı eklenen yeni kayıt (otomatik+elle
            // yedek aynı anda) KORUNUR. Dosya silme kilidin DIŞINDA yapıldı; burada yalnız kısa liste güncellemesi var.
            await fileStore.UpdateAsync(
                IndexPath,
                Array.Empty<BackupRecordDto>(),
                existing => existing.Where(item => !removedIds.Contains(item.Id)).ToArray(),
                cancellationToken);
        }

        return removedIds.Count;
    }

    /// <summary>
    /// Bir yedeğin uzak kopyasını, yerel dosyasını ve indeks yan kaydını siler.
    /// </summary>
    /// <returns>
    /// Silinebildiyse <c>null</c>; silinemediyse GERÇEK SEBEP (kilitli dosya, erişim reddi).
    /// Sebebi taşımak şart: "silinemedi" deyip nedenini yutmak, kullanıcının diskin neden
    /// boşalmadığını asla anlayamaması demektir. Bu durumda KAYIT DA KORUNUR (kör kayıt kaybı
    /// olmasın; bir sonraki budamada tekrar denenir).
    /// </returns>
    private async Task<string?> TryDeleteBackupFilesAsync(
        BackupRecordDto record,
        BackupPruneRemoteOptions? remote,
        CancellationToken cancellationToken)
    {
        try
        {
            // Uzak rotasyon: off-site (S3/FTP/WebDAV) kopyayı kaydı silmeden ÖNCE sil (best-effort).
            if (remote is not null
                && !string.IsNullOrWhiteSpace(record.RemoteUri)
                && record.TargetType is "s3" or "ftp" or "webdav" or "http")
            {
                await TryDeleteRemoteBackupAsync(record, remote, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(record.Path) && File.Exists(record.Path))
            {
                File.Delete(record.Path);
            }
            else if (!string.IsNullOrWhiteSpace(record.Path) && Directory.Exists(record.Path))
            {
                Directory.Delete(record.Path, recursive: true);
            }

            TryDeleteFile(fileStore.ResolvePath(IndexSidecarPath(record.Id)));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"{record.Name}: {exception.Message}";
        }
    }

    /// <summary>
    /// Kayıt hâlâ GERÇEK bir geri yükleme noktası mı? Dosyası elle silinmiş bir kaydın "taze yedek
    /// var" sayılması, koruma varmış gibi görünüp eskileri sildirirdi — ölçmediğimiz şeye güvenmeyiz.
    /// </summary>
    private static bool IsUsableBackup(BackupRecordDto record)
    {
        if (!string.IsNullOrWhiteSpace(record.RemoteUri))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(record.Path) &&
               (File.Exists(record.Path) || Directory.Exists(record.Path));
    }

    public async Task<BackupChainDto> GetChainAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var records = await GetBackupsAsync(cancellationToken);
        if (records.All(item => item.Id != backupId))
        {
            throw new AppException("backup.not_found", 404, 404);
        }

        return BackupChainPlanner.Resolve(records, backupId, IsUsableBackup);
    }

    public async Task DeleteBackupAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var records = await GetBackupsAsync(cancellationToken);
        var record = records.FirstOrDefault(item => item.Id == backupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        // ZİNCİR TUZAĞI: bu yedek silinirse ondan SONRAKİ artımsal halkalar geri yüklenemez hâle
        // gelir — ama panelde "yedek" olarak görünmeye devam ederler. Kullanıcı bunu ancak felaket
        // anında öğrenir. Bu yüzden tek yedek silme isteği REDDEDİLİR; bağımlı halkaların adı
        // döner ve kullanıcı ne kaybedeceğini görerek "zinciri sil" der.
        var bagimlilar = BackupChainPlanner.GetDependents(records, record);
        if (bagimlilar.Count > 0)
        {
            throw new AppException(
                "backup.chain_has_dependents",
                409,
                409,
                $"Bu yedek silinirse su artimsal halkalar geri yuklenemez hale gelir: " +
                $"{string.Join(", ", bagimlilar.Select(item => item.Name))}");
        }

        if (await TryDeleteBackupFilesAsync(record, null, cancellationToken) is { } silmeHatasi)
        {
            // Yerel dosya kilitliyse kayıt KORUNUR (kör kayıt kaybı olmasın); kullanıcı gerçek nedeni görür.
            throw new AppException("backup.delete_failed", 409, 409, silmeHatasi);
        }

        // ATOMİK: yalnız bu ID çıkarılır → eşzamanlı eklenen yeni kayıtlar (otomatik yedek) korunur.
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing => existing.Where(item => item.Id != backupId).ToArray(),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BackupDeleteResultDto> DeleteBackupChainAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var records = await GetBackupsAsync(cancellationToken);
        var record = records.FirstOrDefault(item => item.Id == backupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        // Plesk davranışı: bir halka silinince ondan sonraki bütün halkalar da silinir. Fark şu ki
        // panel bunu KENDİLİĞİNDEN yapmaz — kullanıcı bu ucu bilerek çağırır ve neyin gideceğini
        // DeleteBackupAsync'in verdiği hatadan zaten görmüştür.
        var hedefler = new List<BackupRecordDto> { record };
        hedefler.AddRange(BackupChainPlanner.GetDependents(records, record));

        var silinen = new List<BackupRecordDto>();
        var kilitli = new List<string>();
        foreach (var hedef in hedefler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryDeleteBackupFilesAsync(hedef, null, cancellationToken) is { } sebep)
            {
                kilitli.Add(sebep);
            }
            else
            {
                silinen.Add(hedef);
            }
        }

        if (silinen.Count > 0)
        {
            var silinenIds = silinen.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            await fileStore.UpdateAsync(
                IndexPath,
                Array.Empty<BackupRecordDto>(),
                existing => existing.Where(item => !silinenIds.Contains(item.Id)).ToArray(),
                cancellationToken);
        }

        // Hiçbiri silinemediyse SESSİZ "tamam" DEMEYİZ: kullanıcı diskin boşaldığını sanmamalı.
        if (silinen.Count == 0 && kilitli.Count > 0)
        {
            throw new AppException("backup.delete_failed", 409, 409, string.Join(", ", kilitli));
        }

        return new BackupDeleteResultDto
        {
            DeletedIds = silinen.Select(item => item.Id).ToArray(),
            DeletedNames = silinen.Select(item => item.Name).ToArray()
        };
    }

    // Off-site yedeğin UZAK kopyasını siler (uzak rotasyon). Best-effort: hata/yoksa sessizce geçer, budamayı BOZMAZ.
    private async Task TryDeleteRemoteBackupAsync(BackupRecordDto record, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(record.RemoteUri, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            switch (record.TargetType)
            {
                case "ftp":
                    await DeleteFtpAsync(uri, remote, cancellationToken);
                    break;
                case "s3" when !string.IsNullOrWhiteSpace(remote.UserName):
                    await DeleteS3SignedAsync(uri, remote, cancellationToken);
                    break;
                case "webdav":
                case "http":
                    await DeleteHttpAsync(uri, remote, cancellationToken);
                    break;
                // "s3" kimliksiz (presigned URL) → imza atılamaz, silinemez; atlanır.
            }
        }
        catch
        {
            // Best-effort: uzak silme başarısızsa yerel budama yine sürer (kayıt yine kaldırılır; kalıcı uzak
            // yetim yalnız erişim/kimlik sorununda olur ve nadirdir).
        }
    }

    // S3 (SigV4 imzalı) DELETE. Boş gövde → EmptyPayloadSha256. Olmayan nesnede de 204 (idempotent).
    private async Task DeleteS3SignedAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var region = string.IsNullOrWhiteSpace(remote.Region) ? "us-east-1" : remote.Region;
        var (authorization, amzDate, contentSha) = S3RequestSigner.Sign(
            "DELETE", uri, remote.UserName, remote.Password, region, DateTimeOffset.UtcNow, S3RequestSigner.EmptyPayloadSha256);

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha);
        using var response = await client.SendAsync(request, cancellationToken);
    }

    // WebDAV/HTTP DELETE (basic auth). 404/410 = zaten yok → sorun değil.
    private async Task DeleteHttpAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        if (!string.IsNullOrWhiteSpace(remote.UserName))
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{remote.UserName}:{remote.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        using var response = await client.SendAsync(request, cancellationToken);
    }

#pragma warning disable SYSLIB0014
    // FTP DELETE (DELE). Kimlik uzak seçeneklerden; RemoteUri temizlenmiş (kimliksiz) olduğundan buradan verilir.
    private static async Task DeleteFtpAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = WebRequestMethods.Ftp.DeleteFile;
        request.Credentials = new NetworkCredential(remote.UserName, remote.Password);
        request.UsePassive = true;
        request.KeepAlive = false;
        using var registration = cancellationToken.Register(() => request.Abort());
        using var response = (FtpWebResponse)await request.GetResponseAsync();
    }
#pragma warning restore SYSLIB0014

    // ATOMİK ekleme: arka plan otomatik yedek + kullanıcı elle yedek AYNI ANDA çalışırsa (UpdateAsync dosya-başına
    // kilit tutar) kayıt kaybolmaz. Eski Read+Write deseni "son yazan kazanır" → kayıp yedek kaydı veriyordu.
    private async Task AppendRecordAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing => existing.Append(record).ToArray(),
            cancellationToken);
    }

    private async Task UpsertRecordAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing =>
            {
                var list = existing.ToList();
                var index = list.FindIndex(item => item.Id == record.Id);
                if (index >= 0)
                {
                    list[index] = record;
                }
                else
                {
                    list.Add(record);
                }

                return list.ToArray();
            },
            cancellationToken);
    }

    public async Task<BackupRemoteTestResultDto> TestRemoteTargetAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default)
    {
        var targetType = (request.TargetType ?? "local").Trim().ToLowerInvariant();
        if (targetType is "local" or "")
        {
            // Yerel hedef için ağ testi yok; her zaman "tamam".
            return new BackupRemoteTestResultDto { Success = true, Message = "backup.remote_test.local" };
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"adapanel-conn-test-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(
                tempFile,
                $"AdaPanel off-site backup connection test {DateTime.UtcNow:O}",
                cancellationToken);

            var target = ResolveTarget(targetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);

            // TEST DE GERÇEK YEDEĞİN YOLUNU KULLANIR: <taban>/<gün>/connection-test/<sabit ad>.
            //
            // Neden gün klasörü açılıyor: gecelik yedeğin uzak hedefe inememesinin en olası sebebi
            // artık "yazma izni yok" değil, "ALT DİZİN AÇILAMIYOR"dur (FTP'de MKD, WebDAV'da MKCOL
            // izni ayrıdır; chroot'lu hesaplarda sık kapalıdır). Testi tabana düz bir dosya atarak
            // yapmak, bu kurulumlarda "bağlantı tamam" deyip geceleri sessizce başarısız olmak
            // demekti — kullanıcının kontrol düğmesine güvenip yanılmasının tam tarifi.
            //
            // Kategori BİLEREK "connection-test": gerçek kategori klasörlerini kirletmez, insan
            // baktığında ne olduğunu anlar. Dosya adı sabittir → her test öncekinin üzerine yazar,
            // hedefte birikme olmaz.
            var testPlacement = BackupLayout.RemoteOnly("connection-test", DateTime.Now);
            await UploadToRemoteAsync(tempFile, "adapanel-connection-test.txt", target, testPlacement, cancellationToken);
            return new BackupRemoteTestResultDto { Success = true, Message = "backup.remote_test.ok" };
        }
        catch (AppException exception)
        {
            return new BackupRemoteTestResultDto { Success = false, Message = exception.MessageKey, Details = exception.Details };
        }
        catch (Exception exception)
        {
            return new BackupRemoteTestResultDto { Success = false, Message = "backup.remote_test.failed", Details = exception.Message };
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // geçici dosya temizliği best-effort
            }
        }
    }

    /// <summary>
    /// Dosyayı uzak hedefe yükler ve KAYDA yazılacak (kimliksiz) uzak adresi döndürür.
    /// </summary>
    /// <param name="placement">
    /// Yedeğin yeri; uzak hedefte taban adresin altına <c>&lt;gün&gt;/&lt;kategori&gt;</c> olarak
    /// açılır. ZORUNLUDUR — "yer verilmezse köke yaz" gibi bir yol bırakmak, düzeltilen hatanın
    /// (uzakta düz yığın) sessizce geri gelmesine açık kapı olurdu. Yedeğe ait olmayan yüklemeler
    /// de kendi yerini verir (bkz. bağlantı testi).
    /// </param>
    /// <remarks>
    /// Protokoller dizin konusunda birbirine BENZEMEZ, bu yüzden burada üç ayrı karar var:
    /// <list type="bullet">
    /// <item><b>FTP</b>: ara dizinler kendiliğinden oluşmaz, her seviye için MKD gerekir.</item>
    /// <item><b>WebDAV</b>: ara koleksiyonlar için MKCOL gerekir; yoksa PUT 409 Conflict döner.</item>
    /// <item><b>S3</b>: DİZİN KAVRAMI YOKTUR. "Klasör" yalnızca anahtarın önekidir; MKD/MKCOL
    /// denemek anlamsız (ve S3 API'sinde karşılığı olmayan) bir istektir. Anahtara önek eklemek
    /// yeterlidir — SigV4 imzası anahtarın tamamını kapsar (bkz. <see cref="S3RequestSigner"/>,
    /// kanonik yol segment segment kodlanır), aksi hâlde istek imza uyuşmazlığıyla reddedilirdi.</item>
    /// </list>
    /// </remarks>
    private async Task<string> UploadToRemoteAsync(
        string localPath,
        string fileName,
        BackupTarget target,
        BackupPlacement placement,
        CancellationToken cancellationToken)
    {
        if (target.Type == "local")
        {
            return string.Empty;
        }

        if (target.Uri is null)
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        try
        {
            if (target.Type == "ftp")
            {
                var ftpUri = AppendRemotePath(target.Uri, placement, fileName).Uri;
                await UploadFtpAsync(localPath, ftpUri, target, cancellationToken);
                return SanitizeUri(ftpUri);
            }

            // Erişim anahtarı verilmiş S3: SigV4 imzalı PUT (gerçek S3 / MinIO / R2 / Wasabi).
            // Anahtarsız S3: presigned URL'e düz PUT (geri uyumluluk).
            if (target.Type == "s3" && !string.IsNullOrWhiteSpace(target.UserName))
            {
                // MKD/MKCOL YOK: S3'te dizin diye bir nesne yoktur, önek yeter.
                var s3Uri = AppendRemotePath(target.Uri, placement, fileName).Uri;
                await UploadS3SignedAsync(localPath, s3Uri, target, cancellationToken);
                return SanitizeUri(s3Uri);
            }

            if (target.Type == "webdav")
            {
                var (webdavUri, klasorler) = AppendRemotePath(target.Uri, placement, fileName);
                var koleksiyonSorunu = await EnsureWebDavCollectionsAsync(target.Uri, klasorler, target, cancellationToken);
                await UploadHttpPutAsync(localPath, webdavUri, target, cancellationToken, koleksiyonSorunu);
                return SanitizeUri(webdavUri);
            }

            if (target.Type is "s3" or "http")
            {
                // ANAHTARSIZ S3 ve düz HTTP hedefi: adres OLDUĞU GİBİ kullanılır.
                //
                // Ölçülen davranış: bu iki yol kullanıcının verdiği URL'e (imzalı/tek kullanımlık
                // presigned URL ya da yükleme uç noktası) tek bir PUT atar; dosya adı bile
                // eklenmez. Buraya gün/kategori öneki eklemek presigned URL'in imzasını geçersiz
                // kılar (imza tam yolu kapsar) ve "/upload" gibi bir uç noktayı 404'e düşürür —
                // yani ÇALIŞAN kurulumları bozardı. Klasör düzeni bu iki hedefte hedefin
                // sözleşmesi gereği YOKTUR; adres neyi gösteriyorsa yedek oraya gider.
                await UploadHttpPutAsync(localPath, target.Uri, target, cancellationToken);
                return SanitizeUri(target.Uri);
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppException("backup.remote_upload_failed", 502, 500, BuildDiagnosticDetails(exception));
        }

        throw new AppException("backup.remote_target_invalid", 400, 400);
    }

    private async Task UploadS3SignedAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var region = string.IsNullOrWhiteSpace(target.Region) ? "us-east-1" : target.Region;
        var (authorization, amzDate, contentSha) = S3RequestSigner.Sign(
            "PUT",
            uri,
            target.UserName,
            target.Password,
            region,
            DateTimeOffset.UtcNow,
            S3RequestSigner.UnsignedPayload);

        var client = httpClientFactory.CreateClient();
        // SINIRLI zaman aşımı ŞART: bu yükleme otomatik yedeklemede GLOBAL KİLİDİN İÇİNDE çalışıyor
        // ve aynı kilidi giriş akışı (brute-force denetimi) da alıyor. Yarı açık kalmış bir TCP
        // bağlantısında (NAT zaman aşımı, uzak uç donması) sonsuz bekleme, kilidi kalıcı olarak
        // tutar ve o andan sonra PANELE KİMSE GİREMEZ; tek çare servisi yeniden başlatmaktır.
        // 2 saat, yavaş hatta büyük bir yedeği yüklemeye yeterken sonsuz askıda kalmayı engeller.
        client.Timeout = RemoteUploadTimeout;

        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AppException(
                "backup.remote_upload_failed",
                (int)response.StatusCode,
                502,
                $"Url={SanitizeUri(uri)}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 2000)}");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <param name="directoryFailure">
    /// Klasör hazırlığında (MKCOL) alınan açıklanamayan yanıt. Yükleme BAŞARILIYSA hiç kullanılmaz
    /// (klasör gerçekten vardı); başarısızsa kök sebep olarak hata ayrıntısına eklenir.
    /// </param>
    private async Task UploadHttpPutAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken,
        string? directoryFailure = null)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var client = httpClientFactory.CreateClient();
        // SINIRLI zaman aşımı ŞART: bu yükleme otomatik yedeklemede GLOBAL KİLİDİN İÇİNDE çalışıyor
        // ve aynı kilidi giriş akışı (brute-force denetimi) da alıyor. Yarı açık kalmış bir TCP
        // bağlantısında (NAT zaman aşımı, uzak uç donması) sonsuz bekleme, kilidi kalıcı olarak
        // tutar ve o andan sonra PANELE KİMSE GİREMEZ; tek çare servisi yeniden başlatmaktır.
        // 2 saat, yavaş hatta büyük bir yedeği yüklemeye yeterken sonsuz askıda kalmayı engeller.
        client.Timeout = RemoteUploadTimeout;

        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = content
        };

        var credential = ResolveCredential(target, uri);
        if (credential is not null)
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException(
                "backup.remote_upload_failed",
                (int)response.StatusCode,
                502,
                $"Url={SanitizeUri(uri)}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}"
                + (directoryFailure is null ? string.Empty : $"\r\nKlasor hazirligi: {directoryFailure}"));
        }
    }

#pragma warning disable SYSLIB0014
    private static async Task UploadFtpAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != "ftp")
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var credential = ResolveCredential(target, uri)
            ?? throw new AppException("backup.remote_target_invalid", 400, 400);

        var dizinSorunu = await EnsureFtpDirectoriesAsync(uri, credential);

        try
        {
            await StoreFtpFileAsync(localPath, uri, credential, cancellationToken);
        }
        catch (AppException exception) when (dizinSorunu is not null)
        {
            // Yükleme BAŞARISIZ ve klasör hazırlığında da bir yanıt almışız: kök sebep büyük
            // olasılıkla o yanıttır ("550 Permission denied"). Yutulmaz, mesaja eklenir.
            throw new AppException(
                exception.MessageKey,
                exception.Code,
                exception.HttpStatusCode,
                $"{exception.Details}\r\nKlasor hazirligi: {dizinSorunu}".Trim());
        }
        catch (Exception exception) when (dizinSorunu is not null && !cancellationToken.IsCancellationRequested)
        {
            throw new AppException(
                "backup.remote_upload_failed",
                502,
                502,
                $"{exception.Message}\r\nKlasor hazirligi: {dizinSorunu}");
        }
    }

    private static async Task StoreFtpFileAsync(
        string localPath,
        Uri uri,
        NetworkCredential credential,
        CancellationToken cancellationToken)
    {
        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = WebRequestMethods.Ftp.UploadFile;
        request.UseBinary = true;
        request.Credentials = credential;
        request.ContentLength = new FileInfo(localPath).Length;
        // FtpWebRequest'in VARSAYILANI sonsuzdur. Bu yükleme global kilidin içinde çalıştığı ve aynı
        // kilidi giriş akışı da aldığı için, donmuş bir FTP bağlantısı paneli kalıcı olarak kilitler.
        // Timeout bağlantı/komut aşamasını, ReadWriteTimeout ise veri akışının durmasını sınırlar —
        // ikincisi olmadan yarı açık bir bağlantıda aktarım sonsuza kadar askıda kalır.
        request.Timeout = (int)RemoteUploadTimeout.TotalMilliseconds;
        request.ReadWriteTimeout = (int)RemoteUploadStallTimeout.TotalMilliseconds;
        // İptal edildiğinde soketi gerçekten kapat: CopyToAsync'in token'ı FtpWebRequest'i durdurmaz.
        await using var cancellationRegistration = cancellationToken.Register(request.Abort);

        await using (var source = File.OpenRead(localPath))
        await using (var destination = await request.GetRequestStreamAsync())
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        using var response = (FtpWebResponse)await request.GetResponseAsync();
        if (response.StatusCode is not (FtpStatusCode.ClosingData or FtpStatusCode.CommandOK))
        {
            throw new AppException(
                "backup.remote_upload_failed",
                (int)response.StatusCode,
                502,
                $"Url={SanitizeUri(uri)}\r\nStatusCode={(int)response.StatusCode} {response.StatusDescription?.Trim()}");
        }
    }

    /// <summary>
    /// FTP'de dosyanın bulunduğu dizine kadar HER seviyeyi MKD ile açar (ara dizinler
    /// kendiliğinden oluşmaz; STOR yolun tamamını hazır bekler).
    /// </summary>
    /// <returns>
    /// "Zaten var" ile açıklanamayan sunucu yanıtlarının özeti; her şey yolundaysa <c>null</c>.
    /// Dönen metin bir HATA DEĞİLDİR — yükleme başarısız olursa ayrıntıya eklenecek kanıttır
    /// (gerekçe: yukarıdaki "UZAK HEDEFTE KLASÖR AÇMA" bölümü).
    /// </returns>
    private static async Task<string?> EnsureFtpDirectoriesAsync(Uri uri, NetworkCredential credential)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length <= 1)
        {
            return null;
        }

        // Taban dizin de dahil, kökten dosyanın dizinine kadar HER seviye denenir: gün/kategori
        // klasörleri taban dizinin altına iner ve ara seviye yokken MKD başarısız olur.
        var path = "/";
        var sorunlar = new List<string>();

        for (var index = 0; index < segments.Length - 1; index++)
        {
            path = CombineUriPath(path, segments[index]);
            var builder = new UriBuilder(uri)
            {
                Path = path,
                Query = string.Empty,
                Fragment = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };

            var request = (FtpWebRequest)WebRequest.Create(builder.Uri);
            request.Method = WebRequestMethods.Ftp.MakeDirectory;
            request.Credentials = credential;
            request.UsePassive = true;
            request.KeepAlive = false;
            // Klasör açma da sonsuz beklememeli: bu akış otomatik yedeklemede GLOBAL KİLİDİN
            // içindedir (bkz. yükleme zaman aşımı gerekçesi).
            request.Timeout = (int)RemoteUploadStallTimeout.TotalMilliseconds;

            try
            {
                using var response = (FtpWebResponse)await request.GetResponseAsync();
            }
            catch (WebException exception)
            {
                var response = exception.Response as FtpWebResponse;
                var kod = response is null ? "-" : ((int)response.StatusCode).ToString();
                var aciklama = (response?.StatusDescription ?? exception.Message).Trim();

                // "Zaten var" (çoğu sunucuda 550, bazılarında 521) ile "izin yok" AYNI kodu
                // kullanır; hangisi olduğu buradan bilinemez. Yanıt kaydedilir, karar yüklemenin
                // sonucuna bırakılır: yükleme geçerse dizin vardı, geçmezse bu satır kök sebeptir.
                sorunlar.Add($"MKD {SanitizeUri(builder.Uri)} -> {kod} {aciklama}");
            }
        }

        return sorunlar.Count == 0 ? null : string.Join("; ", sorunlar);
    }
#pragma warning restore SYSLIB0014

    private static BackupTarget ResolveTarget(string? targetType, string? remoteUrl, string? remoteUserName, string? remotePassword, string? remoteRegion = null)
    {
        var normalizedType = string.IsNullOrWhiteSpace(targetType)
            ? "local"
            : targetType.Trim().ToLowerInvariant();

        if (normalizedType is "none" or "")
        {
            normalizedType = "local";
        }

        if (normalizedType == "local")
        {
            return new BackupTarget("local", null, string.Empty, string.Empty, string.Empty);
        }

        if (normalizedType is "s3-compatible")
        {
            normalizedType = "s3";
        }

        // "drive" SESSİZCE "webdav"e çevriliyordu. Google Drive WebDAV KONUŞMAZ: yedek hiçbir zaman
        // karşıya gitmez, ama panel gitmiş gibi davranır ve kullanıcı off-site kopyası olduğunu sanır.
        // Sessizce yanlış hedefe yazmak en kötü seçenektir; desteklenmeyen hedef AÇIKÇA reddedilir.
        // (Gerçek Google Drive desteği ayrı bir iştir: OAuth 2.0 + Drive REST API, WebDAV değil.)
        if (normalizedType is "drive" or "gdrive" or "google-drive" or "googledrive")
        {
            throw new AppException("backup.remote_target_unsupported", 400, 400, normalizedType);
        }

        if (normalizedType is not ("ftp" or "s3" or "webdav" or "http"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var remoteUrlText = remoteUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(remoteUrlText, UriKind.Absolute, out var uri))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        if (normalizedType == "ftp" && uri.Scheme != "ftp")
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        if ((normalizedType is "s3" or "webdav" or "http") && uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        return new BackupTarget(
            normalizedType,
            uri,
            remoteUserName?.Trim() ?? string.Empty,
            remotePassword ?? string.Empty,
            remoteRegion?.Trim() ?? string.Empty);
    }

    private static NetworkCredential? ResolveCredential(BackupTarget target, Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(target.UserName))
        {
            return new NetworkCredential(target.UserName, target.Password);
        }

        if (string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return null;
        }

        var parts = uri.UserInfo.Split(':', 2);
        return new NetworkCredential(
            Uri.UnescapeDataString(parts[0]),
            parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }

    /// <summary>
    /// Uzak adresi tamamlar: <c>&lt;taban&gt;/&lt;gün&gt;/&lt;kategori&gt;/&lt;dosya adı&gt;</c>.
    /// </summary>
    /// <returns>
    /// Yüklenecek tam adres ve TABAN ADRESİN ALTINA eklenen klasör seviyeleri — FTP'de MKD,
    /// WebDAV'da MKCOL bu seviyeler için gerekir (S3'te hiçbiri gerekmez).
    /// </returns>
    private static (Uri Uri, string[] Folders) AppendRemotePath(Uri uri, BackupPlacement placement, string fileName)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var lastSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lastSegment) && Path.HasExtension(lastSegment))
        {
            // Kullanıcı TEK BİR DOSYA adresi vermiş (".../yedek.zip"): ne klasör ne de dosya adı
            // eklenir. Adresi bilerek sabitleyen kurulumların ölçülmüş davranışı budur, korunur.
            return (uri, []);
        }

        var folders = placement.RemoteFolders;
        var directoryPath = folders.Aggregate(path, CombineUriPath);

        var builder = new UriBuilder(uri)
        {
            Path = CombineUriPath(directoryPath, fileName),
            UserName = string.Empty,
            Password = string.Empty
        };

        return (builder.Uri, folders);
    }

    // =====================================================================================
    // UZAK HEDEFTE KLASÖR AÇMA (FTP: MKD, WebDAV: MKCOL)
    //
    // İki protokolde de aynı iki tuzak var ve ikisi de sessizliğe davetiye çıkarıyor:
    //
    // (1) "ZATEN VAR" bir hatadır. FTP'de MKD var olan dizin için 550, WebDAV'da MKCOL 405
    //     döndürür. Bu, İLK GECEDEN SONRAKİ HER GECENİN NORMALİ'dir. Hatayı olduğu gibi
    //     yükseltmek, yedeğin ikinci geceden itibaren "uzak hedefe gönderilemedi" ile
    //     bitmesi demek olurdu.
    //
    // (2) "İZİN YOK" da aynı kodları döndürür (550 / 403). Hatayı görmezden gelmek bu kez
    //     ters tarafa düşer: klasör hiç oluşmaz, dosya karşıya gitmez, panel yine de sessiz
    //     kalır ve kullanıcı olmayan bir off-site kopyaya güvenir.
    //
    // KARAR: yanıt koduna bakıp "bu hangisiydi" diye TAHMİN ETMİYORUZ (aynı kod, iki farklı
    // sebep; sunucudan sunucuya metin de değişir). Klasör açma sonucu ÖLÇÜLEBİLİR tek şeye
    // bağlanır: dosya karşıya gidebildi mi?
    //   - Yükleme BAŞARILIYSA klasör gerçekten vardı → MKD/MKCOL hatası zararsızdı, susulur.
    //     (Dizinlerin elle önceden açıldığı ve MKD'nin hiç izinli olmadığı kurulumlar da böylece
    //      çalışmaya devam eder — bunları baştan reddetmek gereksiz bir arıza olurdu.)
    //   - Yükleme BAŞARISIZSA klasör hazırlığındaki sunucu yanıtı hata ayrıntısına EKLENİR; kök
    //     sebep ("550 Permission denied") kullanıcının gördüğü mesajda durur, yutulmaz.
    // =====================================================================================

    /// <summary>
    /// WebDAV'da taban adresin altındaki ara koleksiyonları MKCOL ile açar.
    /// </summary>
    /// <returns>
    /// Klasör hazırlığında alınan ve "zaten var" ile açıklanamayan sunucu yanıtlarının özeti;
    /// her şey yolundaysa <c>null</c>. Dönen metin bir HATA DEĞİLDİR, yükleme başarısız olursa
    /// ayrıntıya eklenecek KANITTIR.
    /// </returns>
    private async Task<string?> EnsureWebDavCollectionsAsync(
        Uri baseUri,
        IReadOnlyList<string> folders,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
        {
            return null;
        }

        var client = httpClientFactory.CreateClient();
        client.Timeout = RemoteUploadTimeout;

        var credential = ResolveCredential(target, baseUri);
        var path = Uri.UnescapeDataString(baseUri.AbsolutePath);
        var sorunlar = new List<string>();

        // YUKARIDAN AŞAĞIYA: ara koleksiyon yokken alt koleksiyonu açmak 409 Conflict verir.
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = CombineUriPath(path, folder);

            // Koleksiyon adresi SONU '/' ile biter (RFC 4918); bazı sunucular aksi hâlde
            // koleksiyonu dosya sanıp reddeder.
            var builder = new UriBuilder(baseUri)
            {
                Path = path + "/",
                Query = string.Empty,
                Fragment = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };

            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), builder.Uri);
            if (credential is not null)
            {
                var token = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode
                    // 405 = kaynak zaten var (RFC 4918 9.3.1). İkinci geceden sonra BEKLENEN yanıt.
                    || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    continue;
                }

                sorunlar.Add($"MKCOL {SanitizeUri(builder.Uri)} -> {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                sorunlar.Add($"MKCOL {SanitizeUri(builder.Uri)} -> {exception.Message}");
            }
        }

        return sorunlar.Count == 0 ? null : string.Join("; ", sorunlar);
    }

    private static string CombineUriPath(string left, string right)
    {
        var normalizedLeft = string.IsNullOrWhiteSpace(left) ? "/" : left;
        if (!normalizedLeft.EndsWith('/'))
        {
            normalizedLeft += "/";
        }

        return normalizedLeft + Uri.EscapeDataString(right);
    }

    /// <summary>
    /// Kayda yazılacak uzak adresten SIR taşıyan her parçayı düşürür.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eskiden yalnızca <c>kullanıcı:parola@</c> kısmı siliniyordu ve bu YETMİYORDU: S3
    /// <i>presigned</i> hedefte adres <c>X-Amz-Signature</c> ve <c>X-Amz-Credential</c>
    /// parametrelerini SORGU DİZESİNDE taşır. O adres <c>GET /backup</c> ile panele geliyor,
    /// yani imza tarayıcının ağ sekmesinde ve ara katman günlüklerinde görünür hâle geliyordu —
    /// imzayı ele geçiren, süresi dolana kadar o hedefe yazabilir.
    /// </para>
    /// <para>
    /// Sorgu ve parça (fragment) tamamen atılır. Kayıp bilgi yok: yedeğin uzakta NEREDE
    /// olduğunu şema + konak + yol zaten söyler; sorgu dizesi kimlik taşır, konum değil.
    /// </para>
    /// </remarks>
    private static string SanitizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString();
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        var details = exception.ToString();
        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    // Bir dizini tüm alt içeriğiyle arşive verilen önek altında ekler (örn. "files/...").
    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string entryPrefix)
    {
        var root = Path.GetFullPath(sourceDirectory);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                archive.CreateEntryFromFile(file, $"{entryPrefix}/{relative}", CompressionLevel.Optimal);
            }
            catch (IOException)
            {
                // Kilitli/erişilemeyen dosyayı atla; yedek geri kalanıyla devam eder.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
        writer.Write(content);
    }

    // Birleşik tam-site arşivi mi? (manifest.json + files/ öneki olan yeni format).
    private static bool IsCombinedSiteArchive(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return archive.Entries.Any(entry =>
                entry.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("databases/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Geri yükleme öncesi canlı içeriğin taşındığı kardeş klasörün etiketi.</summary>
    private const string RestoreSidelineLabel = "geri_yukleme_yedegi";

    /// <summary>Zincir geri yüklemesinin sonucu: yarım kalan adımlar + yana alınan içeriğin yeri.</summary>
    private sealed record RestoreOutcome(List<BackupRestoreWarningDto> Warnings, string PreservedPath);

    // =====================================================================================
    // ZİNCİR GERİ YÜKLEME
    //
    // Üç kural burada geçerlidir:
    // (1) ÖNCE KORU, SONRA YAZ. files/* canlı kökün ÜZERİNE yazılır; yazmaya başladıktan sonra
    //     eski hâl yoktur. Mevcut içerik önce kardeş klasöre taşınır; taşıma başarısızsa geri
    //     yükleme HİÇ BAŞLAMAZ. Zincirde bu YALNIZCA BİR KEZ, ilk halkadan önce yapılır —
    //     her halkada tekrarlansaydı ikinci halka birincinin yazdıklarını yana alır ve sonuç
    //     yalnızca son halkanın taşıdığı birkaç dosyadan ibaret kalırdı.
    // (2) SIRA ÖNEMLİ. Halkalar 0'dan hedefe doğru uygulanır; ters sırada eski dosya yeninin
    //     üzerine yazılır ve geri yükleme sessizce YANLIŞ içerik üretir.
    // (3) SESSİZ BAŞARISIZLIK YOK. Atlanan her adım uyarıya çevrilir; çağıran kaydı
    //     restored_with_errors işaretler.
    // =====================================================================================

    private async Task<RestoreOutcome> RestoreSiteChainAsync(
        BackupRecordDto record,
        string targetArchivePath,
        BackupChainDto chain,
        IReadOnlyCollection<BackupRecordDto> allRecords,
        string? targetPath,
        string? password,
        CancellationToken cancellationToken)
    {
        // Hedef: belirtilmişse o yol, değilse sitenin gerçek fiziksel kökü (canlı siteyi geri yükle).
        string siteRoot;
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            siteRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(targetPath.Trim()));
        }
        else
        {
            var site = await iisSiteService.GetSiteAsync(record.Source, cancellationToken);
            siteRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        }

        Directory.CreateDirectory(siteRoot);

        var warnings = new List<BackupRestoreWarningDto>();
        var preservedPath = string.Empty;
        var ilkHalka = true;

        for (var i = 0; i < chain.Links.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var halka = chain.Links[i];
            var halkaKaydi = allRecords.First(item => item.Id == halka.BackupId);
            var sonHalka = i == chain.Links.Length - 1;

            BackupContent? acilan = null;
            try
            {
                // Hedef halkanın arşivi çağıran tarafından ZATEN açıldı (şifreliyse çözüldü);
                // ikinci kez çözmek büyük arşivlerde iki katı süre ve geçici disk demektir.
                var arsivYolu = string.Equals(halkaKaydi.Id, record.Id, StringComparison.Ordinal)
                    ? targetArchivePath
                    : (acilan = await OpenContentAsync(halkaKaydi, password, cancellationToken)).FilePath;

                using var archive = ZipFile.OpenRead(arsivYolu);

                if (ilkHalka)
                {
                    ilkHalka = false;
                    preservedPath = SidelineIfArchiveReplacesFiles(archive, "files/", siteRoot);
                }

                ExtractPrefixedEntries(archive, "files/", siteRoot, warnings, cancellationToken);
                await ApplyDeletedEntriesAsync(archive, "files/", siteRoot, warnings, cancellationToken);

                // VERİTABANLARI YALNIZCA SON HALKADAN: her halka TAM döküm taşır, hepsini sırayla
                // içe aktarmak aynı veritabanını n kez yazmak (ve n kat beklemek) olurdu.
                if (sonHalka)
                {
                    await ImportDatabasesFromArchiveAsync(archive, "databases/", warnings, cancellationToken);
                }
            }
            finally
            {
                acilan?.Dispose();
            }
        }

        return new RestoreOutcome(warnings, preservedPath);
    }

    /// <summary>
    /// ABONELİK zincirini geri yükler: her sitenin dosyaları kendi köküne, veritabanları içe
    /// aktarılarak, posta ve FTP hesapları yeniden kurularak.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>PAROLALAR YEDEĞE GİREMEZ.</b> Ne Windows yerel hesap parolaları (FTP) ne de posta kutusu
    /// parolaları (sunucu yalnızca özetlerini tutar) dışa aktarılabilir. Bu yüzden hesaplar
    /// RASTGELE parolayla kurulur ve her biri için "parolayı sıfırlayın" uyarısı üretilir —
    /// kullanıcı hesabın çalıştığını sanıp neden giriş yapamadığını aramamalıdır.
    /// </para>
    /// </remarks>
    private async Task<RestoreOutcome> RestoreSubscriptionChainAsync(
        BackupRecordDto record,
        string targetArchivePath,
        BackupChainDto chain,
        IReadOnlyCollection<BackupRecordDto> allRecords,
        string? password,
        CancellationToken cancellationToken)
    {
        var warnings = new List<BackupRestoreWarningDto>();
        var korunanlar = new List<string>();
        var siteKokleri = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ilkHalka = true;

        for (var i = 0; i < chain.Links.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var halka = chain.Links[i];
            var halkaKaydi = allRecords.First(item => item.Id == halka.BackupId);
            var sonHalka = i == chain.Links.Length - 1;

            BackupContent? acilan = null;
            try
            {
                var arsivYolu = string.Equals(halkaKaydi.Id, record.Id, StringComparison.Ordinal)
                    ? targetArchivePath
                    : (acilan = await OpenContentAsync(halkaKaydi, password, cancellationToken)).FilePath;

                using var archive = ZipFile.OpenRead(arsivYolu);

                if (ilkHalka)
                {
                    ilkHalka = false;
                    await ResolveSubscriptionSiteRootsAsync(archive, siteKokleri, warnings, cancellationToken);

                    foreach (var (siteAdi, kok) in siteKokleri)
                    {
                        var korunan = SidelineIfArchiveReplacesFiles(archive, $"sites/{siteAdi}/files/", kok);
                        if (korunan.Length > 0)
                        {
                            korunanlar.Add(korunan);
                        }
                    }
                }

                foreach (var (siteAdi, kok) in siteKokleri)
                {
                    var onek = $"sites/{siteAdi}/files/";
                    ExtractPrefixedEntries(archive, onek, kok, warnings, cancellationToken);
                    await ApplyDeletedEntriesAsync(archive, onek, kok, warnings, cancellationToken);
                }

                if (sonHalka)
                {
                    await ImportDatabasesFromArchiveAsync(archive, "databases/", warnings, cancellationToken);
                    await RestoreSubscriptionMailAsync(archive, warnings, cancellationToken);
                    await RestoreSubscriptionFtpAsync(archive, warnings, cancellationToken);
                }
            }
            finally
            {
                acilan?.Dispose();
            }
        }

        return new RestoreOutcome(warnings, string.Join(" | ", korunanlar));
    }

    /// <summary>
    /// Abonelik arşivindeki site adlarını bulur ve her biri için canlı kökü çözer. Site IIS'te
    /// yoksa arşivdeki yapılandırmasından YENİDEN KURULUR (tam-sunucu felaket kurtarma yolu);
    /// kurulamıyorsa o sitenin dosyaları atlanır ve sebebi uyarı olur.
    /// </summary>
    private async Task ResolveSubscriptionSiteRootsAsync(
        ZipArchive archive,
        Dictionary<string, string> siteKokleri,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var siteAdlari = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var ad = NormalizeArchivePath(entry.FullName);
            if (!ad.StartsWith("sites/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kalan = ad["sites/".Length..];
            var bolum = kalan.IndexOf('/');
            if (bolum > 0)
            {
                siteAdlari.Add(kalan[..bolum]);
            }
        }

        foreach (var siteAdi in siteAdlari)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var site = await iisSiteService.GetSiteAsync(siteAdi, cancellationToken);
                siteKokleri[siteAdi] = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                    ? site.PhysicalPath
                    : site.ApplicationRootPath;
                Directory.CreateDirectory(siteKokleri[siteAdi]);
                continue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Site yok: arşivdeki yapılandırmadan yeniden kurmayı dene.
                logger?.LogInformation(exception, "Abonelik geri yuklemesi: {Site} IIS'te yok, arsivden kurulacak.", siteAdi);
            }

            var kuruldu = await TryRecreateSiteFromArchiveAsync(archive, siteAdi, warnings, cancellationToken);
            if (kuruldu is not null)
            {
                siteKokleri[siteAdi] = kuruldu;
            }
        }
    }

    private async Task<string?> TryRecreateSiteFromArchiveAsync(
        ZipArchive archive,
        string siteAdi,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry($"sites/{siteAdi}/config/site.json");
        if (entry is null)
        {
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.site_config_missing",
                Target = siteAdi
            });
            return null;
        }

        try
        {
            await using var stream = entry.Open();
            var site = await System.Text.Json.JsonSerializer.DeserializeAsync<AdaPanel.Core.Dtos.Sites.SiteDto>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
            if (site is null)
            {
                warnings.Add(new BackupRestoreWarningDto { MessageKey = "backup.site_config_missing", Target = siteAdi });
                return null;
            }

            // Panel yedeğindeki tam-sunucu DR yoluyla AYNI kanıtlanmış plan (HTTPS bağlamaları
            // bilerek kurulmaz; sertifika makineye bağlıdır ve SSL ekranından yeniden verilir).
            var (create, extraBindings) = IisSnapshotRestore.ToProvisioningPlan(site);
            if (!string.IsNullOrWhiteSpace(create.PhysicalPath))
            {
                Directory.CreateDirectory(create.PhysicalPath);
            }

            await iisSiteService.CreateSiteAsync(create, cancellationToken);
            foreach (var binding in extraBindings)
            {
                try
                {
                    await iisSiteService.AddBindingAsync(siteAdi, binding, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.site_binding_failed",
                        Target = $"{siteAdi}:{binding.Port}",
                        Detail = exception.Message
                    });
                }
            }

            var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
            Directory.CreateDirectory(kok);
            return kok;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.site_create_failed",
                Target = siteAdi,
                Detail = exception.Message
            });
            return null;
        }
    }

    /// <summary>
    /// Arşivdeki posta ENVANTERİNDEN alan adlarını, hesapları ve alias'ları yeniden kurar.
    /// İletiler burada DEĞİLDİR (sunucu geneli posta yedeğindedir) ve parolalar sıfırlanmalıdır.
    /// </summary>
    private async Task RestoreSubscriptionMailAsync(
        ZipArchive archive,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries.Where(item =>
                     NormalizeArchivePath(item.FullName).StartsWith("mail/", StringComparison.OrdinalIgnoreCase) &&
                     item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var domain = Path.GetFileNameWithoutExtension(entry.Name);

            AdaPanel.Core.Dtos.Mail.MailWorkspaceDto? workspace;
            try
            {
                await using var stream = entry.Open();
                workspace = await System.Text.Json.JsonSerializer.DeserializeAsync<AdaPanel.Core.Dtos.Mail.MailWorkspaceDto>(
                    stream,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new BackupRestoreWarningDto { MessageKey = "backup.mail_inventory_unreadable", Target = domain, Detail = exception.Message });
                continue;
            }

            if (workspace is null)
            {
                continue;
            }

            try
            {
                await mailService.CreateDomainAsync(new AdaPanel.Core.Dtos.Mail.CreateMailDomainRequest { DomainName = domain }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Alan zaten varsa sorun yok; gerçekten kurulamadıysa hesaplar da kurulamaz ve
                // her biri kendi uyarısını üretir.
                logger?.LogInformation(exception, "Posta alani zaten var ya da kurulamadi: {Domain}", domain);
            }

            foreach (var account in workspace.Accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await mailService.CreateAccountAsync(new AdaPanel.Core.Dtos.Mail.CreateMailAccountRequest
                    {
                        Domain = domain,
                        Address = account.Address,
                        // Parola dışa aktarılamaz → rastgele. Aşağıdaki uyarı bunu SÖYLER.
                        Password = $"Dr{Guid.NewGuid():N}aA1!",
                        MaxSizeMB = account.MaxSizeMB
                    }, cancellationToken);

                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_password_reset_required",
                        Target = account.Address
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_account_failed",
                        Target = account.Address,
                        Detail = exception.Message
                    });
                }
            }

            foreach (var alias in workspace.Aliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await mailService.CreateAliasAsync(new AdaPanel.Core.Dtos.Mail.CreateMailAliasRequest
                    {
                        Domain = domain,
                        Address = alias.Address,
                        Target = alias.Target
                    }, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_alias_failed",
                        Target = alias.Address,
                        Detail = exception.Message
                    });
                }
            }
        }
    }

    private async Task RestoreSubscriptionFtpAsync(
        ZipArchive archive,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("ftp/accounts.json");
        if (entry is null)
        {
            return;
        }

        SubscriptionFtpEntry[] hesaplar;
        try
        {
            await using var stream = entry.Open();
            hesaplar = await System.Text.Json.JsonSerializer.DeserializeAsync<SubscriptionFtpEntry[]>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(new BackupRestoreWarningDto { MessageKey = "backup.ftp_inventory_unreadable", Detail = exception.Message });
            return;
        }

        foreach (var hesap in hesaplar.Where(item => !string.IsNullOrWhiteSpace(item.UserName)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ftpService.CreateFtpUserAsync(new AdaPanel.Core.Dtos.Ftp.CreateFtpUserRequest
                {
                    UserName = hesap.UserName,
                    Password = $"Dr{Guid.NewGuid():N}aA1",
                    SiteName = hesap.SiteName,
                    HomePath = hesap.HomePath
                }, cancellationToken);

                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.ftp_password_reset_required",
                    Target = hesap.UserName
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.ftp_account_failed",
                    Target = hesap.UserName,
                    Detail = exception.Message
                });
            }
        }
    }

    /// <summary>
    /// Arşiv bu kökün dosyalarını GERÇEKTEN değiştiriyorsa mevcut içeriği kardeş klasöre taşır ve
    /// o klasörü döner; değiştirmiyorsa boş dizge.
    /// </summary>
    /// <remarks>
    /// Yalnızca veritabanı içeren bir yedek, canlı sitenin bütün dosyalarını yana alıp siteyi boş
    /// bırakmamalı — bu yüzden önce arşivde o önek altında gerçek dosya var mı diye BAKILIR.
    /// </remarks>
    private static string SidelineIfArchiveReplacesFiles(ZipArchive archive, string prefix, string root)
    {
        var replacesFiles = archive.Entries.Any(entry =>
            NormalizeArchivePath(entry.FullName).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(entry.Name));

        if (!replacesFiles || !Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any())
        {
            return string.Empty;
        }

        // Site göçündeki (_goc_yedek_) ve bileşen kurulumundaki (PreserveDataDirectories) kalıbın
        // aynısı: üzerine yazmadan önce yana al. Taşıyamıyorsak veriyi koruyamıyoruz demektir ve
        // koruyamadığımız veriyi ezmeye devam etmek kabul edilemez.
        var candidate = string.Empty;
        try
        {
            candidate = ContentSideline.BuildSiblingPath(root, RestoreSidelineLabel);
            ContentSideline.MoveAside(root, candidate);
            return candidate;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AppException("backup.restore_preserve_failed", 500, 500, $"Root={root}\r\nSideline={candidate}\r\n{exception.Message}");
        }
    }

    /// <summary>
    /// Verilen önek altındaki arşiv girdilerini köke çıkarır (önek sıyrılarak). Zip-slip
    /// denetimi burada yapılır; kökün dışına düşen girdi YAZILMAZ ve uyarıya çevrilir.
    /// </summary>
    private static void ExtractPrefixedEntries(
        ZipArchive archive,
        string prefix,
        string root,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // dizin girdisi
            }

            var hedef = ResolveInsideRoot(NormalizeArchivePath(entry.FullName), prefix, root, warnings);
            if (hedef is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(hedef)!);
            entry.ExtractToFile(hedef, overwrite: true);
        }
    }

    /// <summary>
    /// Artımsal halkanın <c>deleted.json</c> listesini uygular: o halkada SİLİNMİŞ dosyalar
    /// hedeften de silinir.
    /// </summary>
    /// <remarks>
    /// Bu adım olmadan geri yükleme, kullanıcının bilerek kaldırdığı dosyaları (temizlenmiş bir
    /// zararlı eklenti, silinmiş eski bir sürüm) geri getirirdi — yani geri yükleme, sitenin
    /// yedeğin alındığı andaki hâline DEĞİL, hâllerin birleşimine dönerdi.
    /// </remarks>
    private static async Task ApplyDeletedEntriesAsync(
        ZipArchive archive,
        string prefix,
        string root,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(ArchiveDeletedEntry);
        if (entry is null)
        {
            return;
        }

        string[] silinecekler;
        try
        {
            await using var stream = entry.Open();
            silinecekler = await System.Text.Json.JsonSerializer.DeserializeAsync<string[]>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(new BackupRestoreWarningDto { MessageKey = "backup.deleted_list_unreadable", Detail = exception.Message });
            return;
        }

        foreach (var yol in silinecekler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hedef = ResolveInsideRoot(NormalizeArchivePath(yol), prefix, root, warnings);
            if (hedef is null)
            {
                continue;
            }

            try
            {
                if (File.Exists(hedef))
                {
                    File.Delete(hedef);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.delete_entry_failed",
                    Target = yol,
                    Detail = exception.Message
                });
            }
        }
    }

    /// <summary>
    /// Arşiv yolunu köke göre çözer. Önek uymuyorsa <c>null</c> (ilgisiz girdi); kökün DIŞINA
    /// düşüyorsa <c>null</c> + uyarı (zip-slip).
    /// </summary>
    private static string? ResolveInsideRoot(string archivePath, string prefix, string root, List<BackupRestoreWarningDto> warnings)
    {
        if (!archivePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = archivePath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return null;
        }

        var hedef = Path.GetFullPath(Path.Combine(root, relative));

        // Zip-slip koruması: hedef kökün dışına çıkamaz. Ayırıcı denetimi olmadan "C:\sites\ada"
        // öneki "C:\sites\ada2\..." ile de eşleşirdi; tek doğru cevap PathContainment'tadır.
        if (!PathContainment.IsInsideRoot(root, hedef))
        {
            // Sessizce atlamak, bozuk/kötücül bir arşivi "geri yüklendi" saymak olurdu.
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.invalid_entry_path",
                Target = archivePath,
                Detail = hedef
            });
            return null;
        }

        return hedef;
    }

    /// <summary>Arşivdeki <c>*.sql</c> dökümlerini ilgili motorlara içe aktarır.</summary>
    private async Task ImportDatabasesFromArchiveAsync(
        ZipArchive archive,
        string prefix,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ad = NormalizeArchivePath(entry.FullName);
            if (!ad.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !ad.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(entry.Name); // "engine-dbname"
            var split = fileName.Split('-', 2);
            if (split.Length != 2)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.invalid_entry_path",
                    Target = entry.FullName,
                    Detail = "Girdi adi 'motor-veritabani' bicimine uymuyor."
                });
                continue;
            }

            var tempDump = Path.Combine(Path.GetTempPath(), $"adabk-restore-{Guid.NewGuid():N}.sql");
            try
            {
                entry.ExtractToFile(tempDump, overwrite: true);
                await databaseService.ImportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
                {
                    Engine = split[0],
                    DatabaseName = split[1],
                    FilePath = tempDump
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AppException exception)
            {
                // Diğer veritabanları/dosyalarla devam edilir AMA sonuç sessizce "restored"
                // sayılmaz: sebep kullanıcıya döner.
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = exception.MessageKey,
                    Target = $"{split[0]}/{split[1]}",
                    Detail = exception.Details
                });
            }
            catch (Exception exception)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "database.restore_failed",
                    Target = $"{split[0]}/{split[1]}",
                    Detail = exception.Message
                });
            }
            finally
            {
                if (File.Exists(tempDump))
                {
                    File.Delete(tempDump);
                }
            }
        }
    }

    private sealed record BackupTarget(string Type, Uri? Uri, string UserName, string Password, string Region);
}
