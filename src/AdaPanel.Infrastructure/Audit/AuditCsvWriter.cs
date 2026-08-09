using System.Text;
using AdaPanel.Core.Dtos.Audit;

namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Denetim kayıtlarını CSV olarak AKITARAK yazar (belleğe toplamadan).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden akış:</b> dışa aktarma tam da en çok gerektiği anda — yıllık bir denetim, yüz binlerce
/// kayıt — çalışmak zorundadır. Önce listeye toplayıp sonra yazan bir uygulama o anda belleği
/// tüketip 500 döner; yani özellik yalnız gerekmediğinde çalışır.
/// </para>
/// <para>
/// <b>Neden BOM:</b> Excel, BOM'suz UTF-8 CSV'yi yerel kod sayfasıyla açar ve Türkçe karakterler
/// bozulur. Denetim kaydı büyük ihtimalle Excel'de açılacaktır.
/// </para>
/// </remarks>
public static class AuditCsvWriter
{
    /// <summary>Sütun başlıkları (dosya alan adlarıyla aynı sırada).</summary>
    public const string Header = "id,createdAtUtc,userName,action,resource,details,ipAddress";

    /// <summary>Excel'in UTF-8 olarak tanıması için BOM'lu kodlama.</summary>
    public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static async Task WriteAsync(
        Stream output,
        IAsyncEnumerable<AuditLogDto> items,
        CancellationToken cancellationToken = default)
    {
        // leaveOpen: akışın sahibi çağıran (ASP.NET yanıt gövdesi); burada kapatılmamalı.
        await using var writer = new StreamWriter(output, Encoding, leaveOpen: true);
        await writer.WriteLineAsync(Header);

        var yazilan = 0;
        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            await writer.WriteLineAsync(BuildRow(item));

            // Düzenli aralıklarla boşalt: uzun dışa aktarmada istemci ilerlemeyi görür ve
            // ara bellek büyümez.
            if (++yazilan % 500 == 0)
            {
                await writer.FlushAsync();
            }
        }

        await writer.FlushAsync();
    }

    /// <summary>Tek satır (test edilebilir olması için ayrı).</summary>
    public static string BuildRow(AuditLogDto item) => string.Join(
        ',',
        Cell(item.Id.ToString()),
        Cell(item.CreatedAtUtc.ToString("O")),
        Cell(item.UserName),
        Cell(item.Action),
        Cell(item.Resource),
        Cell(item.Details),
        Cell(item.IpAddress));

    /// <summary>
    /// Tek hücreyi kaçırır.
    /// </summary>
    /// <remarks>
    /// İki ayrı sorun çözülür:
    /// <list type="number">
    /// <item>CSV dilbilgisi: tırnak ikilenir, alan tırnağa alınır → içindeki virgül/yeni satır satırı bölmez.</item>
    /// <item><b>Formül enjeksiyonu:</b> "=", "+", "-", "@" ile başlayan hücreyi Excel FORMÜL sanır.
    /// Denetim kaydının içeriği kullanıcı girdisinden gelir (dosya adı, komut); yöneticinin bilgisayarında
    /// çalışan bir formüle dönüşmesi gerçek bir saldırı yoludur. Başa tek tırnak konarak metin olarak kalır.</item>
    /// </list>
    /// </remarks>
    private static string Cell(string? value)
    {
        var metin = value ?? string.Empty;

        if (metin.Length > 0 && (metin[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
        {
            metin = "'" + metin;
        }

        return "\"" + metin.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
