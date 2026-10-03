using AdaPanel.Core.Dtos.Update;

namespace AdaPanel.Core.Interfaces;

public interface ISelfUpdateService
{
    /// <param name="forceRefresh">
    /// true ise GitHub'a MUTLAKA gidilir. false (varsayılan) ise yakın zamanda alınmış sonuç
    /// yeniden kullanılır.
    /// <para>
    /// Önbellek şart: "dikkat" rozeti bu kontrolü 60 saniyede bir çağırıyor ve GitHub'ın kimliksiz
    /// API sınırı SAATTE 60 İSTEK. Yani her çağrıda ağa çıkmak, sınırı tek başına doldurur; ikinci
    /// bir sekme ya da başka bir GitHub kullanımı sınırı aşar. Sınır aşılınca çekme başarısız olur
    /// ve panel önbellekteki ESKİ sürümü güncelmiş gibi gösterir — yeni sürüm yayınlansa bile
    /// "güncelsiniz" der. Canlı olarak bu yaşandı.
    /// </para>
    /// Kullanıcı "kontrol et" dediğinde true geçilir: açık istek beklemeyi hak eder.
    /// </param>
    Task<UpdateStatusDto> CheckForUpdateAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>Güncellemelerin çekileceği GitHub deposunu (owner/repo) ayarlar ve güncel durumu döner. Boş = GitHub kapalı.</summary>
    Task<UpdateStatusDto> SetGithubRepoAsync(string repo, CancellationToken cancellationToken = default);

    Task<UpdateStatusDto> ApplyUpdateAsync(ApplyUpdateRequest request, CancellationToken cancellationToken = default);

    Task<UpdateStatusDto> InstallPreparedUpdateAsync(CancellationToken cancellationToken = default);

    Task<UpdateStatusDto> InstallPreparedUpdateAsync(InstallUpdateRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This updater does not support confirmed package identities.");
}
