namespace AdaPanel.Core.Common;

public sealed class AppException(
    string messageKey,
    int code = 1,
    int httpStatusCode = 400,
    string details = "",
    bool resourceAlreadyGone = false) : Exception(messageKey)
{
    public string MessageKey { get; } = messageKey;

    public int Code { get; } = code;

    public int HttpStatusCode { get; } = httpStatusCode;

    public string Details { get; } = details;

    /// <summary>
    /// "Silinmek istenen kaynak ZATEN YOKTU" — yıkıcı bir adımın <b>başarı sayılabildiği tek durum</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NEDEN AYRI BİR İŞARET: cascade silme, adımı "temizlendi" saymak için istisnanın <b>metnine</b>
    /// bakıyordu — mesaj <c>_not_found</c> ile bitiyorsa kaynak yok sayılıyordu. Depoda 35 farklı
    /// <c>*_not_found</c> anahtarı var ve bir kısmı "kaynak zaten yok" DEMİYOR:
    /// <c>php.executable_not_found</c>, <c>nginx.executable_not_found</c>, <c>cron.script_not_found</c>,
    /// <c>quota.path_not_found</c>, <c>git.path_not_found</c> hepsi <b>"araç yok / yol yok"</b> demektir.
    /// Bunlardan biri yıkıcı bir adımda yüzeye çıksa panel "kaldırıldı" derdi: kaynak duruyor, panel
    /// silindiğini söylüyor.
    /// </para>
    /// <para>
    /// Kaynağın var olup olmadığını <b>yalnızca fırlatan taraf</b> bilir; yakalayan taraf dizgeden
    /// tahmin ediyordu. Bu bayrak kararı bilgiyi olan yere taşır: <see cref="Gone"/> ile fırlatan
    /// servis "bu kaynak zaten yoktu" diye <b>söyler</b>, cascade de dizgeye değil bu bayrağa bakar.
    /// Bayrağı taşımayan hiçbir hata bir daha "silindi" sayılmaz.
    /// </para>
    /// </remarks>
    public bool ResourceAlreadyGone { get; } = resourceAlreadyGone;

    /// <summary>
    /// Kaynağın <b>zaten yok olduğunu</b> bildiren istisna. Silme yollarında idempotenttir:
    /// yakalayan taraf bunu "temizlendi" sayabilir.
    /// </summary>
    /// <remarks>
    /// Yalnızca "aradım, YOK" diyebilen yerde kullanılır. "Aracı bulamadım", "yolu çözemedim",
    /// "bağlanamadım" bu değildir — orada kaynak duruyor olabilir ve sıradan
    /// <see cref="AppException"/> fırlatılmalıdır.
    /// </remarks>
    public static AppException Gone(string messageKey, string details = "")
        => new(messageKey, code: 404, httpStatusCode: 404, details: details, resourceAlreadyGone: true);
}
