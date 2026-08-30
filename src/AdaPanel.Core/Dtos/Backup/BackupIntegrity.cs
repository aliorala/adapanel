namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Bir yedek arşivinin ÖLÇÜLMÜŞ bütünlüğü. <b>Üç değerlidir ve ikiye indirilemez.</b>
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BackupPresence"/> ile aynı disiplin, aynı gerekçe: "ölçemedim" ile "bozuk" aynı şey
/// değildir. Şifreli bir arşivin içi parola olmadan okunamaz; kilitli bir dosya o an açılamaz.
/// Bunlara "bozuk" demek, sağlam yedeği çöp ilan etmek olurdu — <see cref="Unknown"/> tam da bu
/// yüzden var ve varsayılan odur.
/// </para>
/// <para>
/// <b>ÖLÇÜLEN HATA:</b> yedek yolunda boyut/bütünlük HİÇ ölçülmüyordu. Sıfır baytlık ya da tek
/// girdisi olmayan bir arşiv "created" damgasıyla listeye giriyor, "Doğrula" düğmesi de arşivi HİÇ
/// AÇMADIĞI için ondan geçiyordu. Yani panel "yedek başarılı + doğrulamadan geçti" diyebiliyordu
/// ve bunun yalan olduğu ancak felaket anında anlaşılırdı.
/// </para>
/// </remarks>
public static class BackupIntegrity
{
    /// <summary>Ölçülmedi ya da ölçülemedi. <b>"Bozuk" ANLAMINA GELMEZ.</b></summary>
    public const string Unknown = "unknown";

    /// <summary>Ölçüldü: arşiv açıldı, içinde gerçek girdi var.</summary>
    public const string Sound = "sound";

    /// <summary>Ölçüldü: boş, açılamıyor ya da hiç girdisi yok. <b>Geri yükleme noktası SAYILMAZ.</b></summary>
    public const string Unsound = "unsound";
}
