using System.Security.Cryptography;
using System.Text;

namespace AdaPanel.Infrastructure.Licensing;

/// <summary>
/// Parmak izinin SAF (yan etkisiz) hesabı. Girdileri toplayan taraf
/// <see cref="MachineFingerprintProvider"/>; burada yalnız "aynı girdi → aynı çıktı" kuralı yaşar,
/// böylece kararlılık makineye/kayıt defterine gerek olmadan test edilebilir.
/// </summary>
public static class MachineFingerprint
{
    /// <summary>Girdi bulunamadığında kullanılan yer tutucu (boş dizgeyle karışmasın diye).</summary>
    internal const string Missing = "-";

    /// <summary>
    /// Makine kimliği + kurulum kimliğinden 64 karakterlik onaltılık parmak izi üretir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Girdiler ayraçla (<c>|</c>) ve etiketle birleştirilir. Etiketsiz düz birleştirmede
    /// ("ab"+"c" ile "a"+"bc") iki farklı makine aynı özeti üretebilirdi.
    /// </para>
    /// <para>
    /// Büyük/küçük harf ve süslü parantez normalize edilir: kayıt defteri ve WMI aynı GUID'i
    /// farklı biçimlerde ("{...}" / büyük harf) dönebiliyor; normalize edilmezse parmak izi
    /// yalnızca okuma yolunu değiştirmekle DEĞİŞİRDİ.
    /// </para>
    /// </remarks>
    public static string Compute(string? machineIdentity, string installId)
    {
        var machine = Normalize(machineIdentity);
        var install = Normalize(installId);

        var material = $"adapanel-fingerprint-v1|machine={machine}|install={install}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Değer gerçekten bir makine kimliği mi? Boş, sıfır GUID ve "hepsi F" gibi doldurulmamış
    /// üretici değerleri KİMLİK SAYILMAZ.
    /// </summary>
    /// <remarks>
    /// Bu makinede ölçüldü: <c>Win32_BIOS.SerialNumber</c> = "To Be Filled By O.E.M",
    /// <c>Win32_ComputerSystemProduct.UUID</c> = "1273EE80-F197-3B91-0000-000000000000".
    /// Üreticilerin doldurmadığı alanlar sabit ve TÜM MAKİNELERDE AYNI olur; kimlik olarak
    /// kullanılırsa binlerce kurulum tek parmak izine düşer.
    /// </remarks>
    public static bool IsUsableIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = Normalize(value);
        if (trimmed.Length < 8)
        {
            return false;
        }

        // Yalnız 0/F/tire kalan değer (00000000-0000-..., ffffffff-ffff-...) doldurulmamış demektir.
        var meaningful = trimmed.Where(character => character is not ('0' or 'f' or '-' or ' ')).ToArray();
        if (meaningful.Length == 0)
        {
            return false;
        }

        // Üreticinin bıraktığı bilinen yer tutucular.
        return trimmed switch
        {
            "to be filled by o.e.m." => false,
            "to be filled by o.e.m" => false,
            "system serial number" => false,
            "default string" => false,
            "none" => false,
            _ => true
        };
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Missing;
        }

        return value.Trim().Trim('{', '}').ToLowerInvariant();
    }
}
