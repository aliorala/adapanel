namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Bir yedek kopyasının ÖLÇÜLMÜŞ varlığı. <b>Üç değerlidir ve ikiye indirilemez.</b>
/// </summary>
/// <remarks>
/// <para>
/// "Ölçemedim" ile "yok" aynı şey değildir. Geçici bir ağ arızasını, hız sınırını ya da kimlik
/// hatasını "yedek silinmiş" diye göstermek, kullanıcıyı yedeği OLMADIĞINA inandırır — panelin
/// verebileceği en pahalı yalan. Bu yüzden değerler dize sabiti olarak TEK yerde tutulur; iki
/// ayrı yerde yazılan bir dize, biri düzeltilip diğeri unutulduğunda sessizce ayrışır.
/// </para>
/// <para>
/// Arayüz bu değerleri kendi metnine çevirir; sunucu ÇEVİRİ ÜRETMEZ (bkz. depo kuralı: sunucunun
/// serbest metni dilden bağımsız teknik bilgidir).
/// </para>
/// </remarks>
public static class BackupPresence
{
    /// <summary>Bu yedeğin böyle bir kopyası HİÇ YOK (örn. yalnız yerel alınmış yedeğin uzak kopyası).</summary>
    public const string None = "none";

    /// <summary>Ölçülmedi ya da ölçülemedi. <b>"Yok" ANLAMINA GELMEZ.</b></summary>
    public const string Unknown = "unknown";

    /// <summary>Ölçüldü: duruyor.</summary>
    public const string Present = "present";

    /// <summary>Ölçüldü: yok.</summary>
    public const string Missing = "missing";
}
