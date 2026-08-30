namespace AdaPanel.Core.Dtos.Components;

public sealed class ComponentManifestItemDto
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string InstallDir { get; init; } = string.Empty;

    public string[] PostInstall { get; init; } = [];

    public string[] Dependencies { get; init; } = [];

    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Bileşen çalışırken görev yöneticisinde görünen SÜREÇ adı (uzantısız), ör. <c>stalwart</c>.
    /// </summary>
    /// <remarks>
    /// Yalnız Windows servisi OLMAYAN, zamanlanmış görevle çalıştırılan bileşenler için doldurulur.
    /// Gerekçesi ölçüldü: yetkisiz bir oturumda (tepsi programının normal hâli) Task Scheduler
    /// nesnesi <c>E_ACCESSDENIED</c> veriyor, <c>Win32_Process.ExecutablePath</c> BOŞ geliyor ve
    /// bileşenin kurulum klasörü listelenemiyor. Geriye ölçülebilen tek şey süreç ADI kalıyor —
    /// bu yüzden ad tahmin edilmez, panelin kendi bildiriminde YAZAR.
    /// </remarks>
    public string ProcessName { get; init; } = string.Empty;

    public string SilentArgs { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    public bool Default { get; init; }
}
