using System.Collections.Concurrent;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// AYNI ANDA kaç ağır isteğin çalışabileceğini ANAHTAR BAŞINA sınırlayan tek kapı.
/// </summary>
/// <remarks>
/// <para>
/// <b>KAPATILAN AÇIK (ölçüldü):</b> dosya yöneticisinin ağır uçları — sıkıştırma
/// (<c>FileService.CompressAsync</c>), arşiv açma (<c>ExtractAsync</c>), kopyalama
/// (<c>CopyAsync</c>) ve boyut hesabı (<c>CalculateSizeAsync</c>) — <c>Customer</c> rolüne açık
/// (<c>FilesController</c>), hepsi İSTEK İŞ PARÇACIĞINDA ve LocalSystem olarak koşuyor, ve
/// hiçbirinde EŞZAMANLILIK DENETİMİ yoktu. Öge/süre bütçeleri TEK bir isteğin ne kadar
/// çalışacağını sınırlıyordu; kaç isteğin AYNI ANDA çalışacağını sınırlayan hiçbir şey yoktu.
/// Yani tek kiracı, tek bir düğmeye N kez basarak N tam hızlı disk taraması başlatabiliyordu.
/// </para>
/// <para>
/// <b>NEDEN <c>IConcurrencyLock</c> KULLANILMADI</b> (okundu, ölçüldü, uymuyor): o kapı
/// KUYRUĞA ALIR (<c>SemaphoreSlim.WaitAsync</c>, tavansız). Saldırı yolunda kuyruk, isteği
/// reddetmek yerine BİRİKTİRİR: 500 istek 500 açık bağlantı ve 500 bekleyen görev olarak durur —
/// yani "kaynak tüketimi" açığı, kuyruk tüketimi açığına dönüşür. Ayrıca anahtar kümesi
/// <c>ConcurrencyKeys</c>'te sabittir; buradaki anahtar kiracı kimliğidir, çalışma zamanında
/// üretilir.
/// </para>
/// <para>
/// <b>KUYRUK YOK, HEMEN 429:</b> bekletmek kullanıcıya "çalışıyor" izlenimi verirken sunucuda
/// kaynak tutar. Reddetmek dürüsttür ve arayüz kullanıcıya "önceki işlem bitince tekrar deneyin"
/// diyebilir.
/// </para>
/// <para>
/// <b>GENEL (makine geneli) TAVAN BİLEREK YOK.</b> Genel bir tavan, kapatılan çapraz kiracı
/// reddini geri getirirdi: yeterince ağır iş açan tek bir kiracı bütün ötekileri kilitlerdi.
/// Sınır kiracı BAŞINA verilir; kiracıları yalnız yönetici oluşturur, yani toplam yük
/// yöneticinin bildiği bir çarpanla sınırlıdır.
/// </para>
/// <para>
/// Semaforlar sözlükten HİÇ silinmez. Anahtar kümesi abonelik sayısıyla sınırlıdır ve her giriş
/// birkaç onlarca bayttır; silmeye kalkmak "sil–yeniden oluştur" arasında sayımı kaybettiren bir
/// yarış açardı. Aynı karar <see cref="JsonFileStore"/> kilit sözlüğünde de yazılı.
/// </para>
/// </remarks>
public sealed class ConcurrentWorkGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> kapilar = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Yer varsa yeri AYIRIR ve serbest bırakan kapsamı döner; yer yoksa
    /// <paramref name="messageKey"/> ile <c>429</c> fırlatır.
    /// </summary>
    /// <param name="key">Bütçenin sahibi (bu depoda: abonelik/kullanıcı kimliği + iş ailesi).</param>
    /// <param name="limit">Aynı anda izin verilen iş sayısı. 0 ve altı = sınır uygulanmaz.</param>
    /// <param name="messageKey">Reddedilince kullanıcıya dönecek çeviri anahtarı.</param>
    public IDisposable Enter(string key, int limit, string messageKey)
    {
        if (limit <= 0)
        {
            return NullKapsam.Instance;
        }

        var kapi = kapilar.GetOrAdd(key, _ => new SemaphoreSlim(limit, limit));

        // BEKLEMEDEN dene (Wait(0)): kuyruğa girmek reddetmenin bütün faydasını götürürdü.
        if (!kapi.Wait(0))
        {
            // DETAILS'E ANAHTAR YAZILMAZ. `AppException.Details` bu depoda YANIT GÖVDESİNE gider
            // (0.8.7'de ölçüldü: MariaDB root parolası tam bu kanaldan sızıyordu). Anahtar bütçe
            // sahibinin kimliğidir; alt kullanıcı kendi isteğinde üst kullanıcısının kimliğini
            // görürdü. Sır değil ama yeni kapatılan kanalı yeniden beslemenin sebebi de yok —
            // sınırın kendisi kullanıcıya yetiyor, kimin bütçesi olduğu sunucunun bileceği iş.
            throw new AppException(messageKey, 429, 429, $"Concurrency limit {limit} reached.");
        }

        return new Kapsam(kapi);
    }

    private sealed class Kapsam(SemaphoreSlim kapi) : IDisposable
    {
        private int birakildi;

        public void Dispose()
        {
            // Çift Dispose semaforu ŞİŞİRİRDİ (limitin üstüne çıkarırdı) — yani koruma sessizce
            // gevşerdi. Interlocked ile bir kez serbest bırakılır.
            if (Interlocked.Exchange(ref birakildi, 1) == 0)
            {
                kapi.Release();
            }
        }
    }

    private sealed class NullKapsam : IDisposable
    {
        public static readonly NullKapsam Instance = new();

        public void Dispose()
        {
            // Sınır uygulanmadı; bırakılacak bir şey yok.
        }
    }
}
