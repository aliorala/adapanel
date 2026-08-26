using System.Runtime.CompilerServices;

namespace AdaPanel.Infrastructure.Statistics;

/// <summary>
/// IIS/HTTP.sys W3C genişletilmiş günlük biçiminin <b>TEK ayrıştırıcısı</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN AYRI DOSYAYA ÇIKARILDI:</b> bu ayrıştırıcı <see cref="WebStatisticsService"/> içinde
/// yazılmıştı ve SEO günlük analizi ikinci bir kopya isteyecekti. Bu depoda "aynı kuralın ikinci
/// kopyası" hatası beş kez ısırdı (motor listesi, hedef türü listesi, günlük temizliği, ayrılmış
/// aygıt adları, denetim kapsamı). Sütun eşlemesi tam da o sınıftan bir kuraldır: IIS'te alan
/// kümesi yöneticinin seçimidir, <c>#Fields</c> satırı dosyanın ORTASINDA yeniden yazılabilir
/// (IIS alan seçimi değişince aynı dosyaya yeni bir başlık basar) ve iki kopyadan biri bunu
/// unutursa o modül sessizce yanlış sütunu okur.
/// </para>
/// <para>
/// <b>Biçim, Microsoft'un kendi belgesinden alındı</b> (Win32 "W3C Logging"):
/// </para>
/// <list type="bullet">
/// <item>Dosya ASCII metindir; <b>alanlar BOŞLUKLA ayrılır</b>.</item>
/// <item><c>#</c> ile başlayan satırlar yönergedir: <c>#Software</c>, <c>#Version</c>,
/// <c>#Date</c>, <c>#Fields</c>. Yalnız <c>#Fields</c> anlam taşır — sütun sırasını O belirler.</item>
/// <item>Seçili ama değeri olmayan alan <b>tire</b> (<c>-</c>) olarak yazılır.</item>
/// <item>Değerin içindeki YAZDIRILAMAYAN karakterler <b>artı</b> (<c>+</c>) ile değiştirilir —
/// kullanıcı aracısındaki boşluklar bu yüzden <c>Googlebot/2.1;++http://...</c> gibi görünür.
/// Ayrıştırıcı bunu ÇÖZMEZ (çözemez: <c>+</c>'ın boşluk mu gerçek artı mı olduğu bilgisi
/// dosyada YOKTUR); ham değeri verir, yorumu okuyan tarafa bırakır.</item>
/// <item>Tarih <c>yyyy-MM-dd</c>, saat <c>HH:mm:ss</c> ve <b>UTC</b>'dir.</item>
/// </list>
/// <para>
/// <b>AKIŞ HÂLİNDE:</b> hiçbir yerde dosyanın tamamı belleğe alınmaz. Erişim günlükleri yüzlerce
/// MB olabilir; <c>File.ReadAllLines</c> tek bir sitenin günlüğüyle paneli düşürürdü.
/// </para>
/// </remarks>
public static class W3cLogReader
{
    /// <summary>Sütun sırasını bildiren yönerge. Biçimin TEK anlamlı başlık satırı.</summary>
    public const string FieldsDirective = "#Fields:";

    /// <summary>
    /// <c>#Fields:</c> satırından sütun eşlemesi kurar.
    /// </summary>
    public static W3cFieldIndex ParseFields(string fieldsLine)
    {
        var names = fieldsLine[FieldsDirective.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new W3cFieldIndex(names);
    }

    /// <summary>
    /// Bellekteki satırları ayrıştırır (birim testleri ve küçük kümeler için).
    /// </summary>
    public static IEnumerable<W3cLogRecord> Read(IEnumerable<string> lines)
    {
        var state = new ReaderState();
        foreach (var line in lines)
        {
            if (TryProject(line, state, out var record))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// Bir metin akışını satır satır ayrıştırır. Bellek kullanımı dosya boyutundan BAĞIMSIZDIR
    /// ve iptal belirteci her satırda geçerlidir.
    /// </summary>
    public static async IAsyncEnumerable<W3cLogRecord> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new ReaderState();
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (TryProject(line, state, out var record))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// <b>KURALIN TEK GÖVDESİ.</b> Senkron ve asenkron pompaların ikisi de buradan geçer; biçim
    /// bilgisi (yönerge satırı, tire, boşluk ayracı) başka hiçbir yerde tekrar edilmez.
    /// </summary>
    /// <returns>Satır bir VERİ satırıysa <c>true</c>; yönerge/boş satırda <c>false</c>.</returns>
    private static bool TryProject(string line, ReaderState state, out W3cLogRecord record)
    {
        record = default;

        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        if (line[0] == '#')
        {
            // #Fields dosyanın ORTASINDA da gelebilir; en son görülen geçerlidir.
            if (line.StartsWith(FieldsDirective, StringComparison.OrdinalIgnoreCase))
            {
                state.Fields = ParseFields(line);
            }

            return false;
        }

        // Başlık görülmeden gelen veri satırı okunamaz: sütunların hangi alan olduğu bilinmiyor.
        // Tahmin etmek (varsayılan alan sırasını uydurmak) yanlış sütunu doğru sanmaktır.
        if (state.Fields is null)
        {
            return false;
        }

        record = new W3cLogRecord(line.Split(' '), state.Fields);
        return true;
    }

    /// <summary>Pompaların paylaştığı tek değişken: en son görülen sütun eşlemesi.</summary>
    private sealed class ReaderState
    {
        public W3cFieldIndex? Fields { get; set; }
    }
}

/// <summary>
/// <c>#Fields</c> satırından çıkan SÜTUN EŞLEMESİ (alan adı → sütun sırası).
/// </summary>
/// <remarks>
/// Alan adları büyük/küçük harfe duyarsız karşılaştırılır: IIS <c>cs(User-Agent)</c> yazar ama
/// yönetici yapılandırmasına göre harf durumu değişebilir ve HTTP.sys belgesi <c>cs(Referrer)</c>,
/// IIS ise <c>cs(Referer)</c> yazar — okuyan taraf HANGİ adı sorduğunu kendi bilir.
/// </remarks>
public sealed class W3cFieldIndex
{
    private readonly Dictionary<string, int> map;

    internal W3cFieldIndex(IReadOnlyList<string> names)
    {
        map = new Dictionary<string, int>(names.Count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < names.Count; index++)
        {
            map[names[index]] = index;
        }
    }

    /// <summary>Bu günlükte alan var mı (yönetici alanı kapatmış olabilir).</summary>
    public bool Has(string name) => map.ContainsKey(name);

    /// <summary>
    /// Sütunun ham değeri. Alan yoksa ya da satır kısa kaldıysa boş dizge döner — <b>istisna
    /// değil</b>: tek bir bozuk satır (disk dolduğunda yarım yazılmış son satır olağandır) bütün
    /// analizi düşürmemeli.
    /// </summary>
    public string Get(string[] columns, string name) =>
        map.TryGetValue(name, out var index) && index < columns.Length ? columns[index] : string.Empty;
}

/// <summary>Ayrıştırılmış TEK bir W3C veri satırı.</summary>
public readonly record struct W3cLogRecord(string[] Columns, W3cFieldIndex Fields)
{
    /// <summary>Alanın ham değeri (bkz. <see cref="W3cFieldIndex.Get"/>).</summary>
    public string Get(string name) => Fields.Get(Columns, name);
}
