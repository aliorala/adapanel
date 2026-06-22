namespace AdaPanel.Core.Dtos.System;

/// <summary>
/// Sunucunun bir ağ arabirimine bağlı IP adresi (Plesk "IP Addresses"). Site binding'lerinde
/// "tüm IP'ler (*)" yerine belirli bir IP (dedicated IP) seçmek için kullanılır.
/// </summary>
public sealed class ServerIpAddressDto
{
    public string Address { get; init; } = string.Empty;

    /// <summary>"IPv4" veya "IPv6".</summary>
    public string Family { get; init; } = string.Empty;

    /// <summary>Adresin bağlı olduğu ağ arabiriminin adı.</summary>
    public string InterfaceName { get; init; } = string.Empty;
}
