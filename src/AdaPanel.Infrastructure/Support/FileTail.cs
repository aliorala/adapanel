using System.Text;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Bir dosyanın SON birkaç kilobaytını, dosyanın tamamını belleğe almadan okur.
/// </summary>
/// <remarks>
/// <para>
/// <b>KAPATILAN AÇIK (ölçüldü):</b> iki yer, DÖNMEYEN bir <c>error.log</c>'u
/// <c>File.ReadAllText</c> ile TAMAMEN belleğe okuyup yalnız sonundan birkaç KB alıyordu —
/// <c>NginxInstallationSupport.ReadErrorLog</c> (son 4 KB) ve <c>WebmailService</c> Roundcube
/// günlüğü (son 1,5 KB). Bu günlüklerin bir üst sınırı yoktur; aylarca çalışan bir sunucuda
/// gigabaytlara çıkabilirler. Yani 1,5 KB göstermek için 1 GB okunuyordu.
/// </para>
/// <para>
/// <b>Doğru kalıp zaten depoda vardı</b> — <c>LogViewerService.ReadTailLines</c> pencereyle SEEK
/// eder ve 1 GB'lık bir günlüğü 2 MB'a mal eder. O metot SATIR sayısına göre çalışır ve kendi
/// yolunda doğrudur; buradaki iki çağrı yerinin ihtiyacı ise "son N BAYT". İki ayrı şekil olduğu
/// için o metot kopyalanmadı, ortak olan DAVRANIŞ buraya yazıldı.
/// </para>
/// <para>
/// <b>Dosya paylaşımı — asıl kazanç burada.</b> <c>File.ReadAllText</c> dosyayı
/// <c>FileShare.Read</c> ile açar; yani "başkaları okuyabilir" der ama YAZMASINA izin vermez.
/// Bu günlükleri yazan süreç (nginx, PHP) dosyayı yazmak için AÇIK tutar, dolayısıyla okuma
/// <c>IOException</c> ile düşebilir. Ölçülen sonuç iki yerde de kötüydü:
/// </para>
/// <list type="bullet">
/// <item><b>Nginx:</b> okuma <c>throw new AppException("nginx.start_failed", …, ReadErrorLog(…))</c>
/// ifadesinin İÇİNDE ve etrafında try/catch YOK. Okuma patlarsa dosya hatası, anlamlı "nginx
/// başlatılamadı" hatasının YERİNE geçer — teşhis tam da teşhise ihtiyaç duyulan anda kaybolur.</item>
/// <item><b>Webmail:</b> try/catch var ama boş dize dönüyor; günlük sessizce yok olur.</item>
/// </list>
/// <para>
/// Burada <c>FileShare.ReadWrite | FileShare.Delete</c> kullanılır: yazan süreç işine devam eder,
/// biz de okuruz. <c>Delete</c> de gerekir çünkü günlük döndürme (rename/delete) sırasında açık
/// tutulan dosya okunamaz hâle gelirdi.
/// </para>
/// </remarks>
public static class FileTail
{
    /// <summary>
    /// Dosyanın son <paramref name="maxBytes"/> baytını metin olarak döner. Dosya yoksa,
    /// okunamıyorsa ya da boşsa boş dize döner — bu bir TEŞHİS yardımcısıdır, kendi arızası
    /// çağıranın asıl hatasını gölgelememelidir.
    /// </summary>
    public static string ReadLastBytes(string path, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            using var akis = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var uzunluk = akis.Length;
            if (uzunluk <= 0)
            {
                return string.Empty;
            }

            var okunacak = (int)Math.Min(uzunluk, maxBytes);
            akis.Seek(-okunacak, SeekOrigin.End);

            var tampon = new byte[okunacak];
            var toplam = 0;
            while (toplam < okunacak)
            {
                // TEK ReadExactly/Read YETMEZ: FileStream istenen kadarını okumak ZORUNDA değildir.
                // Kısa okumayı yok saymak, tamponun sonunda sıfır baytlar bırakırdı.
                var adet = akis.Read(tampon, toplam, okunacak - toplam);
                if (adet <= 0)
                {
                    break;
                }

                toplam += adet;
            }

            return Coz(tampon, toplam);
        }
        catch (Exception)
        {
            // Teşhis okuması ASLA çağıranın hatasının yerine geçmemeli. Bu yakalama, nginx
            // yolundaki "dosya hatası, nginx.start_failed'in yerine geçiyor" davranışının
            // kendisini kapatır.
            return string.Empty;
        }
    }

    /// <summary>
    /// Baytları UTF-8 olarak çözer; baştaki YARIM karakteri atar.
    /// </summary>
    /// <remarks>
    /// Dosyanın ortasından başladığımız için ilk bayt bir karakterin ORTASINA denk gelebilir.
    /// Doğrudan çözmek metnin başına bir "�" koyardı. UTF-8'de devam baytları <c>10xxxxxx</c>
    /// kalıbındadır; baştaki devam baytları atlanınca kalan dizi geçerli bir sınırdan başlar.
    /// </remarks>
    private static string Coz(byte[] tampon, int adet)
    {
        if (adet <= 0)
        {
            return string.Empty;
        }

        var baslangic = 0;
        while (baslangic < adet && (tampon[baslangic] & 0xC0) == 0x80)
        {
            baslangic++;
        }

        return Encoding.UTF8.GetString(tampon, baslangic, adet - baslangic);
    }
}
