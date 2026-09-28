namespace AdaPanel.Infrastructure.Ssl;

/// <summary>
/// "Yenilenen sertifika GERÇEKTEN sunuluyor mu?" sorusunun kararı.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR — canlıda ölçülmüş arıza.</b> 26–27 Eylül 2026'da <c>adapanel.host:443</c>
/// süresi DOLMUŞ bir sertifika sunuyordu; oysa geçerli, yenilenmiş sertifika 22 Ağustos'tan beri
/// depodaydı. Panel bunu hiçbir ekranda söylemedi. Sebep tek bir eksik adımdı:
/// </para>
/// <list type="number">
/// <item>win-acme sertifikayı yeniler, YENİ parmak iziyle depoya koyar.</item>
/// <item><c>RenewAllAsync</c> <c>ssl.json</c> kaydını depodan tazeler — kayıt artık YENİ ve uzak
/// bir bitiş tarihi taşır.</item>
/// <item>IIS bağı ESKİ sertifikada kalmışsa kimse bakmaz. Danışman yalnız kayıttaki bitiş
/// tarihine bakıyordu, o da artık sağlıklı görünüyordu.</item>
/// <item>Site, eski sertifika dolana kadar sessizce onu sunar.</item>
/// </list>
/// <para>
/// <b>Buradaki ders, kaydı tazeleme adımının YANLIŞ olduğu değildir.</b> O adım gerçek bir
/// arızayı (her gün gönderilen yalancı "süresi doldu" alarmı) kapatmak için eklenmişti ve
/// gerekçesi doğruydu. Ama bağ doğrulanmadığı için o düzeltme, gürültülü bir YANLIŞ POZİTİFİ
/// sessiz bir YANLIŞ NEGATİFE çevirdi. Bu sınıf eksik yarıyı tamamlar: kayıt tazelenirken bağ da
/// ÖLÇÜLÜR ve sapma varsa SÖYLENİR.
/// </para>
/// <para>
/// <b>IIS'e DOKUNMAZ:</b> saf karar mantığıdır, IIS kurulu olmayan bir makinede sınanabilir
/// (<see cref="SslRemovalPlan"/> ve <see cref="SslBindingVerification"/> ile aynı gerekçe).
/// </para>
/// </remarks>
public static class SslBindingDrift
{
    /// <summary>
    /// Bir bağ ölçümünün "hâlâ bugünün gerçeği" sayıldığı süre.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ölçüm <c>RenewAllAsync</c> içinde yapılır ve otomasyon onu düzenli çalıştırır
    /// (<c>AutomationService</c>, varsayılan AÇIK). Yani normal bir kurulumda damga her gün
    /// tazelenir ve bu pencere hiç devreye girmez.
    /// </para>
    /// <para>
    /// <b>Pencere, kalıcı yalancı alarmı önlemek için var.</b> Otomasyon kapalıysa ya da yenileme
    /// uzun süre koşmadıysa elde BAYAT bir ölçüm kalır. Bayat bir ölçümle "sertifikanız
    /// sunulmuyor" demek, yönetici bağı çoktan elle düzeltmiş olsa bile sonsuza kadar kırmızı
    /// göstermek olurdu — yani bu sınıfın kapatmaya çalıştığı gürültünün aynısını üretirdi.
    /// Bayat ölçüm "sorun yok" DEĞİL, "ölçmedim"dir; panel o durumda iddia etmez.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan MeasurementFreshness = TimeSpan.FromDays(30);

    /// <summary>Bağ ölçümünden çıkan karar.</summary>
    public enum Verdict
    {
        /// <summary>
        /// İDDİA YOK. Bağ doğru olabilir, ölçülememiş olabilir, ölçüm bayat olabilir ya da hiç
        /// ölçülmemiş (eski kayıt) olabilir. <b>"Sorun yok" ANLAMINA GELMEZ.</b>
        /// </summary>
        NoClaim = 0,

        /// <summary>
        /// ÖLÇÜLDÜ: bağ duruyor ama üzerinde bizim kayıtlı sertifikamız YOK. Tek olumsuz karar.
        /// </summary>
        Drifted = 1
    }

    /// <summary>
    /// <param name="isCertificateBound">
    /// <c>true</c> = bağ bizim sertifikamızı taşıyor, <c>false</c> = ÖLÇÜLDÜ ve taşımıyor,
    /// <c>null</c> = ÖLÇÜLEMEDİ (IIS okunamadı / sertifika alanı görünmedi).
    /// </param>
    /// <param name="checkedAtUtc">Ölçümün yapıldığı an; <c>null</c> = hiç ölçülmemiş (eski kayıt).</param>
    /// <param name="nowUtc">Şimdi.</param>
    /// </summary>
    public static Verdict Evaluate(bool? isCertificateBound, DateTime? checkedAtUtc, DateTime nowUtc)
    {
        // YALNIZ "false" bir iddiadır. true zaten sorunsuz; null ise ÖLÇÜLEMEDİ — ona "sapma" demek
        // geçici bir IIS okuma hatasını kalıcı bir kritik uyarıya çevirirdi.
        if (isCertificateBound != false)
        {
            return Verdict.NoClaim;
        }

        // Damgasız kayıt: bu alan eklenmeden önce yazılmış. Değerin NE ZAMAN ölçüldüğü bilinmiyor,
        // yani bugünün gerçeği olduğu söylenemez.
        if (checkedAtUtc is null)
        {
            return Verdict.NoClaim;
        }

        // Saat kayması: ölçüm "gelecekte" görünüyorsa bu bir bayatlık değil, saat farkıdır. Taze
        // sayılır; aksi hâlde birkaç saniyelik kayma gerçek bir sapmayı susturabilirdi.
        var yas = nowUtc - checkedAtUtc.Value;
        if (yas > MeasurementFreshness)
        {
            return Verdict.NoClaim;
        }

        return Verdict.Drifted;
    }
}
