using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Migration;

/// <summary>
/// cPanel/WHM sunucusuna bağlanıp hesap/domain'leri OTOMATİK keşfeder (göç sihirbazını doldurmak için). WHM API
/// token'ı WHM → Development → Manage API Tokens'tan alınır. Yalnız domain/hesap LİSTESİ çekilir; dosya/DB
/// parolaları WHM'den alınmaz → admin sihirbazda girer. Manuel mod her zaman çalışır; keşif kolaylıktır.
/// </summary>
public sealed class CpanelDiscoverRequest
{
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public string Host { get; init; } = string.Empty;

    /// <summary>WHM portu (varsayılan 2087).</summary>
    public int Port { get; init; } = 2087;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string UserName { get; init; } = "root";

    [Required]
    [StringLength(512, MinimumLength = 1)]
    public string ApiToken { get; init; } = string.Empty;
}
