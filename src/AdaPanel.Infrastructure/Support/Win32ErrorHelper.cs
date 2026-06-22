using System.Text;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Win32/COM istisnalarını DİLE BAĞIMSIZ sınıflandırır. Sunucu Türkçe (veya başka dilde) Windows
/// olduğunda hata METİNLERİ yerelleşir ve İngilizce substring eşleşmeleri tutmaz; HRESULT kodu ise
/// dilden bağımsızdır. Flatten() mesaja her seviyenin HRESULT hex'ini ekler; predikatlar hem hex'i
/// hem İngilizce metni kontrol eder (her iki dilde de doğru çalışır).
/// </summary>
public static class Win32ErrorHelper
{
    private const StringComparison Oic = StringComparison.OrdinalIgnoreCase;

    /// <summary>İstisna zincirini mesaj + HRESULT hex olarak birleştirir.</summary>
    public static string Flatten(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        while (current is not null)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(current.Message)
                   .Append(" (HRESULT=0x").Append(current.HResult.ToString("X8")).Append(')');
            current = current.InnerException;
        }

        return builder.ToString();
    }

    /// <summary>Erişim/izin reddi: 0x80070005 (E_ACCESSDENIED), 0x80070522 (PRIVILEGE_NOT_HELD).</summary>
    public static bool IsAccessDenied(string flat) =>
        flat.Contains("0x80070005", Oic) ||
        flat.Contains("0x80070522", Oic) ||
        flat.Contains("access is denied", Oic) ||
        flat.Contains("permission", Oic) ||
        flat.Contains("unauthorized", Oic) ||
        flat.Contains("insufficient privileges", Oic);

    /// <summary>Kaynak başka işlemde / kilitli: 0x80070020 (ERROR_SHARING_VIOLATION).</summary>
    public static bool IsInUse(string flat) =>
        flat.Contains("0x80070020", Oic) ||
        flat.Contains("being used by another process", Oic) ||
        flat.Contains("the process cannot access the file", Oic) ||
        flat.Contains("configuration file is locked", Oic) ||
        flat.Contains("is locked", Oic);

    /// <summary>Zaten var / çakışma: 0x800700B7 (ERROR_ALREADY_EXISTS).</summary>
    public static bool IsAlreadyExists(string flat) =>
        flat.Contains("0x800700B7", Oic) ||
        flat.Contains("already exists", Oic) ||
        flat.Contains("already been added", Oic) ||
        flat.Contains("cannot add duplicate", Oic) ||
        flat.Contains("unique key attribute", Oic);
}
