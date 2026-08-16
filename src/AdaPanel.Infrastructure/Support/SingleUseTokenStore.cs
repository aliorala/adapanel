using System.Security.Cryptography;
using System.Text;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Kullanıcıya bağlı, TEK KULLANIMLIK ve kısa ömürlü belirteçlerin ortak deposu
/// (parola sıfırlama bağlantısı ve tek tıkla panel girişi bağlantısı).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek yerde:</b> disiplin küçük ama tamamı KRİTİK ve hepsi kolayca yanlış yazılır —
/// belirteci kriptografik rastgelelikten üretmek, dosyada yalnız SHA-256 ÖZETİNİ tutmak, bulma ve
/// tüketmeyi dosya kilidi altında TEK PARÇA yapmak, süresi geçenleri temizlemek. Panelde bunun bir
/// kopyası zaten vardı (parola sıfırlama); giriş bağlantısı ikinci kopya olacaktı. İki kopyada bir
/// gün biri düzeltilir, diğeri "çalışıyor gibi" kalır — ve bu kodda "çalışıyor gibi" demek,
/// belirtecin ikinci kez kullanılabilmesi demektir.
/// </para>
/// <para>
/// <b>Neden özet saklanıyor:</b> dosyayı okuyabilen biri (yedek, hatalı ACL, günlük toplayıcı)
/// belirtecin kendisini bulmasın. Özet tek yönlüdür; doğrulama gelen belirtecin özetiyle yapılır.
/// </para>
/// <para>
/// <b>Neden kullanıcı başına tek kayıt:</b> yeni bir bağlantı üretmek eskisini geçersiz kılar. Aynı
/// anda birden çok canlı bağlantı tutmanın meşru bir kullanımı yok, riski var.
/// </para>
/// </remarks>
public static class SingleUseTokenStore
{
    /// <summary>Belirteç üretir, özetini depoya yazar ve (belirteç, bitiş anı) döner.</summary>
    public static async Task<(string Token, DateTime ExpiresAtUtc)> IssueAsync(
        JsonFileStore fileStore,
        string relativePath,
        Guid userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var token = GenerateToken();
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(lifetime);

        // Kilitsiz oku-değiştir-yaz iki yönde de bozulurdu: A talep ederken B'ninki tamamlanırsa ya
        // A'nın yeni belirteci kaybolur (bağlantı hiç çalışmaz) ya da B'nin TÜKETİLMİŞ belirteci
        // geri dirilir. UpdateAsync yol başına kilit tutar.
        await fileStore.UpdateAsync(
            relativePath,
            new TokenStore(),
            mevcut =>
            {
                var kalan = mevcut.Entries
                    .Where(entry => entry.ExpiresUtc > now && entry.UserId != userId)
                    .ToList();
                kalan.Add(new TokenEntry
                {
                    UserId = userId,
                    TokenHash = HashToken(token),
                    ExpiresUtc = expiresAt
                });
                return new TokenStore { Entries = kalan };
            },
            cancellationToken);

        return (token, expiresAt);
    }

    /// <summary>
    /// Belirteci TÜKETİR; geçerliyse sahibinin kimliğini, değilse <c>null</c> döner.
    /// </summary>
    /// <remarks>
    /// Bulmak ve tüketmek TEK PARÇA olmalı: ayrı yapılsaydı aynı belirteci taşıyan iki eşzamanlı
    /// istek (tarayıcı ön-getirmesi + kullanıcının kendi isteği) ikisi de kaydı bulur ve "tek
    /// kullanımlık" güvencesi eşzamanlılık altında delinirdi.
    /// </remarks>
    public static async Task<Guid?> TryConsumeAsync(
        JsonFileStore fileStore,
        string relativePath,
        string? token,
        CancellationToken cancellationToken)
    {
        var normalized = (token ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        var hash = HashToken(normalized);
        var now = DateTime.UtcNow;

        TokenEntry? bulunan = null;
        await fileStore.UpdateAsync(
            relativePath,
            new TokenStore(),
            mevcut =>
            {
                bulunan = mevcut.Entries.FirstOrDefault(item =>
                    string.Equals(item.TokenHash, hash, StringComparison.OrdinalIgnoreCase) && item.ExpiresUtc > now);

                // Eşleşen kayıt her hâlükârda silinir (süresi geçmiş olsa bile) ve ölü kayıtlar
                // temizlenir: dosya süresiz büyümez.
                return new TokenStore
                {
                    Entries = mevcut.Entries
                        .Where(item => item.ExpiresUtc > now
                            && !string.Equals(item.TokenHash, hash, StringComparison.OrdinalIgnoreCase))
                        .ToList()
                };
            },
            cancellationToken);

        return bulunan?.UserId;
    }

    /// <summary>256 bitlik, URL'de taşınabilir belirteç.</summary>
    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();

    /// <summary>
    /// Dosya biçimi. Alan adları parola sıfırlama deposundaki eski biçimle BİREBİR aynı tutuldu:
    /// sürüm geçişinde mevcut <c>config/password-resets.json</c> dosyası okunmaya devam etsin.
    /// </summary>
    private sealed class TokenStore
    {
        public List<TokenEntry> Entries { get; set; } = [];
    }

    private sealed class TokenEntry
    {
        public Guid UserId { get; set; }

        public string TokenHash { get; set; } = string.Empty;

        public DateTime ExpiresUtc { get; set; }
    }
}
