using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Cluster;

/// <summary>
/// Düğüm kayıt defteri. Okuma/yazma <see cref="JsonFileStore"/> üzerinden ATOMİKTİR.
/// </summary>
/// <remarks>
/// <para>
/// Veritabanı göçü GEREKTİRMEMESİ bilinçli: panelde aynı desen (config/*.json) müşteri kapsamı,
/// otomasyon ayarları ve lisans durumu için zaten kullanılıyor. Düğüm listesi birkaç kayıtlık,
/// nadiren değişen bir yapılandırmadır; onun için şema göçü açmak, güncelleme yolunu kırılganlaştırır.
/// </para>
/// <para>
/// <b>Yarış koşulu:</b> ekleme/güncelleme/silme <c>UpdateAsync</c> (dosya başına kilitli
/// oku-değiştir-yaz) ile yapılır. Düz <c>ReadAsync</c> + <c>WriteAsync</c> yazılsaydı iki eşzamanlı
/// kayıt birbirini silerdi.
/// </para>
/// </remarks>
public sealed class ClusterNodeStore(
    JsonFileStore fileStore,
    ISecretProtector secretProtector,
    ILogger<ClusterNodeStore> logger)
{
    private const string StorePath = "config/cluster/nodes.json";

    public async Task<IReadOnlyList<ClusterNodeRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var file = await fileStore.ReadAsync(StorePath, new ClusterNodeStoreFile(), cancellationToken);
        return file.Nodes;
    }

    public async Task<ClusterNodeRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var nodes = await GetAllAsync(cancellationToken);
        return nodes.FirstOrDefault(node => node.Id == id);
    }

    /// <summary>Kaydı ekler. Çağıran adresi ZATEN doğrulamış olmalıdır (bkz. ClusterService).</summary>
    public async Task<ClusterNodeRecord> AddAsync(ClusterNodeRecord record, string plainToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        record.TokenProtected = secretProtector.Protect(plainToken);
        await fileStore.UpdateAsync(
            StorePath,
            new ClusterNodeStoreFile(),
            current =>
            {
                current.Nodes.Add(record);
                return current;
            },
            cancellationToken);

        return record;
    }

    /// <summary>
    /// Kaydı değiştirir. <paramref name="plainToken"/> boşsa mevcut korunmuş belirteç KORUNUR.
    /// </summary>
    public async Task<ClusterNodeRecord?> UpdateAsync(
        Guid id,
        Action<ClusterNodeRecord> mutate,
        string? plainToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        ClusterNodeRecord? updated = null;
        await fileStore.UpdateAsync(
            StorePath,
            new ClusterNodeStoreFile(),
            current =>
            {
                var record = current.Nodes.FirstOrDefault(node => node.Id == id);
                if (record is null)
                {
                    return current;
                }

                mutate(record);
                if (!string.IsNullOrWhiteSpace(plainToken))
                {
                    record.TokenProtected = secretProtector.Protect(plainToken);
                }

                updated = record;
                return current;
            },
            cancellationToken);

        return updated;
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var removed = false;
        await fileStore.UpdateAsync(
            StorePath,
            new ClusterNodeStoreFile(),
            current =>
            {
                removed = current.Nodes.RemoveAll(node => node.Id == id) > 0;
                return current;
            },
            cancellationToken);

        return removed;
    }

    /// <summary>
    /// Yoklamanın sonucunu kaydeder (son görülme / son hata).
    /// </summary>
    /// <remarks>
    /// Ayrı bir metot çünkü bu yazma yoklama turunun İÇİNDEN çağrılır ve HATA VERMEMELİDİR: disk
    /// dolu olduğu için tarih yazılamaması, panoyu tamamen boş bırakmanın gerekçesi olamaz.
    /// </remarks>
    public async Task RecordProbeAsync(Guid id, bool reachable, string error, CancellationToken cancellationToken = default)
    {
        try
        {
            await UpdateAsync(
                id,
                record =>
                {
                    if (reachable)
                    {
                        record.LastSeenAtUtc = DateTime.UtcNow;
                        record.LastError = string.Empty;
                    }
                    else
                    {
                        record.LastError = error.Length > 300 ? error[..300] : error;
                    }
                },
                plainToken: null,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Düğüm yoklama sonucu kaydedilemedi ({NodeId}).", id);
        }
    }

    /// <summary>
    /// Kaydın düz metin belirtecini çözer; çözülemezse boş döner.
    /// </summary>
    /// <remarks>
    /// DPAPI koruması MAKİNEYE bağlıdır: yapılandırma dosyası başka bir sunucuya kopyalanırsa
    /// çözme başarısız olur. Bu durumda çökmek yerine boş dönüp "belirteç gerekli" demek doğrudur —
    /// yönetici anahtarı yeniden girer.
    /// </remarks>
    public string ResolveToken(ClusterNodeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.TokenProtected))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(record.TokenProtected);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Düğüm belirteci çözülemedi ({NodeId}); yeniden girilmesi gerekiyor.", record.Id);
            return string.Empty;
        }
    }
}
