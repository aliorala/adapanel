using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Migration;

/// <summary>
/// Plesk REST API'sine bağlanıp domain'leri OTOMATİK keşfeder (göç sihirbazını doldurmak için). API anahtarı
/// Plesk'te Tools &amp; Settings → REST API'den alınır. Yalnız domain LİSTESİ çekilir; dosya/DB parolaları
/// Plesk API'sinden alınamaz → admin bunları sihirbazda girer. Manuel mod her zaman çalışır; keşif kolaylıktır.
/// </summary>
public sealed class PleskDiscoverRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 8443;

    [Required]
    [StringLength(512, MinimumLength = 1)]
    public string ApiKey { get; init; } = string.Empty;
}
