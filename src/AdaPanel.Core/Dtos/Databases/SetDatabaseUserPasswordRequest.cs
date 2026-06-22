namespace AdaPanel.Core.Dtos.Databases;

/// <summary>Bir veritabanı kullanıcısının parolasını değiştirir.</summary>
public sealed class SetDatabaseUserPasswordRequest
{
    public string Engine { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>İzinli host (MySQL/MariaDB için; boşsa '%').</summary>
    public string? AllowedHost { get; init; }
}
