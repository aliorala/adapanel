using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace AdaPanel.Api.Security;

/// <summary>
/// İSTEK KATMANININ kaynak tavanları (Kestrel + form ayrıştırıcı) — TEK KAYNAK.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN DURUM:</b> <c>ConfigureKestrel</c> yalnızca sertifika varken çağrılıyordu ve
/// <c>Limits</c>'e HİÇ dokunmuyordu. Bu, "hiçbir sınır yok" demek DEĞİLDİ — Kestrel'in kendi
/// varsayılanları (30 MB gövde, 30 sn başlık zaman aşımı, 240 B/sn en az veri hızı) yürürlükteydi.
/// Gerçekten sınırsız olan TEK şey <c>MaxConcurrentConnections</c>'dı ve asıl düzeltme odur.
/// </para>
/// <para>
/// <b>Varsayılanla aynı olan değerler NEDEN yine de yazılıyor:</b> ölçülemeyen bir varsayılana
/// güvenmek bu depoda ölçülmüş hata sınıfı. Değerler burada adlandırılınca (a) testler tahmin
/// etmek yerine sabiti okur, (b) çerçeve varsayılanı bir gün değişirse panelin davranışı
/// değişmez, (c) birini gevşetmek görünür bir düzenleme gerektirir.
/// </para>
/// </remarks>
public static class PanelServerLimits
{
    /// <summary>
    /// Aynı anda kabul edilecek en fazla TCP bağlantısı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tek gerçek boşluk buydu:</b> Kestrel'in varsayılanı <c>null</c> = SINIRSIZ. Bağlantı
    /// açıp hiç istek göndermeyen bir istemci kümesi, panel süreci bellek/handle tükenene kadar
    /// büyütülebilirdi ve hız sınırlayıcı bunu GÖREMEZ (istek hiç gelmiyor).
    /// </para>
    /// <para>
    /// <b>500 nereden:</b> HTTP/1.1'de tarayıcı ana ad başına 6 bağlantı açar, HTTP/2'de 1.
    /// Aynı anda 20 yönetici en kötü hâlde ~120 bağlantı eder; DbTools vekili ve göç akışı
    /// (SSE) uzun süre açık kalan birkaç bağlantı daha ekler. 500, ölçülen en kötü meşru hâlin
    /// dört katıdır. Sınır AŞILDIĞINDA Kestrel isteği reddetmez, ALTINA DÜŞENE KADAR yeni
    /// bağlantı kabul etmez — yani meşru kullanıcı hata görmez, kısa bir gecikme görür.
    /// </para>
    /// </remarks>
    public const long MaxConcurrentConnections = 500;

    /// <summary>
    /// Yükseltilmiş (WebSocket/CONNECT) bağlantı tavanı.
    /// </summary>
    /// <remarks>
    /// Panelin kendi kodunda WebSocket kullanımı YOK (kaynak tarandı); bu tavan, ileride
    /// eklenecek bir yükseltme yolunun sınırsız doğmasını engeller. Varsayılanı da sınırsızdır.
    /// </remarks>
    public const long MaxConcurrentUpgradedConnections = 100;

    /// <summary>
    /// Genel istek gövdesi tavanı: 30 MB (Kestrel varsayılanıyla aynı, artık YAZILI).
    /// </summary>
    /// <remarks>
    /// Bunu AŞMASI GEREKEN uçlar kendi <c>[RequestSizeLimit]</c> özniteliğini taşır ve öznitelik
    /// sunucu tavanını istek başına EZER: <c>FilesController.UploadAsync</c> (200 MiB),
    /// <c>DbToolsProxyController</c> (1 GiB, phpMyAdmin SQL içe aktarımı). Yani genel tavanı
    /// yazmak o yolları KIRMAZ.
    /// </remarks>
    public const long MaxRequestBodyBytes = 30_000_000;

