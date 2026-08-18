namespace AdaPanel.Infrastructure.Billing;

/// <summary>
/// Panel birimlerini WHMCS'in <c>tblhosting</c> tablosunda beklediği birime (MB) çevirir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden panelde çevriliyor:</b> çeviri PHP tarafında yapılsaydı, orada bir kez yanlış yazılan
/// bölme müşteriye yanlış fatura olarak dönerdi ve bu depoda PHP'nin testi yok. Burada saf bir
/// fonksiyon olarak durduğu için her sınır durumu ölçülebiliyor.
/// </para>
/// <para>
/// <b>1 MB = 1024 KB = 1048576 bayt</b> ve <b>1 GB = 1024 MB</b> (ikilik). WHMCS'in kendi
/// dokümantasyonu da bant genişliği/disk için 1 GB = 1024 MB kabul eder.
/// </para>
/// </remarks>
public static class BillingUnits
{
    private const long BytesPerMegabyte = 1024L * 1024L;

    private const long MegabytesPerGigabyte = 1024L;

    /// <summary>
    /// Baytı MB'ye çevirir (aşağı yuvarlar).
    /// </summary>
    /// <remarks>
    /// <b>Aşağı yuvarlama bilinçli:</b> faturalamaya giren bir sayıda müşterinin aleyhine yuvarlamak,
    /// kimsenin kullanmadığı baytı faturalamak olurdu. Negatif giriş (bozuk günlük) 0 sayılır;
    /// eksi kullanım diye bir şey yoktur ve WHMCS'e eksi yazmak aşım hesabını bozardı.
    /// </remarks>
    public static long BytesToMegabytes(long bytes) =>
        bytes <= 0 ? 0 : bytes / BytesPerMegabyte;

    /// <summary>
    /// GB cinsinden kotayı MB'ye çevirir. <c>0</c> (sınırsız) olduğu gibi kalır.
    /// </summary>
    /// <remarks>
    /// Panelde <c>0</c> "sınırsız" demektir ve WHMCS'te de <c>0</c> sınırsız sayılır; bu yüzden
    /// çarpım öncesi korunur. Negatif kota anlamsızdır, sınırsıza (0) düşürülür — çarpsaydık
    /// WHMCS'e eksi bir limit yazılır ve her hesap "kotayı aşmış" görünürdü.
    /// </remarks>
    public static long GigabytesToMegabytes(int gigabytes) =>
        gigabytes <= 0 ? 0 : gigabytes * MegabytesPerGigabyte;

    /// <summary>
    /// Disk kotasını (panelde zaten MB) normalleştirir: negatif değer sınırsız (0) sayılır.
    /// </summary>
    public static long NormalizeLimitMb(int limitMb) =>
        limitMb <= 0 ? 0 : limitMb;
}
