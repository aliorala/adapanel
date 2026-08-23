using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Yazılan günlük satırlarını BELLEKTE tutan sahte <see cref="ILogger{TCategoryName}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> "servise günlükçü eklendi" bir iddiadır; iddiayı ölçen tek şey, kritik yolun
/// GERÇEKTEN yazdığını görmektir. Kaynak metninde <c>logger.LogWarning</c> aramak bu işi yapmaz —
/// aynı dizge dosyanın onlarca yerinde geçer, dolayısıyla test korunan davranışı değil bugünkü
/// yazımını doğrular (bu depoda bir kez denendi ve mutasyonda YEŞİL KALDI).
/// </para>
/// <para>
/// <b>Neden biçimlendirilmiş metin saklanıyor:</b> asıl soru "hangi bilgi kaydedildi" ve bunun
/// ikizi "hangi bilgi kaydedilMEDİ" (parola, jeton, FTP kimliği). İkisi de ancak günlüğe düşen
/// NİHAİ metin üzerinden sınanabilir; şablonun kendisi yeterli değildir çünkü sır, şablona değil
/// parametreye konur.
/// </para>
/// </remarks>
internal sealed class KayitTutanGunlukcu<T> : ILogger<T>
{
    public List<GunlukSatiri> Kayitlar { get; } = [];

    /// <summary>Tüm satırların birleştirilmiş metni — "şu sır hiçbir yerde geçmiyor" denetimi için.</summary>
    public string TumMetin => string.Join("\n", Kayitlar.Select(item => item.Mesaj));

    public IEnumerable<GunlukSatiri> Seviyede(LogLevel seviye) => Kayitlar.Where(item => item.Seviye == seviye);

    /// <summary>Belirtilen seviyede, verilen parçaların HEPSİNİ içeren bir satır var mı?</summary>
    public bool Icerir(LogLevel seviye, params string[] parcalar) =>
        Kayitlar.Any(item => item.Seviye == seviye &&
                             parcalar.All(parca => item.Mesaj.Contains(parca, StringComparison.OrdinalIgnoreCase)));

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Kayitlar.Add(new GunlukSatiri(logLevel, formatter(state, exception), exception));
    }

    internal sealed record GunlukSatiri(LogLevel Seviye, string Mesaj, Exception? Istisna);
}
