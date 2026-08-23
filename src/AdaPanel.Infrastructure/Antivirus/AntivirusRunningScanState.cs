namespace AdaPanel.Infrastructure.Antivirus;

/// <summary>
/// SÜREN taramanın sunucudaki tek kaydı (<c>config/antivirus-running.json</c>).
/// <para>
/// <b>NEDEN VAR:</b> tarama 125 dakikaya kadar sürüyor ve <c>/antivirus/status</c> yalnız SON BİTEN
/// taramayı döndürüyordu. Kullanıcı taramayı başlatıp sayfayı yenilediğinde panel "tarama yok"
/// diyordu; geri okunacak hiçbir şey yoktu. Bu kayıt o boşluğu kapatır: tarama başlarken yazılır,
/// bittiğinde silinir, arada <c>status</c> ucu onu okur.
/// </para>
/// <para>
/// <b>NEDEN <see cref="Core.Interfaces.IOperationProgressService"/> KULLANILMADI</b> (okundu,
/// uymuyor): o servis TEK YUVALIDIR — <c>Begin()</c> her çağrıldığında önceki işlemin adımlarını
/// SİLER. Yazılım kurulumu (<c>SoftwareController.InstallDefaultsAsync</c>) ve ilk kurulum sihirbazı
/// (<c>SetupService</c>) aynı yuvayı kullanıyor. 2 saatlik bir tarama sürerken yönetici Yazılım
/// ekranından PHP kurmaya kalksa iki durum birbirini EZERDİ: ya tarama "yok olur" ya da kurulum
/// ilerlemesi taramanın adımlarıyla değişirdi. Ayrıca o servis yalnız BELLEKTEDİR; panel yeniden
/// başlarsa yarıda kalan taramanın izi tamamen kaybolur ve kullanıcıya "tarama neden bitmedi"
/// sorusunun cevabı verilemez. Bu kayıt aynı modülün ZATEN kullandığı <c>JsonFileStore</c>'a
/// yazılır — yeni bir depo ya da zamanlayıcı eklenmez.
/// </para>
/// </summary>
public sealed class AntivirusRunningScanState
{
    /// <summary>Taranan hedef (site adı veya dizin yolu).</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>Taramanın başladığı an (UTC).</summary>
    public DateTime StartedAtUtc { get; init; }

    /// <summary>
    /// Taramayı yürüten PANEL sürecinin kimliği.
    /// <para>
    /// SAHİPLİK KAYDI, ilerleme kaydı değil: diskteki "sürüyor" işareti tek başına YALAN
    /// SÖYLEYEBİLİR — panel çökerse ya da servis yeniden başlarsa işaret dosyada kalır ve panel
    /// sonsuza kadar "tarama sürüyor" der. Sahibi bu süreçse tarama gerçekten bu süreçte
    /// koşuyordur (bitişte <c>finally</c> işareti temizler); sahibi BAŞKA bir süreçse tarama
    /// yarıda kalmıştır. Böylece durum tahmin edilmez, ÖLÇÜLÜR.
    /// </para>
    /// </summary>
    public int OwnerProcessId { get; init; }

    /// <summary>
    /// Sahip sürecin başlangıç anı (tick). PID'ler Windows'ta YENİDEN KULLANILIR; yalnız PID'e
    /// bakmak, aynı numarayı alan yepyeni bir sürecin ölü bir taramayı "sürüyor" göstermesine
    /// yol açardı. Başlangıç anıyla birlikte çift, süreci tekilleştirir. Ölçülemezse 0 kalır ve
    /// karşılaştırma yalnız PID üzerinden yapılır.
    /// </summary>
    public long OwnerProcessStartedAtTicks { get; init; }
}
