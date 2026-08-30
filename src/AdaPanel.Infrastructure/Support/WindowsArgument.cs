using System.Text;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Bir değeri Windows'un argüman ayrıştırıcısına (<c>CommandLineToArgvW</c> / CRT) TEK argüman
/// olarak geçirecek biçimde tırnaklar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden <c>Replace("\"", "\\\"")</c> YETMEZ:</b> Windows'ta ters bölünün kaçış anlamı yalnız
/// bir tırnaktan ÖNCE gelirken doğar ve ters bölüler <i>ikilenir</i>. Ölçülmüş iki kırılma:
/// </para>
/// <list type="bullet">
/// <item>Girdideki <c>\"</c> dizisi o basit değiştirmeyle <c>\\"</c> olur; ayrıştırıcı bunu
/// "gerçek bir ters bölü + TIRNAĞI KAPAT" diye okur ve sonrasındaki metin <b>yeni bir argüman</b>
/// olur. Docker'da bu, <c>-e</c> değerinden <c>--privileged -v C:\:/host</c> eklemek demekti.</item>
/// <item>Değer <c>\</c> ile BİTİYORSA kapanış tırnağı kaçırılmış olur ve argüman bir sonrakiyle
/// birleşir.</item>
/// </list>
/// <para>
/// Algoritma Microsoft'un belgelediği kuraldır: tırnaktan (ve dizenin sonundaki kapanış
/// tırnağından) önceki ters bölü dizisi ikilenir, tırnağın kendisi <c>\"</c> yapılır.
/// </para>
/// </remarks>
internal static class WindowsArgument
{
    public static string Quote(string? value)
    {
        var deger = value ?? string.Empty;
        var builder = new StringBuilder(deger.Length + 8);
        builder.Append('"');

        var tersBolu = 0;
        foreach (var karakter in deger)
        {
            if (karakter == '\\')
            {
                tersBolu++;
                continue;
            }

            if (karakter == '"')
            {
                // Tırnaktan önceki ters bölüler İKİLENİR, sonra tırnak kaçırılır.
                builder.Append('\\', (tersBolu * 2) + 1).Append('"');
                tersBolu = 0;
                continue;
            }

            builder.Append('\\', tersBolu);
            builder.Append(karakter);
            tersBolu = 0;
        }

        // Kapanış tırnağından önceki ters bölüler de İKİLENİR; aksi hâlde kapanış tırnağını
        // kaçırırlar ve argüman bir sonrakiyle birleşir.
        builder.Append('\\', tersBolu * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
