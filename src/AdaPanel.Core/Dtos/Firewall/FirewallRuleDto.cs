namespace AdaPanel.Core.Dtos.Firewall;

public sealed class FirewallRuleDto
{
    public string DisplayName { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Protocol { get; set; } = string.Empty;

    public string LocalPort { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>
    /// Bu satır güvenlik duvarından GERİ OKUNDU mu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden var:</b> kural kurma/güncelleme yolu eskiden İSTENEN değerleri geri veriyor ve
    /// <c>Enabled = true</c> UYDURUYORDU — kuralın gerçekten oluşup oluşmadığına hiç bakılmadan.
    /// Denetim kaydı da o uydurmadan besleniyordu ("enabled True").
    /// </para>
    /// <para>
    /// Artık üç durum ayrıdır: kural okundu ve etkin (<c>Verified=true, Enabled=true</c>), kural
    /// okundu ama devre dışı (<c>Verified=true, Enabled=false</c>), kural OKUNAMADI
    /// (<c>Verified=false</c>) — sonuncusunda <see cref="Enabled"/> bir ölçüm DEĞİLDİR.
    /// </para>
    /// <para>
    /// Listeleme yolunda (<c>GetRulesAsync</c>) her satır tanımı gereği okunmuştur; JSON'dan
    /// bağlanan satırlarda alan bulunmadığı için burada varsayılan <c>true</c>'dur.
    /// </para>
    /// </remarks>
    public bool Verified { get; set; } = true;
}
