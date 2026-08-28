using System.Text;
using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Files;

public sealed class FileService(IIisSiteService iisSiteService) : IFileService
{
    public async Task<FileWorkspaceDto> GetWorkspaceAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeVirtualPath(path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        if (target.IsVirtualRoot)
        {
            return await BuildVirtualRootWorkspaceAsync(normalizedPath, cancellationToken);
        }

        if (File.Exists(target.PhysicalPath))
        {
            return new FileWorkspaceDto
            {
                Path = normalizedPath,
                ParentPath = BuildParentPath(normalizedPath),
                DisplayName = Path.GetFileName(target.PhysicalPath),
                PhysicalPath = target.PhysicalPath,
                IsDirectory = false,
                Content = await ReadFileContentAsync(target.PhysicalPath, cancellationToken)
            };
        }

        Directory.CreateDirectory(target.PhysicalPath);
        var directoryInfo = new DirectoryInfo(target.PhysicalPath);
        return new FileWorkspaceDto
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
    }

    public async Task<FileWorkspaceDto> WriteAsync(WriteFileRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeVirtualPath(request.Path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target.PhysicalPath) ?? target.SandboxRoot);

        if (request.FailIfExists)
        {
            // VARLIK DENETİMİ AYRI BİR ADIM DEĞİL: `File.Exists` ile bakıp sonra yazmak, iki adım
            // arasında dosyayı oluşturan ikinci bir istek için AÇIK bir yarış bırakır ve o
            // istemcinin dosyasını sıfırlar. `FileMode.CreateNew` bakmayı ve yaratmayı işletim
            // sisteminde TEK işleme indirir: dosya varsa yaratma hiç olmaz.
            //
            // Klasör de burada yakalanır: aynı adlı bir KLASÖR varsa `CreateNew` yine hata verir
            // ve kullanıcı "zaten var" cevabı alır — sessizce başarısız olmaz.
            try
            {
                await using var akis = new FileStream(
                    target.PhysicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var yazici = new StreamWriter(akis, Encoding.UTF8);
                await yazici.WriteAsync((request.Content ?? string.Empty).AsMemory(), cancellationToken);
            }
            catch (IOException) when (File.Exists(target.PhysicalPath))
            {
                // IOException'ın TEK sebebi "zaten var" değildir (disk dolu, dosya kilitli...).
                // Süzgeç bu yüzden koşullu: yalnızca gerçekten varsa "zaten var" denir, diğer
                // hatalar olduğu gibi yukarı çıkar ve yanlış bir sebeple maskelenmez.
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
        }
        else
        {
            await File.WriteAllTextAsync(target.PhysicalPath, request.Content ?? string.Empty, Encoding.UTF8, cancellationToken);
        }

        return await GetWorkspaceAsync(normalizedPath, cancellationToken);
    }

    public async Task<FileWorkspaceDto> DeleteAsync(DeleteFileRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeVirtualPath(request.Path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);
        if (File.Exists(target.PhysicalPath))
        {
            File.Delete(target.PhysicalPath);
        }
        else if (Directory.Exists(target.PhysicalPath))
        {
            Directory.Delete(target.PhysicalPath, recursive: true);
        }

        return await GetWorkspaceAsync(BuildParentPath(normalizedPath), cancellationToken);
    }

    public async Task<FileWorkspaceDto> CreateDirectoryAsync(CreateDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeVirtualPath(request.Path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);
        Directory.CreateDirectory(target.PhysicalPath);
        return await GetWorkspaceAsync(normalizedPath, cancellationToken);
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
    public async Task<MoveFilesResultDto> MoveAsync(MoveFilesRequest request, CancellationToken cancellationToken = default)
    {
        // GÜVENLİK ÖN HAZIRLIĞI TEK KAYNAKTAN gelir; taşıma ile kopyalama arasında farkı olan tek
        // şey ÖGE BAŞINA yapılan iştir (aşağıdaki MoveOne).
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
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(MoveOne(item.Source, item.Target, item.SourcePath, item.TargetPath, request.Overwrite));
        }

        return new MoveFilesResultDto
        {
            MovedCount = results.Count(item => item.Moved),
            FailedCount = results.Count(item => !item.Moved),
            Items = results.ToArray(),
            Workspace = await GetWorkspaceAsync(plan.DestinationPath, cancellationToken)
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// İstek biçimi/güvenlik hatası ile öge başına hata ayrımı <see cref="MoveAsync"/> ile aynıdır;
    /// gerekçesi orada yazılıdır ve burada tekrar edilmez.
    /// </para>
    /// </remarks>
    public async Task<CopyFilesResultDto> CopyAsync(CopyFilesRequest request, CancellationToken cancellationToken = default)
    {
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

        var results = new List<CopyFileResultDto>(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(CopyOne(item.Source, item.Target, item.SourcePath, item.TargetPath, request.Overwrite, cancellationToken));
        }

        return new CopyFilesResultDto
        {
            CopiedCount = results.Count(item => item.Copied),
            FailedCount = results.Count(item => !item.Copied),
            Items = results.ToArray(),
            Workspace = await GetWorkspaceAsync(plan.DestinationPath, cancellationToken)
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

        return new TransferPlan(normalizedDestination, items);
    }

    /// <summary>Tek ögeyi taşır; başarısızlığı İSTİSNA olarak değil, rapor satırı olarak döner.</summary>
    private static MoveFileResultDto MoveOne(
        ResolvedTarget source,
        ResolvedTarget target,
        string sourcePath,
        string targetPath,
        bool overwrite)
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

            File.Delete(target.PhysicalPath);
        }

        try
        {
            if (kaynakKlasor)
            {
                Directory.Move(source.PhysicalPath, target.PhysicalPath);
            }
            else
            {
                File.Move(source.PhysicalPath, target.PhysicalPath);
            }
        }
        catch (IOException)
        {
            // En sık sebebi: klasörün BAŞKA BİR BİRİME taşınması (Directory.Move bunu yapamaz) ya da
            // dosyanın kullanımda olması. Yalanlamak yerine bu ögenin taşınmadığı raporlanır.
            return basarisiz("files.move_failed");
        }
        catch (UnauthorizedAccessException)
        {
            return basarisiz("files.move_denied");
        }

        return new MoveFileResultDto
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Moved = true
        };
    }

    /// <summary>Tek ögeyi kopyalar; başarısızlığı İSTİSNA olarak değil, rapor satırı olarak döner.</summary>
    /// <remarks>
    /// Kural listesi <see cref="MoveOne"/> ile aynıdır ve gerekçeleri de aynıdır; burada yalnızca
    /// KOPYALAMAYA ÖZGÜ olan kısımlar ayrıca açıklanır.
    /// </remarks>
    private static CopyFileResultDto CopyOne(
        ResolvedTarget source,
        ResolvedTarget target,
        string sourcePath,
        string targetPath,
        bool overwrite,
        CancellationToken cancellationToken)
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

        var baglantiAtlandi = false;
        try
        {
            if (kaynakKlasor)
            {
                baglantiAtlandi = KlasoruKopyala(source.PhysicalPath, target.PhysicalPath, cancellationToken);
            }
            else
            {
                // overwrite yalnız yukarıdaki kapıdan geçmiş olabilir; hedef yoksa bayrağın etkisi yok.
                File.Copy(source.PhysicalPath, target.PhysicalPath, overwrite);
            }
        }
        catch (IOException)
        {
            // En sık sebebi: disk dolu, dosya kullanımda ya da yol sınırının aşılması. Yalanlamak
            // yerine bu ögenin kopyalanmadığı raporlanır.
            return basarisiz("files.copy_failed");
        }
        catch (UnauthorizedAccessException)
        {
            return basarisiz("files.copy_denied");
        }

        return new CopyFileResultDto
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Copied = true,

            // Kopya OLUŞTU ama EKSİK: atlanan bağlantı susulmaz. "Tamamlandı" deyip bağlantıyı
            // yutmak, kullanıcının eksik kopyayı tam sanmasına yol açardı.
            ErrorKey = baglantiAtlandi ? "files.copy_link_skipped" : string.Empty
        };
    }

    /// <summary>
    /// Klasörü ÖZYİNELEMELİ kopyalar. Yolu üzerindeki bağlantı (junction/symlink) ögeleri ATLANIR;
    /// en az bir tanesi atlandıysa <c>true</c> döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bağlantı neden TAKİP EDİLMEZ:</b> iki ayrı felaket birden. (1) Bağlantı kendi üst ağacını
    /// gösteriyorsa özyineleme SONSUZ döngüye girer ve diski doldurur. (2) Bağlantı sanal alanın
    /// DIŞINI gösteriyorsa (panelin veri klasörü, başka kiracının sitesi, <c>C:\Windows</c>) o
    /// içerik olduğu gibi kiracının okuyabildiği bir klasöre KOPYALANIR — sanal alan denetimini yol
    /// çözümlemesinde tuttuğumuz hâlde, özyinelemenin içinden delinmiş olurdu.
    /// </para>
    /// <para>
    /// Aynı karar listelemede (<see cref="GetWorkspaceAsync"/>) ve aramada
    /// (<c>EnumerateSafeFiles</c>) de verilmiştir: bağlantılar panelin dosya görüşünde YOKTUR.
    /// </para>
    /// </remarks>
    private static bool KlasoruKopyala(string kaynak, string hedef, CancellationToken cancellationToken)
    {
        var baglantiAtlandi = false;
        Directory.CreateDirectory(hedef);

        foreach (var oge in new DirectoryInfo(kaynak).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (oge.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                baglantiAtlandi = true;
                continue;
            }

            var altHedef = Path.Combine(hedef, oge.Name);
            if (oge is DirectoryInfo altKlasor)
            {
                baglantiAtlandi |= KlasoruKopyala(altKlasor.FullName, altHedef, cancellationToken);
            }
            else
            {
                // Hedef klasör bu çağrıda yaratıldı; üzerine yazılacak bir şey yok, bayrak yalnızca
                // aynı adın iki kez gelmesi gibi imkânsız duruma karşı.
                File.Copy(oge.FullName, altHedef, overwrite: true);
            }
        }

        return baglantiAtlandi;
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
    public async Task<FileWorkspaceDto> SetPermissionsAsync(SetFilePermissionsRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeVirtualPath(request.Path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        // Site kökünün TAMAMINA /T ile izin uygulamak diğer işlemlerde de yasak; burada eksikti.
        EnsureNotSiteRoot(target);

        // Doğrulama TEK KAYNAKTAN gelir (bkz. AclInputValidator): aynı kural site izolasyonunda da
        // geçerli ve kopyalanmış bir kural, birini sıkılaştırıp diğerini unutmanın yoludur.
        var principal = AclInputValidator.EnsurePrincipal(request.Principal);
        var rights = AclInputValidator.EnsureRights(request.Rights);

        var result = await ProcessRunner.RunAsync(
            "icacls.exe",
            $"\"{target.PhysicalPath}\" /grant \"{principal}:{rights}\" /T /C",
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            throw new AppException("files.acl_failed", 500, 500, result.StandardError);
        }

        return await GetWorkspaceAsync(normalizedPath, cancellationToken);
    }

    public async Task<string> SaveUploadAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeVirtualPath(path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        EnsureNotSiteRoot(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target.PhysicalPath) ?? target.SandboxRoot);
        await using var destination = File.Create(target.PhysicalPath);
        await stream.CopyToAsync(destination, cancellationToken);
        return normalizedPath;
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

    public async Task<FileWorkspaceDto> CompressAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default)
    {
        var sourcePath = NormalizeVirtualPath(request.Path);
        var source = await ResolveTargetAsync(sourcePath, cancellationToken);
        EnsureSiteTarget(source);
        EnsureNotSiteRoot(source);
        if (!File.Exists(source.PhysicalPath) && !Directory.Exists(source.PhysicalPath))
        {
            throw new AppException("files.not_found", 404, 404);
        }

        var destinationPath = string.IsNullOrWhiteSpace(request.DestinationPath)
            ? sourcePath.TrimEnd('/') + ".zip"
            : NormalizeVirtualPath(request.DestinationPath);
        var destination = await ResolveTargetAsync(destinationPath, cancellationToken);
        EnsureSiteTarget(destination);
        EnsureNotSiteRoot(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination.PhysicalPath) ?? destination.SandboxRoot);

        if (File.Exists(destination.PhysicalPath))
        {
            File.Delete(destination.PhysicalPath);
        }

        if (Directory.Exists(source.PhysicalPath))
        {
            ZipFile.CreateFromDirectory(source.PhysicalPath, destination.PhysicalPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        else
        {
            using var archive = ZipFile.Open(destination.PhysicalPath, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(source.PhysicalPath, Path.GetFileName(source.PhysicalPath), CompressionLevel.Optimal);
        }

        return await GetWorkspaceAsync(BuildParentPath(destinationPath), cancellationToken);
    }

    public async Task<FileWorkspaceDto> ExtractAsync(ArchiveFileRequest request, CancellationToken cancellationToken = default)
    {
        var archivePath = NormalizeVirtualPath(request.Path);
        var archive = await ResolveTargetAsync(archivePath, cancellationToken);
        EnsureSiteTarget(archive);
        EnsureNotSiteRoot(archive);
        if (!File.Exists(archive.PhysicalPath) || !string.Equals(Path.GetExtension(archive.PhysicalPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("files.invalid_archive", 400, 400);
        }

        var destinationPath = string.IsNullOrWhiteSpace(request.DestinationPath)
            ? BuildParentPath(archivePath)
            : NormalizeVirtualPath(request.DestinationPath);
        var destination = await ResolveTargetAsync(destinationPath, cancellationToken);
        EnsureSiteTarget(destination);
        Directory.CreateDirectory(destination.PhysicalPath);

        using var zip = ZipFile.OpenRead(archive.PhysicalPath);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = Path.GetFullPath(Path.Combine(destination.PhysicalPath, entry.FullName));
            if (!outputPath.StartsWith(
                    destination.PhysicalPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException("files.path_outside_root", 400, 400);
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            entry.ExtractToFile(outputPath, overwrite: true);
        }

        return await GetWorkspaceAsync(destinationPath, cancellationToken);
    }

    public async Task<IReadOnlyCollection<FileEntryDto>> SearchAsync(SearchFilesRequest request, CancellationToken cancellationToken = default)
    {
        var query = request.Query.Trim();
        if (query.Length < 2)
        {
            throw new AppException("files.search_query_short", 400, 400);
        }

        var normalizedPath = NormalizeVirtualPath(request.Path);
        var target = await ResolveTargetAsync(normalizedPath, cancellationToken);
        EnsureSiteTarget(target);
        if (!Directory.Exists(target.PhysicalPath))
        {
            throw new AppException("files.not_found", 404, 404);
        }

        var results = new List<FileEntryDto>();
        foreach (var path in EnumerateSafeFiles(target.PhysicalPath).Take(5000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            var nameMatch = file.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            var contentMatch = false;
            if (!nameMatch && file.Length <= 2 * 1024 * 1024 && IsSearchableExtension(file.Extension))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(path, cancellationToken);
                    contentMatch = content.Contains(query, StringComparison.OrdinalIgnoreCase);
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

        return results;
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
        var subPath = string.Join(Path.DirectorySeparatorChar, segments.Skip(2));
        return ResolveSandboxTarget(Path.Combine(basePath, subPath), basePath);
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

    private static void EnsureNoReparsePoints(string sandboxRoot, string fullPath)
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

    private static async Task<string> ReadFileContentAsync(string path, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > 1024 * 1024)
        {
            return "File is larger than 1 MB and was not loaded into the editor.";
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
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

    private readonly record struct ResolvedTarget(string PhysicalPath, string SandboxRoot, bool IsVirtualRoot);

    /// <summary>Aktarımın DOĞRULANMIŞ tek ögesi: çözülmüş kaynak/hedef + kullanıcıya dönecek sanal yollar.</summary>
    private readonly record struct TransferItem(
        ResolvedTarget Source,
        ResolvedTarget Target,
        string SourcePath,
        string TargetPath);

    /// <summary>Doğrulanmış aktarım planı; hiçbir ögesi dosya sistemine dokunmadan önce hazırdır.</summary>
    private readonly record struct TransferPlan(string DestinationPath, IReadOnlyList<TransferItem> Items);

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
