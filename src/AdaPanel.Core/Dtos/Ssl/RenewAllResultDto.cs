using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using System.Text.Json.Serialization;

namespace AdaPanel.Core.Dtos.Ssl;

public sealed class RenewAllResultDto
{
    /// <summary>Posta eşitlemesi bu yenileme içinde tamamlandı; null sonuç değişiklik gerektirmediğini belirtir.</summary>
    [JsonIgnore] // In-process orchestration result; not part of the public SSL response.
    public bool MailSynchronizationPerformed { get; set; }

    [JsonIgnore]
    public MailCertificateDto? MailCertificate { get; set; }

    /// <summary>
    /// Yenilenen sertifika sayisi. <see cref="Measured"/> false ise ANLAMSIZDIR.
    /// </summary>
    public int RenewedCount { get; set; }

    /// <summary>
    /// Yenileme GERCEKTEN calistirilabildi mi.
    /// <para>
    /// false = win-acme bulunamadi ya da hic calistirilamadi. Bu durumda "tamamlandi" DENMEZ.
    /// Eski davranis bunun tersiydi: win-acme kurulu degilken de sonuc basariyla donuyor,
    /// otomasyon durumu "completed" yaziyordu. Ekranda her gun "SSL yenileme: tamamlandi"
    /// gorunuyor, gercekte HICBIR sertifika yenilenmiyordu -- ve bu ancak 90 gun sonra tum
    /// siteler ayni anda dustugunde fark edilirdi.
    /// </para>
    /// </summary>
    public bool Measured { get; set; } = true;

    /// <summary>Olculemedi ya da basarisiz olduysa SEBEBI; kullaniciya gosterilir.</summary>
    private string failureReason = string.Empty;
    public string FailureReason
    {
        get => failureReason;
        set => failureReason = SecretRedactor.Redact(value);
    }

    // Command output is returned by the SSL API. Parse provider output before assigning it here.
    private string output = string.Empty;
    public string Output
    {
        get => output;
        set => output = SecretRedactor.Redact(value);
    }

    /// <summary>
    /// Yenileme turundan sonra panel kaydı (<c>config/ssl.json</c>) ile sertifika deposu
    /// karşılaştırılıp GERÇEĞE göre tazelenen kayıt sayısı.
    /// </summary>
    /// <remarks>
    /// Yenileme ile doğrulama AYRI şeylerdir ve ayrı sayılır: win-acme "yenilenecek bir şey yok"
    /// dediğinde <see cref="RenewedCount"/> 0'dır, ama kayıtların gerçekle uyuştuğu yine de
    /// ölçülmüş olur. Eskiden bu tur hiç yapılmadığı için yenilenmiş bir sertifika kayıtta eski
    /// bitiş tarihiyle kalıyor ve panel her gün "süresi doldu" uyarısı üretiyordu.
    /// </remarks>
    public int VerifiedCount { get; set; }

    /// <summary>
    /// Kaydı olan ama sertifika deposunda KARŞILIĞI BULUNMAYAN sertifika sayısı (ölçülmüş).
    /// Sıfırdan büyükse panel o kayıtlar için "geçerli" demeyi bırakmalıdır.
    /// </summary>
    public int MissingFromStoreCount { get; set; }

    /// <summary>
    /// ÖLÇÜLEMEYEN kayıt sayısı (depo açılamadı, host adı yok). Bunlar "geçersiz" DEĞİLDİR;
    /// yalnızca bu turda doğrulanamamıştır ve kayıtlarına dokunulmamıştır.
    /// </summary>
    public int UnverifiedCount { get; set; }

    /// <summary>
    /// ÖLÇÜLDÜ: IIS bağı duruyor ama üzerinde panelin kayıtlı sertifikası YOK — yani yenilenen
    /// sertifika sunulmuyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu sayı canlıda ölçülmüş bir arızadan doğdu.</b> Yenileme kaydı depodan tazeliyor ama
    /// bağı hiç sormuyordu; bağ eski sertifikada kalınca kayıt sağlıklı görünüyor, site ise eski
    /// sertifika dolana kadar onu sunuyordu. 26–27 Eylül 2026'da <c>adapanel.host</c> tam bunu
    /// yaşadı: geçerli sertifika 22 Ağustos'tan beri depodaydı, 443 bağı süresi dolmuş olanı
    /// sunuyordu ve panel hiçbir şey söylemedi.
    /// </para>
    /// <para>
    /// Sıfır olması "bağlar doğru" DEMEK DEĞİLDİR: bağ listesi hiç okunamadıysa ölçüm yapılmaz ve
    /// bu sayı yine 0 kalır. Okunamama <see cref="BindingsMeasured"/> ile ayrılır.
    /// </para>
    /// </remarks>
    public int BindingDriftCount { get; set; }

    /// <summary>IIS bağ listesi bu turda OKUNABİLDİ mi. false ise bağ sayıları anlamsızdır.</summary>
    public bool BindingsMeasured { get; set; }
}
