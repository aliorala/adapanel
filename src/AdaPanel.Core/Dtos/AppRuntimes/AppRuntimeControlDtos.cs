using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.AppRuntimes;

/// <summary>Uygulamayı kaldırma isteği. Onay, sunucuda da zorunludur (yalnız arayüz iletişim kutusu değil).</summary>
public sealed class RemoveAppRuntimeRequest
{
    /// <summary>Onay: site adının AYNEN yazılması gerekir.</summary>
    [Required]
    [StringLength(260)]
    public string ConfirmSiteName { get; init; } = string.Empty;

    /// <summary>Panelin açtığı günlük klasörü (App_Data\AdaPanel\logs) de silinsin mi.</summary>
    public bool DeleteLogs { get; init; } = true;
}

/// <summary>Kaldırma sonucu: neyin kaldırıldığı ve SUNUCUDA NEYİN KALDIĞI.</summary>
public sealed class AppRuntimeRemovalResultDto
{
    public string SiteName { get; init; } = string.Empty;

    public string[] Removed { get; init; } = [];

    /// <summary>Boş değilse kaldırma TAM değildir; her satır bir kalıntıdır.</summary>
    public string[] Leftovers { get; init; } = [];

    /// <summary>Kaldırma sonrası yapılandırma (RuntimeType=none beklenir).</summary>
    public AppRuntimeSiteConfigurationDto Configuration { get; init; } = new();
}

public sealed class AppRuntimeLogsDto
{
    public string SiteName { get; init; } = string.Empty;

    /// <summary>Okunan günlük dosyasının adı (yol değil); günlük yoksa boş.</summary>
    public string FileName { get; init; } = string.Empty;

    public string Output { get; init; } = string.Empty;

    /// <summary>Dosya okuma sınırından büyüktü; yalnız sonu gösteriliyor.</summary>
    public bool Truncated { get; init; }
}
