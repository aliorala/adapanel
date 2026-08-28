using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Files;

/// <summary>
/// Disk kotası kuralının TEK KAYNAĞI: "bu kadar bayt daha yazılırsa abonelik tavanı aşılır mı?"
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var — kapatılan açık:</b> kota denetimi <c>FilesController.UploadAsync</c>'in İÇİNE
/// gömülüydü ve yalnız orada vardı. Diski büyüten diğer üç uç — <c>POST /files/copy</c>,
/// <c>PUT /files</c>, <c>POST /files/extract</c> — hiç sormuyordu. Somut sonuç: 500 MB kotalı bir
/// müşteri 400 MB'lık klasörünü tekrar tekrar kopyalayarak birimi doldurabiliyordu ve o birimde
/// oturan KOMŞU KİRACILAR da yazamaz hâle geliyordu. Yani kota, tek bir uçta durdurulmuş bir
/// tavsiyeydi.
/// </para>
/// <para>
/// <b>Neden saf bir fonksiyon, neden servis değil:</b> kuralın kendisi (bayt hesabı, "0 = sınırsız",
/// kesilmiş ölçümün ne anlama geldiği) hiçbir bağımlılık istemez. Kimin kotasına bakılacağı ise
/// istek bağlamını (rol, site sahipliği, alt-kullanıcı) bilmek zorundadır ve o bilgi yalnız API
/// katmanında vardır. Ayırmak, KURALI dört uçta ortak, PLUMBING'i tek bir denetleyici yardımcısında
/// toplar — ve kuralı bağımlılıksız bıraktığı için tek bir sahte nesne bile gerektirmeden test
/// edilebilir kılar.
/// </para>
/// </remarks>
public static class FileQuotaGuard
{
    /// <summary>Kota aşıldı.</summary>
    /// <remarks>
    /// Anahtar fırlatma yerinde METİN olarak yazılır, bu sabitten okunmaz: çeviri kapsamı testi
    /// (<c>MessageKeyCoverageTests</c>) kaynağı düz metin arayarak tarar ve sabitten okunan anahtar
    /// taramada görünmez. Sabit yalnız okunabilirlik ve testlerin başvurusu için var.
    /// </remarks>
    public const string QuotaKey = "files.quota_exceeded";

