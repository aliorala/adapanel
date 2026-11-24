using System.Collections.Concurrent;

namespace AdaPanel.Infrastructure.Security;

/// <summary>
/// HESAP BAŞINA sert deneme tavanı (denetim G10/G11). IP sayacı dağıtık/IPv6 saldırıda ve loopback'ten
/// (yerel PHP) gelen denemelerde işe yaramıyordu; "5 hata = yumuşak kilit" ise doğru parolayı
/// kabul ettiği için tahmin sayısını sınırlamıyordu.
/// </summary>
/// <remarks>
/// Pencere içinde <see cref="MaxFailures"/> başarısız parola/TOTP denemesinden sonra pencere dolana
/// kadar hesap için HİÇBİR deneme (doğru parola dahil) doğrulanmaz. Bellek içi ve süreç ömrüyle
/// sınırlıdır: yeniden başlatma sayacı sıfırlar (kabul edilen takas; kalıcı kilit meşru kullanıcıyı
/// kalıcı dışarıda bırakabilirdi). Başarılı giriş sayacı SIFIRLAMAZ ki parolayı bilmeyen saldırgan
/// kendi hesabından sayacı temizleyemesin; yalnız o hesabın başarılı girişinde silinir.
/// </remarks>
public static class AccountAttemptThrottle
{
    public const int MaxFailures = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private sealed class Entry { public DateTime FirstUtc; public int Count; }

    private static readonly ConcurrentDictionary<Guid, Entry> Entries = new();

    public static bool IsBlocked(Guid userId, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (!Entries.TryGetValue(userId, out var entry)) return false;
        lock (entry)
        {
            if (now - entry.FirstUtc > Window) { Entries.TryRemove(userId, out _); return false; }
            return entry.Count >= MaxFailures;
        }
    }

    public static void RegisterFailure(Guid userId, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var entry = Entries.GetOrAdd(userId, _ => new Entry { FirstUtc = now });
        lock (entry)
        {
            if (now - entry.FirstUtc > Window) { entry.FirstUtc = now; entry.Count = 0; }
            entry.Count++;
        }
    }

    public static void Reset(Guid userId) => Entries.TryRemove(userId, out _);
}
