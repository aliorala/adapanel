using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Migration;

/// <summary>
/// Plesk REST API'sine (https://host:8443/api/v2) bağlanıp domain'leri OTOMATİK keşfeder (göç sihirbazını doldurmak
/// için). Kimlik DOĞRULAMA iki yoldan: (1) gizli API anahtarı (X-API-Key; Plesk'te <c>plesk bin secret_key</c> ile
/// üretilir) ya da (2) Plesk yönetici kullanıcı adı + parolası (Basic auth). Yalnız domain LİSTESİ çekilir; dosya/DB
/// parolaları Plesk API'sinden alınamaz → admin bunları sihirbazda girer. Manuel mod her zaman çalışır; keşif kolaylıktır.
/// </summary>
public sealed class PleskDiscoverRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 8443;

    /// <summary>Plesk gizli API anahtarı (X-API-Key). Boşsa UserName + Password (Basic auth) kullanılır.</summary>
    [StringLength(512)]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Plesk yönetici kullanıcı adı (API anahtarı verilmediyse Basic auth için).</summary>
    [StringLength(128)]
    public string UserName { get; init; } = string.Empty;

    /// <summary>Plesk yönetici parolası (Basic auth için).</summary>
    [StringLength(256)]
    public string Password { get; init; } = string.Empty;
}
