using System.Text;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// PowerShell tek tirnakli literal kacisinin TEK kaynagi.
/// PowerShell yalniz ASCII ' degil, U+2018, U+2019, U+201A ve U+201B karakterlerini de tek
/// tirnak sayar. Betikler -EncodedCommand ile (UTF-16) gittigi icin bu karakterler oldugu gibi
/// PowerShell'e ulasir; yalniz ASCII ' ikilenirse "x’; komut; ’" literal'den cikar ve
/// LocalSystem olarak calisir. Tek tirnakli dizede baska hicbir karakter ($, `, ", {, }) ozel
/// degildir. Elle Replace("'", "''") YAZILMAZ; bu sinifi kullan (PowerShellQuotingTests tarar).
/// </summary>
public static class PowerShellQuoting
{
    /// <summary>
    /// Tek tirnakli bir PowerShell dizesinin ICERIGI (cevresindeki tirnaklar eklenmez):
    /// ' ve U+2018..U+201B ikilenir. null bos dizedir. NUL (U+0000) kabul edilmez.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains('\0'))
        {
            throw new ArgumentException("PowerShell literal'i NUL karakteri tasiyamaz.", nameof(value));
        }

        var needsEscape = false;
        foreach (var c in value)
        {
            if (IsSingleQuote(c))
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            sb.Append(c);
            if (IsSingleQuote(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Tirnaklariyla birlikte tam literal: 'icerik'.</summary>
    public static string Literal(string? value) => "'" + Escape(value) + "'";

    private static bool IsSingleQuote(char c) => c == '\'' || (c >= '‘' && c <= '‛');
}
