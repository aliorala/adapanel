using System.Security.Cryptography;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// Motorun İKİ hedefte de aynı çalışan küçük yardımcıları.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR:</b> bu klasördeki dosyalar iki kez derleniyor — panel için <c>net8.0-windows</c>,
/// IIS modülü için <c>net48</c> (bkz. <c>AdaPanel.HtaccessModule.csproj</c>). Motorun kullandığı
/// birkaç BCL üyesi yalnızca .NET (Core) tarafında var: <c>char.IsAsciiDigit</c>,
/// <c>SHA1.HashData</c>, <c>CryptographicOperations.FixedTimeEquals</c>,
/// <c>FileInfo.LinkTarget</c>. Modül bunlar yüzünden motoru <b>hiç yükleyemiyordu</b> — özellik
/// yazılmıştı, ekranı vardı, tek bir istek bile etkilenmiyordu.
/// </para>
/// <para>
/// <b>NEDEN <c>#if</c> DEĞİL:</b> iki hedefte iki ayrı yazım, aynı satırın iki sürümü demektir ve
/// ikisi bir gün ayrı düşer. Buradaki karşılıkların hepsi <b>her iki hedefte de aynı</b> çalışır,
/// bu yüzden koşullu derleme kullanılmadı. Tek istisna <c>PathContainment</c> dosyasındadır ve
/// gerekçesi orada yazılıdır.
/// </para>
/// <para>
/// <b>DAVRANIŞ AYNI OLMAK ZORUNDA:</b> her üye BCL aslıyla karşılaştırmalı olarak ölçülüyor
/// (<c>HtaccessPortableApiTests</c>) — "aynıdır" varsayılmıyor, net8 tarafında BCL üyesiyle yan
/// yana koşturulup denetleniyor.
/// </para>
/// </remarks>
internal static class HtaccessPortableApi
{
    /// <summary><c>char.IsAsciiDigit</c> karşılığı: yalnızca U+0030..U+0039.</summary>
    /// <remarks>
    /// <c>char.IsDigit</c> DEĞİL: o, Unicode'un bütün ondalık rakamlarını (Arap-Hint ٣, Devanagari ३...)
    /// kabul eder. IP öneki ve geri başvuru numarası ayrıştırırken bu, ASCII olmayan bir rakamın
    /// sayı sanılıp <c>- '0'</c> ile çöp değere dönüşmesi demekti.
    /// </remarks>
    public static bool AsciiRakamMi(char karakter) => karakter is >= '0' and <= '9';

    /// <summary><c>char.IsAsciiLetterOrDigit</c> karşılığı: A-Z, a-z, 0-9.</summary>
    public static bool AsciiHarfVeyaRakamMi(char karakter) =>
        AsciiRakamMi(karakter) || karakter is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    /// <summary>
    /// <c>SHA1.HashData</c> karşılığı. Örnek oluşturup <c>ComputeHash</c> çağırır — net8'deki
    /// static üye de aynı algoritmayı çalıştırır, tek farkı örnek ayırmamasıdır.
    /// </summary>
    /// <remarks>
    /// SHA-1 BURADA GÜVENLİK SEÇİMİ DEĞİL, UYUMLULUK ZORUNLULUĞU: Apache <c>htpasswd -s</c>
    /// biçimi (<c>{SHA}</c>) tanımı gereği SHA-1'dir. Bunu değiştirmek mevcut parola dosyalarını
    /// okunamaz yapardı.
    /// </remarks>
    public static byte[] Sha1(byte[] veri)
    {
        using var sha1 = SHA1.Create();
        return sha1.ComputeHash(veri);
    }

    /// <summary>
    /// <c>CryptographicOperations.FixedTimeEquals</c> karşılığı: uzunluklar eşitse SABİT SÜREDE
    /// karşılaştırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Erken çıkış YOK</b> ve sebebi budur: normal bir <c>==</c> ilk farklı bayta gelince döner,
    /// yani doğru cevaba yaklaştıkça çağrı UZAR. Saldırgan bunu ölçerek özeti bayt bayt çıkarabilir.
    /// Döngü her zaman bütün baytları gezer ve farkları biriktirir.
    /// </para>
    /// <para>
    /// Uzunluk farkı sızar — BCL aslı da aynı şeyi yapar (<c>a.Length != b.Length</c> hemen
    /// <c>false</c>). Özet uzunlukları sabit olduğu için bu bir bilgi taşımaz.
    /// </para>
    /// </remarks>
    public static bool SabitSuredeEsit(byte[]? sol, byte[]? sag)
    {
        if (sol is null || sag is null || sol.Length != sag.Length)
        {
            return false;
        }

        var fark = 0;
        for (var i = 0; i < sol.Length; i++)
        {
            fark |= sol[i] ^ sag[i];
        }

        return fark == 0;
    }

    /// <summary>
    /// Diziyi SIFIRLAR (<c>Array.Clear(dizi)</c> karşılığı).
    /// </summary>
    /// <remarks>
    /// Parola/özet tamponlarını kullanımdan sonra silmek içindir: temizlenmeyen bir tampon, süreç
    /// belleği döküldüğünde (crash dump, hazırda bekletme dosyası) düz metin parolayı taşır.
    /// </remarks>
    public static void Sifirla(Array? dizi)
    {
        if (dizi is not null)
        {
            Array.Clear(dizi, 0, dizi.Length);
        }
    }
}
