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

    /// <summary>
    /// Serbest teşhis metni. <b>Kurulurken maskelenir</b> — sırrı olan hiçbir biçim buradan çıkamaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN TAM BURADA:</b> <c>Details</c> bu depodaki en geniş sızıntı kanalıdır. ÖLÇÜLDÜ —
    /// tek bir hedefe değil, en az beş ayrı hedefe gider: (1) HTTP yanıt gövdesi
    /// (<c>ApiExceptionHandlingMiddleware.WriteResponseAsync</c>), (2) kurulum ilerleme kaydı
    /// (<c>OperationProgressService</c> → <c>GET /api/v1/software/progress</c>), (3) denetim kaydı
    /// ve veritabanı, (4) sıradan 200 yanıtlarının içindeki DTO alanları (yedek uzak hedef testi,
    /// site taşıma, PHP tanılama, dikkat kartları), (5) MCP araç çıktısı. Bu hedeflerin her birine
    /// ayrı bir maskeleme koymak, "aynı kural kırk yerde" demekti — ve bu depoda o hata sınıfı
    /// defalarca ölçüldü: biri düzelir, öteki sessizce açık kalır. 0.8.7'de MariaDB root parolası
    /// tam olarak böyle sızdı; o TEK vaka çağrı yerinde maskelendi, kanalın kendisi açık kaldı.
    /// </para>
    /// <para>
    /// <c>Details</c>'in üretilebileceği TEK yer bu kurucudur. Maskelemeyi buraya koymak, kuralı
    /// çağrı yerlerinin hatırlamasına bırakmaz: yarın eklenen kırk birinci çağrı yeri de kapsanır.
    /// </para>
    /// <para>
    /// <b>NEDEN ORTAMA GÖRE KESİLMİYOR:</b> "üretimde teşhisi hiç gönderme" seçeneği ölçüldü ve
    /// REDDEDİLDİ. (a) Bu bir barındırma paneli: nginx başlatma hatası, yedek geri yükleme uyarısı,
    /// bileşen indirme 404'ü ürünün KENDİSİDİR — kesilirse kullanıcı kör kalır ve destek yükü
    /// panelin dışına taşar. (b) Ortama göre dallanma, üretimde ÇALIŞAN yolun geliştirmede test
    /// edilen yoldan farklı olması demektir; bir sızıntı tam da gözlenmeyen dalda saklanır.
    /// (c) Sızan şey teşhis değil SIRDI; koruma da sırra göre yapılır. Maskeleme her ortamda aynı
    /// çalışır, bu yüzden testte gördüğümüz davranış üretimdeki davranıştır.
    /// </para>
    /// </remarks>
    public string Details { get; } = SecretRedactor.Redact(details);

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
