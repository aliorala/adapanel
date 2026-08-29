namespace AdaPanel.Core.Common;

/// <summary>
/// Disk ve trafik kotalarının ölçek TABANI — panelin TEK KAYNAĞI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var — ölçülmüş dağınıklık.</b> Kota tabanı beş ayrı yerde HAM SAYIYLA yeniden
/// yazılmıştı: <c>CustomerScopeService</c> (bayt → MiB), <c>SubscriptionEnforcementService</c>
/// (GiB → bayt), <c>FileQuotaGuard</c> (iki kez, MiB → bayt) ve <c>BillingUnits</c> (iki sabit).
/// Beşi de bugün AYNI tabanı kullanıyor — yani ortada bir hesap hatası YOK, ölçüldü. Sorun
/// hatanın kendisi değil, hatanın MÜMKÜN olmasıydı: beş yerden birini 1000 tabanına çeviren bir
/// düzenleme hiçbir yerde kırmızı yakmadan geçerdi ve sonucu sessiz olurdu — panel kullanıcıya
/// "18.6 GiB" gösterirken yaptırımı 20 GB'ta uygulardı, yani müşteri kotasının bittiğini
/// %7 GEÇ öğrenirdi. Taban artık tek yerde; oradan sapmak imkânsız değilse bile GÖRÜNÜR.
/// </para>
/// <para>
/// <b>İSİMLER NEDEN BURADA DÜRÜST, TELDE DEĞİL.</b> Panelin dışa açtığı JSON alanları
/// <c>DiskUsageMb</c>, <c>TrafficLimitGb</c>, <c>MaxDiskMb</c> diye geçer; taşıdıkları değer ise
/// ikilik, yani gerçekte MiB ve GiB'dir. Bu bir ölçüm hatası DEĞİL, adlandırma borcudur ve
/// bilerek ÖDENMEDİ:
/// </para>
/// <list type="number">
/// <item>Alan adını değiştirmek JSON sözleşmesini değiştirir; panel arayüzü ile WHMCS/PHP modülü
/// AYNI ANDA kırılır ve modülün bu depoda testi yoktur.</item>
/// <item>WHMCS'in kendi dokümantasyonu da barındırma kotaları için 1 GB = 1024 MB kabul eder;
/// yani telde <c>Gb</c> yazmak o dünyada DOĞRU olandır.</item>
/// <item>Barındırma sektörünün tamamı (Plesk, cPanel) ikilik sayar ve etikette "GB" yazar.
/// Müşteriye "GiB" göstermek AdaPanel'i tek başına ayrıksı yapardı.</item>
/// </list>
/// <para>
/// Kullanıcıya gösterilen birim ZATEN dürüst: <c>frontend/src/utils/bytes.ts</c> 1024 tabanıyla
/// biçimler ve ekrana "GiB"/"MiB" yazar (kilidi <c>bytes.test.ts</c>). Yani telde uyum, ekranda
/// doğruluk, burada da tek kaynak. Adları "düzeltmek" isteyen bir sonraki okuyucu bu üç maddeyi
/// önce çürütmelidir.
/// </para>
/// </remarks>
public static class PanelUnits
{
    /// <summary>1 MiB = 1024 KiB = 1.048.576 bayt.</summary>
    public const long BytesPerMebibyte = 1024L * 1024L;

    /// <summary>1 GiB = 1024 MiB.</summary>
    public const long MebibytesPerGibibyte = 1024L;

    /// <summary>
    /// Baytı MiB'ye çevirir (AŞAĞI yuvarlar).
    /// </summary>
    /// <remarks>
    /// Aşağı yuvarlama bilinçli: kullanımı yukarı yuvarlamak, kimsenin yazmadığı baytı müşterinin
    /// kotasından düşmek olurdu. Negatif giriş (bozuk ölçüm) 0 sayılır — eksi kullanım diye bir şey
    /// yoktur ve eksi bir sayı kota oranını negatife çevirip göstergeyi bozardı.
    /// </remarks>
    public static long BytesToMebibytes(long bytes) =>
        bytes <= 0 ? 0 : bytes / BytesPerMebibyte;

    /// <summary>
    /// MiB cinsinden bir değeri bayta çevirir. Negatif giriş 0 döner.
    /// </summary>
    /// <remarks>
    /// Kota karşılaştırmaları BAYT üzerinden yapılır, MiB üzerinden değil: MiB'ye çevirip
    /// karşılaştırmak tamsayı bölmesi demektir ve 1 MiB'nin altındaki her dosya kotaya 0 olarak
    /// girerdi (bu hata bir kez yapıldı, bkz. <c>FileQuotaGuard.EnsureRoom</c>).
    /// </remarks>
    public static long MebibytesToBytes(long mebibytes) =>
        mebibytes <= 0 ? 0 : mebibytes * BytesPerMebibyte;

    /// <summary>
    /// GiB cinsinden bir kotayı bayta çevirir. Negatif giriş 0 döner.
    /// </summary>
    public static long GibibytesToBytes(long gibibytes) =>
        gibibytes <= 0 ? 0 : gibibytes * MebibytesPerGibibyte * BytesPerMebibyte;

    /// <summary>
    /// GiB cinsinden bir kotayı MiB'ye çevirir. Negatif giriş 0 döner.
    /// </summary>
    public static long GibibytesToMebibytes(long gibibytes) =>
        gibibytes <= 0 ? 0 : gibibytes * MebibytesPerGibibyte;
}
