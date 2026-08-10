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
        await File.WriteAllTextAsync(target.PhysicalPath, request.Content ?? string.Empty, Encoding.UTF8, cancellationToken);
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
        var sources = (request.Paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (sources.Length == 0)
        {
            throw new AppException("files.move_source_required", 400, 400);
        }

        // Yeniden adlandırma TEK ögeye özgüdür: sekiz dosyaya aynı adı vermek, yedisinin sessizce
        // kaybolması demek olurdu.
        if (!string.IsNullOrWhiteSpace(request.NewName) && sources.Length > 1)
        {
            throw new AppException("files.move_rename_single_only", 400, 400);
        }

        var destinationPath = NormalizeVirtualPath(request.DestinationPath);
        var destination = await ResolveTargetAsync(destinationPath, cancellationToken);
        EnsureSiteTarget(destination);
        EnsureInsideSandbox(destination);

        // Hedef klasör YARATILMAZ, var olması istenir: yazım hatası yüzünden oluşan bir klasöre
        // sekiz dosya taşımak, onları "kaybolmuş" hâle getirir.
        if (!Directory.Exists(destination.PhysicalPath))
        {
            throw new AppException("files.move_destination_missing", 404, 404);
        }

        var results = new List<MoveFileResultDto>(sources.Length);
        foreach (var rawSource in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePath = NormalizeVirtualPath(rawSource);

            // Kaynağın çözümü sanal alan + bağlantı (junction/symlink) denetimini de yapar; bir
            // bağlantıyı taşımak, hedefindeki müşteri sitesini başka bir yere bağlamak demektir.
            var source = await ResolveTargetAsync(sourcePath, cancellationToken);
            EnsureSiteTarget(source);
            EnsureNotSiteRoot(source);
            EnsureInsideSandbox(source);

            var name = string.IsNullOrWhiteSpace(request.NewName)
                ? Path.GetFileName(source.PhysicalPath)
                : EnsureSimpleName(request.NewName);

            var targetPath = CombineVirtualPath(destinationPath, name);

            // Hedef de AYNI kapıdan geçer: adı ben ürettiğim için "güvenli varsayıp" atlamak,
            // korumanın tam da yeni uçta delinmesi olurdu.
            var target = await ResolveTargetAsync(targetPath, cancellationToken);
            EnsureSiteTarget(target);
            EnsureNotSiteRoot(target);
            EnsureInsideSandbox(target);

            results.Add(MoveOne(source, target, sourcePath, targetPath, request.Overwrite));
        }

        return new MoveFilesResultDto
        {
            MovedCount = results.Count(item => item.Moved),
            FailedCount = results.Count(item => !item.Moved),
            Items = results.ToArray(),
            Workspace = await GetWorkspaceAsync(destinationPath, cancellationToken)
        };
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
}
