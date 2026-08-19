using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Cluster;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Cluster;

/// <summary>
/// Çoklu sunucu yönetiminin merkez tarafı (Plesk Multi Server'daki "management node" rolü).
/// </summary>
public interface IClusterService
{
    Task<IReadOnlyList<ClusterNodeDto>> GetNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>Tüm düğümleri PARALEL yoklar; ölü düğüm satırı düşürmez.</summary>
    Task<ClusterOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<ClusterNodeDto> RegisterAsync(RegisterClusterNodeRequest request, CancellationToken cancellationToken = default);

    Task<ClusterNodeDto> UpdateAsync(Guid id, UpdateClusterNodeRequest request, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ClusterNodeStatusDto> ProbeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>"Düğüme git" bağlantısı (oturum devri DEĞİL; bkz. <see cref="ClusterNodeLinkDto"/>).</summary>
    Task<ClusterNodeLinkDto> GetPanelLinkAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ClusterPlacementDto> GetPlacementAsync(CancellationToken cancellationToken = default);

    Task<ClusterSiteCreationDto> CreateSiteAsync(CreateClusterSiteRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ClusterService(
    ClusterNodeStore store,
    IClusterNodeClient nodeClient,
    ILogger<ClusterService> logger) : IClusterService
{
    /// <summary>Adres denetiminde kullanılan çözümleyici; testte sahte DNS ile değiştirilir.</summary>
    public ClusterHostResolver Resolver { get; init; } = ClusterAddressGuard.SystemResolver;

    public async Task<IReadOnlyList<ClusterNodeDto>> GetNodesAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await store.GetAllAsync(cancellationToken);
        return nodes.Select(node => node.ToDto()).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Paralel ve İZOLE.</b> Düğümler sırayla yoklansaydı sekiz saniyelik zaman aşımı düğüm
    /// başına toplanır, on düğümlük bir kurulumda pano seksen saniyede gelirdi. Paralel yoklamada
    /// toplam süre EN YAVAŞ düğüm kadardır.
    /// </para>
    /// <para>
    /// <b>Bir düğümün ölmesi panoyu çökertmez</b> — çünkü istemci istisna değil DEĞER döndürür
    /// (bkz. <see cref="ClusterNodeClient"/>). Yine de burada ayrıca savunma var: yoklama
    /// beklenmedik bir istisna atarsa satır "ulaşılamıyor" olur, <c>Task.WhenAll</c> patlamaz.
    /// </para>
    /// </remarks>
    public async Task<ClusterOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await store.GetAllAsync(cancellationToken);
        var rows = await Task.WhenAll(nodes.Select(node => ProbeSafeAsync(node, cancellationToken)));

        var reachable = rows.Where(row => row.Reachable).ToList();
        return new ClusterOverviewDto
        {
            Nodes = rows.OrderBy(row => row.Node.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            // Yalnız ÖLÇÜLEBİLEN düğümlerin siteleri toplanır; gerekçe ClusterOverviewDto'da.
            TotalSites = reachable.Sum(row => row.Info?.SiteCount ?? 0),
            ReachableCount = reachable.Count,
            UnreachableCount = rows.Length - reachable.Count,
            GeneratedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<ClusterNodeDto> RegisterAsync(RegisterClusterNodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var label = (request.Label ?? string.Empty).Trim();
        if (label.Length < 2)
        {
            throw new AppException(ClusterMessages.InvalidLabel, 400, 400);
        }

        var token = (request.ApiToken ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            throw new AppException(ClusterMessages.TokenRequired, 400, 400);
        }

        var address = await ValidateAddressAsync(request.Address, cancellationToken);
        await EnsureAddressIsNewAsync(address, exceptId: null, cancellationToken);

        var record = new ClusterNodeRecord
        {
            Label = label,
            Address = address,
            Enabled = request.Enabled,
            VerifyTlsCertificate = request.VerifyTlsCertificate
        };

        await store.AddAsync(record, token, cancellationToken);
        return record.ToDto();
    }

    public async Task<ClusterNodeDto> UpdateAsync(Guid id, UpdateClusterNodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await store.GetAsync(id, cancellationToken)
            ?? throw new AppException(ClusterMessages.NodeNotFound, 404, 404);

        var label = (request.Label ?? string.Empty).Trim();
        if (label.Length < 2)
        {
            throw new AppException(ClusterMessages.InvalidLabel, 400, 400);
        }

        var address = await ValidateAddressAsync(request.Address, cancellationToken);
        await EnsureAddressIsNewAsync(address, exceptId: id, cancellationToken);

        var token = (request.ApiToken ?? string.Empty).Trim();
        // Belirteç hiç girilmemişse ve kayıtta da yoksa düğüm kullanılamaz durumda kalır; bunu
        // kaydetme anında söylemek, ilk yoklamada "ulaşılamıyor" demekten dürüsttür.
        if (token.Length == 0 && string.IsNullOrWhiteSpace(existing.TokenProtected))
        {
            throw new AppException(ClusterMessages.TokenRequired, 400, 400);
        }

        var updated = await store.UpdateAsync(
            id,
            record =>
            {
                record.Label = label;
                record.Address = address;
                record.Enabled = request.Enabled;
                record.VerifyTlsCertificate = request.VerifyTlsCertificate;
            },
            token.Length == 0 ? null : token,
            cancellationToken) ?? throw new AppException(ClusterMessages.NodeNotFound, 404, 404);

        return updated.ToDto();
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await store.RemoveAsync(id, cancellationToken))
        {
            throw new AppException(ClusterMessages.NodeNotFound, 404, 404);
        }
    }

    public async Task<ClusterNodeStatusDto> ProbeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await store.GetAsync(id, cancellationToken)
            ?? throw new AppException(ClusterMessages.NodeNotFound, 404, 404);

        return await ProbeSafeAsync(record, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Adres düğümün KENDİ raporundan kurulur (gizli giriş yolu dahil): merkez o yolu tahmin
    /// edemez ve etmemelidir. Düğüme ulaşılamıyorsa bağlantı da üretilmez — yanlış bir adres
    /// vermektense "ulaşılamıyor" demek doğrudur.
    /// </remarks>
    public async Task<ClusterNodeLinkDto> GetPanelLinkAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await store.GetAsync(id, cancellationToken)
            ?? throw new AppException(ClusterMessages.NodeNotFound, 404, 404);

        var probe = await nodeClient.FetchInfoAsync(record, store.ResolveToken(record), cancellationToken);
        if (!probe.Succeeded || probe.Value is null)
        {
            throw new AppException(probe.ErrorKey, 502, 502, probe.Detail);
        }

        var securePath = probe.Value.SecureEntryPath.Trim('/');
        var url = securePath.Length > 0
            ? $"{record.Address}/{securePath}/"
            : record.Address + "/";

        return new ClusterNodeLinkDto { NodeId = record.Id, Url = url, RequiresSignIn = true };
    }

    public async Task<ClusterPlacementDto> GetPlacementAsync(CancellationToken cancellationToken = default)
    {
        var overview = await GetOverviewAsync(cancellationToken);
        return ClusterPlacement.Select(overview.Nodes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Site UZAK düğümde oluşturulur; merkez yalnız isteği taşır. Fiziksel yol düğümün KENDİ
    /// varsayılan kökünden türetilir (bkz. <see cref="ClusterNodeInfoDto.DefaultSiteRoot"/>).
    /// </para>
    /// <para>
    /// <b>Belirtecin yetkisi burada görünür:</b> düğüm belirteci salt-okur ise uzak panel POST'u
    /// 403 ile reddeder (<c>apitoken.read_only</c>). Merkez bunu yutmaz; sebep uzak panelin
    /// anahtarıyla birlikte yukarı çıkar, yönetici anahtarı yenilemesi gerektiğini görür.
    /// </para>
    /// </remarks>
    public async Task<ClusterSiteCreationDto> CreateSiteAsync(CreateClusterSiteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var siteName = (request.Name ?? string.Empty).Trim();
        if (siteName.Length < 2)
        {
            throw new AppException("sites.invalid_name", 400, 400);
        }

        var overview = await GetOverviewAsync(cancellationToken);
        var automatic = request.NodeId is null || request.NodeId == Guid.Empty;

        ClusterNodeStatusDto row;
        if (automatic)
        {
            var placement = ClusterPlacement.Select(overview.Nodes);
            if (placement.NodeId is null)
            {
                throw new AppException(ClusterMessages.NoNodeAvailable, 409, 409);
            }

            row = overview.Nodes.First(candidate => candidate.Node.Id == placement.NodeId);
        }
        else
        {
            row = overview.Nodes.FirstOrDefault(candidate => candidate.Node.Id == request.NodeId)
                ?? throw new AppException(ClusterMessages.NodeNotFound, 404, 404);

            if (!row.Node.Enabled)
            {
                throw new AppException(ClusterMessages.NodeDisabled, 409, 409);
            }

            if (!row.Reachable || row.Info is null)
            {
                throw new AppException(ClusterMessages.NodeUnreachable, 502, 502, row.Detail);
            }
        }

        var record = await store.GetAsync(row.Node.Id, cancellationToken)
            ?? throw new AppException(ClusterMessages.NodeNotFound, 404, 404);

        var hostName = (request.HostName ?? string.Empty).Trim();
        var payload = new
        {
            name = siteName,
            physicalPath = BuildRemotePath(row.Info!.DefaultSiteRoot, siteName),
            hostName,
            protocol = "http",
            port = 80,
            runtimeProfile = "auto"
        };

        var result = await nodeClient.PostAsync(record, store.ResolveToken(record), "api/v1/sites", payload, cancellationToken);
        if (!result.Succeeded)
        {
            throw new AppException(result.ErrorKey, 502, 502, result.Detail);
        }

        logger.LogInformation("Site '{Site}' uzak düğümde oluşturuldu: {Label}.", siteName, record.Label);

        return new ClusterSiteCreationDto
        {
            NodeId = record.Id,
            NodeLabel = record.Label,
            SiteName = siteName,
            PlacementWasAutomatic = automatic,
            RemoteMessage = ReadRemoteSiteName(result.Value) is { Length: > 0 } remote ? remote : siteName
        };
    }

    /// <summary>
    /// Uzak düğümdeki fiziksel yolu kurar. Kök boşsa panelin kendi varsayılanına düşülür.
    /// </summary>
    /// <remarks>
    /// Ayrı ve saf: yol birleştirme Windows'ta sessizce yanlış yapılabilen bir iştir (çift ters
    /// bölü, sondaki ayraç) ve sonucu bir dizin oluşturma çağrısına gider.
    /// </remarks>
    public static string BuildRemotePath(string? defaultSiteRoot, string siteName)
    {
        var root = (defaultSiteRoot ?? string.Empty).Trim().TrimEnd('\\', '/');
        if (root.Length == 0)
        {
            root = @"C:\inetpub\wwwroot";
        }

        return root + "\\" + siteName.Trim().Trim('\\', '/');
    }

    private static string ReadRemoteSiteName(JsonElement value)
    {
        try
        {
            // SiteCreationResultDto'nun şekli uzak panelin sürümüne bağlı; adı bulamazsak sessizce
            // geçeriz (isteğin BAŞARISI zaten zarf üzerinden ölçüldü).
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("site", out var site) &&
                site.ValueKind == JsonValueKind.Object &&
                site.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
            {
                return name.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Biçim beklenmedik: sonuç yine de başarılı, ad merkezden yazılır.
        }

        return string.Empty;
    }

    /// <summary>
    /// Adresi ÜÇ katmanın ilk ikisinden geçirir (biçim + DNS). Üçüncüsü bağlantı anındadır.
    /// </summary>
    private async Task<string> ValidateAddressAsync(string? raw, CancellationToken cancellationToken)
    {
        if (!ClusterAddressGuard.TryNormalize(raw, out var normalized, out var errorKey))
        {
            // DURUM KODU KATMANA GORE DEGISMEZ, SEBEBE GORE DEGISIR: "bicimi bozuk" 400,
            // "hedef yasak" 403. Aksi halde aynı reddin kodu, engelin BICIM katmanında mı yoksa
            // DNS katmanında mı yakalandığına göre değişir ve dışarıdan bakan (denetim kaydı,
            // entegrasyon) iki farklı olay görürdü.
            var forbidden = errorKey == ClusterMessages.AddressForbidden;
            if (forbidden)
            {
                logger.LogWarning("Düğüm adresi REDDEDİLDİ (biçim katmanı): {Address}.", raw);
            }

            throw new AppException(errorKey, forbidden ? 403 : 400, forbidden ? 403 : 400);
        }

        var host = new Uri(normalized).DnsSafeHost;
        var blocked = await ClusterAddressGuard.FindBlockedAddressAsync(host, Resolver, cancellationToken);
        if (blocked is not null)
        {
            logger.LogWarning("Düğüm adresi REDDEDİLDİ: {Address} -> {Blocked}.", normalized, blocked);
            throw new AppException(ClusterMessages.AddressForbidden, 403, 403);
        }

        return normalized;
    }

    /// <summary>
    /// Aynı adres iki kez kaydedilemez.
    /// </summary>
    /// <remarks>
    /// Yalnız düzen kaygısı değil: aynı sunucu iki kez kayıtlıysa site sayısı iki kez toplanır ve
    /// yerleşim kararı, aslında dolu olan bir düğümü boş sanabilir.
    /// </remarks>
    private async Task EnsureAddressIsNewAsync(string address, Guid? exceptId, CancellationToken cancellationToken)
    {
        var nodes = await store.GetAllAsync(cancellationToken);
        if (nodes.Any(node => node.Id != exceptId && string.Equals(node.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException(ClusterMessages.DuplicateAddress, 409, 409);
        }
    }

    /// <summary>Tek düğümün yoklaması. Kapalı düğüm hiç yoklanmaz; hata satıra çevrilir.</summary>
    private async Task<ClusterNodeStatusDto> ProbeSafeAsync(ClusterNodeRecord record, CancellationToken cancellationToken)
    {
        if (!record.Enabled)
        {
            return new ClusterNodeStatusDto
            {
                Node = record.ToDto(),
                Reachable = false,
                StatusKey = ClusterMessages.NodeDisabled
            };
        }

        try
        {
            var token = store.ResolveToken(record);
            var probe = await nodeClient.FetchInfoAsync(record, token, cancellationToken);

            await store.RecordProbeAsync(
                record.Id,
                probe.Succeeded,
                probe.Succeeded ? string.Empty : $"{probe.ErrorKey}:{probe.Detail}",
                cancellationToken);

            return new ClusterNodeStatusDto
            {
                Node = record.ToDto(),
                Reachable = probe.Succeeded && probe.Value is not null,
                StatusKey = probe.ErrorKey,
                Detail = probe.Detail,
                DurationMs = probe.DurationMs,
                Info = probe.Value
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // SON SAVUNMA. İstemci zaten hata döndürüyor; buraya düşen bir şey olursa (ör. belirteç
            // çözme) yine de tek satır bozulur, pano ayakta kalır.
            logger.LogWarning(exception, "Düğüm yoklaması beklenmedik biçimde başarısız: {Label}.", record.Label);
            return new ClusterNodeStatusDto
            {
                Node = record.ToDto(),
                Reachable = false,
                StatusKey = ClusterMessages.NodeUnreachable,
                Detail = exception.GetType().Name
            };
        }
    }
}
