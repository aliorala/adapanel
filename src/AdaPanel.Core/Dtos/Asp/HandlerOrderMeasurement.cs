namespace AdaPanel.Core.Dtos.Asp;

/// <summary>
/// IIS handler koleksiyonundan alinmis TEK bir kaydin gozlemi (ad + path).
/// </summary>
/// <remarks>
/// Ayri bir tip: olcum kurali, yansima (reflection) ile okunan canli IIS nesnelerinden
/// AYRISTIRILABILSIN diye. Boylece karar kurali IIS olmayan bir makinede de GERCEKTEN
/// kosturulabilir; "kaynak taramasi" ile degil, kuralin kendisiyle olculur.
/// </remarks>
public sealed record HandlerSnapshot(string Name, string Path);

/// <summary>
/// "PHP/ASP handler'i gercekten StaticFile'dan ONCE mi?" sorusunun UC DEGERLI cevabi.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR.</b> <c>AspNetService</c> ve <c>PhpService</c> handler'i listenin BASINA
/// <c>AddAt(0, ...)</c> ile koymaya calisiyordu; basarisiz olursa CIPLAK bir <c>catch</c> ile
/// <c>Add(...)</c> yapiyordu -- yani SONA ekliyordu. Kodun BIR USTUNDEKI YORUM tam da sona
/// eklemenin <c>.php</c> dosyalarini <c>404.3</c> yaptigini yaziyordu: yorum hatayi BILIYOR,
/// kod hatayi FALLBACK olarak ISLIYORDU. Ustelik donen DTO handler SIRASINI hic tasimiyordu,
/// panel yine "Mixed ASP.NET and PHP handlers were enabled" diyordu.
/// </para>
/// <para>
/// <b>KARAR KURALI.</b> IIS handler eslesmesini yukaridan asagiya yapar ve ILK eslesen kazanir.
/// Devralinan <c>StaticFile</c> kaydinin path'i <c>*</c>'dir ve HER istegi yakalar. Dolayisiyla
/// bizim <c>*.php</c> / <c>*.asp</c> handler'imiz, path'i <c>*</c> olan ILK kayittan ONCE
/// gelmelidir. Bu kural KASITLI OLARAK TEMKINLIDIR: <c>*</c> path'li bir kaydin verb kumesi
/// istegi kacirabilir, ama "kacirabilir"e guvenmek yerine "onde degil" deriz -- yanlis taraf
/// GUVENLI taraftir.
/// </para>
/// <para>
/// <b>UC DEGER.</b> <see cref="Measured"/> false ise sonuc "sirali degil" DEGILDIR, "OLCULEMEDI"dir
/// (IIS kapandi, koleksiyon okunamadi). Bu ikisini ayni goruntuye dusurmek, bu depoda bedeli
/// defalarca odenmis hata sinifidir.
/// </para>
/// </remarks>
public sealed class HandlerOrderMeasurement
{
    /// <summary>Commit'ten SONRA taze bir okuma yapilabildi mi?</summary>
    public bool Measured { get; init; }

    /// <summary>Handler koleksiyonda BULUNDU mu? (Measured false iken anlamsizdir.)</summary>
    public bool HandlerFound { get; init; }

    /// <summary>Handler, path="*" olan ilk kayittan ONCE mi? (Olculmus gercek.)</summary>
    public bool PrecedesCatchAll { get; init; }

    /// <summary>Handler'in olculen indeksi; bulunamadiysa -1.</summary>
    public int HandlerIndex { get; init; } = -1;

    /// <summary>path="*" olan ilk kaydin indeksi; boyle bir kayit yoksa -1.</summary>
    public int CatchAllIndex { get; init; } = -1;

    /// <summary>Onunde duran catch-all kaydin adi (varsa) -- KISMI BASARIYI ADIYLA yazmak icin.</summary>
    public string BlockingHandlerName { get; init; } = string.Empty;

