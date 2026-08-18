using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Enums;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Bir marka kaydının SAHİBİ. <c>ResellerId</c> null ise sunucu geneli (yöneticinin markası),
/// dolu ise o bayinin markasıdır.
/// </summary>
/// <remarks>
/// Ayrı bir tip olmasının sebebi, "hangi marka" sorusunun kod içinde çıplak bir <c>Guid?</c> ile
/// dolaşmasını engellemek: çıplak bir kimlik yanlışlıkla istekten gelen bir değerle beslenebilir.
/// Bu tipin tek üretildiği yer denetleyicideki <c>ResolveScope</c>'tur ve orası yalnızca oturumun
/// kimliğine bakar.
/// </remarks>
public readonly record struct BrandingScope(Guid? ResellerId)
{
    public static BrandingScope Server => new((Guid?)null);

    public bool IsServer => ResellerId is null;

    /// <summary>Depolama anahtarı: sunucu için "server", bayi için kimliğin "N" biçimi.</summary>
    public string StorageKey => ResellerId is { } id ? id.ToString("N") : "server";
}

/// <summary>Yüklenebilen marka görselleri.</summary>
public enum BrandingImageKind
{
    Logo = 1,
    Favicon = 2
}

/// <summary>
/// Özel marka (white-label) yönetimi: panel adı, logo, favicon, vurgu rengi.
/// </summary>
/// <remarks>
/// Plesk'in modeli birebir izlendi (araştırıldı, sonra kopyalandı):
/// <list type="bullet">
/// <item>Yönetici SUNUCU GENELİ markayı belirler; giriş ekranında görünen marka budur.</item>
/// <item>Bayi KENDİ markasını belirler; bunu yalnızca kendi müşterileri panele girdikten sonra görür.</item>
/// <item>Plesk'te de merkezi giriş sayfası bayi bazında markalanmaz — giriş anında kullanıcının
/// kim olduğu (dolayısıyla hangi bayiye ait olduğu) henüz bilinmez.</item>
/// </list>
/// </remarks>
public interface IBrandingService
{
    /// <summary>
    /// Bir izleyicinin GÖRECEĞİ markayı çözer: bayi kendi markasını, müşteri kendisini barındıran
    /// bayinin markasını, yönetici/operatör ve kimliksiz istek sunucu markasını görür.
    /// </summary>
    Task<BrandingDto> GetEffectiveAsync(Guid? userId, UserRole? role, CancellationToken cancellationToken = default);

    /// <summary>Bir kapsamın HAM kaydı (düzenleme formu için); miras/geri düşme uygulanmaz.</summary>
    Task<BrandingDto> GetScopeAsync(BrandingScope scope, CancellationToken cancellationToken = default);

    /// <summary>Metin alanlarını kaydeder ve kaydedilmiş hâlini döner.</summary>
    Task<BrandingDto> UpdateAsync(BrandingScope scope, UpdateBrandingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Görseli DOĞRULAR ve kaydeder. Doğrulama içeriğe bakar (uzantıya/istemcinin bildirdiği tipe
    /// DEĞİL); kabul edilmeyen içerik <see cref="Common.AppException"/> ile reddedilir.
    /// </summary>
    Task<BrandingDto> SetImageAsync(BrandingScope scope, BrandingImageKind kind, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Görseli kaldırır (yoksa sessizce geçer).</summary>
    Task<BrandingDto> RemoveImageAsync(BrandingScope scope, BrandingImageKind kind, CancellationToken cancellationToken = default);
}