    /// <summary>
    /// Yazılacak baytların aboneliğin disk tavanına sığdığını doğrular; sığmıyorsa istisna atar.
    /// </summary>
    /// <param name="maxDiskMb">Aboneliğin disk tavanı (MB). <c>0</c> ve altı SINIRSIZ demektir.</param>
    /// <param name="kullanilanMb">Şu anki kullanım (MB). Kendisi de bütçeli bir ölçümden gelir ve
    /// bir ALT SINIR olabilir; o kararın gerekçesi <c>CustomerScopeService.GetDiskUsageMbAsync</c>'te
    /// yazılıdır ve burada değiştirilmez.</param>
    /// <param name="gelenBayt">Bu işlemin diske EKLEYECEĞİ bayt.</param>
    /// <param name="gelenOlcumKesilmeSebebi">Gelen baytın ölçümü bütçe yüzünden KESİLDİYSE
    /// yürüyüşün sebep anahtarı, tamsa boş.</param>
    public static void EnsureRoom(
        int maxDiskMb,
        long kullanilanMb,
        long gelenBayt,
        string gelenOlcumKesilmeSebebi)
    {
        if (maxDiskMb <= 0)
        {
            // 0 = SINIRSIZ (panelin her yerinde aynı sözleşme). Kesilmiş ölçüm de burada önemsizdir:
            // denetlenecek bir tavan yoksa ölçemediğimiz şeyin de bir sonucu yoktur.
            return;
        }

        var tavanBayt = (long)maxDiskMb * 1024L * 1024L;
        var kullanilanBayt = kullanilanMb * 1024L * 1024L;

        // ==========================================================================================
        // HESAP BAYT ÜZERİNDEN — ve bu, kapatılan İKİNCİ hatadır (aynı kök).
        //
        // Eski kod `var incomingMb = file.Length / (1024 * 1024);` yazıyordu: TAMSAYI bölmesi. 1 MB
        // altındaki HER dosya kotaya 0 olarak giriyordu, yani 999 KB'lık bir dosya "hiç yer
        // kaplamıyor" sayılıyordu. 0.7.6'nın yeni çoklu/sürükle-bırak yüklemesi bunu tek hamlede
        // yüzlerce dosyaya ölçekliyor: tek seferde yüzlerce megabayt, kotaya sıfır olarak yazılır ve
        // tavan ancak BİR SONRAKİ istekte, çoktan aşılmışken fark edilirdi.
        //
        // Baytla karşılaştırmak sınırı KESİN yapar: aşan istek, aşmadan ÖNCE reddedilir.
        // ==========================================================================================
        if (kullanilanBayt + gelenBayt > tavanBayt)
        {
            throw new AppException("files.quota_exceeded", 403, 403);
        }

        // ==========================================================================================
        // KESİLMİŞ ÖLÇÜM — KARAR: ölçemediğimiz işi BAŞLATMAYIZ.
        //
        // Kesilmiş bir ölçüm bir ALT SINIRDIR. Alt sınır tavanı zaten aşıyorsa yukarıda reddedildi
        // ve o karar KESİN doğrudur. Buraya gelmek "alt sınır sığıyor" demektir — ama gerçek boyut
        // yine de aşabilir, çünkü ağacın geri kalanına hiç bakılmadı. Belirsizliği "sığıyor" yönüne
        // yorumlamak, kotayı İNŞA EDİLEBİLİR biçimde delerdi: bütçeyi dolduracak kadar büyük bir
        // ağaç kuran kiracı, o ağacı istediği kadar kopyalayabilirdi.
        //
        // Bedeli tartıldı ve DÜŞÜK bulundu, çünkü kesilen ölçüm KULLANICININ SEÇTİĞİ ağaçtır (kota
        // hesabındaki gibi sunucunun yavaşlığı değil): tavanı dolduran bir ağaç zaten patolojiktir,
        // kullanıcının elinde bir çare vardır (alt klasörleri ayrı ayrı kopyala) ve ASIL ÖNEMLİSİ —
        // kopyalamanın kendi bütçesi de aynı ağaçta dolar, yani izin verseydik iş zaten yarıda
        // kesilip geri sarılacaktı. Reddetmek, aynı sonucu boşa iş yapmadan ve dürüst bir sebeple
        // verir.
        //
        // NEDEN "erişim reddi" BU KURALIN DIŞINDA: okunamayan alt klasörü ölçüm de kopyalama da
        // AYNI şekilde atlar. Orada ölçüm bir alt sınır değil, yazılacak şeyin TAM karşılığıdır —
        // reddetmek, panelin zaten okuyamadığı bir klasör yüzünden meşru bir kopyalamayı
        // engellemek olurdu.
        //
        // ANAHTAR YÜRÜYÜŞÜN KENDİ SEBEP ANAHTARIDIR ve yeni bir anahtar UYDURULMADI: çevirisi
        // olmayan bir anahtar, kullanıcının kırmızı kutuda ham anahtarı görmesi demektir
        // (MessageKeyCoverageTests). Bu anahtarların çevirisi hem tr hem en'de VAR.
        // ==========================================================================================
        if (gelenOlcumKesilmeSebebi.Length > 0 && gelenOlcumKesilmeSebebi != SafeDirectoryWalk.SebepErisimReddi)
        {
            throw new AppException(
                gelenOlcumKesilmeSebebi,
                413,
                413,
                "Kopyalanacak agac olculemeyecek kadar buyuk; disk kotasi dogrulanamadigi icin islem " +
                "BASLATILMADI. Icerigi alt klasorlere bolerek kopyalayin.");
        }
    }
}
