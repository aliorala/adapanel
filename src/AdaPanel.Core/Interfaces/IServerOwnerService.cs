namespace AdaPanel.Core.Interfaces;

/// <summary>
/// SUNUCU SAHİBİ — paneli kuran, makinenin kendisine sahip olan TEK kişi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden roldEN AYRI bir kavram:</b> panelde "Administrator" bir ROLDÜR ve bir yönetici
/// <c>UsersController</c> üzerinden İKİNCİ bir yönetici yaratabilir. Terminal ucu SYSTEM kabuğu
/// verdiği için, yetkiyi role bağlamak "yönetici olan herkes SYSTEM olabilir" demekti; o zaman da
/// bir yöneticinin başka bir yöneticiye koyduğu HİÇBİR sınır anlamlı olmazdı (saldırgan sınırı
/// aşmak yerine terminale girer). Sunucu sahipliği bu yüzden rolle DEĞİL, rol yükseltmesiyle ele
/// geçirilemeyen ayrı bir nitelikle belirlenir.
/// </para>
/// <para>
/// <b>Kalıbın kaynağı — Plesk ve cPanel aynı ayrımı yapıyor:</b> Plesk'te SSH Terminal eklentisi
/// yönetici için <c>root</c> kabuğu açar, ama EK yönetici hesapları "Restricted Mode" altında
/// çalışır ve Plesk'in kendi belgesi, yetki yükseltmesini önlemek için "SSH Terminal" kutusunun
/// bu hesaplarda İŞARETLENMEMESİ gerektiğini yazar. cPanel'de de WHM terminali root'a aittir;
/// hesap terminali varsayılan olarak KAPALIDIR ("disabled shell") ve açıldığında bile kabuk
/// kullanıcının kendi kimliğiyle koşar, root'la değil.
/// </para>
/// </remarks>
public interface IServerOwnerService
{
    /// <summary>Sunucu sahibinin kullanıcı kimliği; hiç etkin yönetici yoksa <c>null</c>.</summary>
    Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Verilen kullanıcı sunucunun sahibi mi.</summary>
    Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default);
}