    /// <summary>
    /// <c>AddAt(0, ...)</c> gercekten calisti mi? false ise fallback <c>Add(...)</c> kullanildi,
    /// yani kayit SONA eklendi. Bu BELLEKTEKI iddiadir; <see cref="PrecedesCatchAll"/> ise DISKTEN
    /// okunan olcumdur -- ikisi ayri sorulardir ve ayri tutulur.
    /// </summary>
    public bool InsertedAtFront { get; init; }

    /// <summary>Panel "yapildi" diyebilir mi? YALNIZ olculmus ve gercekten onde ise.</summary>
    public bool Ordered => Measured && HandlerFound && PrecedesCatchAll;

    /// <summary>Olculdu ve handler catch-all'in ARDINDA: .php/.asp 404.3 verecek.</summary>
    public bool ShadowedByCatchAll => Measured && HandlerFound && !PrecedesCatchAll;

    /// <summary>Olculdu ama handler koleksiyonda YOK: yazma diske hic inmemis.</summary>
    public bool Missing => Measured && !HandlerFound;

    /// <summary>Sonuc okunamadi. "Sirasiz" DEGIL, BILINMIYOR.</summary>
    public bool Unverified => !Measured;

    /// <summary>
    /// Kullaniciya gosterilecek i18n anahtari; her sey yolundaysa bos doner (sade "yapildi").
    /// </summary>
    public string NoticeKey => Ordered
        ? string.Empty
        : Unverified
            ? "aspnet.handler_order_unverified"
            : Missing
                ? "aspnet.handler_missing"
                : "aspnet.handler_shadowed";

    public static HandlerOrderMeasurement Olculemedi(bool insertedAtFront = false)
        => new() { Measured = false, InsertedAtFront = insertedAtFront };

    /// <summary>
    /// KARAR KURALI. Handler listesi (commit sonrasi taze okuma) ve aranan handler adi verilir.
    /// </summary>
    public static HandlerOrderMeasurement Olc(
        IReadOnlyList<HandlerSnapshot>? handlers,
        string handlerName,
        bool insertedAtFront = false)
    {
        if (handlers is null || string.IsNullOrWhiteSpace(handlerName))
        {
            return Olculemedi(insertedAtFront);
        }

        var handlerIndex = -1;
        for (var index = 0; index < handlers.Count; index += 1)
        {
            if (string.Equals(handlers[index].Name, handlerName, StringComparison.OrdinalIgnoreCase))
            {
                handlerIndex = index;
                break;
            }
        }

        var catchAllIndex = -1;
        for (var index = 0; index < handlers.Count; index += 1)
        {
            if (IsCatchAll(handlers[index].Path))
            {
                catchAllIndex = index;
                break;
            }
        }

        if (handlerIndex < 0)
        {
            return new HandlerOrderMeasurement
            {
                Measured = true,
                HandlerFound = false,
                CatchAllIndex = catchAllIndex,
                InsertedAtFront = insertedAtFront
            };
        }

        var precedes = catchAllIndex < 0 || handlerIndex < catchAllIndex;

        return new HandlerOrderMeasurement
        {
            Measured = true,
            HandlerFound = true,
            PrecedesCatchAll = precedes,
            HandlerIndex = handlerIndex,
            CatchAllIndex = catchAllIndex,
            BlockingHandlerName = precedes || catchAllIndex < 0 ? string.Empty : handlers[catchAllIndex].Name,
            InsertedAtFront = insertedAtFront
        };
    }

    /// <summary>
    /// path="*" HER istegi yakalar (devralinan StaticFile bunun kanonik ornegidir).
    /// <c>*.</c> (uzantisiz URL) ya da <c>*.php</c> gibi desenler catch-all DEGILDIR.
    /// </summary>
    private static bool IsCatchAll(string? path)
        => string.Equals((path ?? string.Empty).Trim(), "*", StringComparison.Ordinal);
}
