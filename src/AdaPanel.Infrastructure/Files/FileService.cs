using System.Text;
using System.IO.Compression;
using System.Globalization;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Files;

public sealed class FileService(IIisSiteService iisSiteService) : IFileService
{
    /// <summary>Boyut hesabında gezilebilecek EN ÇOK öge (dosya + klasör) sayısı.</summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bir bütçe ZORUNLU:</b> kiracının <c>node_modules</c> / <c>vendor</c> klasöründe
    /// milyonlarca öge olabilir ve bu uç kiracının kendi isteğiyle tetiklenir. Sınırsız özyineleme,
    /// tek bir tıkla panel sürecinden bir iş parçacığını dakikalarca tutar; birkaç sekmeden
    /// tekrarlanırsa panel TÜM kiracılar için yanıt veremez hâle gelir. Yani sınırsız hesap, dosya
    /// yöneticisinin içinden çalışan bir hizmet dışı bırakma yoludur.
    /// </para>
    /// <para>
    /// <b>Değer nereden geliyor (ÖLÇÜLDÜ, tahmin değil):</b> bu makinede 19.128 ögelik gerçek bir
    /// <c>node_modules</c> ağacı, .NET'in <c>EnumerateFileSystemInfos</c> yürüyüşüyle 0,57 s'de
    /// gezildi (~34.000 öge/s; ölçüm PowerShell üzerinden yapıldığı için gerçek maliyet bundan
    /// DÜŞÜKtür, yani bu sayı kötümser tarafta). 200.000 öge bu hıza göre ~6 s'lik bir üst sınır
    /// demektir. Karşılaştırma için: tipik bir WordPress kurulumu 3-5 bin öge, eklentileriyle şişmiş
    /// bir site 50 bin civarıdır — yani sınır gerçek sitelerin neredeyse tamamını TAM hesaplar,
    /// yalnız patolojik ağaçlarda devreye girer.
    /// </para>
    /// </remarks>
    private const int BoyutOgeButcesi = 200_000;

    /// <summary>Boyut hesabının EN ÇOK süresi (ms).</summary>
    /// <remarks>
    /// <para>
    /// <b>Neden öge bütçesi TEK BAŞINA yetmez:</b> öge bütçesi işin MİKTARINI sınırlar, süresini
    /// değil. Aynı 200.000 öge yerel SSD'de saniyeler sürerken, ağ paylaşımına ya da uyanması
    /// gereken bir diske bakan bir site kökünde dakikalara çıkar — öge başına maliyet ölçtüğümüz
    /// değerin on katı olabilir. İki bütçe iki ayrı riski kapatır: biri "çok fazla öge", diğeri
    /// "öge başına çok yavaş".
    /// </para>
    /// <para>
    /// <b>Neden 15 s:</b> bu bir HTTP isteğidir; kullanıcı cevabı bekler ve araya giren vekil
    /// sunucular çoğunlukla 30-60 s'de bağlantıyı düşürür. 15 s, ölçülen hızda ~500.000 ögelik bir
    /// ağaca yeten ama kullanıcıyı da vekil sunucuyu da beklemeye zorlamayan bir tavandır. Daha
    /// uzun bir tavan, kesilen bağlantı yüzünden kullanıcının HİÇ sonuç görmediği (ama sunucunun
    /// yürümeye devam ettiği) bir duruma yol açardı.
    /// </para>
    /// </remarks>
    private const int BoyutSureButcesiMs = 15_000;

    /// <summary>Klasör KOPYALAMANIN gezebileceği EN ÇOK öge (dosya + klasör) sayısı.</summary>
    /// <remarks>
    /// <para>
    /// <b>Neden boyut hesabıyla AYNI değer (200.000):</b> ikisi de dosya yöneticisinin AYNI
    /// ekranında, aynı klasör üzerinde, aynı kullanıcı tarafından tetiklenir. Panelin TAM
    /// ÖLÇEMEYECEĞİNİ söylediği bir klasörü sessizce TAM KOPYALAMASI tutarsız olurdu: kullanıcı
    /// "boyutu hesaplanamadı" uyarısı aldığı klasörün kopyasının tam olduğunu varsayardı. Aynı
    /// tavan, aynı ekranda aynı cevabı verir.
    /// </para>
    /// <para>
    /// Değerin ölçülmüş gerekçesi <see cref="BoyutOgeButcesi"/>'nde yazılı ve burada tekrar
    /// edilmiyor; tek fark kopyalamada öge başına maliyetin daha yüksek olmasıdır (okuma değil
    /// yazma) — bu, tavanı DAHA da kötümser tarafa iter, yani sınır fazlasıyla güvenli kalır.
    /// </para>
    /// </remarks>
    private const int KopyaOgeButcesi = 200_000;

    internal Func<string, Stream> OpenCompressionFile { get; set; } = static path =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal Func<string> CompressionStagingPath { get; set; } = static () =>
        SecureTempFile.BuildPath("adapanel-compress", ".zip");

    internal Func<string> TransferStagingPath { get; set; } = static () =>
        SecureTempFile.BuildPath("adapanel-transfer", ".tmp");

    // Per-instance observation boundaries keep lifecycle tests on real temp files.
    internal Func<string, CancellationToken, Task>? TransferItemReady { get; set; }
    internal Action<string>? CopyEntryReady { get; set; }

    internal Func<string, Stream> OpenWorkspaceFile { get; set; } = static path =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal Func<string, string, CancellationToken, Task<CommandResult>> PermissionCommand { get; set; } =
        static (fileName, arguments, token) => ProcessRunner.RunAsync(fileName, arguments, cancellationToken: token);

    internal Func<SafeDirectoryWalkBudget> PermissionWalkBudget { get; set; } =
        static () => new SafeDirectoryWalkBudget(50_000, 5_000);
    internal TimeSpan PermissionExecutionTimeout { get; set; } = TimeSpan.FromMinutes(2);

    internal Func<string, CancellationToken, Task<string>>? ReadSearchContent { get; set; }
    internal Func<string, Stream> OpenSearchContent { get; set; } = static path =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    internal Func<string, CancellationToken, Task>? ExtractEntryReady { get; set; }
    internal Func<CancellationToken, Task>? SizeWalkCompleted { get; set; }

    public Task<FileWorkspaceDto> GetWorkspaceAsync(string path, CancellationToken cancellationToken = default) =>
        GetWorkspaceCoreAsync(path, null, cancellationToken);

    public Task<FileWorkspaceDto> GetWorkspaceAsync(string path,
        Func<SiteDto?, CancellationToken, Task> beforeRead, CancellationToken cancellationToken) =>
        GetWorkspaceCoreAsync(path, beforeRead ?? throw new ArgumentNullException(nameof(beforeRead)), cancellationToken);

    private async Task<FileWorkspaceDto> GetWorkspaceCoreAsync(string path,
        Func<SiteDto?, CancellationToken, Task>? beforeRead, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeVirtualPath(path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        async Task Approve()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeRead is not null) await beforeRead(target.Site, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!target.IsVirtualRoot) EnsureInsideSandbox(target);
        }
        await Approve();
        if (target.IsVirtualRoot)
        {
            var root = await BuildVirtualRootWorkspaceAsync(normalizedPath, cancellationToken);
            await Approve();
            return root;
        }

        if (File.Exists(target.PhysicalPath))
        {
            var content = await ReadFileContentAsync(target.PhysicalPath, cancellationToken);
            await Approve();
            return new FileWorkspaceDto
            {
                Path = normalizedPath,
                ParentPath = BuildParentPath(normalizedPath),
                DisplayName = Path.GetFileName(target.PhysicalPath),
                PhysicalPath = target.PhysicalPath,
                IsDirectory = false,
                Content = content.Content,
                ContentLoaded = content.Loaded,
                ContentUnavailableReasonKey = content.Reason
            };
        }

