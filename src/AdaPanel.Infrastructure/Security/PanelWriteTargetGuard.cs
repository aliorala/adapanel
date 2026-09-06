using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Iis;

namespace AdaPanel.Infrastructure.Security;

/// <summary>Yöneticinin SEÇTİĞİ mutlak bir yazma hedefi hakkındaki karar.</summary>
public enum PanelWriteTargetVerdict
{
    /// <summary>Kabul edilir.</summary>
    Allowed,

    /// <summary>Boş, göreli ya da biçimsel olarak geçersiz.</summary>
    InvalidPath,

    /// <summary>UNC paylaşımı ya da aygıt yolu (<c>\\sunucu\pay</c>, <c>\\?\C:\x</c>).</summary>
    NetworkPath,

    /// <summary>Sürücü kökünün kendisi (<c>C:\</c>, <c>D:\</c>).</summary>
    DriveRoot,

    /// <summary>Windows / Program Files / ProgramData / panelin kendi veri kökü ya da bilinen bir sistem kapsayıcısı.</summary>
    SystemPath
}

/// <summary>
/// "Panel, kullanıcının verdiği BU mutlak yola yazabilir mi?" sorusunun TEK adresi.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR (ölçüldü).</b> Panel servisi <c>LocalSystem</c> olarak koşar. Üç ayrı uç, HTTP
/// gövdesinden gelen MUTLAK bir yolu hiç denetlemeden hedef kabul ediyordu:
/// </para>
/// <list type="bullet">
/// <item><c>POST /backup/restore</c> — <c>TargetPath</c> ile arşiv istenen klasöre açılıyordu;</item>
/// <item><c>POST /backup/restore-items</c> — aynı alan, seçili girdiler için;</item>
/// <item><c>POST /store/install</c> — <c>targetPath</c> mutlak olabiliyor, <c>cleanInstall</c> ise
/// hedefi boşaltıyordu;</item>
/// <item><c>POST /sites</c> (yönetici dalı) — <c>PhysicalPath</c> serbestti, dosya yöneticisinin
/// sanal alanı da site köküdür.</item>
/// </list>
/// <para>
/// Hepsinin ortak sonucu AYNIYDI: <c>%ProgramData%\AdaPanel\config\server-owner.json</c> panelin
/// HTTP yüzeyinden YAZILABİLİR hâle geliyordu. Oysa sunucu sahipliği korumasının bütün gerekçesi
/// o dosyanın HTTP'den yazılamamasıdır (bkz. <see cref="ServerOwnerRegistry"/>): yazılabildiği anda
/// sahip olmayan bir panel yöneticisi kendini sahip ilan edip terminale — yani SYSTEM kabuğuna —
/// giriyordu.
/// </para>
/// <para>
/// <b>KURAL "HER ŞEYİ REDDET" DEĞİLDİR.</b> Yönetici meşru olarak site kökü dışına da kurar
/// (<c>D:\siteler\musteri.com</c>), yedeği ayrı bir diske açar (<c>E:\geri-yukleme</c>). Bunları
/// yasaklayan bir çözüm testlerde yeşil görünür ve paneli işlevsiz bırakırdı. Reddedilen şey
/// SİSTEM ALANIDIR ve listesi ORTAMDAN okunur — sabit <c>"C:\Windows"</c> dizesi yazılmaz.
/// </para>
/// <para>
/// <b>Liste NEDEN burada tekrar yazılmadı:</b> aynı soru silme yolunda zaten sorulmuş ve
/// <see cref="SiteDeletionPathGuard.SystemForbiddenSubtrees"/> /
/// <see cref="SiteDeletionPathGuard.SystemForbiddenContainers"/> olarak ölçülmüştü. İkinci bir
/// kopya, birini sıkılaştırıp diğerini unutmanın yoludur; bu depoda tam olarak o yaşandı
/// (kara liste <c>C:\inetpub</c>, <c>C:\Users</c>, <c>C:\ProgramData</c> ve UNC'yi GEÇİRİYORDU).
/// </para>
/// <para>
/// <b>Silme kuralından FARKI bilerek var:</b> silme, "panelin tanıdığı bir site kökünün altında
/// olduğu KANITLANSIN" diyen bir BEYAZ LİSTEDİR — çünkü özyinelemeli silme geri alınamaz. Yazma
/// hedefi için aynı katılık fazla kapatma olurdu: yönetici panelin hiç bilmediği bir klasöre
/// (<c>D:\yeni-proje</c>) meşru olarak kurulum yapabilmeli. Burada verilen söz daha dar ve
/// ölçülebilir: <b>panelin kendi verisi ve işletim sistemi HTTP gövdesinden gelen bir yolla
/// üzerine yazılamaz.</b>
/// </para>
/// </remarks>
public static class PanelWriteTargetGuard
{
    /// <summary>
    /// Yol, HTTP'den gelen bir yazma hedefi olarak kabul edilebilir mi?
    /// </summary>
    /// <param name="candidate">Kullanıcının verdiği yol (mutlak olmalı; ortam değişkenleri genişletilir).</param>
    /// <param name="panelDataRoot">
    /// Panelin veri kökü (<c>%ProgramData%\AdaPanel</c> ya da taşınmış hâli). Sahiplik kaydı,
    /// <c>adapanel.db</c> ve DataProtection anahtarları oradadır; kendisi ve ALTI reddedilir.
    /// </param>
    public static PanelWriteTargetVerdict Evaluate(string? candidate, string? panelDataRoot)
    {
        var yol = PathContainment.Normalize(candidate);
        if (yol is null)
        {
            return PanelWriteTargetVerdict.InvalidPath;
        }

        // UNC/aygıt yolu: hedef bu sunucunun diski olmayabilir ve kapsama denetimi ağ tarafında
        // hiçbir şey ifade etmez. Silme yolunda da AYNI kararla reddediliyor.
        if (!SiteDeletionPathGuard.IsLocalDrivePath(yol))
        {
            return PanelWriteTargetVerdict.NetworkPath;
        }

        // Sürücü kökünün KENDİSİ (C:\) hedef olamaz: "site fiziksel yolu C:\" tam olarak ölçülen
        // saldırıydı — dosya yöneticisinin sanal alanı o siteye TÜM diski açıyordu.
        if (SiteDeletionPathGuard.SegmentsBelowRoot(yol) < 1)
        {
            return PanelWriteTargetVerdict.DriveRoot;
        }

        if (SiteDeletionPathGuard.IsInsideAny(SiteDeletionPathGuard.SystemForbiddenSubtrees(panelDataRoot), yol))
        {
            return PanelWriteTargetVerdict.SystemPath;
        }

        // KAPSAYICILARIN yalnız KENDİSİ reddedilir, ALTI değil: "C:\inetpub\wwwroot" bütün
        // sitelerin ortak kabıdır ve hedef olamaz, ama "C:\inetpub\wwwroot\musteri.com" panelin
        // EN OLAĞAN hedefidir. Bu ayrım olmadan varsayılan kurulum yolu ölürdü.
        if (SiteDeletionPathGuard.EqualsAny(SiteDeletionPathGuard.SystemForbiddenContainers(), yol))
        {
            return PanelWriteTargetVerdict.SystemPath;
        }

        return PanelWriteTargetVerdict.Allowed;
    }

    /// <summary>Reddin kullanıcıya gösterilecek GERÇEK sebebi (hata ayrıntısına girer).</summary>
    public static string Describe(PanelWriteTargetVerdict verdict) => verdict switch
    {
        PanelWriteTargetVerdict.Allowed => "kabul edildi",
        PanelWriteTargetVerdict.InvalidPath => "yol boş, göreli ya da biçimsel olarak geçersiz",
        PanelWriteTargetVerdict.NetworkPath => "ağ paylaşımı / aygıt yolu (bu sunucunun diski değil)",
        PanelWriteTargetVerdict.DriveRoot => "sürücü kökünün kendisi",
        PanelWriteTargetVerdict.SystemPath => "sistem alanı (Windows / Program Files / ProgramData / panel verisi / ortak site kabı)",
        _ => verdict.ToString()
    };
}
