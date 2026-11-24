namespace AdaPanel.Core.Interfaces;

/// <summary>
/// M3: PANELİN KENDİ başlattığı disk yazımlarında (Git dağıtımı, uygulama kurulumu, site klonu) plan <c>MaxDiskMb</c> tavanını sorar. Dosya yöneticisi yüklemesi
/// zaten kendi yolunda sorar; asıl boşluk panelin dosya yazan DİĞER uçlarıydı.
/// </summary>
/// <remarks>
/// Gerçek bir FTP düzeyinde kota (FSRM ya da hesap başına) bu arayüzün kapsamında DEĞİLDİR; FTP ile
/// yazılan veri yalnız zorlama turundaki disk ölçümüyle (dikkat öğesi) fark edilir.
/// Sığmıyorsa <c>files.quota_exceeded</c> (403) fırlatır. Abonelikte sınır yoksa (0) ya da sahip
/// bulunamazsa sessizce geçer: sınırsız aboneliğin ölçümü yapılmaz.
/// </remarks>
public interface IPanelWriteQuotaGuard
{
    /// <summary>Sitenin sahibi aboneliğin tavanına <paramref name="incomingBytes"/> sığar mı.</summary>
    Task EnsureRoomForSiteAsync(string siteName, long incomingBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitenin MEVCUT içeriğinin bir kopyası (klon) sahibinin tavanına sığar mı. Boyut bütçeli yürüyüşle
    /// ölçülür; ölçüm kesilirse klon BAŞLATILMAZ (kopyalamayla aynı karar, bkz. FileQuotaGuard).
    /// </summary>
    Task EnsureRoomForCopyOfSiteAsync(string siteName, CancellationToken cancellationToken = default);
}
