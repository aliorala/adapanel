using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.Services;

namespace AdaPanel.Infrastructure.Branding;

/// <summary>
/// Özel marka (white-label) deposu ve çözümleyicisi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Depo:</b> metinler <c>config/branding.json</c>, görseller <c>branding/&lt;kapsam&gt;/…</c>
/// altında — ikisi de panelin VERİ KÖKÜNDE (<c>%ProgramData%\AdaPanel</c>). Yüklenen dosya
/// bilerek buraya konur: panelin <c>wwwroot</c>'u, IIS site kökleri ve uygulama dizinleri gibi
/// bir isteğin URL ile ULAŞABİLECEĞİ hiçbir yola düşmez. Dosyayı yalnızca panel süreci okur ve
/// dışarıya her zaman doğrulanmış içerik tipiyle, base64 olarak verir; yani yüklenen içerik
/// hiçbir koşulda "sunulan bir dosya" hâline gelmez. Veri kökünün erişim hakları kurulumda
/// zaten daraltılmıştır (panel hizmeti + yöneticiler).
/// </para>
/// <para>
/// <b>Kapsam:</b> <see cref="BrandingScope"/> bu servise DIŞARIDAN verilir ve tek üreticisi
/// denetleyicideki kimlik çözümüdür. Servis, kapsamın "kime ait olduğunu" sorgulamaz; sorgulasaydı
/// aynı denetim iki yerde yaşar ve biri güncellenmeyi unutabilirdi.
/// </para>
/// </remarks>
public sealed class BrandingService(
    JsonFileStore fileStore,
    ICustomerScopeService customerScopes) : IBrandingService
{
    private const string StatePath = "config/branding.json";
    private const string ImageRoot = "branding";
    internal Func<CancellationToken, Task>? BeforeImageStateWrite { get; init; }
    private readonly ConcurrencyLock scopeLocks = new();
    private Task<T> InScopeAsync<T>(BrandingScope scope, Func<Task<T>> action, CancellationToken token) =>
        scopeLocks.RunExclusiveAsync("branding:" + fileStore.ResolvePath(StatePath).ToUpperInvariant() + ":" + scope.StorageKey, action, token);

    public async Task<BrandingDto> GetEffectiveAsync(Guid? userId, UserRole? role, CancellationToken cancellationToken = default)
    {
        var scope = await ResolveBrandOwnerAsync(userId, role, cancellationToken);
        if (!scope.IsServer)
        {
            var reseller = await ReadAsync(scope, cancellationToken);

            // BAYİNİN EKSİK ALANI SUNUCUNUNKİYLE TAMAMLANMAZ. Beyaz etiketin bütün noktası,
            // müşterinin sağlayıcıyı GÖRMEMESİ: logosunu koymamış bir bayinin müşterisine
            // sağlayıcının logosunu göstermek, tam da gizlenmesi istenen şeyi göstermek olurdu.
            // Bayi bir kez markalandıysa boş alanları panelin NÖTR varsayılanına düşer.
            if (HasContent(reseller))
            {
                reseller.Scope = "reseller";
                return reseller;
            }
        }

        var server = await ReadAsync(BrandingScope.Server, cancellationToken);
        server.Scope = "server";
        return server;
    }

    public async Task<BrandingDto> GetScopeAsync(BrandingScope scope, CancellationToken cancellationToken = default)
    {
        var brand = await ReadAsync(scope, cancellationToken);
        brand.Scope = scope.IsServer ? "server" : "reseller";
        return brand;
    }

    public async Task<BrandingDto> UpdateAsync(BrandingScope scope, UpdateBrandingRequest request, CancellationToken cancellationToken = default)
    {
        // Doğrulama YAZMADAN ÖNCE ve hepsi birden: bir alan reddedilirse hiçbiri kaydedilmez,
        // yarı uygulanmış marka diye bir durum oluşmaz.
        var panelName = BrandingTextRules.NormalizePanelName(request.PanelName);
        var accentColor = BrandingTextRules.NormalizeAccentColor(request.AccentColor);
        var logoLinkUrl = BrandingTextRules.NormalizeLinkUrl(request.LogoLinkUrl);

        return await InScopeAsync(scope, async () =>
        {
            await MutateAsync(scope, entry =>
            {
                entry.PanelName = panelName;
                entry.AccentColor = accentColor;
                entry.LogoLinkUrl = logoLinkUrl;
            }, cancellationToken);
            // The index is committed: read back the accepted result while retaining this scope's lease.
            return await ReadCoreAsync(scope, CancellationToken.None);
        }, cancellationToken);
    }

    public async Task<BrandingDto> SetImageAsync(BrandingScope scope, BrandingImageKind kind, byte[] content, CancellationToken cancellationToken = default)
    {
        // Freeze the bytes validated before any asynchronous lease wait.
        var bytes = content.ToArray();
        var format = BrandingImageRules.Validate(kind, bytes);
        return await InScopeAsync(scope, async () =>
        {
            var previous = (await ReadEntryAsync(scope, cancellationToken)).FileName(kind);
            var directory = fileStore.EnsureDirectory($"{ImageRoot}/{scope.StorageKey}");
            // Immutable versions never truncate a file still referenced by the published index.
            var fileName = (kind == BrandingImageKind.Logo ? "logo-" : "favicon-") + Guid.NewGuid().ToString("N") + format.FileExtension;
            var target = Path.Combine(directory, fileName);
            var committed = false;
            try
            {
                await File.WriteAllBytesAsync(target, bytes, cancellationToken);
                if (BeforeImageStateWrite is not null) await BeforeImageStateWrite(cancellationToken);
                await MutateAsync(scope, entry =>
                {
                    if (kind == BrandingImageKind.Logo)
                    {
                        entry.LogoFileName = fileName;
                        entry.LogoMediaType = format.MediaType;
                    }
                    else
                    {
                        entry.FaviconFileName = fileName;
                        entry.FaviconMediaType = format.MediaType;
                    }
                }, cancellationToken);
                committed = true;
                // Only an accepted index publication makes the old version unreferenced.
                if (previous.Length > 0) DeleteImageFile(directory, previous);
                return await ReadCoreAsync(scope, CancellationToken.None);
            }
            finally
            {
                if (!committed) DeleteImageFile(directory, fileName);
            }
        }, cancellationToken);
    }

    public async Task<BrandingDto> RemoveImageAsync(BrandingScope scope, BrandingImageKind kind, CancellationToken cancellationToken = default)
        => await InScopeAsync(scope, async () =>
        {
            var entry = await ReadEntryAsync(scope, cancellationToken);
            var fileName = entry.FileName(kind);
            if (BeforeImageStateWrite is not null) await BeforeImageStateWrite(cancellationToken);
            await MutateAsync(scope, current =>
            {
                if (kind == BrandingImageKind.Logo)
                {
                    current.LogoFileName = string.Empty;
                    current.LogoMediaType = string.Empty;
                }
                else
                {
                    current.FaviconFileName = string.Empty;
                    current.FaviconMediaType = string.Empty;
                }
            }, cancellationToken);
            if (fileName.Length > 0)
                DeleteImageFile(fileStore.ResolvePath($"{ImageRoot}/{scope.StorageKey}"), fileName);
            return await ReadCoreAsync(scope, CancellationToken.None);
        }, cancellationToken);

    /// <summary>
    /// "Bu kullanıcı KİMİN markasını görür" sorusunun tek cevabı.
    /// </summary>
    /// <remarks>
    /// Bayi kendi markasını (yaptığı işi kendi ekranında da görmeli), müşteri KENDİSİNİ BARINDIRAN
    /// bayinin markasını görür. Alt kullanıcının (abonelik altı ek kullanıcı) kendi sahiplik kaydı
    /// yoktur; bağlı olduğu asıl müşteriye, oradan da onun bayisine gidilir — aksi hâlde alt
    /// kullanıcı, ana kullanıcıdan FARKLI bir marka görürdü.
    /// </remarks>
    private async Task<BrandingScope> ResolveBrandOwnerAsync(Guid? userId, UserRole? role, CancellationToken cancellationToken)
    {
        if (userId is not { } id || role is null)
        {
            return BrandingScope.Server;
        }

        if (role == UserRole.Reseller)
        {
            return new BrandingScope(id);
        }

        if (role != UserRole.Customer)
        {
            // Yönetici ve operatör sunucunun kendi markasını görür: panelin sahibi onlardır.
            return BrandingScope.Server;
        }

        var subscriptionUserId = await customerScopes.GetParentSubscriptionAsync(id, cancellationToken) ?? id;
        var owner = await customerScopes.GetOwnerAsync(subscriptionUserId, cancellationToken);
        return owner is { } resellerId ? new BrandingScope(resellerId) : BrandingScope.Server;
    }

    private static bool HasContent(BrandingDto brand) =>
        brand.PanelName.Length > 0 ||
        brand.AccentColor.Length > 0 ||
        brand.LogoDataUrl.Length > 0 ||
        brand.FaviconDataUrl.Length > 0;

    private Task<BrandingDto> ReadAsync(BrandingScope scope, CancellationToken cancellationToken) =>
        InScopeAsync(scope, () => ReadCoreAsync(scope, cancellationToken), cancellationToken);

    private async Task<BrandingDto> ReadCoreAsync(BrandingScope scope, CancellationToken cancellationToken)
    {
        var entry = await ReadEntryAsync(scope, cancellationToken);
        return new BrandingDto
        {
            PanelName = entry.PanelName,
            AccentColor = entry.AccentColor,
            LogoLinkUrl = entry.LogoLinkUrl,
            LogoDataUrl = await ReadImageAsync(scope, entry.LogoFileName, entry.LogoMediaType, cancellationToken),
            FaviconDataUrl = await ReadImageAsync(scope, entry.FaviconFileName, entry.FaviconMediaType, cancellationToken),
            Scope = scope.IsServer ? "server" : "reseller"
        };
    }

    private async Task<string> ReadImageAsync(BrandingScope scope, string fileName, string mediaType, CancellationToken cancellationToken)
    {
        if (fileName.Length == 0 || mediaType.Length == 0)
        {
            return string.Empty;
        }

        // Dosya adı KAYITTAN gelir, istekten değil; yine de yol birleştirmesi klasör dışına
        // çıkamasın diye yalnız dosya adı kısmı kullanılır (kayıt bir şekilde bozulmuş olabilir).
        var safeName = Path.GetFileName(fileName);
        var path = Path.Combine(fileStore.ResolvePath($"{ImageRoot}/{scope.StorageKey}"), safeName);
        if (!File.Exists(path))
        {
            // Dosya elle silinmişse marka metinleri çalışmaya devam etmeli; görselsiz döneriz.
            return string.Empty;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static void DeleteImageFile(string directory, string fileName)
    {
        try
        {
            var path = Path.Combine(directory, Path.GetFileName(fileName));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Dosya kilitliyse kayıt yine de güncellenir: kullanıcı açısından marka kaldırılmıştır,
            // artıkta kalan bayt hiçbir yerden servis edilmiyor.
        }
    }

    private async Task<BrandingEntry> ReadEntryAsync(BrandingScope scope, CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync<Dictionary<string, BrandingEntry>>(StatePath, [], cancellationToken);
        return new Dictionary<string, BrandingEntry>(state, StringComparer.OrdinalIgnoreCase)
            .TryGetValue(scope.StorageKey, out var entry) ? entry : new BrandingEntry();
    }

    private Task MutateAsync(BrandingScope scope, Action<BrandingEntry> mutate, CancellationToken cancellationToken)
        => fileStore.UpdateAsync(StatePath, NewState(), existing =>
        {
            var state = new Dictionary<string, BrandingEntry>(existing, StringComparer.OrdinalIgnoreCase);
            if (!state.TryGetValue(scope.StorageKey, out var entry))
            {
                entry = new BrandingEntry();
            }

            mutate(entry);

            // Tamamen boşalan kayıt SİLİNİR: "kaydı var ama hepsi boş" hâli, etkin marka çözümünde
            // bayiyi markalanmış saydırıp sunucu markasına düşmeyi engellerdi.
            if (entry.IsEmpty)
            {
                state.Remove(scope.StorageKey);
            }
            else
            {
                state[scope.StorageKey] = entry;
            }

            return state;
        }, cancellationToken);

    private static Dictionary<string, BrandingEntry> NewState() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Diske yazılan kayıt. Görselin KENDİSİ değil, dosya adı ve doğrulanmış tipi durur.</summary>
    private sealed class BrandingEntry
    {
        public string PanelName { get; set; } = string.Empty;

        public string AccentColor { get; set; } = string.Empty;

        public string LogoLinkUrl { get; set; } = string.Empty;

        public string LogoFileName { get; set; } = string.Empty;

        /// <summary>Yüklemede DOĞRULANMIŞ içerik tipi. İstemcinin bildirdiği tip hiçbir zaman saklanmaz.</summary>
        public string LogoMediaType { get; set; } = string.Empty;

        public string FaviconFileName { get; set; } = string.Empty;

        public string FaviconMediaType { get; set; } = string.Empty;

        public string FileName(BrandingImageKind kind) =>
            kind == BrandingImageKind.Logo ? LogoFileName : FaviconFileName;

        public bool IsEmpty =>
            PanelName.Length == 0 &&
            AccentColor.Length == 0 &&
            LogoLinkUrl.Length == 0 &&
            LogoFileName.Length == 0 &&
            FaviconFileName.Length == 0;
    }
}
