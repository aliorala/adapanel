namespace AdaPanel.Core.Dtos.Store;

/// <summary>
/// Uygulama kurulum sonucu. WordPress gibi tam otomatik (DB + admin) kurulan şablonlarda, kullanıcının hemen giriş
/// yapabilmesi için yönetici giriş adresi + kullanıcı + parola döner (yalnız bu yanıtta gösterilir).
/// </summary>
public sealed class AppInstallResultDto
{
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>WordPress otomatik kurulduysa yönetici paneli adresi (.../wp-admin/).</summary>
    public string AdminUrl { get; set; } = string.Empty;

    /// <summary>Oluşturulan WordPress yönetici kullanıcı adı.</summary>
    public string AdminUser { get; set; } = string.Empty;

    /// <summary>Oluşturulan WordPress yönetici parolası (kaydedin — tekrar gösterilmez).</summary>
    public string AdminPassword { get; set; } = string.Empty;
}
