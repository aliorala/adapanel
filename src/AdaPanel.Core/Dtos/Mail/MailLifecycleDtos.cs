namespace AdaPanel.Core.Dtos.Mail;

/// <summary>Posta kutusunu askıya alır (giriş/alım/gönderim kapanır, posta SİLİNMEZ) ya da yeniden açar.</summary>
public sealed class SetMailAccountActiveRequest
{
    public string Address { get; set; } = string.Empty;

    public bool Active { get; set; }
}

/// <summary>Var olan bir alias'ı günceller: hedef hesabı değiştirir ve/veya açar-kapatır. Boş alanlar değişmez.</summary>
public sealed class UpdateMailAliasRequest
{
    public string Address { get; set; } = string.Empty;

    /// <summary>Yeni hedef posta kutusu; null/boş = değişmez.</summary>
    public string? Target { get; set; }

    /// <summary>null = değişmez.</summary>
    public bool? Active { get; set; }
}

/// <summary>Posta kutusu başına yönlendirme (forward).</summary>
public sealed class MailForwardDto
{
    public string Address { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string Target { get; set; } = string.Empty;

    /// <summary>true = kutuda kopya kalır; false = posta yalnız hedefe gider.</summary>
    public bool KeepCopy { get; set; } = true;
}

public sealed class SetMailForwardRequest
{
    public string Address { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string Target { get; set; } = string.Empty;

    public bool KeepCopy { get; set; } = true;
}

public sealed class RotateMailDkimRequest
{
    public string Domain { get; set; } = string.Empty;
}

/// <summary>DKIM anahtar rotasyonunun sonucu. Başarısızlıkta istisna fırlar ve ESKİ anahtar aktif kalır.</summary>
public sealed class MailDkimRotationDto
{
    public string Domain { get; set; } = string.Empty;

    public string OldSelector { get; set; } = string.Empty;

    public string NewSelector { get; set; } = string.Empty;

    /// <summary>Eski anahtarın (ve DNS kaydının) kaldırılacağı an; bu andan önce eski kayıt DNS'te kalır.</summary>
    public DateTime RetireAfterUtc { get; set; }

    /// <summary>"rotated" ya da "retired".</summary>
    public string Status { get; set; } = string.Empty;
}
