using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;

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
    /// <param name="kullanim">Şu anki kullanım — VE ölçülüp ölçülemediği. Düz bir sayı DEĞİL,
    /// çünkü kendisi de bütçeli bir ölçümden gelir ve bir ALT SINIR olabilir; kuralın o belirsizlikle
    /// ne yaptığı aşağıda ölçülmüş gerekçesiyle yazılıdır.</param>
    /// <param name="gelenBayt">Bu işlemin diske EKLEYECEĞİ bayt.</param>
    /// <param name="gelenOlcumKesilmeSebebi">Gelen baytın ölçümü bütçe yüzünden KESİLDİYSE
    /// yürüyüşün sebep anahtarı, tamsa boş.</param>
    public static void EnsureRoom(
        int maxDiskMb,
        DiskUsage kullanim,
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
        var kullanilanBayt = kullanim.UsageMb * 1024L * 1024L;

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
        //
        // BU KARAR, KULLANIM ÖLÇÜMÜ KESİLMİŞ OLSA BİLE KESİNDİR: kesilmiş ölçüm bir ALT SINIRDIR,
        // yani gerçek kullanım EN AZ bu kadardır. Alt sınır bile tavanı aşıyorsa gerçek değer de
        // aşar. Bu yüzden aşağıdaki "ölçülemeyende kota uygulanmaz" kuralı buraya UYGULANMAZ —
        // trafik kotasındaki `olculdu && bayt >= sinir` kalıbı burada bilerek TAŞINMADI ve fark
        // ölçülmüştür: trafiğin yaptırımı ASKIYA ALMAKTIR (bütün siteleri yayından kaldırır,
        // müşteri kendi başına geri açamaz), buradaki ise TEK BİR YAZMAYI reddetmektir. Kesin bir
        // ihlali "ölçümüm eksikti" diye geçirmek, kotayı ölçüm bütçesini doldurabilen herkese
        // açardı.
        if (kullanilanBayt + gelenBayt > tavanBayt)
        {
            throw new AppException("files.quota_exceeded", 403, 403);
        }

        // ==========================================================================================
        // KULLANIM ÖLÇÜLEMEDİYSE VE İŞ SIĞIYORSA — KARAR: KOTA UYGULANMAZ, iş geçer.
        //
        // Buraya gelmek "alt sınır sığıyor ama gerçek kullanım daha büyük olabilir" demektir.
        // Karar TRAFİK KOTASINDAN kopyalandı (SubscriptionEnforcementService.TrafikOlcAsync:
        // ölçülemeyen abonelikte kota UYGULANMAZ) ve buraya da aynı sebeple uyar: kesilmenin üç
        // sebebinden ikisi kiracının elinde DEĞİLDİR (süre bütçesi = yavaş/ağ diski, erişim reddi =
        // ACL). Belirsizliği "aşmış say" yönüne yormak, sunucunun yavaşlığı yüzünden meşru
        // müşterinin HİÇ dosya yükleyememesi olurdu — yaptırım noktası yükleme olduğu için müşteri
        // tümden çalışamaz hâle gelirdi.
        //
        // KARARIN BEDELİ (ve neden sessiz DEĞİL): bütçeyi BİLEREK dolduran bir kiracıya karşı kota
        // kesinliğini kaybederiz. Kaybın kapatıldığı yer burası değil GÖSTERİMDİR: `kullanim`
        // artık "ölçüldü mü" bilgisini taşıdığı için kullanıcı listesi ve abonelik/fatura ekranı
        // eksik sayıyı TAM diye göstermez (DiskMeasured), yönetici de şişmiş aboneliği görür. Bu
        // kuralın parametresi bilerek düz bir sayı DEĞİL: çağıranın alt sınırı kesin bir ölçüm
        // gibi geçirmesi artık derleyici seviyesinde imkânsız.
        // ==========================================================================================

        // ==========================================================================================
        // GELEN İŞİN ÖLÇÜMÜ KESİLDİYSE — KARAR: ölçemediğimiz işi BAŞLATMAYIZ.
        //
        // AYNI KURALDA İKİ KESİLME VE İKİ ZIT CEVAP; fark rastlantı değil, KİMİN ağacı olduğudur.
        // Yukarıdaki kesilme (kullanim.Measured) aboneliğin BÜTÜN sitelerinde, çoğunlukla
        // kiracının elinde olmayan sebeplerle olur ve orada reddetmek müşteriyi tümden çalışamaz
        // hâle getirirdi. Buradaki kesilme ise KULLANICININ BU İSTEKTE SEÇTİĞİ ağaçtadır: sebebi
        // seçimin kendisidir, çaresi vardır ve iş zaten yarıda kesilirdi (aşağıda ölçüldü).
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
