namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Panelin varsayılan yedek şifreleme ayarı. Parolanın KENDİSİ hiçbir zaman dönmez; yalnızca
/// "kayıtlı bir parola var mı" bilgisi döner.
/// </summary>
public sealed class BackupEncryptionSettingsDto
{
    /// <summary>Yeni yedekler varsayılan olarak şifrelensin mi.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Panelde çözülebilir bir parola var mı. Enabled=true ama bu false ise parola kaydı
    /// ÇÖZÜLEMİYOR demektir (ör. panel başka makineye taşındı, DataProtection anahtarları gitti) —
    /// zamanlanmış yedekler şifrelenemez, kullanıcı parolayı yeniden girmelidir.
    /// </summary>
    public bool PasswordUsable { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Şifreleme algoritması ve anahtar türetme özeti (ekranda gösterilir).</summary>
    public string Algorithm { get; set; } = string.Empty;
}

/// <summary>
/// Varsayılan yedek şifreleme parolasını belirler.
/// <para>
/// <b>PAROLA KAYBOLURSA ŞİFRELİ YEDEKLER GERİ GETİRİLEMEZ.</b> Panelin kurtarma kapısı yoktur;
/// parolanın panel dışında da bir kopyası olmalıdır.
/// </para>
/// </summary>
public sealed class SetBackupEncryptionRequest
{
    /// <summary>Boş gönderilirse kayıt temizlenir ve yeni yedekler ŞİFRESİZ alınır.</summary>
    public string Password { get; set; } = string.Empty;
}
