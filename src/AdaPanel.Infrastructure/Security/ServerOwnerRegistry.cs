using AdaPanel.Core.Enums;

namespace AdaPanel.Infrastructure.Security;

/// <summary>Sunucu sahibi kararına giren TEK kullanıcı satırı (veritabanından bağımsız).</summary>
public readonly record struct SunucuSahibiAdayi(Guid Id, UserRole Role, bool IsActive, DateTime CreatedAtUtc);

/// <summary>
/// "Bu panelin sahibi kim?" sorusunun SAF karar noktası — dosya sistemi ve veritabanı olmadan
/// sınanabilir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kural (iki kaynak, sırayla):</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>Sabitlenmiş kayıt</b> — <c>config\server-owner.json</c>. Bu dosya panelin HTTP yüzeyinden
/// yazılamaz; onu değiştirmek sunucunun dosya sistemine erişmeyi gerektirir. Aynı gerekçe bu
/// depoda zaten yazılı (<c>OutboundTargetPolicy.AllowInternalTargets</c>,
/// <c>PanelRuntimeOptions.AllowUnauthenticatedLocalSenders</c>): korumayı, korunan yüzeyin
/// kendisinden kapatılabilir yapmak korumayı kaldırmakla aynı kapıya çıkar.
/// </item>
/// <item>
/// <b>Geri düşüş — EN ESKİ etkin yönetici.</b> Kurulum sihirbazının yarattığı ilk yönetici
/// paneli KURAN kişidir; sonradan yaratılan her yöneticinin <c>CreatedAtUtc</c>'si daha
/// büyüktür, yani yeni bir yönetici hesabı açmak sahipliği ELE GEÇİRMEZ.
/// </item>
/// </list>
/// <para>
/// <b>FAZLA KAPATMA BİLEREK ENGELLENDİ.</b> Kayıt YOKSA (hiç yazılmamış, bozuk, boş, okunamıyor)
/// karar "kimse giremez" DEĞİL, geri düşüştür. Terminali sahibine de kapatan bir çözüm özelliği
/// öldürürdü; buradaki tehdit "yönetici olan herkes SYSTEM olur", "sahip de SYSTEM olamasın"
/// değil.
/// </para>
/// <para>
/// <b>Ama kayıt VARSA son söz onundur — gösterdiği kullanıcı artık etkin bir yönetici olmasa
/// bile sonuç "sahip yok"tur, "sıradaki" değil.</b> Bu ayrım bir testin yakaladığı gerçek bir
/// delikti: geri düşüş burada da geçerli olsaydı, sahibi pasife alan/silen biri sahipliği
/// DEVRALIRDI. Kurtarma yolu kapalı değil, yalnızca panelin dışında: sunucudaki dosya.
/// </para>
/// <para>
/// <b>Berabere kalma:</b> aynı ana yaratılmış iki yönetici olabilir (toplu içe aktarma, saat
/// çözünürlüğü). Karar RASTGELE olamaz — aksi hâlde aynı panel iki istekte iki farklı sahip
/// döndürürdü. Eşitlik kimliğe (<c>Guid</c>) göre bozulur: kararlı ve yeniden üretilebilir.
/// </para>
/// </remarks>
public static class ServerOwnerRegistry
{
    /// <summary>Sabitlenmiş kaydın veri kökündeki göreli yolu.</summary>
    public const string MarkerRelativePath = "config/server-owner.json";

    /// <summary>
    /// Sahibi belirler. <paramref name="sabitlenmis"/> geçerli bir aday değilse en eski etkin
    /// yöneticiye düşer; hiç etkin yönetici yoksa <c>null</c>.
    /// </summary>
    public static Guid? Belirle(Guid? sabitlenmis, IEnumerable<SunucuSahibiAdayi> kullanicilar)
    {
        ArgumentNullException.ThrowIfNull(kullanicilar);

        var adaylar = kullanicilar
            .Where(aday => aday.Role == UserRole.Administrator && aday.IsActive)
            .ToArray();

        // SABİTLENMİŞ KAYIT VARSA SON SÖZ ONUNDUR — geçersiz olsa bile.
        //
        // Bu ayrım bir testin YAKALADIĞI gerçek bir delikti: "geçersizse sıradakine düş" kuralıyla,
        // sahibi pasife alan/silen biri sahipliği DEVRALIYORDU. Kayıt bir kez çivilendikten sonra
        // veritabanındaki sıralamayı değiştirmek sahipliği değiştiremez; kaydın kendisini
        // değiştirmek gerekir ve o dosya panelin HTTP yüzeyinden yazılamaz.
        //
        // "Kayıt VAR ama geçersiz" ile "kayıt YOK" aynı şey DEĞİLDİR: okunamayan/bozuk/boş dosya
        // <c>null</c> olarak gelir ve aşağıdaki geri düşüşe girer (fazla kapatma nöbetçisi).
        if (sabitlenmis is { } pinned)
        {
            return adaylar.Any(aday => aday.Id == pinned) ? pinned : null;
        }

        if (adaylar.Length == 0)
        {
            return null;
        }

        return adaylar
            .OrderBy(aday => aday.CreatedAtUtc)
            .ThenBy(aday => aday.Id)
            .First()
            .Id;
    }
}

/// <summary>Sabitlenmiş sunucu sahibi kaydının disk biçimi.</summary>
public sealed class SunucuSahibiKaydi
{
    /// <summary>
    /// Sahip kullanıcının kimliği. Boş/bozuk değer, kaydın YOK sayılması demektir (geri düşüş).
    /// </summary>
    public Guid? UserId { get; set; }
}
