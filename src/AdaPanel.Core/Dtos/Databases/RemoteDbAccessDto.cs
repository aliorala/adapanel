namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// profesyonel panel benzeri "Uzak Veritabanı Erişimi" durumu. Açıkken Windows Güvenlik Duvarı'nda 3306 portu
/// yalnız <see cref="AllowedIp"/> için açılır (boşsa her yer); kapalıyken kural kaldırılır.
/// </summary>
public sealed class RemoteDbAccessDto
{
    public bool Enabled { get; set; }

    /// <summary>İzin verilen istemci IP'si. Boş = her yer (Any) — önerilmez.</summary>
    public string AllowedIp { get; set; } = string.Empty;

    /// <summary>Masaüstü istemcinin bağlanacağı sunucu adresi (panel sunucu adresi).</summary>
    public string ServerHost { get; set; } = string.Empty;

    public int Port { get; set; } = 3306;
}

public sealed class UpdateRemoteDbAccessRequest
{
    public bool Enabled { get; init; }

    /// <summary>İzin verilen tek IP (boş = her yer). Enabled=true iken geçerli IP olmalı.</summary>
    public string? AllowedIp { get; init; }
}