    /// <summary>
    /// Dosya yükleme ucunun gövde tavanı: 200 MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BURADA ÖLÇÜLEN GERÇEK BİR KOPUKLUK VAR.</b> <c>FilesController.UploadAsync</c>
    /// <c>[RequestSizeLimit(200 MiB)]</c> taşıyor ama yükleme <c>IFormFile</c> ile, yani
    /// ÇOK PARÇALI (multipart) form olarak ayrıştırılıyor ve o ayrıştırıcının AYRI bir tavanı
    /// var: <c>FormOptions.MultipartBodyLengthLimit</c>, varsayılanı <b>128 MiB</b>. İkisi hiç
    /// hizalanmamıştı; 128 MiB ile 200 MiB arasındaki her yükleme, uç "izin veriyor" göründüğü
    /// hâlde ayrıştırıcıda <c>InvalidDataException</c> ile ölüyordu.
    /// </para>
    /// <para>
    /// Dosya/parça boyutu tek kaynaktır. Toplam istek için ayrıca sınırlı multipart
    /// başlık/sınırlandırıcı payı FileUploadRequestMaxBytes ile ayrılır.
    /// </para>
    /// </remarks>
    public const long FileUploadMaxBytes = 1024L * 1024L * 200L;

    /// <summary>One file plus bounded multipart headers/boundaries; the file itself remains limited to 200 MiB.</summary>
    public const long FileUploadRequestMaxBytes = FileUploadMaxBytes + 64L * 1024L;

    /// <summary>
    /// KİMLİKSİZ formların gövde tavanı: 64 KiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Genel 30 MB tavanı bu uçlar için anlamsız derecede geniş: abonelikten çıkma formu iki
    /// kısa alan, iletişim formu en fazla dört alan taşır ve en uzunu
    /// <c>SiteBuilderContactForm.MaxMessageLength</c> = 4000 KARAKTERdir. 64 KiB, UTF-8'de en
    /// kötü hâlde (karakter başına 4 bayt) bile o alanların toplamının dört katıdır — yani meşru
    /// bir gönderimi kesmez.
    /// </para>
    /// <para>
    /// Neden gerekli: hız sınırı İSTEK SAYISINI sınırlar, BAYT sayısını değil. Sınır dolana kadar
    /// izin verilen her istek 30 MB gövde taşıyabilirdi ve <c>[FromForm]</c> ayrıştırıcısı onu
    /// okumak zorundadır. İkisi birlikte kapatır.
    /// </para>
    /// </remarks>
    public const long PublicFormMaxBytes = 64L * 1024L;

    /// <summary>
    /// İstek BAŞLIKLARININ tamamının gelmesi için tanınan süre (yavaş-loris'e karşı).
    /// Kestrel varsayılanıyla aynı; daraltmak için ölçüm yok — mobil/uydu istemcileri kırardı.
    /// </summary>
    public static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Boşta duran bağlantının kapatılma süresi. Varsayılan 130 sn'den kısaltıldı: bağlantı
    /// tavanı artık VAR ve boşta duran bağlantılar o tavanı meşgul ediyor. 90 sn, panelin en
    /// seyrek yoklamasından (60 sn) uzun olduğu için meşru bir sekmenin bağlantısını düşürmez.
    /// </summary>
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Kestrel tavanlarını uygular.
    /// </summary>
    /// <remarks>
    /// UZUN İŞLER KIRILMAZ: burada istek SÜRESİNE dair bir sınır YOK. Kestrel'in gövde/yanıt
    /// için tuttuğu ölçüt SÜRE değil HIZ'dır (<c>MinRequestBodyDataRate</c> /
    /// <c>MinResponseDataRate</c>, 240 B/sn). Yedek indirme, bileşen kurulumu ve 1 GiB'lık SQL
    /// içe aktarımı veri aktığı sürece istedikleri kadar sürebilir — kesilen yalnız hiç veri
    /// göndermeyen bağlantıdır. Bu bilinçli: "30 dakikayı geçen istek düşsün" gibi bir kural,
    /// tam da bu panelin en uzun meşru işlerini keserdi.
    /// </remarks>
    public static void Apply(KestrelServerLimits limits)
    {
        limits.MaxConcurrentConnections = MaxConcurrentConnections;
        limits.MaxConcurrentUpgradedConnections = MaxConcurrentUpgradedConnections;
        limits.MaxRequestBodySize = MaxRequestBodyBytes;
        limits.RequestHeadersTimeout = RequestHeadersTimeout;
        limits.KeepAliveTimeout = KeepAliveTimeout;
    }

    /// <summary>
    /// Form ayrıştırıcısının tavanlarını uygular (bkz. <see cref="FileUploadMaxBytes"/>).
    /// </summary>
    public static void Apply(FormOptions form)
    {
        form.MultipartBodyLengthLimit = FileUploadMaxBytes;
    }
}
