using System.Text.RegularExpressions;

namespace AdaPanel.Core.Dtos.Scheduler;

/// <summary>
/// Zamanlanmış görev adının TEK doğruluk kaynağı.
/// <para>
/// Neden ayrı bir sınıf: aynı kural üç yerde soruluyor — istek DTO'sunun doğrulama özniteliğinde,
/// <c>SchedulerService.ValidateRequest</c> içinde ve ada dokunan her işlem ucunda (çalıştır, sil,
/// etkinleştir, durdur…). Üç ayrı yere yazılsaydı biri güncellenir, diğeri unutulurdu; bu depoda
/// aynı hata daha önce oldu.
/// </para>
/// <para>
/// <b>Neden görev adı bir güvenlik sınırıdır:</b> ad, Windows Görev Zamanlayıcı'yı süren PowerShell
/// betiklerinin İÇİNE gömülür ve panel servisi LocalSystem olarak koşar. Kaçış tek başına yeterli
/// bir savunma DEĞİLDİR (bir gün biri yine çift tırnaklı bir dize yazabilir); bu yüzden kaçışın
/// yanında adın kendisi de dar bir beyaz listeyle sınırlanır. İki katman birbirinden bağımsızdır.
/// </para>
/// </summary>
public static class TaskNameRules
{
    /// <summary>
    /// Kullanıcının YENİ görev için verebileceği ad. Kasten dar: harf, rakam, boşluk, nokta,
    /// alt çizgi ve tire. Kabuk için anlamlı olan hiçbir karakter (<c>$ ` ' " ( ) ; | &amp;</c>,
    /// satır sonu, denetim karakterleri) bu kümede yoktur.
    /// </summary>
    public const string CreatablePattern = "^[A-Za-z0-9 ._-]{1,60}$";

    /// <summary>
    /// Panelin müşteri/bayi görevlerine eklediği abonelik öneki: <c>u&lt;32 onaltılık&gt;--</c>.
    /// <para>
    /// Önek de beyaz listeye UYAR (u + onaltılık + tire), dolayısıyla ön ekli ad da güvenlidir;
    /// ama uzunluğu 35 karakter arttırır. Bu yüzden doğrulama, ön eki AYIRIP kullanıcının gerçekten
    /// yazdığı kısmı ölçer — aksi hâlde 60 karakter sınırı sessizce 25'e düşerdi.
    /// </para>
    /// </summary>
    private static readonly Regex OwnerPrefix = new("^u[0-9a-fA-F]{32}--", RegexOptions.Compiled);

    private static readonly Regex Creatable = new(CreatablePattern, RegexOptions.Compiled);

    /// <summary>Kullanıcının yazdığı kısım: varsa abonelik öneki ayrılır.</summary>
    public static string StripOwnerPrefix(string? name)
    {
        var deger = (name ?? string.Empty).Trim();
        var eslesme = OwnerPrefix.Match(deger);
        return eslesme.Success ? deger[eslesme.Length..] : deger;
    }

    /// <summary>Yeni bir görev bu adla kurulabilir mi (ön ek varsa hesaba katılır).</summary>
    public static bool IsCreatable(string? name) => Creatable.IsMatch(StripOwnerPrefix(name));

    /// <summary>
    /// VAR OLAN bir görev üzerinde işlem yapılabilir mi (çalıştır, sil, devre dışı bırak, çıktı oku).
    /// <para>
    /// Burada kural bilerek <see cref="IsCreatable"/>'dan GENİŞ: panelde bu doğrulama hiç yokken
    /// kurulmuş görevlerin adında Türkçe harf (ör. "Günlük Yedek") olabilir. Dar kuralı buraya da
    /// uygulamak, o görevleri panelden silinemez hâle getirirdi — yani bir güvenlik düzeltmesi
    /// kullanıcının elinden var olan bir yeteneği alırdı.
    /// </para>
    /// <para>
    /// Genişleme YALNIZCA Unicode harf/rakam yönünde. Kabuk için anlamlı her karakter burada da
    /// reddedilir; yani gevşeme, korumanın kendisine dokunmaz.
    /// </para>
    /// </summary>
    public static bool IsUsable(string? name)
    {
        var deger = (name ?? string.Empty).Trim();
        if (deger.Length is 0 or > 120)
        {
            return false;
        }

        foreach (var karakter in deger)
        {
            if (!char.IsLetterOrDigit(karakter) && karakter is not (' ' or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
