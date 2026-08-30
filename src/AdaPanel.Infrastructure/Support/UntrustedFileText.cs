namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// BOYUTUNU KİRACININ BELİRLEDİĞİ bir dosyayı, üst sınırla okur.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık (ölçüldü):</b> panel, kiracının KENDİ yazabildiği dosyaları
/// <c>File.ReadAllText</c> ile tamamen belleğe alıyordu. Bu dosyaların boyutunu panel değil
/// KİRACI belirler; yani bellek tüketimini de o belirliyordu.
/// </para>
/// <list type="bullet">
/// <item><b>Dizin koruması:</b> <c>DirectoryProtectionService</c> bir sitenin korumalı klasörleri
/// için <c>web.config</c> dosyalarını bir DÖNGÜDE okuyordu — yani TEK istekte N dosya, her biri
/// sınırsız. Çarpan da kiracıda: korumalı klasör sayısını o belirliyor.</item>
/// <item><b>WordPress araç kutusu:</b> <c>wp-config.php</c> tamamen okunup üzerinde düzenli ifade
/// çalıştırılıyordu, ve bu her pano yüklenişinde oluyordu.</item>
/// </list>
/// <para>
/// <b>Neden ayrı bir yardımcı — ve neden mevcut olanlar kullanılmadı:</b> depoda iki sınırlı
/// okuyucu var ama ikisi de başka bir şey yapıyor. <see cref="FileTail"/> dosyanın SONUNU okur
/// (günlükler için); buradaki ihtiyaç dosyanın BAŞINDAN itibaren sınırlı okumak.
/// <c>IHtaccessFileSystem.TryReadText</c> doğru davranışa sahip ama htaccess modülünün test
/// soyutlamasına bağlı; onu buraya çekmek, ilgisiz iki modülü birbirine bağlardı. Ortaklaştırılan
/// şey KARARDIR: "güvenilmeyen boyut sınırla okunur, sınır aşılırsa OKUNMAMIŞ sayılır".
/// </para>
/// <para>
/// <b>Neden istisna değil <c>false</c>:</b> her iki çağrı yeri de zaten "okuyamadıysam bir şey
/// İDDİA ETMEM" davranışında. Sınırı aşan dosya, okunamayan dosyayla aynı kovaya düşer — yani
/// panel yanlış bir şey söylemek yerine hiçbir şey söylemez.
/// </para>
/// </remarks>
public static class UntrustedFileText
{
    /// <summary>Kiracının yazabildiği yapılandırma dosyaları için makul üst sınır.</summary>
    /// <remarks>
    /// Gerçek bir <c>web.config</c> ya da <c>wp-config.php</c> birkaç KB'dır. 256 KiB, meşru
    /// hiçbir dosyayı dışarıda bırakmayacak kadar cömert; sınırın amacı normal dosyaları elemek
    /// değil, 2 GB'lık bir dosyanın paneli düşürmesini engellemek.
    /// </remarks>
    public const int VarsayilanSinirBayt = 256 * 1024;

    /// <summary>
    /// Dosyayı okur. Yoksa, okunamıyorsa ya da <paramref name="maxBytes"/> sınırını aşıyorsa
    /// <c>false</c> döner ve <paramref name="content"/> boş kalır.
    /// </summary>
    public static bool TryRead(string path, out string content, int maxBytes = VarsayilanSinirBayt)
    {
        content = string.Empty;

        if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0)
        {
            return false;
        }

        try
        {
            var bilgi = new FileInfo(path);

            // BOYUT OKUMADAN ÖNCE denetlenir. Önce okuyup sonra uzunluğa bakmak, tam da
            // engellemeye çalıştığımız belleği zaten harcamak olurdu.
            if (!bilgi.Exists || bilgi.Length > maxBytes)
            {
                return false;
            }

            // Paylaşım: dosyayı yazan taraf (IIS, PHP) onu açık tutuyor olabilir.
            // FileShare.Read ile açmak, o yazarla çakışıp okumayı düşürürdü.
            using var akis = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var okuyucu = new StreamReader(akis, detectEncodingFromByteOrderMarks: true);
            content = okuyucu.ReadToEnd();
            return true;
        }
        catch (Exception)
        {
            content = string.Empty;
            return false;
        }
    }
}
