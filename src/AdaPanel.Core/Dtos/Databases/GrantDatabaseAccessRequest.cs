using System.ComponentModel.DataAnnotations;

namespace AdaPanel.Core.Dtos.Databases;

public sealed class GrantDatabaseAccessRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string Engine { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string DatabaseName { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 2)]
    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// Yetkinin verileceği hesabın host kısmı. BOŞ BIRAKIN: yetki, kullanıcının motorda VAR OLAN
    /// bütün hesaplarına verilir.
    /// </summary>
    /// <remarks>
    /// Varsayılan eskiden <c>%</c> idi ve bu iki şekilde zarar veriyordu: (1) hesap yerelle
    /// sınırlıyken yetki <c>'u'@'%'</c>'e yazılmaya çalışılıyor, işlem "kullanıcı bulunamadı" ile
    /// düşüyordu; (2) GRANT'ın hesap oluşturabildiği yapılandırmalarda panelin az önce yerelle
    /// sınırladığı kullanıcı için SESSİZCE her yerden geçerli yeni bir hesap doğuyordu. Boş
    /// bırakıldığında panel hesabı tahmin etmez, motordan okur.
    /// </remarks>
    [StringLength(255)]
    public string AllowedHost { get; init; } = string.Empty;

    public string[] Privileges { get; init; } = [];
}
