using System.Text.Json;

namespace AdaPanel.Api.Tray;

/// <summary>Tepside gösterilecek bir bileşen: panelin bildirdiği ad, başlık ve kurulum dizini.</summary>
/// <param name="ServisAdi">Windows servis adı; bileşenin servisi yoksa boş.</param>
public sealed record IzlenenServis(string Anahtar, string Baslik, string ServisAdi, string KurulumDizini)
{
    /// <summary>Bu bileşen bir Windows servisi olarak çalışıyor mu (yoksa yalnız dosya mı).</summary>
    public bool ServisiVar => ServisAdi.Length > 0;
}

/// <summary>
/// Tepsinin izleyeceği servisleri PANELİN KENDİ BİLDİRİMİNDEN okur.
/// </summary>
/// <remarks>
/// <para>
/// İlk sürümde servis adları elle yazılmış bir liste olarak tutuluyordu ("MariaDB", "MySQL",
/// "postgresql-16"…). Bu bir TAHMİNDİ ve yanlıştı: panel bileşenleri kendi kurduğunda servisi
/// <c>AdaMariaDB</c>, <c>AdaMySQL</c>, <c>AdaPostgreSQL</c>, <c>AdaStalwart</c> gibi adlarla
/// kaydediyor. Sonuç: kurulu bir MariaDB, tepside "kurulu değil" görünüyordu.
/// </para>
/// <para>
/// Doğru kaynak <c>components.json</c>: panelle birlikte kurulan, exe'nin yanında duran ve her
/// bileşenin <c>serviceName</c> alanını içeren bildirim. Yönetici yetkisi gerektirmez ve panel
/// çalışmıyorken de okunur — tepsi programının şartı budur. (Panelin veritabanı da bu bilgiyi
/// tutuyor ama sertleştirme gereği normal kullanıcıya kapalı; ölçüldü.)
/// </para>
/// <para>
/// Bildirim okunamazsa liste UYDURULMAZ; çağıran tarafa "okunamadı" bilgisi döner ve ekranda
/// öyle yazar. Var olmayan bir listeyi göstermek, kurulu servisleri yok saymaktan daha kötüdür.
/// </para>
/// </remarks>
public static class BilesenListesi
{
    /// <summary>
    /// Bileşen olmayan, sabit servisler. Bunlar tahmin değil: <c>AdaPanel</c> panelin kendi
    /// servisi (kurulum onu bu adla kaydeder), <c>W3SVC</c> ve <c>FTPSVC</c> ise Windows'un
    /// kendi bileşenlerinin değişmeyen servis adlarıdır.
    /// </summary>
    private static readonly IzlenenServis[] Sabitler =
    [
        new("adapanel", "AdaPanel", "AdaPanel", string.Empty),
        new("iis", "IIS (web sunucusu)", "W3SVC", string.Empty),
        new("ftp", "FTP", "FTPSVC", string.Empty)
    ];

    /// <summary>Bildirim okunabildi mi. Okunamadıysa liste yalnız sabitleri içerir.</summary>
    public static bool BildirimOkundu { get; private set; }

    /// <summary>Bildirim okunamadıysa sebebi.</summary>
    public static string BildirimHatasi { get; private set; } = string.Empty;

    public static IReadOnlyList<IzlenenServis> Oku()
    {
        var liste = new List<IzlenenServis>(Sabitler);
        BildirimOkundu = false;
        BildirimHatasi = string.Empty;

        var yol = BildirimYolu();
        if (yol is null)
        {
            BildirimHatasi = "components.json bulunamadı";
            return liste;
        }

        try
        {
            using var akis = File.OpenRead(yol);
            using var belge = JsonDocument.Parse(akis);

            if (!belge.RootElement.TryGetProperty("components", out var bilesenler)
                || bilesenler.ValueKind != JsonValueKind.Array)
            {
                BildirimHatasi = "components.json beklenen biçimde değil";
                return liste;
            }

            foreach (var bilesen in bilesenler.EnumerateArray())
            {
                // SERVİSİ OLMAYAN BİLEŞENLER DE LİSTELENİR.
                //
                // Önce yalnız `serviceName` alanı dolu olanlar alınıyordu; Roundcube, phpMyAdmin,
                // PHP, Composer gibi Windows servisi olmayan ama KURULU bileşenler tepside hiç
                // görünmüyordu. Kullanıcı "kurulu ama listede yok" diye her birini tek tek
                // bildirmek zorunda kaldı — oysa bildirim hepsini zaten sayıyor. Bileşenin
                // servisi olup olmaması, onun VAR OLUP OLMADIĞINDAN ayrı bir sorudur.
                var servisAdi = Metin(bilesen, "serviceName");
                var kimlik = Metin(bilesen, "id");
                var baslik = Metin(bilesen, "displayName");
                // installDir DOSYALARIN kurulu olup olmadigini soyler. Servisin kayitli olmamasi
                // "kurulu degil" DEMEK DEGILDIR: olculdu -- stalwart-mail'in 121 MB dosyasi
                // duruyordu ama AdaStalwart servisi Windows'a hic kayitli degildi. Ikisini tek
                // duruma indirmek, duzeltilebilir bir arizayi "yok" diye gostermek olurdu.
                var dizin = Metin(bilesen, "installDir");
                liste.Add(new IzlenenServis(
                    kimlik.Length > 0 ? kimlik : servisAdi,
                    baslik.Length > 0 ? baslik : servisAdi,
                    servisAdi,
                    dizin.Length > 0 ? Environment.ExpandEnvironmentVariables(dizin) : string.Empty));
            }

            BildirimOkundu = true;
        }
        catch (Exception hata)
        {
            BildirimHatasi = hata.Message;
        }

        return liste;
    }

    private static string Metin(JsonElement oge, string ad)
        => oge.TryGetProperty(ad, out var deger) && deger.ValueKind == JsonValueKind.String
            ? deger.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Bildirim dosyasının yolu: önce programın yanı, sonra servisin kurulum klasörü.
    /// Taşınabilir bir kopyadan çalıştırıldığında da doğru dosya bulunsun.
    /// </summary>
    private static string? BildirimYolu()
    {
        var adaylar = new List<string>();

        var kendiKlasoru = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(kendiKlasoru))
        {
            adaylar.Add(Path.Combine(kendiKlasoru, "components.json"));
        }

        var kurulum = PanelAdresi.KurulumKlasoru();
        if (!string.IsNullOrWhiteSpace(kurulum))
        {
            adaylar.Add(Path.Combine(kurulum, "components.json"));
        }

        return adaylar.FirstOrDefault(File.Exists);
    }
}
