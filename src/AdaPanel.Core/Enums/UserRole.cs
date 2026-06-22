namespace AdaPanel.Core.Enums;

public enum UserRole
{
    Administrator = 1,
    Operator = 2,

    /// <summary>Yalnızca kendisine atanan sitelerle sınırlı müşteri hesabı.</summary>
    Customer = 3,

    /// <summary>
    /// Bayi: yalnızca KENDI oluşturduğu müşterileri ve onların sitelerini yönetir.
    /// Sunucu seviyesindeki ayarlara/araçlara erişemez. İzolasyon sahiplik (OwnerId) ile sağlanır.
    /// </summary>
    Reseller = 4
}

