namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Bir budama turunun sonucu: kaç kayıt GERÇEKTEN kapandı ve kaçının uzak kopyası silinemedi.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN HATA — "Tamamlandı, 7 eski yedek silindi" derken FTP'de dosyalar duruyordu.</b>
/// Budama yalnızca BAŞARI SAYISINI döndürüyordu. Uzak kopyası silinemeyen kayıtlar sayıya
/// girmiyordu (o kısmı doğruydu) ama çağıran bunu ÖĞRENEMİYORDU: başarısızlık sadece
/// <c>LogWarning</c>'e gidiyor, gece işinin durumu <c>completed</c> kalıyor ve özet cümlesi yalnız
/// başarıları yazıyordu. Kullanıcı panelde "Tamamlandı" okuyup FTP'de dosyaları görüyordu —
/// bu deponun en pahalı hata sınıfı: "başarılı der, arkada olmamıştır".
/// </para>
/// <para>
/// Bu yüzden dönüş tipi sayıdan bu kayda çevrildi. Tek bir <c>int</c>, çağıranın doğruyu
/// söyleyebilmesi için YETERSİZ bilgidir.
/// </para>
/// </remarks>
public sealed class BackupPruneResultDto
{
    /// <summary>Kaydı ve dosyaları (yerel + varsa uzak) TAMAMEN silinmiş yedek sayısı.</summary>
    public int Removed { get; set; }

    /// <summary>
    /// Yerel kopyası silinen ama UZAK kopyası silinemeyen yedek sayısı. Bu kayıtlar
    /// <c>deleted_remote_failed</c> olarak işaretlenir, listeden ÇIKARILMAZ (yetim üretmemek için)
    /// ve bir sonraki budamada yeniden denenir.
    /// </summary>
    public int RemoteDeleteFailed { get; set; }

    /// <summary>
    /// Yerel dosyası silinemediği için (kilit/izin) hiç dokunulmamış yedek sayısı. Kayıt olduğu
    /// gibi korunur.
    /// </summary>
    public int LocalDeleteFailed { get; set; }

    /// <summary>
    /// Başarısızlıkların TEKNİK özeti (<c>ad: sebep</c>). ÇEVRİLMEZ; teşhis içindir ve olduğu gibi
    /// gösterilir — bu depoda sunucunun ürettiği serbest metin dilden bağımsız teknik bilgi taşır.
    /// </summary>
    public List<string> Failures { get; set; } = [];

    /// <summary>Tur temiz mi? Sayıyı kullanan çağıranın "tamamlandı" demeden önce bakacağı tek alan.</summary>
    public bool HasFailures => RemoteDeleteFailed > 0 || LocalDeleteFailed > 0;
}
