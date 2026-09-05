using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Services;

public sealed class PanelPreferencesService(
    JsonFileStore fileStore,
    IOptions<PanelRuntimeOptions> runtimeOptions) : IPanelPreferencesService
{
    private const string PreferencesPath = "config/panel-preferences.json";
    private const string RuntimeSettingsPath = "config/runtime-settings.json";
    private readonly PanelRuntimeOptions defaults = runtimeOptions.Value;

    public async Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var fallback = CreateDefaults();
        var settings = await fileStore.ReadAsync(PreferencesPath, fallback, cancellationToken);
        settings.ServerAddress = string.IsNullOrWhiteSpace(settings.ServerAddress)
            ? ResolveServerAddress()
            : settings.ServerAddress.Trim();
        // Alan adı karşılaştırmaları (IIS binding, SNI, DNS) küçük harf ve noktasızdır.
        settings.PanelHostName = (settings.PanelHostName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        // TÜRETİLMİŞ: kayıtta ne yazarsa yazsın her okumada yeniden hesaplanır. Adres elle de
        // değişebilir, otomatik de çözülebilir; ikisinde de uyarının doğru olması gerekiyor.
        settings.ServerAddressExternallyReachable = PanelAddress.IsExternallyReachable(settings.ServerAddress);
        settings.RestartRequired = false;
        return settings;
    }

    public async Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default)
    {
        var previous = await GetAsync(cancellationToken);
        var settings = new PanelPreferencesDto
        {
            PanelName = request.PanelName.Trim(),
            DefaultLanguage = request.DefaultLanguage.Trim().ToLowerInvariant(),
            ThemeMode = request.ThemeMode.Trim().ToLowerInvariant(),
            UiDensity = request.UiDensity.Trim().ToLowerInvariant(),
            DefaultSiteRoot = NormalizePath(request.DefaultSiteRoot),
            DefaultBackupRoot = NormalizePath(request.DefaultBackupRoot),
            ServerAddress = NormalizeServerAddress(request.ServerAddress, request.PreferredHttpsPort),
            // Panel alan adını Ayarlar > Genel formu YÖNETMEZ (istekte hiç gelmez). Sahibi panel
            // erişimi ekranıdır; buradan geçerken sessizce sıfırlanmasın diye önceki değer taşınır.
            PanelHostName = previous.PanelHostName,
            PreferredHttpsPort = request.PreferredHttpsPort,
            AllowHttpFallback = request.AllowHttpFallback,
            LoginLockoutMinutes = request.LoginLockoutMinutes,
            DefaultRuntimeProfile = request.DefaultRuntimeProfile.Trim().ToLowerInvariant(),
            InstallRuntimeIfMissing = request.InstallRuntimeIfMissing,
            RestartRequired =
                previous.PreferredHttpsPort != request.PreferredHttpsPort ||
                previous.AllowHttpFallback != request.AllowHttpFallback ||
                previous.LoginLockoutMinutes != request.LoginLockoutMinutes
        };

        // Kaydeden yönetici, yazdığı adresin dışarıdan açılıp açılmayacağını YANITTA görsün:
        // form kaydedildikten sonra uyarı yeni değere göre güncellenir (okuma yolu da aynı
        // hesabı yapar; burada yapılmasa kaydeden kişi bir sonraki yüklemeye kadar eski
        // uyarıyı görürdü).
        settings.ServerAddressExternallyReachable = PanelAddress.IsExternallyReachable(settings.ServerAddress);

        Directory.CreateDirectory(Environment.ExpandEnvironmentVariables(settings.DefaultSiteRoot));
        Directory.CreateDirectory(Environment.ExpandEnvironmentVariables(settings.DefaultBackupRoot));
        await fileStore.WriteAsync(PreferencesPath, settings, cancellationToken);
        await fileStore.WriteAsync(
            RuntimeSettingsPath,
            new RuntimeSettingsDocument
            {
                Panel = new PanelRuntimeSettings
                {
                    PreferredHttpsPort = settings.PreferredHttpsPort,
                    AllowHttpFallback = settings.AllowHttpFallback,
                    LoginLockoutMinutes = settings.LoginLockoutMinutes,
                    DefaultLanguage = settings.DefaultLanguage
                }
            },
            cancellationToken);
        return settings;
    }

    private PanelPreferencesDto CreateDefaults()
    {
        return new PanelPreferencesDto
        {
            PanelName = "AdaPanel",
            DefaultLanguage = defaults.DefaultLanguage,
            ThemeMode = "light",
            UiDensity = "compact",
            DefaultSiteRoot = "C:\\inetpub\\wwwroot",
            DefaultBackupRoot = Path.Combine(defaults.DataRoot, "backups"),
            PreferredHttpsPort = defaults.PreferredHttpsPort,
            AllowHttpFallback = defaults.AllowHttpFallback,
            LoginLockoutMinutes = defaults.LoginLockoutMinutes,
            DefaultRuntimeProfile = "auto",
            InstallRuntimeIfMissing = true,
            ServerAddress = ResolveServerAddress()
        };
    }

    /// <summary>
    /// Yöneticinin Ayarlar &gt; Genel formuna yazdığı sunucu adresini kanonik HOST'a çevirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden kaydetme yolunda:</b> bozuk bir adres yalnız e-posta bağlantılarını değil,
    /// bu alanı okuyan her tüketiciyi (varsayılan DNS A kaydı, FTP istemcisine gösterilen sunucu
    /// adresi, posta DNS yayını, uzak veritabanı bağlantı host'u) etkiler. Yalnız URL üretiminde
    /// düzeltmek, ötekileri bozuk bırakırdı. Ayrıca yönetici hatayı KAYDEDERKEN görür — bir sonraki
    /// müşteri postası ölü gidene kadar beklemek zorunda kalmaz.
    /// </para>
    /// <para>
    /// <b>Port neden BURADA kabul edilmiyor:</b> <c>ServerAddress</c> bu kod tabanında saf bir
    /// host/IP olarak tüketiliyor (yukarıdaki liste); içine <c>:8890</c> yazmak o tüketicileri
    /// bozar. Adresteki port ayardaki portla AYNIYSA bilgi kaybı yoktur, sessizce elenir — bu, en
    /// sık gerçek durumdur (yönetici panele girdiği tarayıcı çubuğunu yapıştırır ve panel zaten o
    /// portta dinler). FARKLIYSA taraf sessizce SEÇİLMEZ: iki portun hangisinin dışarıdan
    /// açılabildiğini panel bilemez ve yanlış seçim, hiçbir yerde raporlanmayan ölü bir bağlantı
    /// üretir. Bu yüzden hata döner ve iki değeri de yöneticiye söyler; kararı o verir.
    /// </para>
    /// </remarks>
    private static string NormalizeServerAddress(string? requested, int preferredHttpsPort)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            // PANELİN KENDİ ÜRETTİĞİ DEĞER ASLA REDDEDİLMEZ. Makine adı biçim kuralına
            // takılırsa bile ham hâliyle kabul edilir; aksi hâlde adres alanını boş bırakan
            // yönetici Ayarlar formunu HİÇ kaydedemezdi (kapatılan sorundan kötü).
            var otomatik = ResolveServerAddress();
            var kanonikOtomatik = PanelAddress.NormalizeHost(otomatik);
            return kanonikOtomatik.Length == 0 ? otomatik : kanonikOtomatik;
        }

        if (!PanelAddress.TryParseAddress(requested, out var parcalar))
        {
            throw new InvalidOperationException("settings.server_address_invalid");
        }

        if (parcalar.Port is int yazilanPort && yazilanPort != preferredHttpsPort)
        {
            throw new InvalidOperationException("settings.server_address_port_conflict");
        }

        return parcalar.Host;
    }

    private static string NormalizePath(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.GetFullPath(expanded);
    }

    private static string ResolveServerAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            ?.ToString() ?? Environment.MachineName;
    }

    private sealed class RuntimeSettingsDocument
    {
        public PanelRuntimeSettings Panel { get; set; } = new();
    }

    private sealed class PanelRuntimeSettings
    {
        public string DefaultLanguage { get; set; } = "tr";

        public int PreferredHttpsPort { get; set; }

        public bool AllowHttpFallback { get; set; }

        public int LoginLockoutMinutes { get; set; }
    }
}
