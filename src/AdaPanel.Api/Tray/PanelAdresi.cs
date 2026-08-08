using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdaPanel.Api.Tray;

/// <summary>
/// Panelin tarayıcıda açılacak adresini bulur.
/// </summary>
/// <remarks>
/// <para>
/// Adres PANELE SORULMAZ. Bu programın var oluş sebebi panelin açılmadığı an; adresi panelin
/// kendi ucundan öğrenmeye çalışmak, tam ihtiyaç duyulduğunda çalışmayan bir çözüm olurdu.
/// Kaynak, Windows'un servis kaydındaki komut satırı: orada <c>--urls https://0.0.0.0:8890</c>
/// biçiminde duruyor ve servis durmuş olsa bile okunabiliyor.
/// </para>
/// <para>
/// GİZLİ GİRİŞ YOLU okunmaya çalışılmaz. O bilgi panelin yapılandırmasında ve orası bilerek
/// kısıtlı; ayrıca gerek de yok: panel kök adresten giriş yoluna kendisi yönlendiriyor.
/// </para>
/// </remarks>
public static class PanelAdresi
{
    private const string VarsayilanAdres = "https://localhost:8890/";

    public static string Bul()
    {
        var komut = ServisKomutSatiri();
        if (string.IsNullOrWhiteSpace(komut))
        {
            return VarsayilanAdres;
        }

        var eslesme = Regex.Match(komut, @"--urls\s+(?<url>\S+)", RegexOptions.IgnoreCase);
        if (!eslesme.Success)
        {
            return VarsayilanAdres;
        }

        var adres = eslesme.Groups["url"].Value.Trim('"', '\'');

        // Birden çok adres ";" ile ayrılabilir; ilki yeterli.
        var ilk = adres.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ilk))
        {
            return VarsayilanAdres;
        }

        // "0.0.0.0" ve "+" DİNLEME ifadeleridir, gidilecek adres değil. Tarayıcıya böyle
        // verilirse sayfa açılmaz; makinenin kendisinden bakıldığı için localhost'a çevrilir.
        ilk = ilk.Replace("://0.0.0.0", "://localhost", StringComparison.OrdinalIgnoreCase)
                 .Replace("://+", "://localhost", StringComparison.OrdinalIgnoreCase)
                 .Replace("://[::]", "://localhost", StringComparison.OrdinalIgnoreCase);

        return ilk.EndsWith('/') ? ilk : ilk + "/";
    }

    /// <summary>Servisin kayıtlı komut satırı; okunamazsa boş.</summary>
    private static string ServisKomutSatiri()
    {
        try
        {
            using var anahtar = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\AdaPanel", writable: false);
            return anahtar?.GetValue("ImagePath") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Panelin kurulu olduğu klasör (günlükleri açmak için); bulunamazsa boş.</summary>
    public static string KurulumKlasoru()
    {
        var komut = ServisKomutSatiri();
        if (string.IsNullOrWhiteSpace(komut))
        {
            return string.Empty;
        }

        // ImagePath tırnaklı ya da tırnaksız olabilir: "C:\...\AdaPanel.Api.exe" --urls ...
        var eslesme = Regex.Match(komut, "^\\s*\"(?<yol>[^\"]+)\"|^\\s*(?<yol2>\\S+)");
        var exe = eslesme.Groups["yol"].Success ? eslesme.Groups["yol"].Value : eslesme.Groups["yol2"].Value;

        try
        {
            return string.IsNullOrWhiteSpace(exe) ? string.Empty : Path.GetDirectoryName(exe) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
