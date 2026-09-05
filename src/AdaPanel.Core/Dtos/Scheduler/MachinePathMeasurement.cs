namespace AdaPanel.Core.Dtos.Scheduler;

/// <summary>
/// "Klasorler GERCEKTEN makine PATH'ine girdi mi?" sorusunun UC DEGERLI cevabi.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR.</b> <c>SchedulerService.AddPythonToMachinePathAsync</c> bir HKLM yazmasi yapiyor
/// (<c>SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c>) ama <c>Set-ItemProperty</c>
/// cagrisinda <c>-ErrorAction Stop</c> YOKTU, kosucu gevsekti ve GERI OKUMA hic yoktu. Basilan
/// <c>ADAPANEL-DEGISIKLIK=</c> sayisi EKLENMESI ISTENEN klasor sayisiydi; uc de tam onu
/// "Python added to machine PATH (N folder(s))" diye yaziyordu. Yani panel MAKINE GENELI bir
/// yazma icin, hicbir olcume dayanmayan bir sayi raporluyordu.
/// </para>
/// <para>
/// <b>UC DEGER.</b> <see cref="Measured"/> false ise sonuc "eklenmedi" DEGIL, BILINMIYOR'dur:
/// yazma tutmus ama geri okuma yapilamamis olabilir. Bu ikisini ayni goruntuye dusurmek bu
/// depoda bedeli defalarca odenmis hata sinifidir.
/// </para>
/// </remarks>
public sealed class MachinePathMeasurement
{
    /// <summary>PATH'te OLMASI beklenen (diskte var olan) klasor sayisi.</summary>
    public int Expected { get; init; }

    /// <summary>Yazmadan ONCE eksik olan, yani EKLENMESI ISTENEN klasor sayisi.</summary>
    public int Requested { get; init; }

    /// <summary>Yazmadan SONRA GERI OKUNARAK PATH'te bulunan klasor sayisi.</summary>
    public int Verified { get; init; }

    /// <summary>Geri okuma yapilabildi mi?</summary>
    public bool Measured { get; init; }

    /// <summary>Insan tarafindan okunacak kanit.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Hedeflenen butun klasorler PATH'te OLCULDU.</summary>
    public bool Applied => Measured && Expected > 0 && Verified >= Expected;

    /// <summary>Olculdu ve EKSIK: en az bir klasor PATH'e girmedi.</summary>
    public bool Incomplete => Measured && Verified < Expected;

    /// <summary>Sonuc okunamadi. "Eklenmedi" DEGIL, BILINMIYOR.</summary>
    public bool Unverified => !Measured;

    /// <summary>Gosterilecek anahtar; her sey olculdu ve yolundaysa bos doner.</summary>
    public string NoticeKey => Applied
        ? string.Empty
        : Unverified
            ? "cron.python_path_unverified"
            : "cron.python_path_incomplete";

    public static MachinePathMeasurement Olculemedi(string detail = "")
        => new() { Detail = detail };

    /// <summary>
    /// KARAR KURALI. Betigin bastigi damgalar ayristirilir.
    /// </summary>
    /// <remarks>
    /// Damganin BULUNMAMASI "sifir klasor eklendi" DEGILDIR: betik damgayi basamadan dustuyse
    /// sonuc OLCULEMEDI'dir. Eski kod tam tersini yapiyordu -- eslesme yoksa <c>0</c> alip
    /// devam ediyordu.
    /// </remarks>
    public static MachinePathMeasurement Olc(string? standartCikti)
    {
        var cikti = standartCikti ?? string.Empty;

        var istenen = DamgaOku(cikti, "ADAPANEL-DEGISIKLIK=");
        var dogrulanan = DamgaOku(cikti, "ADAPANEL-PATH-DOGRULANDI=");
        var hedef = DamgaOku(cikti, "ADAPANEL-PATH-HEDEF=");

        if (dogrulanan is null || hedef is null)
        {
            return Olculemedi("Geri okuma damgasi basilmadi: makine PATH'i OKUNAMADI.");
        }

        return new MachinePathMeasurement
        {
            Measured = true,
            Requested = istenen ?? 0,
            Verified = dogrulanan.Value,
            Expected = hedef.Value,
            Detail = $"hedef {hedef.Value}, istenen {istenen ?? 0}, olculen {dogrulanan.Value}"
        };
    }

    private static int? DamgaOku(string cikti, string damga)
    {
        var index = cikti.IndexOf(damga, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + damga.Length;
        var end = start;
        while (end < cikti.Length && char.IsDigit(cikti[end]))
        {
            end += 1;
        }

        return end > start && int.TryParse(cikti[start..end], out var value) ? value : null;
    }
}