        if (beforeRead is null) Directory.CreateDirectory(target.PhysicalPath);
        else if (!Directory.Exists(target.PhysicalPath)) throw new AppException("files.not_found", 404, 404);
        var directoryInfo = new DirectoryInfo(target.PhysicalPath);
        var workspace = new FileWorkspaceDto
        {
            Path = normalizedPath,
            ParentPath = BuildParentPath(normalizedPath),
            DisplayName = directoryInfo.Name,
            PhysicalPath = directoryInfo.FullName,
            IsDirectory = true,
            Entries = directoryInfo.EnumerateFileSystemInfos()
                .Where(item => !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(item => item is DirectoryInfo)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new FileEntryDto
                {
                    Name = item.Name,
                    Path = CombineVirtualPath(normalizedPath, item.Name),
                    IsDirectory = item.Attributes.HasFlag(FileAttributes.Directory),
                    Size = item is FileInfo fileInfo ? fileInfo.Length : 0,
                    LastModifiedUtc = item.LastWriteTimeUtc,
                    Extension = item.Extension
                })
                .ToArray()
        };
        await Approve();
        return workspace;
    }

    public Task<FileWorkspaceDto> WriteAsync(WriteFileRequest request, CancellationToken cancellationToken = default) =>
        WriteCoreAsync(request, null, cancellationToken);

    public Task<FileWorkspaceDto> WriteAsync(WriteFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforePublication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforePublication);
        return WriteCoreAsync(request, beforePublication, cancellationToken);
    }

    public Task<FileWorkspaceDto> WriteAsync(WriteFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforePublication,
        Func<long, CancellationToken, Task> beforeWrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforePublication);
        ArgumentNullException.ThrowIfNull(beforeWrite);
        return WriteCoreAsync(request, beforePublication, cancellationToken, beforeWrite);
    }

    private async Task<FileWorkspaceDto> WriteCoreAsync(WriteFileRequest request,
        Func<SiteDto, CancellationToken, Task>? beforePublication, CancellationToken cancellationToken,
        Func<long, CancellationToken, Task>? beforeWrite = null)
    {
        var normalizedPath = NormalizeVirtualPath(request.Path);
        var content = request.Content ?? string.Empty;
        var failIfExists = request.FailIfExists;
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);
        await ApprovePublicationAsync(target, beforePublication, cancellationToken);
        var original = !failIfExists && File.Exists(target.PhysicalPath)
            ? await ReadFileContentAsync(target.PhysicalPath, cancellationToken) : FileTextContent.NewFile;
        // The quota and publication use this exact encoded snapshot. Do not reread the codec
        // after an awaited quota check and publish a different byte count or preamble.
        var encoded = original.Encode(content);
        if (beforeWrite is not null)
            await beforeWrite(Math.Max(0, encoded.LongLength - original.OriginalLength), cancellationToken);
        await ApprovePublicationAsync(target, beforePublication, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(target.PhysicalPath) ?? target.SandboxRoot);

        if (failIfExists && beforePublication is null)
        {
            // VARLIK DENETİMİ AYRI BİR ADIM DEĞİL: `File.Exists` ile bakıp sonra yazmak, iki adım
            // arasında dosyayı oluşturan ikinci bir istek için AÇIK bir yarış bırakır ve o
            // istemcinin dosyasını sıfırlar. `FileMode.CreateNew` bakmayı ve yaratmayı işletim
            // sisteminde TEK işleme indirir: dosya varsa yaratma hiç olmaz.
            //
            // ==========================================================================
            // YARATMA VE YAZMA AYRI İKİ ADIM — ve ayrı olmaları ZORUNLU.
            //
            // ÖNCEKİ HÂLDE tek bir `try` hem `CreateNew` ile YARATMAYI hem de YAZMAYI kapsıyordu ve
            // süzgeç `catch (IOException) when (File.Exists(...))` idi. Bu süzgeç kendi yorumunun
            // TERSİNİ yapıyordu: `CreateNew` geçtiği anda dosya BİZİM tarafımızdan yaratılmıştır,
            // yani `File.Exists` o noktadan sonra HER ZAMAN true'dur. Süzgeç "koşullu" değil,
            // fiilen koşulsuzdu — yazma sırasındaki HER IOException (disk dolu, antivirüs kilidi,
            // ağ payı düştü) kullanıcıya "bu dosya zaten var" diye dönüyordu. Asıl sebep hiçbir
            // yerde görünmüyordu.
            //
            // İkinci ve daha kötü yarısı ÖLÇÜLDÜ (bu makinede, gerçek dosya sistemine yazarak):
            // yazma yarıda kesildiğinde diskte 3 BAYTLIK yarım bir dosya KALIYORDU ve geri
            // sarılmıyordu. Kullanıcı tekrar denediğinde dosya artık GERÇEKTEN vardı ve 409
            // alıyordu — yani hiç oluşturamadığı bir dosyanın kalıntısı yüzünden kilitleniyordu.
            //
            // Bu yüzden: "zaten var" YALNIZ yaratma adımının başarısızlığında söylenir; yazma
            // sırasındaki hata kendi sebebiyle yukarı çıkar ve yarattığımız dosya GERİ SARILIR.
            // ==========================================================================
            FileStream akis;
            try
            {
                akis = new FileStream(
                    target.PhysicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(target.PhysicalPath) || Directory.Exists(target.PhysicalPath))
            {
                // IOException'ın TEK sebebi "zaten var" değildir (yol çok uzun, aygıt hazır değil...).
                // Süzgeç bu yüzden koşullu ve ARTIK GERÇEKTEN koşullu: bu noktada dosyayı henüz biz
                // yaratmadık, dolayısıyla varlık sorgusu bize gerçekten bilgi verir. Yol diskte
                // yoksa istisna GERÇEK bir G/Ç hatasıdır ve olduğu gibi yukarı çıkar.
                throw new AppException("files.already_exists", 409, 409);
            }
            catch (UnauthorizedAccessException) when (Directory.Exists(target.PhysicalPath))
            {
                // ÖLÇÜLDÜ: aynı adda bir KLASÖR varsa Windows `IOException` değil
                // `UnauthorizedAccessException` atıyor ("bir dizini dosya olarak açamazsın").
                // Süzgeç yine koşullu — klasör gerçekten yoksa bu istisna GERÇEK bir izin
                // reddidir ve "zaten var" diye maskelenmesi kullanıcıyı yanlış yere bakmaya
                // gönderirdi.
                throw new AppException("files.already_exists", 409, 409);
            }

            try
            {
                await using (akis)
                {
                    await akis.WriteAsync(encoded, cancellationToken);
                }
            }
            catch
            {
                // GERİ SARMA. Buraya ancak `CreateNew` BAŞARDIYSA gelinir, yani bu dosya çağrıdan
                // önce YOKTU: sildiğimiz şey kesinlikle bizim yarattığımız kalıntıdır, kullanıcının
                // verisi değil. `using` blokları yukarıda kapandığı için tanıtıcı serbesttir
                // (FileShare.None ile açık bir tanıtıcı silmeyi engellerdi).
                //
                // İptal (OperationCanceledException) de buraya düşer ve bilerek öyle: sekmesini
                // kapatan kullanıcının klasöründe yarım bir dosya kalması, işin hiç yapılmamış
                // olmasından kötüdür — sonraki denemesi "zaten var" ile reddedilirdi.
                try
                {
                    File.Delete(target.PhysicalPath);
                }
                catch (Exception silmeHatasi) when (silmeHatasi is IOException or UnauthorizedAccessException)
                {
                    // GERİ SARMA HATASI ASIL HATAYI GİZLEMEZ. Buradan yeni bir istisna atmak,
                    // kullanıcıya gerçek sebep ("disk dolu") yerine ikincil bir sebep ("dosya
                    // silinemedi") göstermek olurdu; kalıntı kalır ama sebep kaybolmaz.
                }

                throw;
            }
        }
        else
        {
            await WriteAtomicallyAsync(target, async (stream, token) =>
            {
                await stream.WriteAsync(encoded, token);
            }, cancellationToken, token => ApprovePublicationAsync(target, beforePublication, token), failIfExists);
        }

        return await ReadMutationWorkspaceAsync(normalizedPath, beforePublication, cancellationToken);
    }

    public Task<FileWorkspaceDto> DeleteAsync(DeleteFileRequest request, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(request, null, cancellationToken);

    public Task<FileWorkspaceDto> DeleteAsync(DeleteFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        DeleteCoreAsync(request, beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), cancellationToken);

    private async Task<FileWorkspaceDto> DeleteCoreAsync(DeleteFileRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeVirtualPath(request.Path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);
        await ApprovePublicationAsync(target, beforeMutation, cancellationToken);
        EnsureInsideSandbox(target);
        if (File.Exists(target.PhysicalPath))
        {
            File.Delete(target.PhysicalPath);
        }
        else if (Directory.Exists(target.PhysicalPath))
        {
            Directory.Delete(target.PhysicalPath, recursive: true);
        }

        await ApprovePublicationAsync(target, beforeMutation, cancellationToken);
        var workspace = await ReadMutationWorkspaceAsync(BuildParentPath(normalizedPath), beforeMutation, cancellationToken);
        await ApprovePublicationAsync(target, beforeMutation, cancellationToken);
        return workspace;
    }

    public Task<FileWorkspaceDto> CreateDirectoryAsync(CreateDirectoryRequest request, CancellationToken cancellationToken = default) =>
        CreateDirectoryCoreAsync(request, null, cancellationToken);

    public Task<FileWorkspaceDto> CreateDirectoryAsync(CreateDirectoryRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        CreateDirectoryCoreAsync(request, beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), cancellationToken);

    private async Task<FileWorkspaceDto> CreateDirectoryCoreAsync(CreateDirectoryRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeVirtualPath(request.Path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);
        await ApprovePublicationAsync(target, beforeMutation, cancellationToken);
        EnsureNoReparsePoints(target.SandboxRoot, target.PhysicalPath);
        Directory.CreateDirectory(target.PhysicalPath);
        await ApprovePublicationAsync(target, beforeMutation, cancellationToken);
        return await ReadMutationWorkspaceAsync(normalizedPath, beforeMutation, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>HANGİ HATA İSTEĞİ DÜŞÜRÜR, HANGİSİ RAPORA GİRER:</b> ayrım bilinçlidir.
    /// İsteğin BİÇİMİ geçersizse (hedef sanal alan dışında, hedef klasör yok, yeni ad bir yol
    /// parçası içeriyor, yol üzerinde bağlantı var) istek TÜMÜYLE reddedilir — bunlar kullanıcı
    /// hatası değil, ya bozuk istemci ya da saldırı denemesidir ve 200 dönüp "kısmen başarılı"
    /// demek onları sessizleştirirdi. Tek tek ögelerin durumu (hedefte aynı ad var, kaynak yok,
    /// klasör kendi içine taşınıyor, disk hatası) ise RAPORA girer; bir ögenin takılması diğer
    /// yedisinin taşınmasını engellemez.
    /// </para>
    /// </remarks>
    public Task<MoveFilesResultDto> MoveAsync(MoveFilesRequest request, CancellationToken cancellationToken = default) =>
        MoveCoreAsync(request, null, cancellationToken);

    public Task<MoveFilesResultDto> MoveAsync(MoveFilesRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        MoveCoreAsync(request, beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), cancellationToken);

    public Task<MoveFilesResultDto> MoveAsync(MoveFilesRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, FileTransferQuotaCheck beforeTransfer,
        CancellationToken cancellationToken) =>
        MoveCoreAsync(request, beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), cancellationToken,
            beforeTransfer ?? throw new ArgumentNullException(nameof(beforeTransfer)));

    private async Task<MoveFilesResultDto> MoveCoreAsync(MoveFilesRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken,
        FileTransferQuotaCheck? beforeTransfer = null)
    {
        request = new MoveFilesRequest { Paths = (request.Paths ?? []).ToArray(), DestinationPath = request.DestinationPath,
            NewName = request.NewName, Overwrite = request.Overwrite };
        // GÜVENLİK ÖN HAZIRLIĞI TEK KAYNAKTAN gelir; taşıma ile kopyalama arasında farkı olan tek
        // şey ÖGE BAŞINA yapılan iştir (aşağıdaki MoveOneAsync).
        var plan = await BuildTransferPlanAsync(
            request.Paths,
            request.DestinationPath,
            request.NewName,
            new TransferErrorKeys
            {
                SourceRequiredMessageKey = "files.move_source_required",
                RenameSingleOnlyMessageKey = "files.move_rename_single_only",
                DestinationMissingMessageKey = "files.move_destination_missing"
            },
            cancellationToken);

        var results = new List<MoveFileResultDto>(plan.Items.Count);
        var measurementBudget = new SafeDirectoryWalkBudget(BoyutOgeButcesi, BoyutSureButcesiMs);
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TransferItemReady is not null) await TransferItemReady(item.SourcePath, cancellationToken);
            await ApproveTransferAsync(item.Source, item.Target, beforeMutation, cancellationToken);
            results.Add(await MoveOneAsync(item.Source, item.Target, item.SourcePath, item.TargetPath,
                request.Overwrite, beforeMutation, beforeTransfer, measurementBudget, cancellationToken));
        }

        await ApprovePublicationAsync(plan.Destination, beforeMutation, cancellationToken);
        var workspace = await ReadMutationWorkspaceAsync(plan.DestinationPath, beforeMutation, cancellationToken);
        await ApprovePublicationAsync(plan.Destination, beforeMutation, cancellationToken);
        return new MoveFilesResultDto
        {
            MovedCount = results.Count(item => item.Moved),
            FailedCount = results.Count(item => !item.Moved),
            Items = results.ToArray(),
            Workspace = workspace
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// İstek biçimi/güvenlik hatası ile öge başına hata ayrımı <see cref="MoveAsync"/> ile aynıdır;
    /// gerekçesi orada yazılıdır ve burada tekrar edilmez.
    /// </para>
    /// </remarks>
    public Task<CopyFilesResultDto> CopyAsync(CopyFilesRequest request, CancellationToken cancellationToken = default)
        => CopyAsync(request, KopyaOgeButcesi, cancellationToken);

    public Task<CopyFilesResultDto> CopyAsync(CopyFilesRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        CopyCoreAsync(request, KopyaOgeButcesi,
            beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), cancellationToken);

    public Task<CopyFilesResultDto> CopyAsync(CopyFilesRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, FileTransferQuotaCheck beforeTransfer,
        CancellationToken cancellationToken) =>
        CopyCoreAsync(request, KopyaOgeButcesi,
            beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), cancellationToken,
            beforeTransfer ?? throw new ArgumentNullException(nameof(beforeTransfer)));

    /// <summary>Yukarıdakinin BÜTÇESİ DIŞARIDAN verilen hâli — tek uygulama budur.</summary>
    /// <remarks>
    /// <para>
    /// <b>Neden var:</b> bütçe korumasının GERÇEKTEN çalıştığı ancak bütçe DOLDURULARAK
    /// kanıtlanabilir; üretim değeriyle bu, testte 200.000 dosya kopyalamak demekti. Aynı gerekçe ve
    /// aynı kalıp <see cref="CalculateSizeAsync(string,int,int,CancellationToken)"/> ve
    /// <c>CustomerScopeService.GetDiskUsageMbAsync</c>'te de yazılı. Aşırı yükleme
    /// <c>internal</c>'dır: dışarıya, bütçesi kaldırılabilir bir uç AÇMAZ.
    /// </para>
    /// </remarks>
    internal Task<CopyFilesResultDto> CopyAsync(
        CopyFilesRequest request,
        int ogeButcesi,
        CancellationToken cancellationToken) => CopyCoreAsync(request, ogeButcesi, null, cancellationToken);

    private async Task<CopyFilesResultDto> CopyCoreAsync(CopyFilesRequest request, int ogeButcesi,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken,
        FileTransferQuotaCheck? beforeTransfer = null)
    {
        request = new CopyFilesRequest { Paths = (request.Paths ?? []).ToArray(), DestinationPath = request.DestinationPath,
            NewName = request.NewName, Overwrite = request.Overwrite };
        // Taşımayla AYNI ön hazırlık. Bu satırın kopya bir doğrulama bloğuna dönüşmesi, iki akıştan
        // birinde bir denetimin unutulması demektir — ve o an tek başına deliktir.
        var plan = await BuildTransferPlanAsync(
            request.Paths,
            request.DestinationPath,
            request.NewName,
            new TransferErrorKeys
            {
                SourceRequiredMessageKey = "files.copy_source_required",
                RenameSingleOnlyMessageKey = "files.copy_rename_single_only",
                DestinationMissingMessageKey = "files.copy_destination_missing"
            },
            cancellationToken);

        // TEK BÜTÇE, BÜTÜN ÖGELER — <see cref="SafeDirectoryWalkBudget"/>'in var oluş sebebi olan
        // kural: bütçe öge başına verilseydi tavan öge sayısıyla ÇARPILIRDI (8 klasör seçen bir
        // istek 8 kat daha uzun bir işe hak kazanır) ve bütçe fiilen kalkardı. Aynı karar kota
        // hesabında ve MeasureAsync'te de verilidir.
        var butce = new SafeDirectoryWalkBudget(ogeButcesi, int.MaxValue);

        var results = new List<CopyFileResultDto>(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TransferItemReady is not null) await TransferItemReady(item.SourcePath, cancellationToken);
            // Authority is an operation failure, never an ordinary per-item IO result.
            await ApproveTransferAsync(item.Source, item.Target, beforeMutation, cancellationToken);
            results.Add(await CopyOneAsync(item.Source, item.Target, item.SourcePath, item.TargetPath, request.Overwrite,
                butce, beforeMutation, cancellationToken, beforeTransfer));
        }

        await ApprovePublicationAsync(plan.Destination, beforeMutation, cancellationToken);
        var workspace = await ReadMutationWorkspaceAsync(plan.DestinationPath, beforeMutation, cancellationToken);
        await ApprovePublicationAsync(plan.Destination, beforeMutation, cancellationToken);
        return new CopyFilesResultDto
        {
            CopiedCount = results.Count(item => item.Copied),
            FailedCount = results.Count(item => !item.Copied),
            Items = results.ToArray(),
            Workspace = workspace
        };
    }

    /// <summary>
    /// Taşıma ve kopyalamanın ORTAK güvenlik ön hazırlığı: isteği doğrular, kaynak/hedef çiftlerini
    /// ÇÖZER ve her birini sanal alan kapısından geçirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden tek yardımcı:</b> bu blok iki akışta da BİREBİR aynı olmak zorundadır. Kopyalanmış
    /// bir doğrulama listesinde birini sıkılaştırıp diğerini unutmak an meselesidir ve unutulan tek
    /// denetim (ör. hedefte <c>EnsureInsideSandbox</c>) tek başına sanal alandan çıkış demektir.
    /// Öge başına yapılan İŞ ise ayrı kalır — orada davranış gerçekten farklıdır.
    /// </para>
    /// <para>
    /// <b>Plan ÖNCE tümüyle kurulur, sonra işlenir:</b> biçim/güvenlik hatası veren bir istek, hiçbir
    /// öge dosya sistemine dokunmadan reddedilir. Doğrulamayı işin arasına serpiştirmek, "istek
    /// tümüyle reddedildi" denen bir çağrının üçüncü ögede patlayıp ilk ikisini çoktan taşımış
    /// olması demekti.
    /// </para>
    /// </remarks>
    private async Task<TransferPlan> BuildTransferPlanAsync(
        string[]? paths,
        string? destinationPath,
        string? newName,
        TransferErrorKeys errorKeys,
        CancellationToken cancellationToken)
    {
        var sources = (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (sources.Length == 0)
        {
            throw new AppException(errorKeys.SourceRequiredMessageKey, 400, 400);
        }

        // Yeniden adlandırma TEK ögeye özgüdür: sekiz dosyaya aynı adı vermek, yedisinin sessizce
        // kaybolması (kopyalamada: yedisinin sessizce ezilmesi) demek olurdu.
        if (!string.IsNullOrWhiteSpace(newName) && sources.Length > 1)
        {
            throw new AppException(errorKeys.RenameSingleOnlyMessageKey, 400, 400);
        }

        var normalizedDestination = NormalizeVirtualPath(destinationPath);
        var destination = await ResolveTargetAsync(normalizedDestination, cancellationToken);
        EnsureSiteTarget(destination);
        EnsureInsideSandbox(destination);

        // Hedef klasör YARATILMAZ, var olması istenir: yazım hatası yüzünden oluşan bir klasöre
        // sekiz dosya taşımak/kopyalamak, onları "kaybolmuş" hâle getirir.
        if (!Directory.Exists(destination.PhysicalPath))
        {
            throw new AppException(errorKeys.DestinationMissingMessageKey, 404, 404);
        }

        var items = new List<TransferItem>(sources.Length);
        foreach (var rawSource in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePath = NormalizeVirtualPath(rawSource);

            // Kaynağın çözümü sanal alan + bağlantı (junction/symlink) denetimini de yapar; bir
            // bağlantıyı taşımak/kopyalamak, hedefindeki müşteri sitesine dokunmak demektir.
            var source = await ResolveTargetAsync(sourcePath, cancellationToken);
            EnsureSiteTarget(source);
            EnsureNotSiteRoot(source);
            EnsureInsideSandbox(source);

            var name = string.IsNullOrWhiteSpace(newName)
                ? Path.GetFileName(source.PhysicalPath)
                : EnsureSimpleName(newName);

            var targetPath = CombineVirtualPath(normalizedDestination, name);

            // Hedef de AYNI kapıdan geçer: adı ben ürettiğim için "güvenli varsayıp" atlamak,
            // korumanın tam da yeni uçta delinmesi olurdu.
            var target = await ResolveTargetAsync(targetPath, cancellationToken);
            EnsureSiteTarget(target);
            EnsureNotSiteRoot(target);
            EnsureInsideSandbox(target);

            items.Add(new TransferItem(source, target, sourcePath, targetPath));
        }

        return new TransferPlan(normalizedDestination, destination, items);
    }

    private static async Task ApproveTransferAsync(ResolvedTarget source, ResolvedTarget target,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        await ApprovePublicationAsync(source, beforeMutation, token);
        await ApprovePublicationAsync(target, beforeMutation, token);
        EnsureTransferPath(source);
        EnsureTransferPath(target);
        token.ThrowIfCancellationRequested();
    }

    private static void EnsureTransferPath(ResolvedTarget target)
    {
        EnsureInsideSandbox(target);
        if (File.GetAttributes(target.SandboxRoot).HasFlag(FileAttributes.ReparsePoint))
            throw new AppException("files.path_outside_root", 400, 400);
        EnsureNoReparsePoints(target.SandboxRoot, target.PhysicalPath);
    }

    /// <summary>Tek ögeyi taşır; başarısızlığı İSTİSNA olarak değil, rapor satırı olarak döner.</summary>
    private static async Task<MoveFileResultDto> MoveOneAsync(
        ResolvedTarget source,
        ResolvedTarget target,
        string sourcePath,
        string targetPath,
        bool overwrite,
        Func<SiteDto, CancellationToken, Task>? beforeMutation,
        FileTransferQuotaCheck? beforeTransfer,
        SafeDirectoryWalkBudget measurementBudget,
        CancellationToken cancellationToken)
    {
        var basarisiz = (string anahtar) => new MoveFileResultDto
        {
            SourcePath = sourcePath,
            Moved = false,
            ErrorKey = anahtar
        };

        var kaynakKlasor = Directory.Exists(source.PhysicalPath);
        if (!kaynakKlasor && !File.Exists(source.PhysicalPath))
        {
            return basarisiz("files.not_found");
        }

        if (string.Equals(source.PhysicalPath, target.PhysicalPath, StringComparison.OrdinalIgnoreCase))
        {
            return basarisiz("files.move_same_path");
        }

        // KLASÖRÜ KENDİ ALT AĞACINA taşımak: dosya sistemi bunu ya reddeder ya da klasörü kendi
        // içine gömerek ERİŞİLEMEZ kılar. İki durumda da kullanıcının verisi kaybolmuş sayılır.
        if (kaynakKlasor && PathContainment.IsInsideRoot(source.PhysicalPath, target.PhysicalPath))
        {
            return basarisiz("files.move_into_itself");
        }

        if (Directory.Exists(target.PhysicalPath))
        {
            // KLASÖR, Overwrite ile bile EZİLMEZ. Ezmek, hedef klasörün özyinelemeli silinmesi
            // demektir; bu panelde özyinelemeli silme bir kez müşteri sitesini götürmeye
            // ramak kalmış bir hata sınıfıdır. Kullanıcı gerçekten istiyorsa önce kendisi siler.
            return basarisiz("files.move_target_exists");
        }

        if (File.Exists(target.PhysicalPath))
        {
            if (!overwrite)
            {
                return basarisiz("files.move_target_exists");
            }

            if (kaynakKlasor)
            {
                // Klasörü bir dosyanın üstüne yazmak anlamsız; sessizce "başarılı" demek yerine söylenir.
                return basarisiz("files.move_target_exists");
            }

        }

        FileStream? sourceRead = null;
        FileStream? original = null;
        long? measuredBytes = null;
        var approvalFailed = false;
        Exception? measurementFailure = null;
        Task<long> Measure(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                EnsureTransferPath(source);
                EnsureTransferPath(target);
                if (measuredBytes is null)
                {
                    if (kaynakKlasor)
                    {
                        var walk = SafeDirectoryWalk.Yuru(source.PhysicalPath, measurementBudget, token);
                        if (walk.Truncated) throw new AppException(walk.TruncatedReasonKey, 413, 413);
                        measuredBytes = walk.TotalBytes;
                    }
                    else
                    {
                        sourceRead = new FileStream(source.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        measuredBytes = sourceRead.Length;
                    }
                    original = OpenCompressionDestination(target.PhysicalPath, overwrite);
                }
                return Task.FromResult(Math.Max(0, measuredBytes.Value - (original?.Length ?? 0)));
            }
            catch (Exception error) { measurementFailure = error; throw; }
        }
        try
        {
            if (beforeTransfer is not null)
            {
                try { await beforeTransfer(sourcePath, targetPath, Measure, cancellationToken); }
                catch (Exception error) { approvalFailed = !ReferenceEquals(error, measurementFailure); throw; }
                try { await ApproveTransferAsync(source, target, beforeMutation, cancellationToken); }
                catch { approvalFailed = true; throw; }
                if (kaynakKlasor && measuredBytes is not null)
                {
                    // A directory cannot be locked by opening all of its files. After the last
                    // await, reject a changed or incomplete measurement before the native move.
                    var current = SafeDirectoryWalk.Yuru(source.PhysicalPath,
                        new SafeDirectoryWalkBudget(BoyutOgeButcesi, BoyutSureButcesiMs), cancellationToken);
                    if (current.Truncated || current.TotalBytes != measuredBytes.Value)
                        return basarisiz("files.move_failed");
                }
                EnsureTransferPath(source);
                EnsureTransferPath(target);
                cancellationToken.ThrowIfCancellationRequested();
            }
            var mayOverwrite = overwrite && (measuredBytes is null || original is not null);
            // Nothing awaits between releasing the measured files and the native move.
            sourceRead?.Dispose();
            original?.Dispose();
            if (kaynakKlasor)
            {
                Directory.Move(source.PhysicalPath, target.PhysicalPath);
            }
            else
            {
                // Let the filesystem replace the destination only as part of a successful
                // move. Pre-deleting it loses the old file when the source is locked/unreadable.
                File.Move(source.PhysicalPath, target.PhysicalPath, mayOverwrite);
            }
        }
        catch (AppException error) when (!approvalFailed && error.MessageKey == "files.already_exists")
        {
            return basarisiz("files.move_target_exists");
        }
        catch (IOException) when (!approvalFailed)
        {
            // En sık sebebi: klasörün BAŞKA BİR BİRİME taşınması (Directory.Move bunu yapamaz) ya da
            // dosyanın kullanımda olması. Yalanlamak yerine bu ögenin taşınmadığı raporlanır.
            return basarisiz("files.move_failed");
        }
        catch (UnauthorizedAccessException) when (!approvalFailed)
        {
            return basarisiz("files.move_denied");
        }
        finally { sourceRead?.Dispose(); original?.Dispose(); }

        return new MoveFileResultDto
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Moved = true
        };
    }

    /// <summary>Tek ögeyi kopyalar; başarısızlığı İSTİSNA olarak değil, rapor satırı olarak döner.</summary>
    /// <remarks>
    /// Kural listesi <see cref="MoveOneAsync"/> ile aynıdır ve gerekçeleri de aynıdır; burada yalnızca
    /// KOPYALAMAYA ÖZGÜ olan kısımlar ayrıca açıklanır.
    /// </remarks>
    private async Task<CopyFileResultDto> CopyOneAsync(
        ResolvedTarget source,
        ResolvedTarget target,
        string sourcePath,
        string targetPath,
        bool overwrite,
        SafeDirectoryWalkBudget butce,
        Func<SiteDto, CancellationToken, Task>? beforeMutation,
        CancellationToken cancellationToken,
        FileTransferQuotaCheck? beforeTransfer = null)
    {
        var basarisiz = (string anahtar) => new CopyFileResultDto
        {
            SourcePath = sourcePath,
            Copied = false,
            ErrorKey = anahtar
        };

        var kaynakKlasor = Directory.Exists(source.PhysicalPath);
        if (!kaynakKlasor && !File.Exists(source.PhysicalPath))
        {
            return basarisiz("files.not_found");
        }

        if (string.Equals(source.PhysicalPath, target.PhysicalPath, StringComparison.OrdinalIgnoreCase))
        {
            // Kaynağın kendi üstüne kopyalanması: dosya sistemi bunu ya reddeder ya da dosyayı
            // BOŞALTIR. "Çoğalt" isteniyorsa NewName verilir.
            return basarisiz("files.copy_same_path");
        }

        // KLASÖRÜ KENDİ ALT AĞACINA kopyalamak: taşımadan daha kötüdür, çünkü özyineleme
        // kopyalarken büyümeye devam eden bir ağacı okur — disk dolana kadar süren bir kopya.
        if (kaynakKlasor && PathContainment.IsInsideRoot(source.PhysicalPath, target.PhysicalPath))
        {
            return basarisiz("files.copy_into_itself");
        }

        if (Directory.Exists(target.PhysicalPath))
        {
            // KLASÖR, Overwrite ile bile EZİLMEZ (gerekçe MoveOne'da): ezmek, hedef klasörün
            // özyinelemeli silinmesi demektir.
            return basarisiz("files.copy_target_exists");
        }

        if (File.Exists(target.PhysicalPath))
        {
            if (!overwrite)
            {
                return basarisiz("files.copy_target_exists");
            }

            if (kaynakKlasor)
            {
                // Klasörü bir dosyanın üstüne yazmak anlamsız; sessizce "başarılı" demek yerine söylenir.
                return basarisiz("files.copy_target_exists");
            }
        }

        var eksiklikAnahtari = string.Empty;
        var approvalFailed = false;
        async Task Reapprove(CancellationToken token)
        {
            try { await ApproveTransferAsync(source, target, beforeMutation, token); }
            catch { approvalFailed = true; throw; }
        }
        CopyPayload? payload = null;
        FileStream? original = null;
        var originalMeasured = false;
        Exception? measurementFailure = null;
        async Task<long> Measure(CancellationToken token)
        {
            try
            {
                payload ??= await PrepareCopyPayloadAsync(source, kaynakKlasor, butce, Reapprove, token);
                EnsureTransferPath(target);
                if (!originalMeasured)
                {
                    original = OpenCompressionDestination(target.PhysicalPath, overwrite);
                    originalMeasured = true;
                }
                return Math.Max(0, payload.Length - (original?.Length ?? 0));
            }
            catch (Exception error) { measurementFailure = error; throw; }
        }
        try
        {
            if (beforeTransfer is not null)
            {
                try { await beforeTransfer(sourcePath, targetPath, Measure, cancellationToken); }
                catch (Exception error)
                {
                    approvalFailed |= !ReferenceEquals(error, measurementFailure);
                    throw;
                }
                await Reapprove(cancellationToken);
            }
            if (payload is not null)
            {
                if (kaynakKlasor)
                    await PublishCopyPayloadDirectoryAsync(source, target, payload, Reapprove, cancellationToken);
                else
                {
                    var entry = payload.Entries[0];
                    await WriteAtomicallyAsync(target,
                        (output, token) => payload.CopyEntryAsync(entry, output, token), cancellationToken, Reapprove,
                        failIfExists: !overwrite || original is null, lastWriteTimeUtc: entry.LastWriteTimeUtc,
                        releasePublicationRead: () => original?.Dispose());
                }
                eksiklikAnahtari = payload.Warning;
            }
            else if (kaynakKlasor)
            {
                var kopya = await KlasoruKopyalaAsync(source, target, butce, Reapprove, cancellationToken);
                if (!kopya.Tamamlandi)
                {
                    // Bütçe doldu: bu öge yayımlanmadı ve başarısız raporlanır.
                    // İSTİSNA ATILMAZ — bu uç çok ögeli bir isteği karşılar ve
                    // istisna, bütçe dolmadan ÖNCE düzgün kopyalanan ögelerin raporunu da götürürdü;
                    // kullanıcı diskte duran kopyaların hangileri olduğunu öğrenemezdi.
                    return basarisiz(kopya.EksiklikAnahtari);
                }

                eksiklikAnahtari = kopya.EksiklikAnahtari;
            }
            else
            {
                // overwrite yalnız yukarıdaki kapıdan geçmiş olabilir; hedef yoksa bayrağın etkisi yok.
                File.Copy(source.PhysicalPath, target.PhysicalPath, overwrite);
            }
        }
        catch (CopyPreparationException error) when (!approvalFailed)
        {
            return basarisiz(error.ErrorKey);
        }
        catch (AppException error) when (!approvalFailed && error.MessageKey == "files.already_exists")
        {
            return basarisiz("files.copy_target_exists");
        }
        catch (IOException) when (!approvalFailed)
        {
            // En sık sebebi: disk dolu, dosya kullanımda ya da yol sınırının aşılması. Yalanlamak
            // yerine bu ögenin kopyalanmadığı raporlanır.
            //
            // YARIM KOPYA BIRAKILMAZ: "kopyalanmadı" diyip diskte yarım bir ağaç bırakmak,
            // kullanıcının raporda görmediği bir kalıntıyla baş başa kalması demekti (üstelik o
            // kalıntı kotasından da yer yer).
            return basarisiz("files.copy_failed");
        }
        catch (UnauthorizedAccessException) when (!approvalFailed)
        {
            return basarisiz("files.copy_denied");
        }
        catch (OperationCanceledException)
        {
            // İptal edilen istek de yarım kopya bırakmaz; sonra istisna yukarı çıkar (iptal bir
            // rapor satırı değildir, cevabı bekleyen kimse kalmamıştır).
            throw;
        }
        finally { original?.Dispose(); payload?.Dispose(); }

        return new CopyFileResultDto
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Copied = true,

            // Kopya OLUŞTU ama EKSİK: atlanan bağlantı susulmaz. "Tamamlandı" deyip bağlantıyı
            // yutmak, kullanıcının eksik kopyayı tam sanmasına yol açardı.
            ErrorKey = eksiklikAnahtari
        };
    }

    /// <summary>Yarım kalan KLASÖR kopyasını siler; dosya kopyasında yapacak bir şey yoktur.</summary>
    /// <remarks>
    /// <para>
    /// <b>Neden özyinelemeli silme burada GÜVENLİ</b> (oysa bu depoda genel kural onu yasaklar):
    /// Yalnız bu çağrının rastgele adlı hazırlama dizini buraya verilir; son hedef verilmez.
    /// Son hedef başka bir istek tarafından oluşturulduysa Directory.Move onu korur ve yalnız
    /// hazırlama dizini temizlenir. Kopyalama bağlantı oluşturmaz.
    /// </para>
    /// <para>
    /// <b>Silme hatası YUTULUR:</b> geri sarma bir en iyi çabadır ve asıl hatanın (bütçe doldu /
    /// disk dolu / izin reddi) yerine geçemez — kullanıcıya ikincil bir sebep göstermek, gerçek
    /// sebebi gizlemek olurdu.
    /// </para>
    /// </remarks>
    private static void KopyayiGeriSar(bool kaynakKlasor, string hedef)
    {
        if (!kaynakKlasor)
        {
            return;
        }

        try
        {
            if (Directory.Exists(hedef))
            {
                Directory.Delete(hedef, recursive: true);
            }
        }
        catch (Exception istisna) when (istisna is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Klasörü kopyalar. <b>Ağacı kendi başına GEZMEZ</b> — ortak, bütçeli, bağlantı takip etmeyen
    /// yürüyüşü kullanır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ELLE YAZILMIŞ ÖZYİNELEME KALDIRILDI:</b> önceki hâl kendini özyinelemeli çağırıyordu
    /// ve hiçbir bütçesi yoktu; yani <see cref="SafeDirectoryWalk"/>'un var oluş sebebi olan üç
    /// kuraldan ikisini (yığınla yürüyüş, bütçe) çiğniyordu — üstelik o dosyanın kendi belgesi
    /// "panelde bir ağacın fiziksel yol üzerinden gezildiği TEK yer" diyordu ve bu iddia YANLIŞ
    /// durumdaydı. İki somut tehlike: (1) derin bir ağaçta <c>StackOverflowException</c> — .NET'te
    /// YAKALANAMAZ, yani bir kiracının klasör ağacı panel sürecini TÜM kiracılar için öldürürdü
    /// (bu makinede <c>LongPathsEnabled=1</c> ve apphost <c>longPathAware</c>, yani MAX_PATH
    /// derinlik tavanı YOK — ölçüldü); (2) bütçesiz iş — kiracı milyon ögelik bir ağacı kopyalar,
    /// birkaç sekmeden tekrarlarsa panel yanıt veremez hâle gelir.
    /// </para>
    /// <para>
    /// <b>Bağlantı neden TAKİP EDİLMEZ</b> (kural artık ortak yürüyüşte, gerekçesi burada kalıyor
    /// çünkü ilk olarak burada verildi): iki ayrı felaket birden. (1) Bağlantı kendi üst ağacını
    /// gösteriyorsa yürüyüş SONSUZ döngüye girer ve diski doldurur. (2) Bağlantı sanal alanın
    /// DIŞINI gösteriyorsa (panelin veri klasörü, başka kiracının sitesi, <c>C:\Windows</c>) o
    /// içerik olduğu gibi kiracının okuyabildiği bir klasöre KOPYALANIR — sanal alan denetimini yol
    /// çözümlemesinde tuttuğumuz hâlde, kopyalamanın içinden delinmiş olurdu. Aynı karar
    /// listelemede (<see cref="GetWorkspaceAsync"/>) ve aramada (<c>EnumerateSafeFiles</c>) de
    /// verilmiştir: bağlantılar panelin dosya görüşünde YOKTUR.
    /// </para>
    /// <para>
    /// <b>BÜTÇE DOLARSA: KISMİ KOPYA BIRAKILMAZ, öge REDDEDİLİR — kararın gerekçesi.</b>
    /// İki seçenek tartıldı:
    /// <list type="bullet">
    /// <item><b>Kısmi rapor</b> (bağlantı atlamasında yaptığımız gibi "kopya oluştu ama eksik").
    /// Bağlantıda bu doğrudur çünkü atlanan şey kullanıcının hiç GÖRMEDİĞİ bir işaretçidir ve
    /// kopyanın geri kalanı tamdır. Bütçede ise atlanan şey kullanıcının GERÇEK DOSYALARIDIR ve
    /// nerede kesildiği rastgeledir: kullanıcı "kopyalandı" yazısına bakıp yarısı olmayan bir
    /// klasörle çalışmaya devam ederdi. Yedekleme amacıyla kopyalayan biri için bu, veri kaybının
    /// ta kendisidir.</item>
    /// <item><b>Reddet ve geri sar</b> (SEÇİLEN). 0.7.6'da içe aktarma için verilen kararla aynı
    /// yönde ve aynı sebeple: kopyalama bir YAZMA işlemidir. Ama burada bedeli DAHA DÜŞÜK, çünkü
    /// geri sarma tam ve ucuzdur — içe aktarmada sitenin kökü yana alınmış olduğu için geri sarma
    /// ayrı bir makine gerektiriyordu; burada hedef klasör bu çağrıdan önce HİÇ YOKTU
    /// (<see cref="CopyOneAsync"/> aksini reddeder), yani silmek kalıntıyı tam olarak temizler.</item>
    /// </list>
    /// İçe aktarmadan tek FARK: orada istisna atılır (tek işlem), burada ÖGE BAŞINA rapor satırı
    /// üretilir — çünkü bu uç çok ögeli bir isteği karşılar ve istisna, bütçe dolmadan önce düzgün
    /// kopyalanan ögelerin raporunu da götürürdü.
    /// </para>
    /// <para>
    /// <b>BÜTÇE İSTEK BAŞINADIR, öge başına DEĞİL</b> — nesne <see cref="CopyAsync(CopyFilesRequest,
    /// int, CancellationToken)"/> içinde bir kez kurulup bütün ögelerde paylaşılır. Öge başına
    /// verilseydi tavan seçilen öge sayısıyla ÇARPILIR ve bütçe fiilen kalkardı; gerekçesi
    /// <see cref="SafeDirectoryWalkBudget"/>'te yazılı ve kota hesabında da aynısı uygulanıyor.
    /// </para>
    /// <para>
    /// <b>SÜRE bütçesi bilerek YOKTUR</b> (<see cref="int.MaxValue"/>) — içe aktarma yolundaki
    /// gerekçenin aynısı: burada ölçülen iş DOSYA KOPYALAMAKTIR ve süresi diskin hızına ve dosya
    /// BOYUTUNA bağlıdır. Süreye bakan bir bütçe, tek bir 4 GB'lık dosyanın ortasında dolar ve
    /// neredeyse tamamlanmış meşru bir kopyayı geri sardırırdı. Tehlikenin büyüklüğü ÖGE SAYISIDIR;
    /// sınırlanması gereken de odur.
    /// </para>
    /// </remarks>
    private async Task<KlasorKopyasi> KlasoruKopyalaAsync(
        ResolvedTarget source,
        ResolvedTarget target,
        SafeDirectoryWalkBudget butce,
        Func<CancellationToken, Task> approve,
        CancellationToken cancellationToken)
    {
        // Keep the single bounded/link-safe traversal, then authorize each planned mutation
        // asynchronously. The plan is bounded by the same request-wide entry budget.
        var entries = new List<FileSystemInfo>();
        var baglantiAtlandi = false;
        var yuruyus = SafeDirectoryWalk.Yuru(source.PhysicalPath, butce, entries.Add,
            null, cancellationToken, () => baglantiAtlandi = true);
        if (yuruyus.Truncated)
            return new KlasorKopyasi(false, yuruyus.TruncatedReasonKey == SafeDirectoryWalk.SebepErisimReddi
                ? "files.copy_denied" : yuruyus.TruncatedReasonKey);

        var temporary = Path.Combine(Path.GetDirectoryName(target.PhysicalPath)!, $".adapanel-copy-{Guid.NewGuid():N}.tmp");
        try
        {
            await approve(cancellationToken);
            Directory.CreateDirectory(temporary);
            foreach (var entry in entries)
            {
                CopyEntryReady?.Invoke(entry.FullName);
                await approve(cancellationToken);
                // The source may have changed since traversal. Re-check links after the await.
                EnsureNoReparsePoints(source.SandboxRoot, entry.FullName);
                var destination = Path.Combine(temporary, Path.GetRelativePath(source.PhysicalPath, entry.FullName));
                EnsureNoReparsePoints(target.SandboxRoot, destination);
                if (entry is DirectoryInfo) Directory.CreateDirectory(destination);
                else File.Copy(entry.FullName, destination, overwrite: false);
            }
            await approve(cancellationToken);
            // No overwrite: a competing creator keeps its directory. Recovery only ever removes
            // this invocation's temporary tree, never the final destination or prior items.
            Directory.Move(temporary, target.PhysicalPath);
            return new KlasorKopyasi(true, baglantiAtlandi ? "files.copy_link_skipped" : string.Empty);
        }
        finally { KopyayiGeriSar(kaynakKlasor: true, temporary); }
    }

    private async Task<CopyPayload> PrepareCopyPayloadAsync(ResolvedTarget source, bool directory,
        SafeDirectoryWalkBudget budget, Func<CancellationToken, Task> approve, CancellationToken token)
    {
        // Resolving quota ownership can await; recheck before enumerating the source tree.
        await approve(token);
        var planned = new List<FileSystemInfo>();
        var skippedLink = false;
        if (directory)
        {
            var walk = SafeDirectoryWalk.Yuru(source.PhysicalPath, budget, planned.Add, null, token,
                () => skippedLink = true);
            if (walk.Truncated)
                throw new CopyPreparationException(walk.TruncatedReasonKey == SafeDirectoryWalk.SebepErisimReddi
                    ? "files.copy_denied" : walk.TruncatedReasonKey);
        }
        else planned.Add(new FileInfo(source.PhysicalPath));

        // One private payload keeps the measured bytes immutable without keeping a handle
        // for every file in a large directory. It is outside the site's usage measurement.
        var path = TransferStagingPath();
        var created = false;
        var entries = new List<CopyPayloadEntry>(planned.Count);
        try
        {
            await using (var output = SecureTempFile.Create(path))
            {
                created = true;
                foreach (var entry in planned)
                {
                    await approve(token);
                    EnsureNoReparsePoints(source.SandboxRoot, entry.FullName);
                    var attributes = File.GetAttributes(entry.FullName);
                    var isDirectory = entry is DirectoryInfo;
                    if (attributes.HasFlag(FileAttributes.Directory) != isDirectory)
                        throw new IOException("The selected source changed during transfer preparation.");
                    var relative = directory ? Path.GetRelativePath(source.PhysicalPath, entry.FullName) : string.Empty;
                    if (isDirectory)
                    {
                        entries.Add(new CopyPayloadEntry(entry.FullName, relative, true, output.Position, 0, default));
                        continue;
                    }
                    await using var input = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var length = input.Length;
                    var offset = output.Position;
                    var timestamp = File.GetLastWriteTimeUtc(entry.FullName);
                    await CopyExactTransferBytesAsync(input, output, length, token);
                    var probe = new byte[1];
                    if (await input.ReadAsync(probe, token) != 0)
                        throw new IOException("The selected source grew during transfer preparation.");
                    entries.Add(new CopyPayloadEntry(entry.FullName, relative, false, offset, length, timestamp));
                }
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new CopyPayload(path, stream, entries, skippedLink ? "files.copy_link_skipped" : string.Empty);
        }
        catch
        {
            if (created) TryDeleteTransferPayload(path);
            throw;
        }
    }

    private async Task PublishCopyPayloadDirectoryAsync(ResolvedTarget source, ResolvedTarget target,
        CopyPayload payload, Func<CancellationToken, Task> approve, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(target.PhysicalPath)!, $".adapanel-copy-{Guid.NewGuid():N}.tmp");
        try
        {
            await approve(token);
            EnsureNoReparsePoints(target.SandboxRoot, temporary);
            Directory.CreateDirectory(temporary);
            foreach (var entry in payload.Entries)
            {
                CopyEntryReady?.Invoke(entry.SourcePath);
                await approve(token);
                EnsureNoReparsePoints(source.SandboxRoot, entry.SourcePath);
                var destination = Path.GetFullPath(Path.Combine(temporary, entry.RelativePath));
                if (!PathContainment.IsInsideRoot(temporary, destination))
                    throw new AppException("files.path_outside_root", 400, 400);
                EnsureNoReparsePoints(target.SandboxRoot, destination);
                if (entry.IsDirectory) Directory.CreateDirectory(destination);
                else
                {
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 81920, FileOptions.Asynchronous))
                        await payload.CopyEntryAsync(entry, output, token);
                    File.SetLastWriteTimeUtc(destination, entry.LastWriteTimeUtc);
                }
            }
            await approve(token);
            EnsureNoReparsePoints(target.SandboxRoot, temporary);
            EnsureInsideSandbox(target);
            token.ThrowIfCancellationRequested();
            Directory.Move(temporary, target.PhysicalPath);
        }
        finally { KopyayiGeriSar(kaynakKlasor: true, temporary); }
    }

    private static async Task CopyExactTransferBytesAsync(Stream input, Stream output, long length, CancellationToken token)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token);
            token.ThrowIfCancellationRequested();
            if (count == 0) throw new IOException("Transfer content ended before its measured length.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            length -= count;
        }
        token.ThrowIfCancellationRequested();
    }

    private static void TryDeleteTransferPayload(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* The private file keeps its restrictive ACL even if cleanup fails. */ }
    }

    private sealed record CopyPayloadEntry(string SourcePath, string RelativePath, bool IsDirectory,
        long Offset, long Length, DateTime LastWriteTimeUtc);

    private sealed class CopyPayload(string path, FileStream stream, IReadOnlyList<CopyPayloadEntry> entries,
        string warning) : IDisposable
    {
        internal IReadOnlyList<CopyPayloadEntry> Entries { get; } = entries;
        internal string Warning { get; } = warning;
        internal long Length => stream.Length;
        internal Task CopyEntryAsync(CopyPayloadEntry entry, Stream output, CancellationToken token)
        {
            stream.Position = entry.Offset;
            return CopyExactTransferBytesAsync(stream, output, entry.Length, token);
        }
        public void Dispose()
        {
            stream.Dispose();
            TryDeleteTransferPayload(path);
        }
    }

    private sealed class CopyPreparationException : IOException
    {
        internal CopyPreparationException(string errorKey) : base(errorKey) => ErrorKey = errorKey;
        internal string ErrorKey { get; }
    }

    /// <summary>
    /// Yeni adın gerçekten bir AD olduğunu doğrular.
    /// <para>
    /// Ad alanı bir YOL kabul etseydi ("../../evil"), hedef klasör denetimi anlamını yitirirdi:
    /// kullanıcı hedefi meşru gösterip dosyayı bambaşka bir yere yazdırabilirdi.
    /// </para>
    /// </summary>
    private static string EnsureSimpleName(string? name)
    {
        var kirpilmis = (name ?? string.Empty).Trim();
        if (kirpilmis.Length == 0
            || kirpilmis is "." or ".."
            || kirpilmis.IndexOfAny(['/', '\\', ':']) >= 0
            || kirpilmis.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new AppException("files.invalid_name", 400, 400);
        }

        // AYRILMIŞ AYGIT ADI (CON, NUL, COM1...) DENETİMİ BURADA DEĞİL, <see cref="ResolveTargetAsync"/>
        // YOLUNDA — ve bu bilinçli bir karar.
        //
        // Denetim raporu bu metoda bir WindowsReservedNames çağrısı eklenmesini istiyordu. Ölçüldü:
        // GEREKSİZ ve zararlı olurdu. Burada üretilen ad hemen ardından CombineVirtualPath ile
        // hedef yola çevrilip AYNI ResolveTargetAsync kapısından geçiriliyor (bkz.
        // BuildTransferPlanAsync — "hedef de AYNI kapıdan geçer"), yani aygıt adı zaten orada
        // yakalanıyor ve aynı `files.invalid_name` anahtarı dönüyor. İkinci bir kopya, davranışı
        // hiç değiştirmeden kuralı İKİ yere yazmak olurdu — bu depoda liste/kural kopyası beş kez
        // ısırdı ve WindowsReservedNames'in var oluş sebebi tam olarak buydu.
        //
        // Üstelik iki kopya AYNI olmazdı: yol kapısı diskte GERÇEKTEN duran bir adı muaf tutar
        // (gerekçesi EnsureNoReservedSegments'te: var olan veriye erişimi kapatmamak için), burası
        // ise tutamazdı — sonuç, var olan bir dosyanın listelenip silinebildiği ama ADININ
        // verilemediği tutarsız bir panel olurdu.

        return kirpilmis;
    }

    /// <summary>
    /// "Bu yol sanal alanın içinde mi?" — cevabı <see cref="PathContainment"/> verir.
    /// <para>
    /// Burada elle <c>StartsWith</c> yazılmaz: "C:\sites\a" ile "C:\sites\a-evil" ön ek olarak
    /// eşleşir ve o sınıf tam da bu ayırıcı hatası yüzünden yazıldı. Kökün KENDİSİ geçerlidir
    /// (hedef klasör pekâlâ site kökü olabilir); "site kökünün kendisi taşınamaz/ezilemez" ayrı
    /// bir kuraldır ve <see cref="EnsureNotSiteRoot"/> ile uygulanır.
    /// </para>
    /// </summary>
    private static void EnsureInsideSandbox(ResolvedTarget target)
    {
        if (!PathContainment.IsRootOrInside(target.SandboxRoot, target.PhysicalPath))
        {
            throw new AppException("files.path_outside_root", 400, 400);
        }
    }

    /// <summary>
    /// Klasöre bir kullanıcı/grup için izin verir.
    /// <para>
    /// GİRDİ KATI DOĞRULANIR. Önceki hâlde <c>Principal</c> ve <c>Rights</c> doğrudan bir
    /// PowerShell dizesine gömülüyordu; içine tırnak koyan biri <c>icacls</c>'e istediği bayrağı
    /// (<c>/inheritance:r</c>, <c>/deny</c>, <c>/remove</c>) geçirebilir, noktalı virgülle de
    /// istediği PowerShell komutunu çalıştırabilirdi — panel LocalSystem olarak koştuğu için
    /// bunun ağırlığı SYSTEM seviyesindedir. Uç yalnız yöneticiye açık olsa da, geri alınamayan
    /// bir izin değişikliğinin hiçbir korkuluğu yoktu.
    /// </para>
    /// <para>
    /// Ayrıca komut PowerShell'e hiç uğramadan doğrudan <c>icacls.exe</c>'ye argüman dizisiyle
    /// verilir: kabuk ayrıştırması devreden çıkar.
    /// </para>
    /// </summary>
    public Task<FileWorkspaceDto> SetPermissionsAsync(SetFilePermissionsRequest request, CancellationToken cancellationToken = default) =>
        SetPermissionsCoreAsync(request, null, cancellationToken);

    public Task<FileWorkspaceDto> SetPermissionsAsync(SetFilePermissionsRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        SetPermissionsCoreAsync(request, beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), cancellationToken);

    private async Task<FileWorkspaceDto> SetPermissionsCoreAsync(SetFilePermissionsRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeVirtualPath(request.Path);
        // Capture and validate every native argument before the first resolver/authority wait.
        var principal = AclInputValidator.EnsurePrincipal(request.Principal);
        var rights = AclInputValidator.EnsureRights(request.Rights);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);

        await ApprovePublicationAsync(target, beforeMutation, cancellationToken);
        EnsurePermissionPath(target, target.PhysicalPath);
        var plan = new List<(string Path, bool Directory)>
        {
            (target.PhysicalPath, File.GetAttributes(target.PhysicalPath).HasFlag(FileAttributes.Directory))
        };
        var linksSkipped = 0;
        if (plan[0].Directory)
        {
            // /T follows directory junctions even with /L. Measure the entire ordinary tree
            // before the first effect; do not silently apply a partial/truncated plan.
            var walk = SafeDirectoryWalk.Yuru(target.PhysicalPath, PermissionWalkBudget(), entry =>
            {
                EnsurePermissionPath(target, entry.FullName);
                plan.Add((entry.FullName, entry is DirectoryInfo));
            }, null, cancellationToken, () => linksSkipped++);
            if (walk.Truncated)
                throw new AppException("files.acl_failed", 500, 500, "The ACL target tree could not be fully measured before processing.");
        }

        var allMeasured = true;
        using (var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            execution.CancelAfter(PermissionExecutionTimeout);
            try
            {
                foreach (var entry in plan)
                {
                    await ApprovePublicationAsync(target, beforeMutation, execution.Token);
                    // Repeat after every authority await: a descendant can be replaced with
                    // a junction after planning. /L alone is not an ancestor boundary.
                    EnsurePermissionPath(target, entry.Path);
                    if (File.GetAttributes(entry.Path).HasFlag(FileAttributes.Directory) != entry.Directory)
                        throw new AppException("files.acl_failed", 409, 409, "An ACL target changed while processing was pending.");
                    var result = await PermissionCommand("icacls.exe",
                        $"\"{entry.Path}\" /grant \"{principal}:{rights}\" /C /L", execution.Token);
                    await ApprovePublicationAsync(target, beforeMutation, execution.Token);
                    allMeasured &= MeasurePermissionProcessing(result).Verified == true;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && execution.IsCancellationRequested)
            {
                throw new AppException("files.acl_failed", 503, 503, "ACL processing exceeded its time limit; previously applied entries may remain changed.");
            }
        }
        var workspace = await ReadMutationWorkspaceAsync(normalizedPath, beforeMutation, cancellationToken);
        workspace.PermissionsVerified = allMeasured ? true : null;
        workspace.PermissionsDetail = allMeasured
            ? $"ACL command processed {plan.Count.ToString(CultureInfo.InvariantCulture)} ordinary item(s); failed processing 0 item(s)."
            : "ACL commands exited successfully, but at least one processing summary could not be measured.";
        if (linksSkipped > 0)
            workspace.PermissionsDetail += $" Skipped {linksSkipped.ToString(CultureInfo.InvariantCulture)} link(s); their destinations were not included.";
        return workspace;
    }

    private static void EnsurePermissionPath(ResolvedTarget target, string path)
    {
        if (!PathContainment.IsRootOrInside(target.PhysicalPath, path) ||
            File.GetAttributes(target.SandboxRoot).HasFlag(FileAttributes.ReparsePoint))
            throw new AppException("files.path_outside_root", 400, 400);
        EnsureNoReparsePoints(target.SandboxRoot, path);
    }

    private Task<FileWorkspaceDto> ReadMutationWorkspaceAsync(string path,
        Func<SiteDto, CancellationToken, Task>? beforeMutation, CancellationToken token) =>
        GetWorkspaceCoreAsync(path, beforeMutation is null ? null : (site, currentToken) =>
            beforeMutation(site ?? throw new AppException("files.site_required", 400, 400), currentToken), token);

    private static readonly Regex PermissionSummary = new(
        @"^[ \t]*Successfully processed ([0-9]+) files; Failed processing ([0-9]+) files[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static (bool? Verified, string Detail) MeasurePermissionProcessing(CommandResult result)
    {
        // This measures icacls processing, not the effective DACL or a user's effective access.
        // /C can report failed files even when the process exits successfully. Do not publish
        // arbitrary process output (paths, account names, or credentials) as a result detail.
        if (!result.Success)
            throw new AppException("files.acl_failed", 500, 500, $"ACL command exited with code {result.ExitCode}.");
        var summaries = PermissionSummary.Matches(result.StandardOutput + "\n" + result.StandardError);
        long processed = 0;
        var measured = 0;
        foreach (Match summary in summaries)
        {
            if (!long.TryParse(summary.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var success) ||
                !long.TryParse(summary.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var failed))
                continue;
            if (failed > 0 || success == 0)
                throw new AppException("files.acl_failed", 500, 500,
                    $"ACL command processed {success.ToString(CultureInfo.InvariantCulture)} item(s); failed processing {failed.ToString(CultureInfo.InvariantCulture)} item(s).");
            measured++;
            processed = success;
        }
        return summaries.Count == 1 && measured == 1
            ? (true, $"ACL command processed {processed.ToString(CultureInfo.InvariantCulture)} item(s); failed processing 0 item(s).")
            : (null, "ACL command exited successfully, but its processing summary could not be measured.");
    }

    public Task<string> SaveUploadAsync(string path, Stream stream, CancellationToken cancellationToken = default) =>
        SaveUploadCoreAsync(path, stream, null, cancellationToken);

    public Task<string> SaveUploadAsync(string path, Stream stream,
        Func<SiteDto, CancellationToken, Task> beforePublication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforePublication);
        return SaveUploadCoreAsync(path, stream, beforePublication, cancellationToken);
    }

    private async Task<string> SaveUploadCoreAsync(string path, Stream stream,
        Func<SiteDto, CancellationToken, Task>? beforePublication, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeVirtualPath(path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);
        await ApprovePublicationAsync(target, beforePublication, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(target.PhysicalPath) ?? target.SandboxRoot);
        await WriteAtomicallyAsync(target, (destination, token) => stream.CopyToAsync(destination, token), cancellationToken,
            token => ApprovePublicationAsync(target, beforePublication, token));
        return normalizedPath;
    }

    private static async Task WriteAtomicallyAsync(
        ResolvedTarget target, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforePublication = null, bool failIfExists = false,
        DateTime? lastWriteTimeUtc = null, Action? releasePublicationRead = null)
    {
        // Aynı birimdeki geçici dosya tamamlanmadan mevcut site dosyasına dokunma.
        // Doğrudan File.Create, aktarımın ilk baytından önce eski içeriği siliyordu.
        var temporary = Path.Combine(Path.GetDirectoryName(target.PhysicalPath)!, $".adapanel-write-{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                await write(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            if (lastWriteTimeUtc is { } timestamp) File.SetLastWriteTimeUtc(temporary, timestamp);
            if (beforePublication is not null) await beforePublication(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(target.SandboxRoot, target.PhysicalPath);
            // Release an optional original-file read lock only after all awaits.
            releasePublicationRead?.Invoke();
            if (failIfExists)
            {
                // No-overwrite publication is atomic too. A concurrent creator wins;
                // only the Move error is interpreted as an existing destination.
                try { File.Move(temporary, target.PhysicalPath); }
                catch (IOException) when (File.Exists(target.PhysicalPath) || Directory.Exists(target.PhysicalPath))
                { throw new AppException("files.already_exists", 409, 409); }
                catch (UnauthorizedAccessException) when (Directory.Exists(target.PhysicalPath))
                { throw new AppException("files.already_exists", 409, 409); }
            }
            else if (File.Exists(target.PhysicalPath))
            {
                // Windows ReplaceFile hedefin özel ACL'lerini korur; yeni dosyanın
                // klasörden devraldığı izinlerle kullanıcının izinlerini ezme.
                File.Replace(temporary, target.PhysicalPath, null);
            }
            else
            {
                File.Move(temporary, target.PhysicalPath);
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* Temizleme hatası asıl yazma hatasını gizlemesin. */ }
        }
    }

    public async Task<(string FileName, string ContentType, string PhysicalPath)> DownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeVirtualPath(path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        if (!File.Exists(target.PhysicalPath))
        {
            throw new AppException("files.not_found", 404, 404);
        }

        return (Path.GetFileName(target.PhysicalPath), "application/octet-stream", target.PhysicalPath);
    }

    public async Task<(string FileName, string ContentType, Stream Content)> OpenDownloadAsync(string path,
        Func<SiteDto, CancellationToken, Task> beforeRead, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforeRead);
        var target = await ResolveTargetAsync(NormalizeVirtualPath(path), cancellationToken);
        EnsureSiteTarget(target);
        await ApprovePublicationAsync(target, beforeRead, cancellationToken);
        EnsureInsideSandbox(target);
        if (!File.Exists(target.PhysicalPath)) throw new AppException("files.not_found", 404, 404);
        var input = new FileStream(target.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await ApprovePublicationAsync(target, beforeRead, cancellationToken);
            EnsureInsideSandbox(target);
            return (Path.GetFileName(target.PhysicalPath), "application/octet-stream", input);
        }
        catch { await input.DisposeAsync(); throw; }
    }

    public Task<FileWorkspaceDto> CompressAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default) =>
        CompressCoreAsync(request, null, null, cancellationToken);

    public Task<FileWorkspaceDto> CompressAsync(ArchiveFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforePublication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforePublication);
        return CompressCoreAsync(request, beforePublication, null, cancellationToken);
    }

    public Task<FileWorkspaceDto> CompressAsync(ArchiveFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforePublication,
        Func<long, CancellationToken, Task> beforeCompress, CancellationToken cancellationToken) =>
        CompressCoreAsync(request, beforePublication ?? throw new ArgumentNullException(nameof(beforePublication)),
            beforeCompress ?? throw new ArgumentNullException(nameof(beforeCompress)), cancellationToken);

    private async Task<FileWorkspaceDto> CompressCoreAsync(ArchiveFileRequest request,
        Func<SiteDto, CancellationToken, Task>? beforePublication,
        Func<long, CancellationToken, Task>? beforeCompress, CancellationToken cancellationToken)
    {
        var overwrite = request.Overwrite;
        var sourcePath = NormalizeVirtualPath(request.Path);
        var destinationPath = string.IsNullOrWhiteSpace(request.DestinationPath)
            ? sourcePath.TrimEnd('/') + ".zip"
            : NormalizeVirtualPath(request.DestinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var source = await ResolveTargetAsync(sourcePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSiteTarget(source);
        EnsureNotSiteRoot(source);
        await ApprovePublicationAsync(source, beforePublication, cancellationToken);
        if (!File.Exists(source.PhysicalPath) && !Directory.Exists(source.PhysicalPath))
        {
            throw new AppException("files.not_found", 404, 404);
        }

        var destination = await ResolveTargetAsync(destinationPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSiteTarget(destination);
        EnsureNotSiteRoot(destination);
        await ApprovePublicationAsync(destination, beforePublication, cancellationToken);
        if (string.Equals(source.PhysicalPath, destination.PhysicalPath, StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(source.PhysicalPath) && destination.PhysicalPath.StartsWith(
                source.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException("files.archive_target_invalid", 400, 400);
        }

        if (!overwrite && (File.Exists(destination.PhysicalPath) || Directory.Exists(destination.PhysicalPath)))
            throw new AppException("files.already_exists", 409, 409);

        // Finish the ZIP outside the site's usage tree. Its central directory and
        // compressed bytes must be complete before quota is measured.
        var stagedPath = CompressionStagingPath();
        var stagedCreated = false;
        try
        {
            await using (var output = SecureTempFile.Create(stagedPath))
            {
                stagedCreated = true;
                await WriteCompressedSourceAsync(source, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            await using var staged = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await ApproveTransferAsync(source, destination, beforePublication, cancellationToken);
            EnsureInsideSandbox(destination);

            // Keep the deducted original length stable through quota and the copy.
            // On Windows this handle denies writes/replacement until final approval.
            using var original = OpenCompressionDestination(destination.PhysicalPath, overwrite);
            var hadOriginal = original is not null;
            if (beforeCompress is not null)
                await beforeCompress(Math.Max(0, staged.Length - (original?.Length ?? 0)), cancellationToken);
            await ApproveTransferAsync(source, destination, beforePublication, cancellationToken);
            EnsureInsideSandbox(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination.PhysicalPath) ?? destination.SandboxRoot);
            await WriteAtomicallyAsync(destination,
                (output, token) => staged.CopyToAsync(output, token), cancellationToken,
                token => ApproveTransferAsync(source, destination, beforePublication, token),
                failIfExists: !overwrite || !hadOriginal,
                releasePublicationRead: () => original?.Dispose());
        }
        finally
        {
            if (stagedCreated)
            {
                try { File.Delete(stagedPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { /* Preserve the original failure if private staging cleanup fails. */ }
            }
        }
        return await ReadMutationWorkspaceAsync(BuildParentPath(destinationPath), beforePublication, cancellationToken);
    }

    private static FileStream? OpenCompressionDestination(string path, bool overwrite)
    {
        if (Directory.Exists(path) || (!overwrite && File.Exists(path)))
            throw new AppException("files.already_exists", 409, 409);
        if (!overwrite) return null;
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private async Task WriteCompressedSourceAsync(ResolvedTarget source, Stream output, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var parent = Path.GetDirectoryName(source.PhysicalPath)!;
        if (File.Exists(source.PhysicalPath))
        {
            await AddFileAsync(new FileInfo(source.PhysicalPath));
            return;
        }

        // Reject links rather than silently publishing an incomplete archive. Check
        // each directory before traversing and each file immediately before opening.
        var directories = new Stack<string>();
        directories.Push(source.PhysicalPath);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = directories.Pop();
            EnsureNoReparsePoints(source.SandboxRoot, path);
            var directory = new DirectoryInfo(path);
            var hasEntries = false;
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasEntries = true;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new AppException("files.path_outside_root", 400, 400);
                if (entry is DirectoryInfo child)
                    directories.Push(child.FullName);
                else if (entry is FileInfo file)
                    await AddFileAsync(file);
            }
            if (!hasEntries)
                SetTimestamp(archive.CreateEntry(EntryName(path) + "/"), directory.LastWriteTime);
        }

        string EntryName(string path) => Path.GetRelativePath(parent, path).Replace('\\', '/');

        async Task AddFileAsync(FileInfo file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(source.SandboxRoot, file.FullName);
            await using var input = OpenCompressionFile(file.FullName);
            var entry = archive.CreateEntry(EntryName(file.FullName), CompressionLevel.Optimal);
            SetTimestamp(entry, file.LastWriteTime);
            await using var compressed = entry.Open();
            await input.CopyToAsync(compressed, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        static void SetTimestamp(ZipArchiveEntry entry, DateTime value)
        {
            // ZIP's DOS timestamp cannot represent dates outside this range;
            // CreateEntryFromFile uses the same fallback for such source files.
            entry.LastWriteTime = value.Year is < 1980 or > 2107
                ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
                : new DateTimeOffset(value);
        }
    }

    public Task<FileWorkspaceDto> ExtractAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default) =>
        ExtractCoreAsync(request, null, null, cancellationToken);

    public Task<FileWorkspaceDto> ExtractAsync(ArchiveFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation, CancellationToken cancellationToken) =>
        ExtractCoreAsync(request, beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)), null, cancellationToken);

    public Task<FileWorkspaceDto> ExtractAsync(ArchiveFileRequest request,
        Func<SiteDto, CancellationToken, Task> beforeMutation,
        Func<long, CancellationToken, Task> beforeExtract, CancellationToken cancellationToken) =>
        ExtractCoreAsync(request, beforeMutation ?? throw new ArgumentNullException(nameof(beforeMutation)),
            beforeExtract ?? throw new ArgumentNullException(nameof(beforeExtract)), cancellationToken);

    private async Task<FileWorkspaceDto> ExtractCoreAsync(ArchiveFileRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeMutation,
        Func<long, CancellationToken, Task>? beforeExtract, CancellationToken cancellationToken)
    {
        var archivePath = NormalizeVirtualPath(request.Path);
        var destinationPath = string.IsNullOrWhiteSpace(request.DestinationPath)
            ? BuildParentPath(archivePath)
            : NormalizeVirtualPath(request.DestinationPath);
        var archive = await ResolveTargetAsync(archivePath, cancellationToken);
        EnsureSiteTarget(archive);
        EnsureNotSiteRoot(archive);
        await ApprovePublicationAsync(archive, beforeMutation, cancellationToken);
        if (!File.Exists(archive.PhysicalPath) || !string.Equals(Path.GetExtension(archive.PhysicalPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("files.invalid_archive", 400, 400);
        }

        var destination = await ResolveTargetAsync(destinationPath, cancellationToken);
        EnsureSiteTarget(destination);
        await ApproveTransferAsync(archive, destination, beforeMutation, cancellationToken);

        // Keep the measured archive open without write/delete sharing through quota and extraction.
        await using var input = new FileStream(archive.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var zip = OpenArchive(input);
        // Validate every header before effects; quota below accounts for each published
        // entry's net growth, including repeated names that replace an earlier entry.
        _ = MeasureArchiveContentBytes(zip, cancellationToken);
        var quotaChecked = false;

        foreach (var entry in zip.Entries)
        {
            if (ExtractEntryReady is not null) await ExtractEntryReady(entry.FullName, cancellationToken);
            await ApproveTransferAsync(archive, destination, beforeMutation, cancellationToken);
            var outputPath = Path.GetFullPath(Path.Combine(destination.PhysicalPath, entry.FullName));
            if (!outputPath.StartsWith(
                    destination.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException("files.path_outside_root", 400, 400);
            }

            // Await'ler sırasında önceki temiz önek bir bağlantıyla değiştirilebilir. Her
            // girdide, klasör/dosya yazmadan önce hedef klasör dahil bütün yolu yeniden denetle.
            EnsureNoReparsePoints(destination.SandboxRoot, outputPath);

            if (string.IsNullOrEmpty(entry.Name))
            {
                if (!quotaChecked) await CheckGrowthAsync(0);
                EnsureNoReparsePoints(destination.SandboxRoot, outputPath);
                Directory.CreateDirectory(outputPath);
                continue;
            }

            // Keep the deducted destination length stable through quota, decompression
            // and the final authority await, without holding a handle for every ZIP entry.
            using var original = OpenCompressionDestination(outputPath, overwrite: true);
            await CheckGrowthAsync(Math.Max(0, entry.Length - (original?.Length ?? 0)));
            EnsureNoReparsePoints(destination.SandboxRoot, outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await WriteAtomicallyAsync(destination with { PhysicalPath = outputPath },
                (output, token) => CopyVerifiedArchiveEntryAsync(entry, output, token), cancellationToken,
                token => ApproveTransferAsync(archive, destination, beforeMutation, token),
                failIfExists: original is null,
                lastWriteTimeUtc: entry.LastWriteTime.UtcDateTime,
                releasePublicationRead: () => original?.Dispose());
        }

        if (!quotaChecked)
        {
            // An empty archive still creates the selected directory, after approval.
            await CheckGrowthAsync(0);
            EnsureNoReparsePoints(destination.SandboxRoot, destination.PhysicalPath);
            Directory.CreateDirectory(destination.PhysicalPath);
        }
        await ApproveTransferAsync(archive, destination, beforeMutation, cancellationToken);
        return await ReadMutationWorkspaceAsync(destinationPath, beforeMutation, cancellationToken);

        async Task CheckGrowthAsync(long growth)
        {
            if (beforeExtract is not null) await beforeExtract(growth, cancellationToken);
            await ApproveTransferAsync(archive, destination, beforeMutation, cancellationToken);
            quotaChecked = true;
        }
    }

    private static ZipArchive OpenArchive(Stream input)
    {
        try { return new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new AppException("files.invalid_archive", 400, 400); }
    }

    private static long MeasureArchiveContentBytes(ZipArchive zip, CancellationToken token)
    {
        try
        {
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length < 0) throw new InvalidDataException();
                total = checked(total + entry.Length);
            }
            return total;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        { throw new AppException("files.invalid_archive", 400, 400); }
    }

    private static async Task CopyVerifiedArchiveEntryAsync(ZipArchiveEntry entry, Stream output, CancellationToken token)
    {
        try
        {
            await using var source = entry.Open();
            var remaining = entry.Length;
            var buffer = new byte[81920];
            var crc = uint.MaxValue;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                token.ThrowIfCancellationRequested();
                if (read == 0) throw new InvalidDataException();
                for (var i = 0; i < read; i++) crc = ArchiveCrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                remaining -= read;
            }
            // .NET 8 decompression does not validate CRC or the declared uncompressed length.
            // A one-byte EOF probe detects extra content without writing beyond the quota measurement.
            var extra = await source.ReadAsync(buffer.AsMemory(0, 1), token);
            token.ThrowIfCancellationRequested();
            if (extra != 0 || ~crc != entry.Crc32) throw new InvalidDataException();
        }
        catch (InvalidDataException) { throw new AppException("files.invalid_archive", 400, 400); }
    }

    private static readonly uint[] ArchiveCrcTable = CreateArchiveCrcTable();

    private static uint[] CreateArchiveCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xEDB88320u);
            table[i] = value;
        }
        return table;
    }

    public async Task<IReadOnlyCollection<FileEntryDto>> SearchAsync(SearchFilesRequest request, CancellationToken cancellationToken = default) =>
        (await SearchMeasuredAsync(request, cancellationToken)).Items;

    public Task<FileSearchResultDto> SearchMeasuredAsync(SearchFilesRequest request, CancellationToken cancellationToken = default) =>
        SearchMeasuredCoreAsync(request, null, cancellationToken);

    public Task<FileSearchResultDto> SearchMeasuredAsync(SearchFilesRequest request,
        Func<SiteDto, CancellationToken, Task> beforeRead, CancellationToken cancellationToken) =>
        SearchMeasuredCoreAsync(request, beforeRead ?? throw new ArgumentNullException(nameof(beforeRead)), cancellationToken);

    private async Task<FileSearchResultDto> SearchMeasuredCoreAsync(SearchFilesRequest request,
        Func<SiteDto, CancellationToken, Task>? beforeRead, CancellationToken cancellationToken)
    {
        var query = request.Query.Trim();
        if (query.Length < 2)
        {
            throw new AppException("files.search_query_short", 400, 400);
        }

        var normalizedPath = NormalizeVirtualPath(request.Path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        await ApprovePublicationAsync(target, beforeRead, cancellationToken);
        if (!Directory.Exists(target.PhysicalPath))
        {
            throw new AppException("files.not_found", 404, 404);
        }

        var results = new List<FileEntryDto>();
        var scannedFiles = 0;
        foreach (var path in EnumerateSafeFiles(target.PhysicalPath).Take(5000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedFiles++;
            var file = new FileInfo(path);
            var nameMatch = file.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            var contentMatch = false;
            if (!nameMatch && file.Length <= 2 * 1024 * 1024 && IsSearchableExtension(file.Extension))
            {
                try
                {
                    var content = ReadSearchContent is { } read
                        ? await read(path, cancellationToken)
                        : await ReadSearchFileAsync(path, cancellationToken);
                    await ApprovePublicationAsync(target, beforeRead, cancellationToken);
                    contentMatch = content?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
                }
                catch (DecoderFallbackException)
                {
                    contentMatch = false;
                }
            }

            if (!nameMatch && !contentMatch)
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(target.PhysicalPath, path).Replace('\\', '/');
            results.Add(new FileEntryDto
            {
                Name = file.Name,
                Path = CombineVirtualPath(normalizedPath, relativePath),
                IsDirectory = false,
                Size = file.Length,
                LastModifiedUtc = file.LastWriteTimeUtc,
                Extension = file.Extension
            });

            if (results.Count >= 100)
            {
                break;
            }
        }

        await ApprovePublicationAsync(target, beforeRead, cancellationToken);
        var reason = results.Count >= 100 ? "files.search_truncated_results"
            : scannedFiles >= 5000 ? "files.search_truncated_files" : string.Empty;
        return new FileSearchResultDto { Path = normalizedPath, Query = query, Items = results.ToArray(), ScannedFiles = scannedFiles,
            Truncated = reason.Length != 0, TruncatedReasonKey = reason };
    }

    private async Task<string?> ReadSearchFileAsync(string path, CancellationToken token)
    {
        await using var stream = OpenSearchContent(path);
        token.ThrowIfCancellationRequested();
        // The path's earlier FileInfo may describe a different version. Measure and read
        // the same opened handle, and never let a later growth turn this into an unbounded read.
        var length = stream.Length;
        if (length > 2 * 1024 * 1024) return null;
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, token);
        token.ThrowIfCancellationRequested();
        using var content = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024, leaveOpen: true);
        return await reader.ReadToEndAsync(token);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Yürüyüşün kendisi burada DEĞİL:</b> ağaç <see cref="SafeDirectoryWalk"/> ile gezilir —
    /// yığın (özyineleme yok), bağlantı atlama ve bütçe kuralları orada, gerekçeleriyle birlikte
    /// yazılıdır. Bu metoda kalan iş, o yürüyüşün ÖNÜNDEKİ KAPIDIR: sanal yolu çözümlemek ve sanal
    /// alan denetimini yapmak. Yürüyüş burada ikinci kez yazılsaydı, kota yolunda tam olarak bu
    /// olmuştu: aynı ağaç iki farklı kuralla gezilir ve biri korumasız kalırdı.
    /// </para>
    /// </remarks>
    public Task<DirectorySizeDto> CalculateSizeAsync(string path, CancellationToken cancellationToken = default) =>
        CalculateSizeAsync(path, BoyutOgeButcesi, BoyutSureButcesiMs, cancellationToken);

    public Task<DirectorySizeDto> CalculateSizeAsync(string path,
        Func<SiteDto, CancellationToken, Task> beforeRead, CancellationToken cancellationToken) =>
        CalculateSizeCoreAsync(path, BoyutOgeButcesi, BoyutSureButcesiMs,
            beforeRead ?? throw new ArgumentNullException(nameof(beforeRead)), cancellationToken);

    /// <summary>
    /// Yukarıdakinin BÜTÇESİ DIŞARIDAN verilen hâli — tek uygulama budur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden var:</b> bütçe korumasının GERÇEKTEN çalıştığı ancak bütçe doldurularak
    /// kanıtlanabilir; üretim değerleriyle bu, testte 200.000 dosya yaratmak (dakikalar süren,
    /// diski yoran ve hiçbir ek şey kanıtlamayan bir kurulum) demekti. Alternatif — testin kaynağa
    /// bakıp "bütçe kodu duruyor mu" demesi — davranışı değil metni ölçerdi.
    /// </para>
    /// <para>
    /// Aşırı yükleme <c>internal</c>'dır: dışarıya, bütçesi kaldırılabilir bir uç AÇMAZ. Genel
    /// yüzeyde yalnız sabitlerle çağrılan biçim vardır.
    /// </para>
    /// </remarks>
    internal Task<DirectorySizeDto> CalculateSizeAsync(
        string path,
        int ogeButcesi,
        int sureButcesiMs,
        CancellationToken cancellationToken) =>
        CalculateSizeCoreAsync(path, ogeButcesi, sureButcesiMs, null, cancellationToken);

    private async Task<DirectorySizeDto> CalculateSizeCoreAsync(string path, int ogeButcesi, int sureButcesiMs,
        Func<SiteDto, CancellationToken, Task>? beforeRead, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeVirtualPath(path);

        // AYNI KAPI: yol çözümlemesi (sanal alan + yol üzerindeki bağlantı denetimi) diğer uçlarla
        // birebir aynı yardımcıdan gelir. Kendi yol birleştirmesini yazan bir uç, korumanın tam da
        // en yeni yerde delinmesi olurdu.
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureInsideSandbox(target);
        await ApprovePublicationAsync(target, beforeRead, cancellationToken);

        // SİTE KÖKÜ BİLEREK YASAKLANMADI (EnsureNotSiteRoot çağrılmaz): "sitem ne kadar yer
        // kaplıyor" bu ucun en sık sorulacak sorusudur. Kök koruması, kökü DEĞİŞTİREN/silen
        // işlemler içindir; okuma yapan bir hesabın kökü dışlaması, ucu asıl işi için kullanılamaz
        // hâle getirirdi.
        if (!Directory.Exists(target.PhysicalPath))
        {
            // Dosya yolu da buraya düşer ve bilerek öyle bırakıldı: bir DOSYANIN boyutu zaten
            // listede yazılıdır (FileEntryDto.Size). Dosya için "klasör boyutu" cevabı üretmek,
            // aynı bilgiyi ikinci ve doğrulanmamış bir yoldan vermek olurdu.
            throw new AppException("files.not_found", 404, 404);
        }

        // ORTAK YÜRÜYÜŞ. Bütçe her çağrıda YENİDEN kurulur: bu uç TEK bir klasör için TEK bir
        // soru cevaplar, dolayısıyla bütçenin kapsamı da tek çağrıdır. (Kota yolu aynı bütçe
        // nesnesini aboneliğin BÜTÜN sitelerinde paylaşır; gerekçesi orada yazılı.)
        var yuruyus = SafeDirectoryWalk.Yuru(
            target.PhysicalPath,
            new SafeDirectoryWalkBudget(ogeButcesi, sureButcesiMs),
            cancellationToken);

        if (SizeWalkCompleted is not null) await SizeWalkCompleted(cancellationToken);
        await ApprovePublicationAsync(target, beforeRead, cancellationToken);

        return new DirectorySizeDto
        {
            // Path YALNIZ burada doldurulur: ortak yürüyüş fiziksel yolu bilir, SANAL yolu bilmez —
            // bilmemelidir de, çünkü sanal yol kavramı kota yolunda hiç yoktur.
            Path = normalizedPath,
            TotalBytes = yuruyus.TotalBytes,
            FileCount = yuruyus.FileCount,
            DirectoryCount = yuruyus.DirectoryCount,
            Truncated = yuruyus.Truncated,
            TruncatedReasonKey = yuruyus.TruncatedReasonKey
        };
    }

    /// <inheritdoc />
    public async Task<DirectorySizeDto> MeasureAsync(string[] paths, CancellationToken cancellationToken = default)
    {
        // TEK BÜTÇE, BÜTÜN YOLLAR: kota yolundaki kuralla aynı ve aynı sebeple. Bütçe yol başına
        // verilseydi tavan yol sayısıyla ÇARPILIRDI — 20 klasör seçen bir istek 20 kat daha uzun
        // bir işe hak kazanır ve bütçe fiilen kalkardı.
        var butce = new SafeDirectoryWalkBudget(BoyutOgeButcesi, BoyutSureButcesiMs);

        long toplamBayt = 0;
        var dosyaSayisi = 0;
        var klasorSayisi = 0;
        var kesildi = false;
        var kesilmeSebebi = string.Empty;

        foreach (var hamYol in paths ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(hamYol))
            {
                continue;
            }

            // AYNI KAPI: yol çözümlemesi (sanal alan + yol üzerindeki bağlantı ve aygıt adı
            // denetimi) diğer uçlarla birebir aynı yardımcıdan gelir. Ölçüm bir OKUMADIR ama
            // kapsam dışı bir yolun BOYUTUNU söylemek de bir sızıntıdır.
            var target = await ResolveTargetAsync(NormalizeVirtualPath(hamYol), cancellationToken);
            EnsureSiteTarget(target);
            EnsureInsideSandbox(target);

            // SİTE KÖKÜ YASAKLANMAZ (EnsureNotSiteRoot çağrılmaz): gerekçe CalculateSizeAsync'te
            // yazılı — kök koruması kökü DEĞİŞTİREN işlemler içindir, okuyan bir hesap için değil.

            if (Directory.Exists(target.PhysicalPath))
            {
                var yuruyus = SafeDirectoryWalk.Yuru(target.PhysicalPath, butce, cancellationToken);
                toplamBayt += yuruyus.TotalBytes;
                dosyaSayisi += yuruyus.FileCount;
                klasorSayisi += yuruyus.DirectoryCount;
                if (yuruyus.Truncated)
                {
                    kesildi = true;
                    kesilmeSebebi = yuruyus.TruncatedReasonKey;
                }
            }
            else if (File.Exists(target.PhysicalPath))
            {
                try
                {
                    toplamBayt += new FileInfo(target.PhysicalPath).Length;
                    dosyaSayisi++;
                }
                catch (IOException)
                {
                    // Ölçüm sırasında silinmiş/okunamayan dosya 0 sayılır: artık diskte yeri yok.
                }
            }

            // YOL YOKSA İSTİSNA ATILMAZ, 0 sayılır. İki çağıran da bunu gerektiriyor: yazma yolunda
            // "hedef dosya henüz yok" NORMAL durumdur (yeni dosya), kopyalama yolunda ise olmayan
            // kaynak zaten öge başına `files.not_found` ile raporlanır — ölçümün istisna atması,
            // sekiz ögeli bir isteği tek eksik dosya yüzünden hiç başlatmamak olurdu.
        }

        return new DirectorySizeDto
        {
            // Path BOŞ bırakılır ve bilerek: bu sonuç TEK bir yola değil, bir yol KÜMESİNE aittir;
            // birinin yolunu yazmak, arayüzde yanlış klasöre iliştirilebilecek bir sayı üretirdi.
            TotalBytes = toplamBayt,
            FileCount = dosyaSayisi,
            DirectoryCount = klasorSayisi,
            Truncated = kesildi,
            TruncatedReasonKey = kesilmeSebebi
        };
    }

    /// <inheritdoc />
    public async Task<long> MeasureArchiveContentBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        var archivePath = NormalizeVirtualPath(path);
        var archive = await ResolveTargetAsync(archivePath, cancellationToken);
        EnsureSiteTarget(archive);
        EnsureInsideSandbox(archive);

        // ExtractAsync ile AYNI kabul kuralı ve AYNI anahtar: iki yol farklı şeye "geçerli arşiv"
        // derse, kota denetimi açılmayacak bir dosyayı ölçer ya da tersi.
        if (!File.Exists(archive.PhysicalPath)
            || !string.Equals(Path.GetExtension(archive.PhysicalPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("files.invalid_archive", 400, 400);
        }

        // Advisory metadata measurement for trusted callers. HTTP extraction measures its own
        // retained archive and verifies actual entry length/CRC before each file is published.
        await using var input = new FileStream(archive.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var zip = OpenArchive(input);
        return MeasureArchiveContentBytes(zip, cancellationToken);
    }

    private async Task<FileWorkspaceDto> BuildVirtualRootWorkspaceAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return new FileWorkspaceDto
            {
                Path = string.Empty,
                DisplayName = "sites",
                PhysicalPath = string.Empty,
                IsDirectory = true,
                Entries =
                [
                    new FileEntryDto { Name = "sites", Path = "sites", IsDirectory = true }
                ]
            };
        }

        if (normalizedPath == "sites")
        {
            var sites = await iisSiteService.GetSitesAsync(cancellationToken);
            return new FileWorkspaceDto
            {
                Path = "sites",
                ParentPath = string.Empty,
                DisplayName = "sites",
                PhysicalPath = string.Empty,
                IsDirectory = true,
                Entries = sites
                    .OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(site => new FileEntryDto
                    {
                        Name = site.Name,
                        Path = $"sites/{site.Name}",
                        IsDirectory = true
                    })
                    .ToArray()
            };
        }

        throw new AppException("files.not_found", 404, 404);
    }

    private async Task<ResolvedTarget> ResolveTargetAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return new ResolvedTarget(string.Empty, string.Empty, true);
        }

        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = segments[0].ToLowerInvariant();

        return root switch
        {
            "sites" when segments.Length == 1 => new ResolvedTarget(string.Empty, string.Empty, true),
            "sites" => await ResolveSiteTargetAsync(segments, cancellationToken),
            _ => throw new AppException("files.not_found", 404, 404)
        };
    }

    private async Task<ResolvedTarget> ResolveSiteTargetAsync(string[] segments, CancellationToken cancellationToken)
    {
        var site = await iisSiteService.GetSiteAsync(segments[1], cancellationToken);
        var basePath = Path.GetFullPath(ResolveApplicationRoot(site));

        // AYGIT ADI DENETİMİ Path API'lerinden ÖNCE: gerekçesi EnsureNoReservedSegments'te.
        EnsureNoReservedSegments(basePath, segments.Skip(2));

        var subPath = string.Join(Path.DirectorySeparatorChar, segments.Skip(2));
        return ResolveSandboxTarget(Path.Combine(basePath, subPath), basePath) with { Site = site };
    }

    /// <summary>
    /// Yolun hiçbir parçasının Windows AYGIT ADI (<c>CON</c>, <c>NUL</c>, <c>COM1</c>...) olmadığını
    /// doğrular; öyleyse istek <c>files.invalid_name</c> ile reddedilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>DENETİM ÖNCE NE YAPIYORDU — ÖLÇÜLDÜ (bu makinede, gerçek çağrılarla):</b> aygıt adlı bir
    /// istek 500 ÜRETMİYOR, <c>files.path_outside_root</c> (400) ile reddediliyordu. Sebep:
    /// <c>Path.GetFullPath(&lt;klasör&gt;\NUL)</c> .NET 8'de <c>\\.\NUL</c> döndürüyor — yani yol
    /// sanal alanın dışına ÇIKIYOR ve mevcut kapı onu zaten eliyor. Aynısı <c>CON</c>, <c>COM1</c>,
    /// <c>LPT9</c>, <c>AUX</c>, <c>PRN</c>, <c>con.json</c>, <c>NUL.txt</c> ve hatta listede
    /// OLMAYAN <c>CONIN$</c> için de ölçüldü.
    /// </para>
    /// <para>
    /// <b>O hâlde neden bu denetim:</b> düzeltilen şey çökme değil, YALAN. Kendi klasöründe "NUL"
    /// adlı bir klasör açmaya çalışan kullanıcı "yol kök dizinin dışında" cevabı alıyordu ve bu onu
    /// var olmayan bir yol/izin sorununu aramaya gönderiyordu; asıl sebep — "bu bir ad değil, bir
    /// aygıt" — hiçbir yerde yazmıyordu. Üstelik koruma KAZAYDI: bir <c>GetFullPath</c> yan
    /// etkisine dayanıyordu, panelin bilinçli bir kuralına değil. Bugün gürültülü olanın yarın
    /// sessiz olmayacağını garanti eden tek şey, kuralın AÇIKÇA yazılmasıdır — nitekim ham
    /// <c>FileStream(&lt;klasör&gt;\NUL, CreateNew)</c> bu makinede HİÇ İSTİSNA ATMIYOR (ölçüldü),
    /// yani sanal alan kapısı bir gün gevşerse arkasında sessiz veri kaybı var.
    /// </para>
    /// <para>
    /// <b>Neden VAR OLAN yol MUAF:</b> <see cref="WindowsReservedNames"/> listesinde <c>CLOCK$</c>
    /// da var ve o ad bu makinede ayrılmış DEĞİL — <c>CLOCK$</c> adlı klasör/dosya gerçekten
    /// oluşturulabiliyor (ölçüldü; kaynağın kendi belgesi de bunu dürüstçe yazıyor). Koşulsuz bir
    /// denetim, diskte GERÇEKTEN duran bir <c>clock$.txt</c>'yi dosya yöneticisinden görünmez,
    /// indirilemez ve SİLİNEMEZ yapardı: kullanıcının verisine erişimini kapatmak, düzeltmeye
    /// çalıştığımız hatadan ağır olurdu. Gerçek aygıt adları bu muafiyetten yararlanamaz, çünkü
    /// onlar için varlık sorgusu FALSE döner (ölçüldü: <c>File.Exists(&lt;klasör&gt;\NUL)</c> =
    /// false, <c>Directory.Exists</c> = false).
    /// </para>
    /// </remarks>
    private static void EnsureNoReservedSegments(string sandboxRoot, IEnumerable<string> segments)
    {
        var currentPath = sandboxRoot;
        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!WindowsReservedNames.IsReserved(segment))
            {
                continue;
            }

            if (File.Exists(currentPath) || Directory.Exists(currentPath))
            {
                continue;
            }

            throw new AppException("files.invalid_name", 400, 400);
        }
    }

    private static string ResolveApplicationRoot(AdaPanel.Core.Dtos.Sites.SiteDto site)
    {
        return string.IsNullOrWhiteSpace(site.ApplicationRootPath)
            ? site.PhysicalPath
            : site.ApplicationRootPath;
    }

    private static ResolvedTarget ResolveSandboxTarget(string candidatePath, string rootPath)
    {
        var fullPath = Path.GetFullPath(candidatePath);
        var sandboxRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var sandboxPrefix = sandboxRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(fullPath, sandboxRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(sandboxPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("files.path_outside_root", 400, 400);
        }

        EnsureNoReparsePoints(sandboxRoot, fullPath);
        return new ResolvedTarget(fullPath, sandboxRoot, false);
    }

    /// <summary>
    /// Kökten hedefe giden yolun HİÇBİR parçasının bağlantı (junction/symlink) olmadığını doğrular.
    /// </summary>
    private static void EnsureNoReparsePoints(
        string sandboxRoot,
        string fullPath)
    {
        var relativePath = Path.GetRelativePath(sandboxRoot, fullPath);
        var currentPath = sandboxRoot;
        foreach (var segment in relativePath.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.Exists(currentPath) || Directory.Exists(currentPath)) &&
                File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new AppException("files.path_outside_root", 400, 400);
            }
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    directories.Push(childDirectory.FullName);
                }
                else if (entry is FileInfo file)
                {
                    yield return file.FullName;
                }
            }
        }
    }

    private static void EnsureSiteTarget(ResolvedTarget target)
    {
        if (target.IsVirtualRoot || string.IsNullOrWhiteSpace(target.SandboxRoot))
        {
            throw new AppException("files.site_required", 400, 400);
        }
    }

    private static void EnsureNotSiteRoot(ResolvedTarget target)
    {
        if (string.Equals(target.PhysicalPath, target.SandboxRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("files.site_root_protected", 400, 400);
        }
    }

    private async Task<FileTextContent> ReadFileContentAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Measure and read the same handle; a concurrently growing file must not turn a
        // bounded preview into an unbounded read. An unloaded file never impersonates text.
        await using var input = OpenWorkspaceFile(path);
        return await FileTextContent.ReadAsync(input, cancellationToken);
    }

    private static string NormalizeVirtualPath(string? path)
    {
        return (path ?? string.Empty).Trim().Trim('/').Replace('\\', '/');
    }

    private static string BuildParentPath(string path)
    {
        var normalized = NormalizeVirtualPath(path);
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : normalized[..lastSlash];
    }

    private static string CombineVirtualPath(string parent, string name)
    {
        var normalizedParent = NormalizeVirtualPath(parent);
        return string.IsNullOrWhiteSpace(normalizedParent) ? name : $"{normalizedParent}/{name}";
    }

    private static bool IsSearchableExtension(string extension)
    {
        return extension.ToLowerInvariant() is
            ".txt" or ".log" or ".json" or ".xml" or ".config" or ".ini" or ".md" or
            ".html" or ".htm" or ".css" or ".js" or ".ts" or ".vue" or ".php" or
            ".asp" or ".aspx" or ".cs" or ".py";
    }

    private static async Task ApprovePublicationAsync(ResolvedTarget target,
        Func<SiteDto, CancellationToken, Task>? beforePublication, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (beforePublication is not null)
            await beforePublication(target.Site ?? throw new AppException("files.site_required", 400, 400), token);
        token.ThrowIfCancellationRequested();
    }

    private readonly record struct ResolvedTarget(string PhysicalPath, string SandboxRoot, bool IsVirtualRoot,
        SiteDto? Site = null);

    /// <summary>Klasör kopyalamanın sonucu: iş bitti mi, ve kullanıcıya söylenecek EKSİKLİK anahtarı.</summary>
    /// <remarks>
    /// <b>Neden tek bir <c>bool</c> yetmiyor:</b> üç ayrı durum var ve üçü de kullanıcıya farklı şey
    /// söyler — (a) tam kopya, (b) kopya OLUŞTU ama eksik (bağlantı atlandı / alt klasör okunamadı),
    /// (c) kopya HİÇ OLUŞMADI (bütçe doldu, geri sarıldı). (b) ile (c)'yi tek bayrakta toplamak,
    /// diskte duran bir klasörle hiç olmayan bir klasörü aynı satırla anlatmak olurdu.
    /// </remarks>
    private readonly record struct KlasorKopyasi(bool Tamamlandi, string EksiklikAnahtari);

    /// <summary>Aktarımın DOĞRULANMIŞ tek ögesi: çözülmüş kaynak/hedef + kullanıcıya dönecek sanal yollar.</summary>
    private readonly record struct TransferItem(
        ResolvedTarget Source,
        ResolvedTarget Target,
        string SourcePath,
        string TargetPath);

    /// <summary>Doğrulanmış aktarım planı; hiçbir ögesi dosya sistemine dokunmadan önce hazırdır.</summary>
    private readonly record struct TransferPlan(string DestinationPath, ResolvedTarget Destination, IReadOnlyList<TransferItem> Items);

    /// <summary>
    /// İsteğin BİÇİMİ geçersiz olduğunda atılacak anahtarlar — taşımada <c>files.move_*</c>,
    /// kopyalamada <c>files.copy_*</c>.
    /// </summary>
    /// <remarks>
    /// <b>Özellik adlarının <c>MessageKey</c> ile bitmesi bilinçlidir:</b> arayüz çevirisi denetimi
    /// (MessageKeyCoverageTests) kaynakta <c>...MessageKey = "anahtar"</c> ve
    /// <c>new AppException("anahtar"</c> kalıplarını tarar. Anahtarlar ortak yardımcıya değişken
    /// olarak girdiği için <c>new AppException(</c> kalıbı burada artık literal görmez; adlandırma
    /// bu yüzden seçildi — aksi hâlde bu anahtarların çevirisi unutulsa kimse fark etmez, kullanıcı
    /// ekranda ham anahtarı görürdü.
    /// </remarks>
    private sealed class TransferErrorKeys
    {
        /// <summary>Hiç kaynak verilmediğinde.</summary>
        public required string SourceRequiredMessageKey { get; init; }

        /// <summary>Birden çok ögeye tek yeni ad verilmeye çalışıldığında.</summary>
        public required string RenameSingleOnlyMessageKey { get; init; }

        /// <summary>Hedef klasör yok olduğunda (yaratılmaz, istenir).</summary>
        public required string DestinationMissingMessageKey { get; init; }
    }
}
