using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Licensing;

/// <summary>
/// Lisans durumunu diske yazar/okur; anahtarı ŞİFRELİ saklar.
/// </summary>
/// <remarks>
/// Kalıp bilerek <c>DatabaseCredentialStore</c> ile aynı: <c>JsonFileStore</c> (atomik yazım +
/// dosya başına kilit) + <c>ISecretProtector</c> (DataProtection, anahtarları DPAPI ile korunmuş).
/// Yeni bir saklama yöntemi icat etmek, aynı sırrı iki farklı disiplinle korumak olurdu.
/// </remarks>
public sealed class LicenseStateStore(JsonFileStore fileStore, ISecretProtector secretProtector)
{
    internal const string StatePath = "config/license/state.json";

    public Task<LicenseRecord> ReadAsync(CancellationToken cancellationToken)
    {
        return fileStore.ReadAsync(StatePath, new LicenseRecord(), cancellationToken);
    }

    public Task<LicenseRecord> UpdateAsync(Func<LicenseRecord, LicenseRecord> mutate, CancellationToken cancellationToken)
    {
        return fileStore.UpdateAsync(StatePath, new LicenseRecord(), mutate, cancellationToken);
    }

    /// <summary>Anahtarı korunmuş biçimde kaydeder; son dört karakteri gösterim için ayrıca tutar.</summary>
    public string ProtectKey(string licenseKey)
    {
        return secretProtector.Protect(licenseKey);
    }

    /// <summary>
    /// Korunmuş anahtarı çözer. Çözülemezse boş döner.
    /// </summary>
    /// <remarks>
    /// Çözülememe GERÇEK bir senaryodur: veri kökündeki <c>keys</c> klasörü silinirse ya da durum
    /// dosyası başka bir makineden kopyalanırsa DPAPI çözemez. Bu durumda PATLAMAK yerine "anahtar
    /// yok" gibi davranırız; kullanıcı anahtarı yeniden girer. İstisna fırlatmak, panelin lisans
    /// ekranını tamamen açılamaz hâle getirirdi.
    /// </remarks>
    public string UnprotectKey(string protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(protectedKey);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Gösterim için son dört karakter (anahtarın kendisi asla gösterilmez).</summary>
    public static string Suffix(string licenseKey)
    {
        var trimmed = licenseKey.Trim();
        return trimmed.Length <= 4 ? new string('*', trimmed.Length) : trimmed[^4..];
    }
}
