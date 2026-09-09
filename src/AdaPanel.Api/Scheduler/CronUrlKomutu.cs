using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Configuration;

namespace AdaPanel.Api.Scheduler;

/// <summary>
/// <c>AdaPanel.Api.exe --cron-url &lt;adres&gt;</c> — <c>url</c> tipli zamanlanmış görevin
/// yürütücüsü. Eskiden bu işi <c>curl.exe</c> yapıyordu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden panelin içinde, ayrı bir küçük araç değil:</b> kapatılan açık, adres denetiminin
/// yönlendirme zincirinin HER adımında çalışması gereğidir. O denetim
/// (<see cref="OutboundConnectGuard"/>) ve engel listesi
/// (<see cref="OutboundAddressGuard"/>) zaten bu derlemelerde ve TEK nüsha. Ayrı bir araç, aynı
/// listenin ikinci nüshasını taşımak zorunda kalırdı — bu deponun ölçülmüş hata sınıfı tam olarak
/// budur. Gerekçenin tamamı <see cref="UrlTaskRunner"/>'da.
/// </para>
/// <para>
/// <b>Panelin normal açılışından ÖNCE, tepsi ve parola kurtarma komutlarıyla AYNI yerde.</b>
/// Buradan sonra <c>WebApplication.CreateBuilder</c> gelir; görev her tetiklendiğinde İKİNCİ bir
/// web sunucusu ayağa kalksaydı panelin dinlediği port çakışır ve SYSTEM olarak ikinci bir panel
/// örneği doğardı. Bu yüzden <see cref="BuBizim"/> eşleşince süreç ÇIKAR.
/// </para>
/// <para>
/// <b>Servis grafiği KURULMAZ.</b> Bu komutun ihtiyacı olan tek şey <c>Panel:AllowInternalOutboundTargets</c>
/// açık kapısının değeri; veritabanı, IIS, zamanlayıcı hiç açılmaz. Görev dakikada bir
/// tetiklenebilir, yani açılış maliyeti gerçek bir maliyettir.
/// </para>
/// <para>
/// <b>Bu yol AĞDAN çağrılamaz</b> ve yeni bir HTTP yüzeyi açmaz: yalnız komut satırından çalışır,
/// zaten SYSTEM olarak koşan bir görevin yapabileceğinden fazlasını yapmaz — aksine, curl'ün
/// yapabildiğinden AZINI yapar (yalnız http/https, yalnız denetlenen adresler).
/// </para>
/// </remarks>
public static class CronUrlKomutu
{
    /// <summary>Anahtar bu komuta mı ait? (Panelin normal açılışında hiçbirine dokunulmaz.)</summary>
    public static bool BuBizim(string[] argumanlar)
        => argumanlar.Length > 0 && string.Equals(argumanlar[0], UrlTaskRunner.Verb, StringComparison.Ordinal);

    public static async Task<int> CalistirAsync(string[] argumanlar)
    {
        // Adres İKİNCİ argümandır. Yoksa "geçersiz adres" koduyla çıkılır; sessizce başarı dönmek,
        // bozuk bir görevin listede "başarılı" görünmesi demekti.
        var adres = argumanlar.Length > 1 ? argumanlar[1] : string.Empty;

        var yapilandirma = YapilandirmaOku();
        var acikKapi = yapilandirma.GetValue<bool>(
            $"{PanelRuntimeOptions.SectionName}:{nameof(PanelRuntimeOptions.AllowInternalOutboundTargets)}");

        // Politika HER BAĞLANTIDA okunur; burada değer süreç ömrü boyunca sabittir (görev kısa
        // ömürlü), ama imza tek kaynaktan geldiği için kapalı devre aynı kalır.
        return await UrlTaskRunner.RunAsync(
            adres,
            () => OutboundTargetPolicy.Connecting(acikKapi),
            Console.Out,
            Console.Error);
    }

    /// <summary>
    /// Açık kapı bayrağını panelin KENDİ katmanlamasıyla AYNI sırada okur.
    /// </summary>
    /// <remarks>
    /// Sıra <c>appsettings.json</c> → veri kökündeki <c>runtime-settings.json</c> → ortam
    /// değişkenleri. Aynı sıra <c>ParolaBelirlemeKomutu.SaglayiciKur</c>'da da yazılı ve sebebi
    /// aynı: <c>runtime-settings.json</c> ayarı ezebiliyor ve servis <c>Panel__DataRoot</c> gibi
    /// bir ortam değişkeniyle başlatılmış olabilir. Farklı bir sıra okusaydık görev, panelin
    /// GÖRDÜĞÜNDEN BAŞKA bir açık kapı değeriyle çalışırdı — yani işletici kapıyı kapattığını
    /// sanırken cron yolu açık kalabilirdi.
    /// </remarks>
    private static IConfigurationRoot YapilandirmaOku()
    {
        var onYukleme = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var veriKoku = Environment.ExpandEnvironmentVariables(
            onYukleme[$"{PanelRuntimeOptions.SectionName}:DataRoot"] ?? @"%ProgramData%\AdaPanel");

        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(Path.Combine(veriKoku, "config", "runtime-settings.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
    }
}
