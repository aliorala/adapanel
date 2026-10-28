namespace AdaPanel.Core.Dtos.Backup;

public sealed class RestoreBackupRequest
{
    public string BackupId { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public bool AdaptDefiners { get; set; }

    /// <summary>
    /// Şifreli yedeğin parolası. Boş bırakılırsa panelde KAYITLI parola denenir.
    /// <para>
    /// Yanlış parolada işlem <c>backup.decrypt_wrong_password</c> ile durur ve canlı içeriğe
    /// DOKUNULMAZ — "bozuk dosya" denip kullanıcının yedeğinden şüphelenmesi kabul edilemez.
    /// </para>
    /// </summary>
    public string EncryptionPassword { get; set; } = string.Empty;
}
