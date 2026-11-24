using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private readonly Services.ConcurrencyLock lifecycle = new();

    // Verification includes a network wait. Serialize the whole decision with
    // activation/deactivation so a late verdict cannot overwrite a newer key.
    internal Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken token) =>
        lifecycle.RunExclusiveAsync("license:" + fileStore.ResolvePath(StatePath).ToUpperInvariant(), action, token);

    internal const string SealKeyPath = "config/license/seal.key";
    private readonly object sealKeyLock = new();

    public async Task<LicenseRecord> ReadAsync(CancellationToken cancellationToken)
    {
        var record = await fileStore.ReadAsync(StatePath, new LicenseRecord(), cancellationToken);
        return VerifySeal(record);
    }

    public Task<LicenseRecord> UpdateAsync(Func<LicenseRecord, LicenseRecord> mutate, CancellationToken cancellationToken)
    {
        // Okunan kayit once muhurden gecer (oynanmissa karar alanlari sifirlanir), degisiklikten sonra
        // yeniden muhurlenir; boylece disaridan duzenleme bir sonraki yazimda "mesru" hale gelemez.
        return fileStore.UpdateAsync(StatePath, new LicenseRecord(), existing => Seal(mutate(VerifySeal(existing))), cancellationToken);
    }

    /// <summary>
    /// Yerel butunluk: muhur eslesmiyorsa KARAR alanlari (hukum, tarih, SKU, limit, bitis) sifirlanir;
    /// anahtar korunur, sunucuya yeniden sorulur. Eski (muhursuz) dosya, muhur anahtari DA yokken
    /// kabul edilir (gecis). Muhur anahtari varken muhursuz dosya, oynanmis sayilir.
    /// </summary>
    internal LicenseRecord VerifySeal(LicenseRecord record)
    {
        var (key, created) = GetSealKey();
        if (string.IsNullOrEmpty(record.Seal))
        {
            if (created || string.IsNullOrEmpty(record.ProtectedKey))
            {
                return record;
            }
        }
        else if (string.Equals(record.Seal, ComputeSeal(key, record), StringComparison.Ordinal))
        {
            return record;
        }

        record.LastVerdict = string.Empty;
        record.LastVerifiedAtUtc = null;
        record.Sku = string.Empty;
        record.MaxSubscriptions = 0;
        record.ExpiresAtUtc = null;
        record.LastError = "state_integrity_failed";
        record.Seal = string.Empty;
        return record;
    }

    private LicenseRecord Seal(LicenseRecord record)
    {
        record.Seal = ComputeSeal(GetSealKey().Key, record);
        return record;
    }

    internal static string ComputeSeal(byte[] key, LicenseRecord record)
    {
        var text = string.Join('\n',
            record.ProtectedKey,
            record.KeySuffix,
            record.KeyStoredAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            record.LastVerdict,
            record.LastVerifiedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            record.Sku,
            record.MaxSubscriptions.ToString(CultureInfo.InvariantCulture),
            record.ExpiresAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Muhur anahtari: rastgele 32 bayt, DPAPI'ye bagli korumayla saklanir. Okunamazsa yenisi uretilir.</summary>
    private (byte[] Key, bool Created) GetSealKey()
    {
        lock (sealKeyLock)
        {
            var path = fileStore.ResolvePath(SealKeyPath);
            if (File.Exists(path))
            {
                try
                {
                    return (Convert.FromBase64String(secretProtector.Unprotect(File.ReadAllText(path).Trim())), false);
                }
                catch
                {
                    // Anahtar klasoru silindi / dosya baska makineden: yenisi uretilir, eski muhurler gecersiz kalir.
                }
            }

            var key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, secretProtector.Protect(Convert.ToBase64String(key)));
            return (key, true);
        }
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
