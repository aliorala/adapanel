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

    // Store SCOPED kayitli (ServiceCollectionExtensions): kilit ornek basina olsaydi eszamanli ilk
    // erisim iki farkli anahtar uretirdi. Kilit ve "eski dosya benimseme" isareti bu yuzden STATIK, yol basina.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> KeyLocks = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> LegacyAdoption = new();

    public async Task<LicenseRecord> ReadAsync(CancellationToken cancellationToken)
    {
        var record = await fileStore.ReadAsync(StatePath, new LicenseRecord(), cancellationToken);
        var muhurVardi = !string.IsNullOrEmpty(record.Seal);
        var sonuc = VerifySeal(record);
        if (!muhurVardi && !string.IsNullOrEmpty(record.ProtectedKey) && LegacyAdoption.ContainsKey(SealPathKey()))
        {
            // Eski (muhursuz) dosya kabul edildi: HEMEN muhurle (atomik, JsonFileStore). Aksi halde ikinci
            // okumada seal.key var + muhur bos oldugu icin kayit "oynanmis" sayilirdi.
            return await fileStore.UpdateAsync(StatePath, new LicenseRecord(), existing =>
                string.IsNullOrEmpty(existing.Seal) ? Seal(existing) : existing, cancellationToken);
        }

        return sonuc;
    }

    public Task<LicenseRecord> UpdateAsync(Func<LicenseRecord, LicenseRecord> mutate, CancellationToken cancellationToken)
    {
        // Okunan kayit once muhurden gecer (oynanmissa karar alanlari sifirlanir), degisiklikten sonra
        // yeniden muhurlenir; boylece disaridan duzenleme bir sonraki yazimda "mesru" hale gelemez.
        return fileStore.UpdateAsync(StatePath, new LicenseRecord(), existing => Seal(mutate(VerifySeal(existing))), cancellationToken);
    }

    /// <summary>
    /// Yerel butunluk: muhur eslesmiyorsa KARAR alanlari sifirlanir ve limit KISITLAYICI yone cekilir
    /// (<c>MaxSubscriptions=0</c> "sinirsiz" demektir; oynanmis kayitta sinirsiz birakilmaz): hukum bos,
    /// limit 1, deneme zamani silinir (sunucuya hemen yeniden sorulur). Lisans anahtari korunur.
    /// Eski (muhursuz) dosya yalniz seal.key'i BU surecin yarattigi pencerede kabul edilir ve hemen muhurlenir.
    /// Anahtar okunamiyorsa (gecici G/C hatasi) mevcut karar OLDUGU GIBI korunur; hicbir sey sifirlanmaz.
    /// </summary>
    internal LicenseRecord VerifySeal(LicenseRecord record)
    {
        var anahtar = GetSealKey();
        if (anahtar is null)
        {
            return record;
        }

        if (string.IsNullOrEmpty(record.Seal))
        {
            if (string.IsNullOrEmpty(record.ProtectedKey) || LegacyAdoption.ContainsKey(SealPathKey()))
            {
                return record;
            }
        }
        else if (string.Equals(record.Seal, ComputeSeal(anahtar, record), StringComparison.Ordinal))
        {
            return record;
        }

        record.LastVerdict = string.Empty;
        record.LastVerifiedAtUtc = null;
        record.LastAttemptAtUtc = null;
        record.Sku = string.Empty;
        record.MaxSubscriptions = 1;
        record.ExpiresAtUtc = null;
        record.LastError = "state_integrity_failed";
        record.Seal = string.Empty;
        return record;
    }

    private LicenseRecord Seal(LicenseRecord record)
    {
        var anahtar = GetSealKey()
            ?? throw new IOException("Lisans muhur anahtari okunamadi; kayit muhurlenemedi (mevcut kayit korundu).");
        record.Seal = ComputeSeal(anahtar, record);
        LegacyAdoption.TryRemove(SealPathKey(), out _);
        return record;
    }

    /// <summary>Testler: surec yeniden baslatmayi taklit eder (statik kilit/benimseme durumu silinir).</summary>
    internal static void ResetProcessStateForTests()
    {
        KeyLocks.Clear();
        LegacyAdoption.Clear();
    }

    private string SealPathKey() => fileStore.ResolvePath(SealKeyPath).ToUpperInvariant();

    internal static string ComputeSeal(byte[] key, LicenseRecord record)
    {
        var text = string.Join('
',
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

    /// <summary>
    /// Muhur anahtari: rastgele 32 bayt, DPAPI'ye bagli korumayla saklanir. YENI anahtar YALNIZ dosya
    /// YOKKEN uretilir (gecici dosya + tasima: atomik). Okuma G/C hatasi birkac kez denenir; yine olmazsa
    /// <c>null</c> doner (anahtar EZILMEZ). Dosya okunuyor ama cozulemiyorsa (DPAPI anahtar klasoru silindi,
    /// anahtarli kayit da zaten cozulemez) yeni anahtar uretilir; eski muhurler gecersiz kalir.
    /// </summary>
    internal byte[]? GetSealKey()
    {
        var path = fileStore.ResolvePath(SealKeyPath);
        var gate = KeyLocks.GetOrAdd(path.ToUpperInvariant(), _ => new object());
        lock (gate)
        {
            if (File.Exists(path))
            {
                string? metin = null;
                for (var deneme = 0; deneme < 3 && metin is null; deneme++)
                {
                    try
                    {
                        metin = File.ReadAllText(path).Trim();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Thread.Sleep(50);
                    }
                }

                if (metin is null)
                {
                    return null;
                }

                try
                {
                    return Convert.FromBase64String(secretProtector.Unprotect(metin));
                }
                catch (Exception exception) when (exception is CryptographicException or FormatException)
                {
                    // Cozulemeyen anahtar: asagida yenisi uretilir.
                }
            }

            var legacyCandidate = File.Exists(fileStore.ResolvePath(StatePath)) && !File.Exists(path);
            var key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var gecici = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(gecici, secretProtector.Protect(Convert.ToBase64String(key)));
            File.Move(gecici, path, overwrite: true);
            if (legacyCandidate)
            {
                LegacyAdoption[path.ToUpperInvariant()] = true;
            }

            return key;
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
